using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// When the host notifies, what it sends, and to whom: the rules ported from agent-ha-bridge,
// on a fake clock with a fake source and push sender, so every delay is exact.
public sealed class PushNotifierTests : IDisposable
{
    const string Account = "1";
    const string Machine = "53d1997f0a1b2c3d4e5f60718293a4b5";
    const string Phone = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string Laptop = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    static readonly DateTimeOffset T = new(2026, 10, 28, 4, 0, 0, TimeSpan.Zero);

    readonly FakeTimeProvider time = new(T);
    readonly FakeSource source = new();
    readonly FakeSender sender = new(Account);
    readonly Dictionary<string, (P256KeyPair Key, byte[] Auth)> browsers = [];
    readonly List<string> logs = [];
    readonly string folder = Path.Combine(Path.GetTempPath(), "abpush-" + Guid.NewGuid().ToString("N"));
    PushNotifier notifier = null!;
    PushStore store = null!;

    public PushNotifierTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        foreach (var b in browsers.Values) { b.Key.Dispose(); }
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    string StorePath => Path.Combine(folder, "push-subscriptions.json");

    PushNotifier Start(PushOptions? options = null, params Session[] existing)
    {
        source.Sessions.AddRange(existing);
        store = PushStore.Load(StorePath, Account);
        notifier = new PushNotifier(source, new MachineInfo(Machine, "DEV", "windows", "0.1.0"), store, options, logs.Add, time);
        notifier.Attach(sender);
        return notifier;
    }

    static Session S(string id, SessionStatus status, string? response = null, Question? question = null, int turns = 1, string? title = "Fix the login") => new()
    {
        Id = id,
        Agent = AgentKind.Copilot,
        Title = title,
        Status = question is null ? status : SessionStatus.Waiting,
        StatusSince = T,
        UpdatedAt = T,
        Activity = new Activity { Response = response, TurnCount = turns },
        Question = question,
    };

    static Question Q(string id, string message = "Which database?", QuestionKind kind = QuestionKind.Form) =>
        new() { Id = id, Kind = kind, Message = message, AskedAt = T };

    PushPeer Peer(string device, string account = Account, bool inAccount = true) => new(device, device == Phone ? "iPhone" : "Laptop", account, inAccount);

