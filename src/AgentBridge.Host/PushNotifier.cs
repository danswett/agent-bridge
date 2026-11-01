using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// Where pushes go out: the host's relay connection now. Replaced on every reconnect.
public interface IPushSender
{
    string AccountId { get; }
    bool Connected { get; }
    Task<PushResult> PushAsync(string endpoint, byte[] body, int ttl, string urgency, string? topic);
}

public sealed class RelayPushSender(RelayClient relay) : IPushSender
{
    public string AccountId => relay.AccountId;
    public bool Connected => !relay.Completion.IsCompleted;
    public Task<PushResult> PushAsync(string endpoint, byte[] body, int ttl, string urgency, string? topic) =>
        relay.PushAsync(endpoint, body, ttl, urgency, topic);
}

// Who asked to be notified: the channel's peer, the account its relay connection is signed
// in to, and whether that relay lists the peer among the account's devices.
public sealed record PushPeer(string Device, string Name, string AccountId, bool InAccount)
{
    public static PushPeer Of(SecureChannel ch) =>
        new(ch.PeerId, ch.PeerName, ch.Relay.AccountId, ch.Relay.Presence.Any(p => p.Id == ch.PeerId));
}

public sealed record PushOptions
{
    // How long a question must stay pending before it is notified, and how long a session
    // must stay idle after a turn: one answered at the machine, or a turn that carries on
    // at once (the person is there), is not worth a phone buzzing.
    public TimeSpan QuestionDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan FinishedDelay { get; init; } = TimeSpan.FromSeconds(3);
    // Under the relay's 30 a minute per account, which every host of the account shares.
    // Past it, notices wait (only the latest per session and device is kept) rather than
    // being refused by the relay.
    public int MaxPerMinute { get; init; } = 20;
    // A push service that is briefly unavailable (429, 5xx) or a relay that is rate-limiting
    // is retried with backoff from RetryDelay, this many times in all.
    public int MaxAttempts { get; init; } = 5;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan QuestionTtl { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan FinishedTtl { get; init; } = TimeSpan.FromHours(6);
}

// Notifies the account's phones (Web Push, through the relay) when a session asks a
// question and when it finishes a turn, as agent-ha-bridge does through Home Assistant:
//
//   question  a session has a pending question it has not been notified for, still pending
//             after QuestionDelay. Once per question id, however often the session changes
//             (agent-ha-bridge's DaemonAnswerButtonSent): an answer typed in that briefly
//             still shows the question pending does not notify it again.
//   finished  a session went from working (or waiting) to idle with a response, and is still
//             idle after FinishedDelay. Nothing for an empty response, and once per finished
//             turn (agent-ha-bridge's agentStop / Stop hooks).
//   resolved  a question that was notified is no longer pending (answered anywhere, or the
//             session ended): its notification is replaced, as agent-ha-bridge clears it
//             (Clear-DaemonAnswerButtons), except on the device that answered it.
//
// Every notice about a session shares one tag and topic, so a device shows only the
// latest one per session. Notices are kept per device and session until sent: a newer
// one replaces an older one still waiting, and one that failed because the relay
// connection dropped is sent on the next (spike S5 finding 5).
public sealed class PushNotifier : IAsyncDisposable
{
    readonly ISessionSource source;
    readonly MachineInfo machine;
    readonly PushStore store;
    readonly PushOptions options;
    readonly Action<string> log;
    readonly TimeProvider time;
    readonly object gate = new();
    readonly Dictionary<string, Tracked> sessions = new(StringComparer.Ordinal);
    readonly List<Outgoing> outbox = [];
    // The question each device was last notified of per session, while it is still on that
    // device: a resolved notice goes only where there is something to replace.
    readonly Dictionary<(string Device, string Session), string> shownOn = [];
    // Who answered a question from a client: that device needs no notice that it was answered.
    readonly Dictionary<(string Session, string Question), string> answeredBy = [];
    readonly Queue<DateTimeOffset> recent = new();
    readonly SemaphoreSlim wake = new(0);
    IPushSender? sender;
    // A sender whose connection failed a push: nothing more is tried on it, and the next
    // Attach (a new connection) sends what it could not.
    IPushSender? broken;
    CancellationTokenSource? stop;
    Task loop = Task.CompletedTask;

