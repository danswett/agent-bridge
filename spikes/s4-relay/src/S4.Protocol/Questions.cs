using System.Collections.Concurrent;
using System.Text.Json;

namespace S4;

// Spike S5's stand-in for ask_user: a host holds questions, notifies the account's phones
// by Web Push, and takes the answer back over the end-to-end channel. Push carries only a
// short, encrypted notice; the question and its answer travel over Noise.
public sealed class QuestionService
{
    public sealed record Question(string Id, string Text, string[] Choices, DateTimeOffset AskedAt)
    {
        public string? Answer { get; set; }
        public string? AnsweredBy { get; set; }
        // Subscribers this question's notice has not reached because the relay connection
        // was down; sent again on the next connection.
        internal ConcurrentDictionary<string, byte> Unsent { get; } = new();
    }

    public sealed record Subscriber(PushSubscription Sub, string AppUrl);

    sealed record Store(string Account, Dictionary<string, Subscriber> Subscribers);

    // Replaced on every reconnect; questions and subscriptions outlive a connection - but
    // only within one account. Push goes straight to the phone, around the relay's
    // account-scoped routing, so a subscription kept across a change of account would
    // deliver the new account's questions to the old account's phones.
    public RelayClient Relay
    {
        get => relay;
        set
        {
            if (value.AccountId != account)
            {
                subscribers.Clear();
                account = value.AccountId;
                Save();
            }
            relay = value;
            // A question asked while the host was reconnecting must still reach a phone
            // that is asleep; nothing else would ever tell it.
            _ = Task.Run(SendUnsentAsync);
        }
    }
    RelayClient relay;
    string account;

    async Task SendUnsentAsync()
    {
        foreach (var q in questions.Values.Where(q => q.Answer is null && !q.Unsent.IsEmpty).ToArray())
        {
            await NotifyAsync(q, [.. q.Unsent.Keys]);
        }
    }
    readonly string machine;
    readonly Action<string>? log;
    readonly string? storePath;
    readonly ConcurrentDictionary<string, Subscriber> subscribers = new();
    readonly ConcurrentDictionary<string, Question> questions = new();

    // Subscriptions persist, so a host that restarts can still reach a phone whose PWA is
    // closed; the phone has no reason to send its subscription again until it is opened.
    // The store names the account it belongs to, and one written for another account (or
    // in no account's name) is ignored.
    public QuestionService(RelayClient relay, string machine, Action<string>? log = null, string? storePath = null)
    {
        this.relay = relay; account = relay.AccountId; this.machine = machine; this.log = log; this.storePath = storePath;
        if (storePath != null && File.Exists(storePath))
        {
            Store? store = null;
            try { store = JsonSerializer.Deserialize<Store>(File.ReadAllText(storePath)); } catch (JsonException) { }
            if (store?.Account == account && store.Subscribers != null)
            {
                foreach (var (k, v) in store.Subscribers) { subscribers[k] = v; }
            }
        }
    }

    void Save()
    {
        if (storePath != null) { File.WriteAllText(storePath, JsonSerializer.Serialize(new Store(account, subscribers.ToDictionary()))); }
    }

    public int Subscribers => subscribers.Count;
    public IReadOnlyCollection<Question> Questions => [.. questions.Values];
    public event Action<Question>? Answered;