    object? Subscribe(string device, string? endpoint = null, PushPeer? peer = null)
    {
        if (!browsers.TryGetValue(device, out var b)) { browsers[device] = b = (P256KeyPair.Generate(), System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)); }
        var data = JsonSerializer.SerializeToElement(new
        {
            endpoint = endpoint ?? $"https://web.push.apple.com/{device}",
            keys = new { p256dh = B64Url.Encode(b.Key.Public), auth = B64Url.Encode(b.Auth) },
            app = "https://danswett.github.io/agent-bridge/app/",
            device = device == Phone ? "iPhone" : "Laptop",
        });
        return notifier.Handle(peer ?? Peer(device), Ops.PushSubscribe, data);
    }

    // What the browser decrypts: the endpoint's device, the notification and its push headers.
    List<(string Device, JsonElement Notification, int Ttl, string Urgency, string? Topic)> Received() =>
        [.. sender.Sent.Select(p =>
        {
            var device = p.Endpoint.Split('/')[^1];
            var (key, auth) = browsers[device];
            var plain = WebPushCrypto.Decrypt(p.Body, key, auth);
            Assert.True(plain.Length <= PushNotice.MaxBytes);
            return (device, JsonDocument.Parse(plain).RootElement.GetProperty("notification"), p.Ttl, p.Urgency, p.Topic);
        })];

    static string Kind(JsonElement n) => n.GetProperty("data").GetProperty("kind").GetString()!;

    async Task After(TimeSpan t)
    {
        time.Advance(t);
        await notifier.PumpAsync();
    }

    [Fact]
    public async Task A_new_question_is_notified_once_after_its_delay_however_often_the_session_changes()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Working));
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));

        await After(TimeSpan.FromSeconds(1));
        Assert.Empty(sender.Sent);
        await After(TimeSpan.FromSeconds(1));
        var (device, n, ttl, urgency, topic) = Assert.Single(Received());
        Assert.Equal(Phone, device);
        Assert.Equal("Fix the login", n.GetProperty("title").GetString());
        Assert.Equal("DEV · waiting on you\nWhich database?", n.GetProperty("body").GetString());
        Assert.Equal("question", Kind(n));
        Assert.Equal("q1", n.GetProperty("data").GetProperty("question").GetString());
        Assert.Equal($"https://danswett.github.io/agent-bridge/app/?host={Machine}&session=s1&question=q1", n.GetProperty("navigate").GetString());
        Assert.Equal((86400, "high", PushNotice.Topic(Machine, "s1")), (ttl, urgency, topic));

        // The same question in later upserts, and seen pending again after an answer was
        // typed (agent-ha-bridge's reason for keeping its dedupe until the question is done).
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        source.Upsert(S("s1", SessionStatus.Waiting, response: "thinking", question: Q("q1")));
        await After(TimeSpan.FromSeconds(10));
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task A_question_answered_at_the_machine_before_its_delay_is_never_notified()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(1));
        source.Upsert(S("s1", SessionStatus.Working));

        await After(TimeSpan.FromSeconds(10));
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task An_answered_question_is_replaced_quietly_where_it_was_shown_but_not_on_the_device_that_answered()
    {
        Start();
        Subscribe(Phone);
        Subscribe(Laptop);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        Assert.Equal(2, sender.Sent.Count);

        notifier.Answered(Laptop, "s1", "q1");
        source.Upsert(S("s1", SessionStatus.Working));
        await After(TimeSpan.FromSeconds(2));

        var resolved = Received().Skip(2).ToList();
        var (device, n, _, urgency, topic) = Assert.Single(resolved);
        Assert.Equal(Phone, device);
        Assert.Equal("resolved", Kind(n));
        Assert.True(n.GetProperty("silent").GetBoolean());
        Assert.Equal("DEV · no longer waiting", n.GetProperty("body").GetString());
        // Replaces the question: on the device (tag) and, if still undelivered, at the push
        // service (topic).
        Assert.Equal($"{Machine}:s1", n.GetProperty("tag").GetString());
        Assert.Equal((PushNotice.Topic(Machine, "s1"), "normal"), (topic, urgency));
    }

    [Fact]
    public async Task A_session_that_ends_while_its_question_is_shown_says_so()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));

        source.Remove("s1");
        await After(TimeSpan.FromSeconds(1));

        var n = Received()[^1].Notification;
        Assert.Equal(("resolved", "DEV · session ended"), (Kind(n), n.GetProperty("body").GetString()));
        Assert.Equal(2, sender.Sent.Count);
    }

    [Fact]
    public async Task A_finished_turn_is_notified_with_the_start_of_its_response_after_it_stays_idle()
    {
        Start();
        Subscribe(Phone);
        var response = "First line.\n\n" + new string('x', 2000);
        source.Upsert(S("s1", SessionStatus.Working, turns: 0));
        source.Upsert(S("s1", SessionStatus.Idle, response: response));

        await After(TimeSpan.FromSeconds(2));
        Assert.Empty(sender.Sent);
        await After(TimeSpan.FromSeconds(1));
        var (_, n, ttl, urgency, _) = Assert.Single(Received());
        Assert.Equal("finished", Kind(n));
        var body = n.GetProperty("body").GetString()!;
        Assert.StartsWith("DEV · finished\nFirst line. xxx", body);
        Assert.EndsWith("…", body);
        Assert.Equal("DEV · finished\n".Length + PushNotice.PreviewChars + 1, body.Length);
        Assert.False(n.GetProperty("data").TryGetProperty("question", out _));
        Assert.Equal($"https://danswett.github.io/agent-bridge/app/?host={Machine}&session=s1", n.GetProperty("navigate").GetString());
        Assert.Equal((21600, "normal"), (ttl, urgency));
    }

    [Fact]
    public async Task A_turn_that_carries_on_within_its_delay_is_not_notified_until_it_really_finishes()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Working));
        source.Upsert(S("s1", SessionStatus.Idle, response: "one", turns: 1));
        await After(TimeSpan.FromSeconds(2));
        source.Upsert(S("s1", SessionStatus.Working, response: "one", turns: 1));
        await After(TimeSpan.FromSeconds(5));
        Assert.Empty(sender.Sent);

        source.Upsert(S("s1", SessionStatus.Idle, response: "two", turns: 2));
        await After(TimeSpan.FromSeconds(3));
        Assert.Equal("DEV · finished\ntwo", Assert.Single(Received()).Notification.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Only_a_turn_seen_ending_with_a_response_is_notified_and_each_once()
    {
        // Idle with a response when the host started: not news.
        Start(existing: S("old", SessionStatus.Idle, response: "earlier"));
        Subscribe(Phone);
        source.Upsert(S("old", SessionStatus.Idle, response: "earlier", title: "renamed"));
        // A turn that ends with nothing to say.
        source.Upsert(S("s1", SessionStatus.Working));
        source.Upsert(S("s1", SessionStatus.Idle, response: "  "));
        await After(TimeSpan.FromSeconds(5));
        Assert.Empty(sender.Sent);

        source.Upsert(S("s2", SessionStatus.Working));
        source.Upsert(S("s2", SessionStatus.Idle, response: "done"));
        await After(TimeSpan.FromSeconds(5));
        // The same finished turn seen ending again (a status flap) is the same notice.
        source.Upsert(S("s2", SessionStatus.Working, response: "done"));
        source.Upsert(S("s2", SessionStatus.Idle, response: "done"));
        await After(TimeSpan.FromSeconds(5));
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task A_question_already_pending_when_the_host_starts_is_notified_as_after_a_daemon_restart()
    {
        Start(existing: S("s1", SessionStatus.Waiting, question: Q("q1", "Approve?", QuestionKind.Approval)));
        Subscribe(Phone);
        await After(TimeSpan.FromSeconds(2));
        Assert.Equal("DEV · waiting for your approval\nApprove?", Assert.Single(Received()).Notification.GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_finished_turn_replaces_the_answered_question_instead_of_adding_a_notice()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        source.Upsert(S("s1", SessionStatus.Working));
        source.Upsert(S("s1", SessionStatus.Idle, response: "All done."));
        await After(TimeSpan.FromSeconds(3));

        Assert.Equal(["question", "finished"], Received().Select(r => Kind(r.Notification)));
    }

    [Fact]
    public async Task A_gone_subscription_404_or_410_is_removed_from_the_store_and_its_file()
    {
        Start();
        Subscribe(Phone);
        Subscribe(Laptop);
        sender.Respond = e => e.EndsWith(Phone) ? new PushResult(410, null) : e.EndsWith(Laptop) ? new PushResult(404, null) : new PushResult(201, null);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));

        Assert.Equal(0, store.Count);
        Assert.Equal(0, PushStore.Load(StorePath, Account).Count);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q2")));
        await After(TimeSpan.FromSeconds(2));
        Assert.Equal(2, sender.Attempts);
        Assert.Contains(logs, l => l.Contains("is gone (410); removed"));
    }

    [Fact]
    public async Task A_gone_answer_for_an_old_endpoint_keeps_the_subscription_the_device_registered_meanwhile()
    {
        Start();
        Subscribe(Phone, endpoint: "https://web.push.apple.com/old/" + Phone);
        // While the push to the old endpoint is on its way, the browser renews its subscription
        // and the app registers the new one; then the push service says the old one is gone.
        sender.Respond = e =>
        {
            if (!e.Contains("/old/")) { return new PushResult(201, null); }
            Subscribe(Phone, endpoint: "https://web.push.apple.com/new/" + Phone);
            return new PushResult(410, null);
        };
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));

        Assert.Equal("https://web.push.apple.com/new/" + Phone, Assert.Single(store.All).Endpoint);
        Assert.Equal(1, PushStore.Load(StorePath, Account).Count);
        source.Upsert(S("s2", SessionStatus.Waiting, question: Q("q2")));
        await After(TimeSpan.FromSeconds(2));
        Assert.Equal("https://web.push.apple.com/new/" + Phone, Assert.Single(sender.Sent).Endpoint);
    }

    [Fact]
    public async Task A_notice_lost_to_a_dropped_relay_connection_is_sent_once_after_reconnecting()
    {
        Start();
        Subscribe(Phone);
        sender.Throw = new IOException("relay connection ended");
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        await After(TimeSpan.FromSeconds(30));
        Assert.Equal(1, sender.Attempts);
        Assert.Equal(1, notifier.Pending);

        var next = new FakeSender(Account);
        notifier.Attach(next);
        await notifier.PumpAsync();
        await After(TimeSpan.FromSeconds(30));
        Assert.Single(next.Sent);
        Assert.Equal(0, notifier.Pending);
    }

    [Fact]
    public async Task Nothing_is_sent_while_disconnected_and_only_the_latest_state_of_a_session_goes_on_reconnecting()
    {
        Start();
        Subscribe(Phone);
        sender.Connected = false;
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        source.Upsert(S("s1", SessionStatus.Working));
        source.Upsert(S("s1", SessionStatus.Idle, response: "Done."));
        await After(TimeSpan.FromSeconds(3));
        Assert.Equal(0, sender.Attempts);

        var next = new FakeSender(Account);
        notifier.Attach(next);
        await notifier.PumpAsync();
        Assert.Equal("finished", Kind(Decrypt(Assert.Single(next.Sent))));
    }

    JsonElement Decrypt((string Endpoint, byte[] Body, int, string, string?) p)
    {
        var (key, auth) = browsers[p.Endpoint.Split('/')[^1]];
        return JsonDocument.Parse(WebPushCrypto.Decrypt(p.Body, key, auth)).RootElement.GetProperty("notification");
    }

    [Fact]
    public async Task A_push_the_relay_never_answers_is_retried_later_and_holds_up_nothing_else()
    {
        Start();
        Subscribe(Phone);
        sender.Failures.Enqueue(new TimeoutException());
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        Assert.Equal((1, 0), (sender.Attempts, sender.Sent.Count));

        source.Upsert(S("s2", SessionStatus.Waiting, question: Q("q2")));
        await After(TimeSpan.FromSeconds(2));
        await After(TimeSpan.FromSeconds(3));
        Assert.Equal(["q2", "q1"], Received().Select(r => r.Notification.GetProperty("data").GetProperty("question").GetString()));
        Assert.Equal(0, notifier.Pending);
    }

    [Fact]
    public async Task A_question_that_flickers_away_and_back_before_its_resolution_goes_is_left_on_the_phone()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));

        // Briefly not pending (an answer being typed), then pending again: still waiting on you.
        source.Upsert(S("s1", SessionStatus.Working));
        await After(TimeSpan.FromSeconds(1));
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(5));
        Assert.Equal(["question"], Received().Select(r => Kind(r.Notification)));

        // And once it really goes, it is resolved.
        source.Upsert(S("s1", SessionStatus.Working));
        await After(TimeSpan.FromSeconds(2));
        Assert.Equal(["question", "resolved"], Received().Select(r => Kind(r.Notification)));
    }

    [Fact]
    public async Task A_resolution_still_waiting_to_be_sent_is_withdrawn_when_its_question_comes_back()
    {
        Start(new PushOptions { MaxPerMinute = 1 });
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        // Gone long enough to be resolved, but the rate limit holds the resolution back...
        source.Upsert(S("s1", SessionStatus.Working));
        await After(TimeSpan.FromSeconds(2));
        Assert.Equal(1, notifier.Pending);
        // ...and before it can go, the question is pending again.
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(60));
        Assert.Equal(["question"], Received().Select(r => Kind(r.Notification)));
        Assert.Equal(0, notifier.Pending);

        source.Upsert(S("s1", SessionStatus.Working));
        await After(TimeSpan.FromSeconds(2));
        Assert.Equal(["question", "resolved"], Received().Select(r => Kind(r.Notification)));
    }

    [Fact]
    public async Task A_host_at_the_rate_limit_sleeps_until_the_window_has_room_instead_of_spinning()
    {
        Start(new PushOptions { MaxPerMinute = 1 });
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        source.Upsert(S("s2", SessionStatus.Waiting, question: Q("q2")));
        time.Advance(TimeSpan.FromSeconds(2));

        var wait = await notifier.PumpAsync();

        Assert.Single(sender.Sent);
        Assert.Equal(TimeSpan.FromMinutes(1), wait);
    }

    [Fact]
    public async Task Sends_past_the_rate_limit_wait_for_the_next_minute_rather_than_being_refused()
    {
        Start(new PushOptions { MaxPerMinute = 3 });
        Subscribe(Phone);
        for (var i = 0; i < 5; i++) { source.Upsert(S($"s{i}", SessionStatus.Waiting, question: Q($"q{i}"))); }
        await After(TimeSpan.FromSeconds(2));
        Assert.Equal(3, sender.Sent.Count);

        await After(TimeSpan.FromSeconds(30));
        Assert.Equal(3, sender.Sent.Count);
        await After(TimeSpan.FromSeconds(31));
        Assert.Equal(5, sender.Sent.Count);
        Assert.Equal(5, Received().Select(r => r.Topic).Distinct().Count());
    }

    [Fact]
    public async Task A_push_service_that_is_briefly_unavailable_is_retried_with_backoff()
    {
        Start();
        Subscribe(Phone);
        var statuses = new Queue<PushResult>([new(503, null), new(0, "rate-limited"), new(201, null)]);
        sender.Respond = _ => statuses.Dequeue();
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        Assert.Equal(1, sender.Attempts);
        await After(TimeSpan.FromSeconds(5));
        Assert.Equal(2, sender.Attempts);
        await After(TimeSpan.FromSeconds(5));
        Assert.Equal(2, sender.Attempts);
        await After(TimeSpan.FromSeconds(5));
        Assert.Equal(3, sender.Attempts);
        Assert.Equal(0, notifier.Pending);
    }

    [Fact]
    public void Only_a_device_of_the_hosts_own_account_may_register_list_or_remove()
    {
        Start();
        var otherAccount = Assert.Throws<InvalidOperationException>(() => Subscribe(Phone, peer: Peer(Phone, account: "2")));
        Assert.Contains("own account", otherAccount.Message);
        Assert.Throws<InvalidOperationException>(() => Subscribe(Phone, peer: Peer(Phone, inAccount: false)));
        Assert.Throws<InvalidOperationException>(() => notifier.Handle(Peer(Laptop, account: "2"), Ops.PushList, default));
        Assert.Equal(0, store.Count);

        Assert.Equal(new PushSubscribeResult(true, 1), Subscribe(Phone));
    }

    [Fact]
    public async Task Subscriptions_saved_for_another_account_are_neither_loaded_nor_kept()
    {
        Start();
        Subscribe(Phone);
        Assert.Equal(1, PushStore.Load(StorePath, Account).Count);
        Assert.Equal(0, PushStore.Load(StorePath, "2").Count);

        // The profile now signs in as another account: nothing of the first one's survives.
        notifier.Attach(new FakeSender("2"));
        Assert.Equal(0, store.Count);
        Assert.Equal(0, PushStore.Load(StorePath, Account).Count);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public void A_subscription_is_checked_when_it_arrives()
    {
        Start();
        using var key = P256KeyPair.Generate();
        object? Try(string endpoint, string p256dh, string auth, string app) => notifier.Handle(Peer(Phone), Ops.PushSubscribe,
            JsonSerializer.SerializeToElement(new { endpoint, keys = new { p256dh, auth }, app }));
        var good = (E: "https://web.push.apple.com/x", K: B64Url.Encode(key.Public), A: B64Url.Encode(new byte[16]), App: "https://danswett.github.io/agent-bridge/app/");

        Assert.Contains("endpoint", Assert.Throws<ArgumentException>(() => Try("http://web.push.apple.com/x", good.K, good.A, good.App)).Message);
        Assert.Contains("endpoint", Assert.Throws<ArgumentException>(() => Try("https://user:pw@web.push.apple.com/x", good.K, good.A, good.App)).Message);
        Assert.Contains("p256dh", Assert.Throws<ArgumentException>(() => Try(good.E, B64Url.Encode(new byte[65]), good.A, good.App)).Message);
        Assert.Contains("auth", Assert.Throws<ArgumentException>(() => Try(good.E, good.K, B64Url.Encode(new byte[8]), good.App)).Message);
        Assert.Contains("keys", Assert.Throws<ArgumentException>(() => Try(good.E, "not base64!", good.A, good.App)).Message);
        Assert.Contains("app", Assert.Throws<ArgumentException>(() => Try(good.E, good.K, good.A, "javascript:alert(1)")).Message);
        Assert.Throws<ArgumentException>(() => notifier.Handle(Peer(Phone), Ops.PushSubscribe, JsonSerializer.SerializeToElement(new { endpoint = good.E })));
        Assert.Equal(0, store.Count);

        Assert.NotNull(Try(good.E, good.K, good.A, good.App));
        // A test stack's loopback push service is http.
        Assert.NotNull(Try("http://127.0.0.1:5000/push/x", good.K, good.A, "http://127.0.0.1:8080/app/"));
    }

    [Fact]
    public void A_device_lists_the_devices_notified_without_their_endpoints_and_removes_its_own()
    {
        Start();
        Subscribe(Phone);
        Subscribe(Laptop, endpoint: "https://fcm.googleapis.com/fcm/send/" + Laptop);

        var list = Assert.IsType<PushListResult>(notifier.Handle(Peer(Phone), Ops.PushList, default));
        Assert.Equal([(Phone, "iPhone", "web.push.apple.com", true), (Laptop, "Laptop", "fcm.googleapis.com", false)],
            list.Devices.Select(d => (d.Device, d.Name, d.Service, d.Current)));
        Assert.DoesNotContain(Phone, JsonSerializer.Serialize(list.Devices.Select(d => d.Service)));

        Assert.Equal(new PushUnsubscribeResult(true), notifier.Handle(Peer(Phone), Ops.PushUnsubscribe, default));
        Assert.Equal(new PushUnsubscribeResult(false), notifier.Handle(Peer(Phone), Ops.PushUnsubscribe, default));
        Assert.Equal(Laptop, Assert.Single(store.All).Device);
        Assert.Null(notifier.Handle(Peer(Phone), "push.other", default));
    }

    [Fact]
    public void Registering_again_replaces_a_devices_subscription_and_a_reinstalled_apps_old_one()
    {
        Start();
        Subscribe(Phone);
        Subscribe(Phone, endpoint: "https://web.push.apple.com/renewed");
        Assert.Equal("https://web.push.apple.com/renewed", Assert.Single(store.All).Endpoint);

        // The same browser endpoint registered by a new device key (the app was reinstalled).
        Subscribe(Laptop, endpoint: "https://web.push.apple.com/renewed");
        Assert.Equal(Laptop, Assert.Single(store.All).Device);
    }

    sealed class FakeSource : ISessionSource
    {
        public List<Session> Sessions { get; } = [];
        public long Revision => 0;
        public event EventHandler<SessionChange>? Changed;
        public IReadOnlyList<Session> Snapshot() => Sessions;
        public SessionList List(string? cursor = null) => new(Sessions, 0);
        public Session? Get(string id) => Sessions.FirstOrDefault(s => s.Id == id);
        public SessionSubscription Subscribe() => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Upsert(Session s) => Changed?.Invoke(this, new SessionChange(SessionChangeKind.Upserted, s.Id, s, 0));
        public void Remove(string id) => Changed?.Invoke(this, new SessionChange(SessionChangeKind.Removed, id, null, 0));
    }

    sealed class FakeSender(string account) : IPushSender
    {
        public string AccountId => account;
        public bool Connected { get; set; } = true;
        public Func<string, PushResult>? Respond { get; set; }
        public Exception? Throw { get; set; }
        public Queue<Exception> Failures { get; } = new();
        public int Attempts { get; private set; }
        public List<(string Endpoint, byte[] Body, int Ttl, string Urgency, string? Topic)> Sent { get; } = [];

        public Task<PushResult> PushAsync(string endpoint, byte[] body, int ttl, string urgency, string? topic)
        {
            Attempts++;
            if (Throw is { } e) { throw e; }
            if (Failures.TryDequeue(out var once)) { throw once; }
            var r = Respond?.Invoke(endpoint) ?? new PushResult(201, null);
            if (r.Status is >= 200 and < 300) { Sent.Add((endpoint, body, ttl, urgency, topic)); }
            return Task.FromResult(r);
        }
    }
}