    sealed class Tracked
    {
        public Session? Last;
        public bool Removed;
        public Due? Due;
        // The question on devices now (notified, not yet resolved).
        public string? Shown;
        public string? FinishedKey;
        // The main agent's answer as last seen outside a wait on a delegate: answering a
        // delegate's question while the main agent stayed idle ends no turn of its.
        public string? MainKey;
        public bool MainRunning;
        public readonly HashSet<string> Notified = new(StringComparer.Ordinal);
        public readonly Queue<string> NotifiedOrder = new();
    }

    sealed record Due(NoticeKind Kind, DateTimeOffset At, string? Question);

    sealed class Outgoing
    {
        public required string Device;
        public required string Session;
        public required NoticeKind Kind;
        public string? Question;
        public required byte[] Plain;
        public required int Ttl;
        public required string Urgency;
        public required string Topic;
        public required DateTimeOffset Created;
        public DateTimeOffset NotBefore;
        public int Attempts;
    }

    public PushNotifier(ISessionSource source, MachineInfo machine, PushStore store, PushOptions? options = null, Action<string>? log = null, TimeProvider? time = null)
    {
        this.source = source;
        this.machine = machine;
        this.store = store;
        this.options = options ?? new PushOptions();
        this.log = log ?? (_ => { });
        this.time = time ?? TimeProvider.System;
        source.Changed += OnChanged;
        var now = this.time.GetUtcNow();
        lock (gate)
        {
            // What is already true when the host starts is the baseline: a turn that finished
            // before is not news. A question already pending is notified, as agent-ha-bridge's
            // daemon does after a restart, since nothing else would ever tell the phone.
            foreach (var s in source.Snapshot())
            {
                if (sessions.ContainsKey(s.Id)) { continue; }
                var t = sessions[s.Id] = new Tracked { Last = s, MainKey = MainKey(s), MainRunning = s.WaitingOn is null && s.Status is SessionStatus.Working or SessionStatus.Waiting };
                if (s.Question is { } q) { t.Due = new Due(NoticeKind.Question, now + this.options.QuestionDelay, q.Id); }
            }
        }
    }

    public int Subscribers => store.Count;

    // Outgoing notices not yet sent; tests read it.
    internal int Pending { get { lock (gate) { return outbox.Count; } } }

    public void Start()
    {
        if (stop is not null) { return; }
        stop = new CancellationTokenSource();
        loop = Task.Run(() => RunAsync(stop.Token));
    }

    // A new relay connection. Subscriptions saved for another account are dropped, and
    // anything the previous connection could not send goes now.
    public void Attach(IPushSender next)
    {
        lock (gate)
        {
            if (store.Account != next.AccountId)
            {
                var dropped = store.Bind(next.AccountId);
                if (dropped > 0) { log($"signed in to another account: dropped {dropped} push subscription(s)"); }
                outbox.Clear();
                shownOn.Clear();
            }
            sender = next;
            broken = null;
        }
        Signal();
    }

    // A client answered a question (session.answer delivered or typed): its device is where
    // the answer was given, so it gets no notice that the question was answered.
    public void Answered(string device, string session, string question)
    {
        lock (gate)
        {
            // Only a question that was notified gets a resolved notice to skip.
            if (sessions.GetValueOrDefault(session)?.Shown == question) { answeredBy[(session, question)] = device; }
            if (shownOn.GetValueOrDefault((device, session)) == question) { shownOn.Remove((device, session)); }
            // A question notice still waiting for this device would only announce what it did.
            outbox.RemoveAll(o => o.Device == device && o.Session == session && o.Kind == NoticeKind.Question && o.Question == question);
        }
    }