    public async Task<object?> HandleAsync(SecureChannel ch, string op, JsonElement data)
    {
        switch (op)
        {
            case "push-subscribe":
                {
                    var sub = new PushSubscription(data.GetProperty("endpoint").GetString()!, data.GetProperty("p256dh").GetString()!, data.GetProperty("auth").GetString()!);
                    var url = data.GetProperty("appUrl").GetString()!;
                    // Checked here, where it is cheap: a bad key would only fail at the next push.
                    S2.Noise.P256KeyPair.AssertOnCurve(B64Url.Decode(sub.P256dh));
                    if (B64Url.Decode(sub.Auth).Length != 16) { throw new ArgumentException("auth must be 16 bytes"); }
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Host is not ("127.0.0.1" or "localhost"))) { throw new ArgumentException("appUrl must be https"); }
                    subscribers[ch.PeerId] = new Subscriber(sub, url);
                    Save();
                    log?.Invoke($"push subscription from {ch.PeerName} ({new Uri(sub.Endpoint).Host})");
                    return new { subscribed = true };
                }
            case "ask-me":
                {
                    var delay = Math.Clamp(data.ValueKind == JsonValueKind.Object && data.TryGetProperty("delaySeconds", out var d) ? d.GetInt32() : 10, 0, 300);
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(delay));
                        await AskAsync("Which database should I use for the session store?", ["PostgreSQL (Recommended)", "SQLite", "DuckDB"]);
                    });
                    return new { scheduled = delay };
                }
            case "questions":
                return questions.Values.Where(q => q.Answer is null).OrderBy(q => q.AskedAt).Select(View).ToArray();
            case "question":
                return questions.TryGetValue(data.GetProperty("q").GetString() ?? "", out var one) ? View(one) : new { missing = true };
            case "answer":
                {
                    var q = questions.GetValueOrDefault(data.GetProperty("q").GetString() ?? "");
                    if (q is null) { return new { accepted = false, reason = "unknown question" }; }
                    var answer = data.GetProperty("answer").GetString() ?? "";
                    lock (q)
                    {
                        // First answer wins, as on the dashboard today; a later tap is refused, not applied.
                        if (q.Answer != null) { return new { accepted = false, reason = $"already answered: {q.Answer}" }; }
                        q.Answer = answer.Length > 500 ? answer[..500] : answer;
                        q.AnsweredBy = ch.PeerName;
                    }
                    log?.Invoke($"answer to {q.Id} from {ch.PeerName}: {q.Answer}");
                    Answered?.Invoke(q);
                    return new { accepted = true };
                }
        }
        return null;
    }

    static object View(Question q) => new { q = q.Id, text = q.Text, choices = q.Choices, askedAt = q.AskedAt, answer = q.Answer };

    public async Task<Question> AskAsync(string text, string[] choices)
    {
        var q = new Question(Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(), text, choices, DateTimeOffset.UtcNow);
        questions[q.Id] = q;
        log?.Invoke($"asked {q.Id}: {text}; notifying {subscribers.Count} subscriber(s)");
        await NotifyAsync(q, [.. subscribers.Keys]);
        return q;
    }

    async Task NotifyAsync(Question q, string[] peers)
    {
        var relay = Relay;
        foreach (var peer in peers)
        {
            if (!subscribers.TryGetValue(peer, out var s)) { q.Unsent.TryRemove(peer, out _); continue; }
            // Declarative Web Push (Safari 18.4+) shape: shown even if the service worker does
            // not run. The service worker uses the same fields elsewhere.
            var navigate = $"{s.AppUrl}?q={q.Id}&host={relay.DeviceId}";
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                web_push = 8030,
                notification = new { title = $"{machine} asks", body = q.Text, navigate, tag = $"q{q.Id}", lang = "en" },
            });
            try
            {
                var r = await relay.PushAsync(s.Sub.Endpoint, WebPushCrypto.Encrypt(payload, s.Sub), ttl: 3600, urgency: "high", topic: PushTopic.From(Convert.FromHexString(q.Id)));
                q.Unsent.TryRemove(peer, out _);
                log?.Invoke($"push to {new Uri(s.Sub.Endpoint).Host}: {r.Status}{(r.Error is null ? "" : $" {r.Error}")}");
                // The browser has unsubscribed or the subscription expired.
                if (r.Status is 404 or 410) { subscribers.TryRemove(peer, out _); Save(); }
            }
            // The connection is down or went down mid-push (a disposed socket throws, too).
            catch (Exception e) when (e is IOException or TimeoutException or System.Net.WebSockets.WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                q.Unsent[peer] = 0;
                log?.Invoke($"push failed, will retry on reconnect: {e.Message}");
            }
        }
    }
}