    // push.subscribe, push.unsubscribe and push.list; null for any other op. Only a channel
    // through the relay can carry them: the account boundary they rely on is the relay's, and
    // an app on this machine needs no Web Push to hear from its own host.
    public Task<object?> HandleAsync(IProtocolChannel channel, string op, JsonElement data) =>
        Task.FromResult(!op.StartsWith("push.", StringComparison.Ordinal) ? null
            : channel is SecureChannel secure ? Handle(PushPeer.Of(secure), op, data)
            : throw new InvalidOperationException("notifications are for devices that reach this host through the relay"));

    internal object? Handle(PushPeer peer, string op, JsonElement data)
    {
        if (op is not (Ops.PushSubscribe or Ops.PushUnsubscribe or Ops.PushList)) { return null; }
        // Push goes straight to a device, around the relay's account boundary, so the host
        // keeps it itself: only a device of the account this host is signed in to, which that
        // account's relay connection lists, may register, list or remove.
        var account = store.Account;
        if (account is null || peer.AccountId != account || !peer.InAccount)
        {
            throw new InvalidOperationException("notifications are only for devices of this host's own account");
        }
        switch (op)
        {
            case Ops.PushSubscribe:
                {
                    var request = Read<PushSubscribeRequest>(data, "push.subscribe needs an endpoint, keys and an app URL");
                    if (Problem(request) is { } problem) { throw new ArgumentException(problem); }
                    var name = Clean(request.Device) ?? Clean(peer.Name) ?? "device";
                    store.Put(new PushSubscriber(peer.Device, name, request.Endpoint, request.Keys.P256dh, request.Keys.Auth, request.App, time.GetUtcNow()));
                    log($"push subscription from {name} ({peer.Device[..Math.Min(8, peer.Device.Length)]}, {new Uri(request.Endpoint).Host}); {store.Count} in all");
                    return new PushSubscribeResult(true, store.Count);
                }
            case Ops.PushUnsubscribe:
                {
                    var request = data.ValueKind == JsonValueKind.Object ? Read<PushUnsubscribeRequest>(data, "push.unsubscribe takes an optional endpoint") : new PushUnsubscribeRequest();
                    var removed = store.Remove(peer.Device, request.Endpoint);
                    lock (gate) { Forget(peer.Device); }
                    if (removed) { log($"push subscription removed by {Clean(peer.Name) ?? peer.Device[..Math.Min(8, peer.Device.Length)]}; {store.Count} left"); }
                    return new PushUnsubscribeResult(removed);
                }
            default:
                return new PushListResult([.. store.All.Select(s => new PushDevice(s.Device, s.Name, ServiceOf(s.Endpoint), s.Since, s.Device == peer.Device))]);
        }
    }

    static T Read<T>(JsonElement data, string what)
    {
        try { return data.Deserialize<T>(ProtocolJson.Options) ?? throw new ArgumentException(what); }
        catch (JsonException) { throw new ArgumentException(what); }
    }

    // Checked when it arrives, where it is cheap: a bad key would only fail at the next push.
    internal static string? Problem(PushSubscribeRequest r)
    {
        if (!WebUrl(r.Endpoint, 2048)) { return "endpoint must be an https URL"; }
        if (r.Keys is not { P256dh.Length: > 0, Auth.Length: > 0 }) { return "keys must have p256dh and auth"; }
        try
        {
            var p256dh = B64Url.Decode(r.Keys.P256dh);
            if (p256dh.Length != 65) { return "keys.p256dh must be an uncompressed P-256 point"; }
            P256KeyPair.AssertOnCurve(p256dh);
            if (B64Url.Decode(r.Keys.Auth).Length != 16) { return "keys.auth must be 16 bytes"; }
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException)
        {
            return "keys must be base64url: p256dh a P-256 point, auth 16 bytes";
        }
        if (!WebUrl(r.App, 1024)) { return "app must be an https URL"; }
        return null;
    }

    // https, or http to this machine for a test stack; never credentials in the URL.
    static bool WebUrl(string? s, int max) =>
        s is { Length: > 0 } && s.Length <= max && Uri.TryCreate(s, UriKind.Absolute, out var u) && u.UserInfo.Length == 0
        && (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback));

    static string ServiceOf(string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var u) ? u.Host : "";

    static string? Clean(string? name)
    {
        var s = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length == 0 ? null : s.Length > 64 ? s[..64] : s;
    }

    void Forget(string device)
    {
        outbox.RemoveAll(o => o.Device == device);
        foreach (var k in shownOn.Keys.Where(k => k.Device == device).ToList()) { shownOn.Remove(k); }
    }

    void OnChanged(object? _, SessionChange change)
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            if (change.Kind == SessionChangeKind.Removed)
            {
                if (!sessions.TryGetValue(change.Id, out var t)) { return; }
                t.Removed = true;
                t.Due = t.Shown is { } shown ? new Due(NoticeKind.Resolved, now, shown) : null;
                if (t.Due is null) { sessions.Remove(change.Id); }
            }
            else if (change.Session is { } s) { Observe(s, now); }
        }
        Signal();
    }

    void Observe(Session s, DateTimeOffset now)
    {
        if (!sessions.TryGetValue(s.Id, out var t)) { sessions[s.Id] = t = new Tracked(); }
        var prev = t.Last;
        t.Last = s;
        t.Removed = false;
        // The main agent's answer as it stood before any wait on a delegate began, and whether
        // its turn was running then.
        var baseline = t.MainKey;
        var wasRunning = t.MainRunning;
        if (s.WaitingOn is null)
        {
            t.MainKey = MainKey(s);
            t.MainRunning = s.Status is SessionStatus.Working or SessionStatus.Waiting;
        }
        if (s.Question is { } q)
        {
            // Back before its resolution went out (it flickered while an answer was typed):
            // the question is still on devices, and still asked.
            if (t.Due is { Kind: NoticeKind.Resolved } r && r.Question == q.Id) { t.Due = null; }
            // Likewise once the resolution was made but is still waiting to be sent (the rate
            // limit, or the relay reconnecting): withdrawn, so the question stays on devices.
            if (t.Shown != q.Id && t.Notified.Contains(q.Id)
                && outbox.RemoveAll(o => o.Session == s.Id && o.Kind == NoticeKind.Resolved && o.Question == q.Id) > 0)
            {
                t.Shown = q.Id;
            }
            if (!t.Notified.Contains(q.Id) && !(t.Due is { Kind: NoticeKind.Question } d && d.Question == q.Id))
            {
                t.Due = new Due(NoticeKind.Question, now + options.QuestionDelay, q.Id);
            }
            return;
        }
        // Answered (or gone) before it was notified: nothing to say.
        if (t.Due is { Kind: NoticeKind.Question }) { t.Due = null; }
        // Waiting on a delegate (status rule 2) hides the main agent's turn: a turn finished
        // then if it was running when the wait began (it may have ended, its answer already
        // given, while the delegate asked), or if its answer is seen to have changed since.
        // With neither (a session first seen already waiting on one), nothing says a turn
        // ran. Sessions present at startup are seeded with theirs.
        var turnRan = prev is { Status: SessionStatus.Waiting, WaitingOn: not null }
            ? wasRunning || (baseline is { } before && MainKey(s) != before)
            : prev is { Status: SessionStatus.Working or SessionStatus.Waiting };
        if (s.Status == SessionStatus.Idle && turnRan
            && !string.IsNullOrWhiteSpace(OwnResponse(s)) && FinishedKey(s) != t.FinishedKey)
        {
            t.Due = new Due(NoticeKind.Finished, now + options.FinishedDelay, null);
        }
        // The turn carried on before the notice went: the person is at the machine.
        else if (s.Status != SessionStatus.Idle && t.Due is { Kind: NoticeKind.Finished }) { t.Due = null; }
        // A question on devices is no longer asked. Not at once: a turn that finishes soon
        // after replaces it with its own notice.
        if (t.Shown is { } shown && t.Due is null) { t.Due = new Due(NoticeKind.Resolved, now + options.QuestionDelay, shown); }
    }

    static string FinishedKey(Session s) =>
        $"{s.Activity.TurnCount}:{MainKey(s)}";

    // The main agent's own answer, whatever the status: unlike turnCount, it reads the same
    // waiting and idle.
    static string MainKey(Session s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(OwnResponse(s) ?? "")))[..16];

    // "Finished" is the main agent's turn only (subagents.md §7): its own response, never the
    // delegate's that may be the latest text.
    static string? OwnResponse(Session s) => s.Activity.Source?.Response is null ? s.Activity.Response ?? s.Activity.Own?.Response : s.Activity.Own?.Response;

    void Signal()
    {
        if (wake.CurrentCount == 0) { wake.Release(); }
    }

    async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            try { wait = await PumpAsync(); }
            catch (Exception e)
            {
                log($"notifications: {e.GetType().Name}: {e.Message}");
                wait = TimeSpan.FromSeconds(1);
            }
            try { await wake.WaitAsync(wait, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Turns notices that are due into pushes, and sends what can be sent now. Returns how
    // long until there may be more to do.
    internal async Task<TimeSpan> PumpAsync()
    {
        lock (gate) { MakeDue(time.GetUtcNow()); }
        while (true)
        {
            Outgoing next;
            PushSubscriber sub;
            IPushSender via;
            lock (gate)
            {
                var now = time.GetUtcNow();
                outbox.RemoveAll(o => o.Created + TimeSpan.FromSeconds(o.Ttl) < now);
                var ready = outbox.FirstOrDefault(o => o.NotBefore <= now);
                if (ready is null || sender is not { Connected: true } s || s == broken) { return NextWake(now); }
                while (recent.Count > 0 && now - recent.Peek() >= TimeSpan.FromMinutes(1)) { recent.Dequeue(); }
                if (recent.Count >= options.MaxPerMinute) { return NextWake(now); }
                outbox.Remove(ready);
                if (store.Get(ready.Device) is not { } found) { continue; }
                recent.Enqueue(now);
                (next, sub, via) = (ready, found, s);
            }
            PushResult result;
            try
            {
                result = await via.PushAsync(sub.Endpoint, WebPushCrypto.Encrypt(next.Plain, sub.Subscription), next.Ttl, next.Urgency, next.Topic);
            }
            catch (TimeoutException)
            {
                // The relay never said how the push went, but the connection is still up: it may
                // have gone, so it is tried again later rather than at once.
                lock (gate) { Sent(next, sub, new PushResult(0, "unreachable")); }
                continue;
            }
            catch (Exception e) when (e is IOException or WebSocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
            {
                // The relay connection is going or gone. Kept for the next one; a resend that
                // duplicates a push that did get out only replaces it (same tag and topic).
                lock (gate)
                {
                    if (sender == via) { broken = via; }
                    if (!outbox.Any(o => o.Device == next.Device && o.Session == next.Session)) { outbox.Insert(0, next); }
                    log($"push to {sub.Name} not sent ({e.GetType().Name}); will send after reconnecting");
                    // Still wakes for notices coming due; sending waits for Attach.
                    return NextWake(time.GetUtcNow());
                }
            }
            catch (Exception e) when (e is CryptographicException or ArgumentException)
            {
                log($"push to {sub.Name} could not be encrypted ({e.Message}); dropped");
                continue;
            }
            lock (gate) { Sent(next, sub, result); }
        }
    }

    void Sent(Outgoing o, PushSubscriber sub, PushResult r)
    {
        var key = (o.Device, o.Session);
        if (r.Status is >= 200 and < 300)
        {
            if (o.Kind == NoticeKind.Question && o.Question is { } q) { shownOn[key] = q; }
            else { shownOn.Remove(key); }
            log($"notified {sub.Name}: {o.Kind.ToString().ToLowerInvariant()} for {o.Session[..Math.Min(8, o.Session.Length)]}");
            return;
        }
        if (r.Status is 404 or 410)
        {
            // The browser unsubscribed, or the subscription expired: it can never be used again.
            // Only that subscription: the device may have registered a new one while this push
            // was on its way, and that one, with what is queued for it, stays.
            if (store.RemoveIfEndpoint(o.Device, sub.Endpoint))
            {
                Forget(o.Device);
                log($"push subscription of {sub.Name} is gone ({r.Status}); removed");
            }
            return;
        }
        var transient = r.Status is 429 or >= 500 || (r.Status == 0 && r.Error is "rate-limited" or "unreachable");
        if (transient && ++o.Attempts < options.MaxAttempts)
        {
            o.NotBefore = time.GetUtcNow() + options.RetryDelay * Math.Pow(2, o.Attempts - 1);
            if (!outbox.Any(x => x.Device == o.Device && x.Session == o.Session)) { outbox.Add(o); }
            log($"push to {sub.Name}: {r.Status} {r.Error}; retrying");
            return;
        }
        log($"push to {sub.Name} refused: {r.Status} {r.Error}; dropped");
    }

    TimeSpan NextWake(DateTimeOffset now)
    {
        var times = new List<DateTimeOffset>();
        times.AddRange(sessions.Values.Where(t => t.Due is not null).Select(t => t.Due!.At));
        if (sender is { Connected: true } s && s != broken)
        {
            // A notice held back by the rate limit is ready only once the window has room:
            // its own time is usually already past, and waking for it would spin.
            DateTimeOffset? room = recent.Count >= options.MaxPerMinute ? recent.Peek() + TimeSpan.FromMinutes(1) : null;
            times.AddRange(outbox.Select(o => room is { } r && r > o.NotBefore ? r : o.NotBefore));
        }
        if (times.Count == 0) { return Timeout.InfiniteTimeSpan; }
        var wait = times.Min() - now;
        return wait < TimeSpan.FromMilliseconds(10) ? TimeSpan.FromMilliseconds(10) : wait > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait;
    }

    void MakeDue(DateTimeOffset now)
    {
        foreach (var (id, t) in sessions.ToList())
        {
            if (t.Due is not { } due || due.At > now) { continue; }
            t.Due = null;
            var s = t.Last;
            switch (due.Kind)
            {
                case NoticeKind.Question when !t.Removed && s?.Question?.Id == due.Question:
                    Remember(t, due.Question!);
                    t.Shown = due.Question;
                    Enqueue(s!, NoticeKind.Question, due.Question, store.All);
                    break;
                case NoticeKind.Finished when !t.Removed && s is { Status: SessionStatus.Idle, Question: null }:
                    t.FinishedKey = FinishedKey(s);
                    t.Shown = null;
                    Enqueue(s, NoticeKind.Finished, null, store.All);
                    break;
                // Checked again as it goes: a question asked once more is not resolved.
                case NoticeKind.Resolved when s is not null && (t.Removed || s.Question?.Id != due.Question):
                    {
                        var q = due.Question!;
                        t.Shown = null;
                        var answerer = answeredBy.GetValueOrDefault((id, q));
                        answeredBy.Remove((id, q));
                        // A question notice that never left needs no follow-up: it just goes.
                        outbox.RemoveAll(o => o.Session == id && o.Kind == NoticeKind.Question && o.Question == q);
                        var to = store.All.Where(d => d.Device != answerer && shownOn.GetValueOrDefault((d.Device, id)) == q).ToList();
                        Enqueue(s, NoticeKind.Resolved, q, to, ended: t.Removed);
                        break;
                    }
            }
            if (t.Removed && t.Due is null) { sessions.Remove(id); }
        }
    }

    // Dedupe, as agent-ha-bridge's: a question id is notified once, for as long as the
    // session lives. Bounded, so a session that asks all day does not grow the host.
    static void Remember(Tracked t, string question)
    {
        if (!t.Notified.Add(question)) { return; }
        t.NotifiedOrder.Enqueue(question);
        while (t.NotifiedOrder.Count > 64) { t.Notified.Remove(t.NotifiedOrder.Dequeue()); }
    }

    void Enqueue(Session s, NoticeKind kind, string? question, IReadOnlyList<PushSubscriber> to, bool ended = false)
    {
        var now = time.GetUtcNow();
        foreach (var sub in to)
        {
            var plain = PushNotice.Serialize(Notice(s, kind, question, sub.App, now, ended));
            var ttl = kind == NoticeKind.Finished ? options.FinishedTtl : options.QuestionTtl;
            // The latest state of a session wins: a newer notice replaces one still waiting.
            outbox.RemoveAll(o => o.Device == sub.Device && o.Session == s.Id);
            outbox.Add(new Outgoing
            {
                Device = sub.Device,
                Session = s.Id,
                Kind = kind,
                Question = question,
                Plain = plain,
                Ttl = (int)ttl.TotalSeconds,
                Urgency = kind == NoticeKind.Question ? "high" : "normal",
                Topic = PushNotice.Topic(machine.Id, s.Id),
                Created = now,
                NotBefore = now,
            });
        }
    }

    internal PushMessage Notice(Session s, NoticeKind kind, string? question, string app, DateTimeOffset now, bool ended = false)
    {
        var title = PushNotice.Flatten(s.Title);
        if (title.Length == 0) { title = $"{AgentName(s.Agent)} session"; }
        // A delegate's question says who asks; it keeps the session's tag, so the newer of the
        // two questions replaces the older on the device.
        if (kind == NoticeKind.Question && s.Question?.Agent is { } asker)
        {
            var name = PushNotice.Flatten(s.Subagents?.FirstOrDefault(a => a.Id == asker)?.Name);
            title = $"{(name.Length > 0 ? name : "A delegate")} asks · {title}";
        }
        var body = kind switch
        {
            NoticeKind.Question => $"{machine.Name} · {Waiting(s.Question)}\n" +
                (PushNotice.Flatten(s.Question?.Message) is { Length: > 0 } m ? m : "This session is waiting for an answer."),
            NoticeKind.Finished => $"{machine.Name} · finished\n{Preview(OwnResponse(s))}",
            _ => $"{machine.Name} · {(ended ? "session ended" : "no longer waiting")}",
        };
        return PushNotice.Fit(new PushMessage
        {
            Notification = new PushNotification
            {
                Title = title,
                Body = body,
                Navigate = PushNotice.Navigate(app, machine.Id, s.Id, kind == NoticeKind.Finished ? null : question),
                Tag = PushNotice.Tag(machine.Id, s.Id),
                Silent = kind == NoticeKind.Resolved ? true : null,
                Data = new PushNoticeData { Kind = kind, Machine = machine.Id, Session = s.Id, Question = kind == NoticeKind.Finished ? null : question, At = now },
            },
        });
    }

    // The first part of the response, as agent-ha-bridge's response notification previews
    // it. The full text is in the app.
    static string Preview(string? response)
    {
        var flat = PushNotice.Flatten(response);
        if (flat.Length <= PushNotice.PreviewChars) { return flat; }
        var end = PushNotice.PreviewChars;
        if (char.IsHighSurrogate(flat[end - 1])) { end--; }
        return flat[..end].TrimEnd() + "…";
    }

    static string AgentName(AgentKind a) => a switch
    {
        AgentKind.Copilot => "Copilot",
        AgentKind.Claude => "Claude",
        AgentKind.Codex => "Codex",
        _ => "Agent",
    };

    // A prompt only the machine can answer says so on the lock screen, so the person goes to
    // the machine rather than opening the app to find nothing to press.
    static string Waiting(Question? q) => q switch
    {
        { AnswerAtMachine: true } => "waiting at the machine",
        { Kind: QuestionKind.Approval } => "waiting for your approval",
        _ => "waiting on you",
    };

    public async ValueTask DisposeAsync()
    {
        source.Changed -= OnChanged;
        if (stop is not null)
        {
            await stop.CancelAsync();
            try { await loop; } catch (OperationCanceledException) { }
            stop.Dispose();
        }
    }
}
