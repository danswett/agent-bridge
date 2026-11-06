using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// Protocol v1's sessions ops, served on every channel a client opens to this host. Reads,
// and the three ops that reach the machine, each only through its own service:
// session.answer (the answerer types an answer to a pending question), session.reply (the
// replier types a prompt into a session's input, with any files sent first by attachment.*,
// which go to the AttachmentStore) and session.stop (the stopper ends a session). The
// push.* ops, which register a device for notifications, go to the
// PushNotifier, usage.* to the UsageService, and launch.options and session.launch to the
// launcher (docs/protocol/v1-launch.md).
public sealed class SessionService
{
    readonly ISessionSource source;
    readonly MachineInfo machine;
    readonly Action<string> log;
    readonly ISessionAnswerer? answers;
    readonly ISessionReplier? replies;
    readonly PushNotifier? push;
    readonly AttachmentStore? attachments;
    readonly ISessionStopper? stops;
    readonly UsageService? usage;
    readonly ISessionLauncher? launcher;
    readonly SessionTransfers? transfers;
    readonly ConcurrentDictionary<IProtocolChannel, Subscriber> subscribers = new();

    // What a device asking this machine for notifications hears when it sends none.
    public const string NoNotifications = "this machine doesn't send notifications (push.enabled is off in its config)";

    // Why this machine is read-only for other devices now (agent-ha-bridge running for the user,
    // design G5), or null: said in hello, beside the capabilities it takes away.
    public Func<string?>? ReadOnly { get; init; }

    // The host's shared G5 check, held for each hello so its readOnly and every capability G5 takes
    // away come from one look (SharedAgentHaBridgeProbe.Hold); null where nothing shares it.
    public SharedAgentHaBridgeProbe? G5 { get; init; }

    // Who drives each session (Session.Driver). Every device that reaches this service is a
    // person's (agents on the pipe are refused before it, design §5.5), so an answer or reply that
    // may have arrived takes the session back from an agent at once, as one typed at the machine.
    public SessionDrivers? Drivers { get; init; }
    public TimeProvider Time { get; init; } = TimeProvider.System;

    // hello's machine.restore (#780): what went with the machine or the host when it last stopped, or
    // null. Set, it offers sessions.restore.
    public Func<RestoreNotice?>? Restore { get; init; }
    // What each answer or reply request may take back, fixed when the request is first seen: the
    // session it named and the driver's generation then. Keyed by the request as the deliveries
    // remember it (its UUID, however it is spelt), so a resend, which they answer from the first
    // outcome with nothing typed, takes nothing back of its own, even one still waiting beside the
    // first. And taken back once: whichever of them finishes first, with the first one's generation,
    // so an agent's input while the delivery was under way, which came after the person's, stands.
    sealed class TakeBackEntry(string session, long generation, DateTimeOffset at)
    {
        public string Session { get; } = session;
        public long Generation { get; } = generation;
        public DateTimeOffset At { get; } = at;
        public int Done;
    }

    readonly ConcurrentDictionary<string, TakeBackEntry> takeBacks = new(StringComparer.Ordinal);
    static readonly TimeSpan TakeBackKept = TimeSpan.FromHours(1);

    TakeBackEntry? BeginTakeBack(string sessionId, string request)
    {
        if (Drivers is null) { return null; }
        var now = Time.GetUtcNow();
        if (takeBacks.Count > 1024)
        {
            foreach (var (k, e) in takeBacks) { if (now - e.At > TakeBackKept) { takeBacks.TryRemove(k, out _); } }
        }
        var key = Guid.TryParse(request, out var g) ? g.ToString("N") : request;
        return takeBacks.GetOrAdd(key, _ => new TakeBackEntry(sessionId, Drivers.GenerationOf(sessionId), now));
    }

    void TakeBack(TakeBackEntry? entry, bool answer)
    {
        if (entry is null || Interlocked.Exchange(ref entry.Done, 1) == 1) { return; }
        Drivers!.TakeBack(entry.Session, Time.GetUtcNow(), entry.Generation, answer);
    }

    // launch.trust.answer, through the relay only (FolderTrustAnswers).
    public FolderTrustAnswers? TrustAnswers { get; init; }

    // Approve on an approval the host escaped anything in, confirmed from a person's device through
    // the relay only (EscapedApprovals, #803).
    public EscapedApprovals? EscapedApprovals { get; init; }

    // update.*, through the relay only, and never on a read-only machine (UpdateService).
    public UpdateService? Updates { get; init; }

    // Framework only: no production auth factory/entrypoint is registered. Both gates
    // remain off until Engineering approves the agent-owned private login path.
    internal SignIn.AgentSignInService? SignIn { get; init; }
    internal bool SignInEntrypointApproved { get; init; } = AgentSignIn.EnabledByDefault;

    public SessionService(ISessionSource source, MachineInfo machine, Action<string>? log = null, ISessionAnswerer? answers = null,
        ISessionReplier? replies = null, PushNotifier? push = null, AttachmentStore? attachments = null, ISessionStopper? stops = null,
        UsageService? usage = null, ISessionLauncher? launcher = null, SessionTransfers? transfers = null)
    {
        this.source = source;
        this.machine = machine;
        this.log = log ?? (_ => { });
        this.answers = answers;
        this.replies = replies;
        this.push = push;
        this.attachments = attachments;
        this.stops = stops;
        this.usage = usage;
        this.launcher = launcher;
        this.transfers = transfers;
        if (push is not null) { push.Watching = Watching; }
    }

    // How often live changes to the sessions a client does not show go to it, once it has said
    // which it shows (sessions.focus): each change woke a phone's radio, every 250 ms to 4 s while
    // a session worked, ~50–100 mAh an hour with the app open (#610).
    public TimeSpan QuietInterval { get; init; } = TimeSpan.FromSeconds(12);

    // Channels with a subscription running; tests and the host's log read it.
    public int Subscribers => subscribers.Values.Count(s => s.Feed is not null);

    // Each feed as it is made, so tests can check a replaced one is let go.
    internal event Action<object>? FeedCreated;

    public Task<object?> HandleAsync(IProtocolChannel channel, string op, JsonElement data) => HandleAsync(channel, op, data, null);

    // shape: what the caller's role (RemoteAuthorization) or kind (LocalApi) lets this channel's
    // hello say. hello.changed compares what was shaped, so a channel hears only of a change it
    // can see; compared unshaped, a Service peer heard of update coming and going (#651).
    public async Task<object?> HandleAsync(IProtocolChannel channel, string op, JsonElement data, Func<HelloResult, HelloResult>? shape)
    {
        switch (op)
        {
            case Ops.Hello:
                {
                    // Computed for each hello, so it says what is true now: agent-ha-bridge
                    // starting for this user takes sessions.answer and sessions.reply away until
                    // it stops.
                    var hello = HelloFor(channel);
                    if (shape is not null) { hello = shape(hello); }
                    if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.True)
                    {
                        WatchHello(channel, hello, shape);
                    }
                    return hello;
                }
            case Ops.SessionAnswer:
                return await AnswerAsync(channel, data);
            case Ops.SessionReply:
                return await ReplyAsync(channel, data);
            case Ops.AttachmentStart or Ops.AttachmentChunk or Ops.AttachmentDiscard when attachments is not null:
                return Attach(channel, op, data);
            case Ops.SessionStop:
                return await StopAsync(data);
            case AgentSignIn.Start:
                return SignInEntrypointApproved && SignIn is not null ? SignIn.Start(channel, data)
                    : new { outcome = "refused", reason = "agent sign-in is not enabled" };
            case AgentSignIn.Cancel:
                return SignInEntrypointApproved && SignIn is not null ? await SignIn.CancelAsync(channel, data)
                    : new { outcome = "refused", reason = "agent sign-in is not enabled" };
            case Ops.PushSubscribe or Ops.PushUnsubscribe or Ops.PushList when push is not null:
                return await push.HandleAsync(channel, op, data);
            // Notifications turned off on this machine (push.enabled), or never set up: a device
            // that asks anyway hears so, rather than an unknown op it might take for a fault.
            case Ops.PushSubscribe:
                throw new InvalidOperationException(NoNotifications);
            case Ops.PushUnsubscribe:
                return new PushUnsubscribeResult(false);
            case Ops.PushList:
                return new PushListResult([]);
            case LaunchProtocol.Options when launcher is not null:
                var options = await launcher.OptionsAsync();
                return options.Trust is null ? options : options with { Trust = [.. options.Trust.Select(q => ForPeer(channel, q))] };
            case LaunchProtocol.Launch when launcher is not null:
                return await LaunchAsync(channel, data);
            // Host to host, through the relay only: another machine of the account asking for a
            // session with a command a person's device signed.
            case TransferProtocol.Op when transfers is not null && channel is SecureChannel secure:
                return await transfers.ServeAsync(secure, data);
            // A person's device through the relay, with a command it signed. Never this machine's
            // pipe, which agents and the MCP server reach, nor the Home Assistant connector.
            case TrustProtocol.Answer when TrustAnswers is not null && channel is SecureChannel secure:
                return await TrustAnswers.AnswerAsync(secure.PeerId, data);
            case TrustProtocol.Answer:
                return new TrustAnswerResult(TrustAnswerOutcome.Refused,
                    "a folder-trust question is answered by a person, at the machine or from their phone, browser or Windows app through the relay");
            case UsageProtocol.Get or UsageProtocol.Subscribe when usage is not null:
                return await usage.HandleAsync(channel, op, data);
            case UpdateProtocol.Get or UpdateProtocol.Subscribe or UpdateProtocol.Check or UpdateProtocol.Install when Updates is not null:
                return await Updates.HandleAsync(channel, op, data);
            case Ops.SessionsList:
                return source.List(String(data, "cursor"));
            case Ops.SessionGet:
                {
                    var id = String(data, "id") ?? throw new ArgumentException("session.get needs an id");
                    if (String(data, "agent") is { } agent) { return (object?)await source.GetAgentAsync(id, agent) ?? new { missing = true }; }
                    return (object?)source.Get(id) ?? new { missing = true };
                }
            case Ops.SessionsSubscribe:
                return await SubscribeAsync(channel, data);
            case Ops.SessionsFocus:
                return Focus(channel, data);
            default:
                return null;
        }
    }

    // A channel has one subscription. A second subscribe (a client recovering from a gap)
    // stops the first before answering, so nothing from the old one can follow the new
    // response, and the new snapshot covers whatever the old one never sent.
    async Task<object> SubscribeAsync(IProtocolChannel channel, JsonElement data)
    {
        // Read strictly, as every request a client sends, but only an object is a request: clients
        // before patches send {} or nothing at all.
        var request = data.ValueKind == JsonValueKind.Object ? data.Deserialize<SubscribeRequest>(ProtocolJson.Options) ?? new() : new();
        // Revisions start again from 0 in each run of the host, so one from another run says nothing
        // about what the client holds.
        var since = request.Since is { } s && machine.Instance is { } instance && s.Instance == instance ? s.Revision : (long?)null;
        var state = StateOf(channel);
        // ChannelHub serves each request as it arrives, so two subscribes on one channel can
        // run at once; the lock keeps the old feed's stop and the new feed's start in order.
        await state.Gate.WaitAsync();
        try
        {
            if (state.Feed is { } old)
            {
                state.Feed = null;
                await old.StopAsync();
            }
            if (!channel.IsOpen) { throw new IOException("the channel is closed"); }
            var feed = new Feed(source.Subscribe(since), request.Patch == true, state, Time, QuietInterval);
            state.Feed = feed;
            FeedCreated?.Invoke(feed);
            // The snapshot waits until the response has gone (ChannelReply), so the client
            // has {revision, count} before the first upsert.
            return new ChannelReply(feed.Subscription.Result, ch => feed.RunAsync(ch, log));
        }
        finally { state.Gate.Release(); }
    }

    Subscriber StateOf(IProtocolChannel channel)
    {
        var state = subscribers.GetOrAdd(channel, _ => new Subscriber());
        // Cleanup is registered once per channel and finds whatever feed is current then.
        // One registration per subscribe would keep every replaced feed (and anything still
        // queued in it) reachable from the channel until it closes, so a client that keeps
        // recovering on one long-lived channel would grow the host without bound. Tied to the
        // channel rather than to the feed's run: when the response cannot be sent, ChannelHub
        // never starts the run, and the subscription would stay with the source forever.
        if (Interlocked.Exchange(ref state.Registered, 1) == 0)
        {
            _ = channel.Closed.ContinueWith(_ => EndAsync(channel, state), TaskScheduler.Default);
        }
        return state;
    }

    const int FocusesPerWindow = 20;
    static readonly TimeSpan FocusWindow = TimeSpan.FromSeconds(10);

    // sessions.focus: kept for the channel, across subscribes on it, until it says again or closes.
    // A session it now shows whose change is being held goes at once.
    object Focus(IProtocolChannel channel, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) { throw new ArgumentException("sessions.focus needs visible"); }
        FocusRequest focus;
        try { focus = data.Deserialize<FocusRequest>(ProtocolJson.Options) ?? throw new ArgumentException("sessions.focus needs visible"); }
        catch (JsonException) { throw new ArgumentException("sessions.focus needs visible, and open only as a session id"); }
        var state = StateOf(channel);
        // Each one puts a marker in the feed's queue; a client says it on a page or session change,
        // so more than this in a window is a client gone wrong (#697).
        lock (state)
        {
            var now = Time.GetUtcNow();
            if (now - state.FocusWindow >= FocusWindow)
            {
                state.FocusWindow = now;
                state.FocusCount = 0;
            }
            if (++state.FocusCount > FocusesPerWindow) { throw new InvalidOperationException($"sessions.focus is said at most {FocusesPerWindow} times in {FocusWindow.TotalSeconds:0} s"); }
        }
        Volatile.Write(ref state.Focus, focus);
        state.Feed?.Refocus();
        return new { };
    }

    // A device of the account that has a subscription open on a channel that says it is on screen
    // now: it sees a turn finish, and needs no notification of it (PushNotifier).
    public bool Watching(string device)
    {
        foreach (var (channel, state) in subscribers)
        {
            if (channel.PeerId == device && channel.IsOpen && state.Feed is not null && Volatile.Read(ref state.Focus) is { Visible: true }) { return true; }
        }
        return false;
    }

    async Task EndAsync(IProtocolChannel channel, Subscriber state)
    {
        subscribers.TryRemove(new KeyValuePair<IProtocolChannel, Subscriber>(channel, state));
        // Under the lock, so a subscribe already running finishes first and its feed is the
        // one stopped here. The lock itself is left for the GC rather than disposed: a
        // subscribe still waiting on it would otherwise throw instead of finding the channel
        // closed.
        await state.Gate.WaitAsync();
        try
        {
            if (state.Feed is { } feed)
            {
                state.Feed = null;
                await feed.StopAsync();
            }
        }
        finally { state.Gate.Release(); }
    }

    internal sealed class Subscriber
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public Feed? Feed;
        public int Registered;
        // Null until the channel says (sessions.focus): every live change then goes at once, as before.
        public FocusRequest? Focus;
        // sessions.focus said in the window that started at FocusWindow, under lock (this).
        public DateTimeOffset FocusWindow = DateTimeOffset.MinValue;
        public int FocusCount;
    }

    async Task<AnswerResult> AnswerAsync(IProtocolChannel channel, JsonElement data)
    {
        // A request without an id, question or request UUID fails as a request; anything
        // that can be read gets an outcome.
        if (data.ValueKind != JsonValueKind.Object) { throw new ArgumentException("session.answer needs an id, a question and a request"); }
        var request = data.Deserialize<AnswerRequest>(ProtocolJson.Options) ?? throw new ArgumentException("session.answer needs an id, a question and a request");
        var (watch, progress) = Watch(request.Progress);
        if (answers is null) { return new AnswerResult(AnswerOutcome.Refused, "this host does not take answers; answer at the machine"); }
        var takeBack = BeginTakeBack(request.Id, request.Request);
        // A confirmation is checked as coming from the device on the other end of a channel through
        // the relay, and from nowhere else: on this machine's pipe it is none, and refused as such.
        var confirmed = channel is SecureChannel secure && EscapedApprovals is { } escaped ? escaped.For(secure.PeerId, request) : null;
        var result = await answers.AnswerAsync(request, watch: watch, confirmed: confirmed);
        // Typed from this device: it needs no notification that the question was answered.
        if (result.Outcome is AnswerOutcome.Delivered or AnswerOutcome.Unconfirmed)
        {
            push?.Answered(channel.PeerId, request.Id, request.Question);
            TakeBack(takeBack, answer: true);
        }
        return progress ? result with { Timings = watch.Timings } : result;
    }

    // What a caller of session.reply or session.answer hears as the delivery goes. With progress
    // asked for (and a channel that can carry it), received at once, before any check, and typed
    // once the keys have gone (v1-sessions.md, Channel framing); without, nothing on the wire
    // changes. Either way the host's timing line learns when the result went.
    static (DeliveryWatch Watch, bool Progress) Watch(bool? asked)
    {
        var served = ServedRequest.Current;
        var progress = asked == true && served is not null;
        if (progress) { served!.Progress(new DeliveryProgress(DeliveryProgress.Received)); }
        var watch = new DeliveryWatch(
            progress ? working => served!.Progress(new DeliveryProgress(DeliveryProgress.Typed, working)) : null,
            sendsResult: served is not null);
        served?.OnResponded(watch.ResultSent);
        return (watch, progress);
    }

    // Now is at most SharedAgentHaBridgeProbe.Keep old: what acts on it looks again first.
    HelloResult HelloFor(IProtocolChannel channel)
    {
        using (G5?.Hold())
        {
            return new(1, machine with { Sessions = source.Snapshot().Count, ReadOnly = ReadOnly?.Invoke(), Restore = Restore?.Invoke() }, Offered(channel));
        }
    }

    // ---- hello.changed: what hello would say, told to the channels that asked, when it changes ----
    //
    // Apps asked hello again every 10 s on every connection, to see agent-ha-bridge start or stop
    // (G5) or an update be offered: ~36 hellos a minute at a host from other machines' apps (#640).
    // Now one host timer looks for them, and a channel hears only of a change.

    // How often the channels that asked are looked at again. As often as apps asked before, so a
    // change is heard as soon as it was, and a look costs no more than one of their hellos did.
    public TimeSpan HelloCheck { get; init; } = TimeSpan.FromSeconds(10);

    // What each channel that asked was last told: its readOnly and capabilities, as shaped for it.
    readonly ConcurrentDictionary<IProtocolChannel, HelloWatch> helloWatches = new();
    readonly Lock helloGate = new();
    ITimer? helloTimer;
    int checkingHellos;

    sealed class HelloWatch
    {
        public string Said = "";
        public Func<HelloResult, HelloResult>? Shape;
    }

    // Only what a client acts on; machine.sessions changes all the time and is not a change here.
    // machine.restore is: a session judged after its agent's discovery was held is offered later.
    static string Said(string? readOnly, RestoreNotice? restore, IEnumerable<string> offered) =>
        (readOnly ?? "") + "\n" + Said(restore) + "\n" + string.Join("\n", offered);

    // The whole notice as shaped for the channel: its cause, its time and what each offer says change
    // too, with the counts the same (an uncertain session found live, the rest rebuilt; Copilot on
    // #813).
    static string Said(RestoreNotice? n) => n is null ? "" : JsonSerializer.Serialize(n, ProtocolJson.Options);

    void WatchHello(IProtocolChannel channel, HelloResult hello, Func<HelloResult, HelloResult>? shape)
    {
        var watch = helloWatches.GetOrAdd(channel, c =>
        {
            _ = c.Closed.ContinueWith(closed => helloWatches.TryRemove(c, out _), TaskScheduler.Default);
            return new HelloWatch();
        });
        lock (watch)
        {
            watch.Said = Said(hello.Machine.ReadOnly, hello.Machine.Restore, hello.Capabilities);
            watch.Shape = shape;
        }
        lock (helloGate) { helloTimer ??= Time.CreateTimer(_ => CheckHellos(), null, HelloCheck, HelloCheck); }
    }

    void CheckHellos()
    {
        // A slow look is not run again over itself.
        if (Interlocked.Exchange(ref checkingHellos, 1) == 1) { return; }
        try
        {
            // One G5 look for every channel at this tick, not one each.
            using var once = G5?.Hold();
            foreach (var (channel, watch) in helloWatches)
            {
                if (channel.Closed.IsCompleted)
                {
                    helloWatches.TryRemove(channel, out _);
                    continue;
                }
                // Not open yet: a hello answered in an IK handshake (#603) watches the channel a
                // moment before it opens. Looked at from the next tick, rather than told a change
                // now that could not be sent.
                if (!channel.IsOpen) { continue; }
                // Not HelloFor: a look needs no copy of the session list, only what Said compares.
                // Shaped afresh at each look, so a peer whose role changed is compared as it is now.
                Func<HelloResult, HelloResult>? shape;
                lock (watch) { shape = watch.Shape; }
                var readOnly = ReadOnly?.Invoke();
                var restore = Restore?.Invoke();
                var offered = Offered(channel);
                string said;
                if (shape is null) { said = Said(readOnly, restore, offered); }
                else
                {
                    var shaped = shape(new HelloResult(1, machine with { ReadOnly = readOnly, Restore = restore }, offered));
                    said = Said(shaped.Machine.ReadOnly, shaped.Machine.Restore, shaped.Capabilities);
                }
                lock (watch)
                {
                    if (said == watch.Said) { continue; }
                    watch.Said = said;
                }
                _ = TellAsync(channel);
            }
            // Nobody left to tell: the timer goes until a hello asks again.
            lock (helloGate)
            {
                if (helloWatches.IsEmpty && helloTimer is { } timer)
                {
                    timer.Dispose();
                    helloTimer = null;
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { log($"could not look at what hello says: {e.Message}"); }
        finally { Volatile.Write(ref checkingHellos, 0); }
    }

    // Only that it changed: the client asks hello again. The shaped result is not sent either:
    // hello itself is checked afresh by the caller's role or kind, and says it.
    async Task TellAsync(IProtocolChannel channel)
    {
        try { await channel.SendEventAsync(Events.HelloChanged, new { }); }
        // The channel has gone: Closed removes its watch.
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
    }

    List<string> Offered(IProtocolChannel channel)
    {
        List<string> offered = [Capabilities.SessionsRead, Capabilities.SessionsPatch, Capabilities.HelloChanged, Capabilities.SessionsFocus];
        if (SignInEntrypointApproved && SignIn?.Offered(channel) == true) { offered.Add(AgentSignIn.Capability); }
        if (answers?.Available == true) { offered.Add(Capabilities.SessionsAnswer); }
        if (answers?.ApprovesPermissions == true) { offered.Add(Capabilities.SessionsApprove); }
        if (replies?.Available == true) { offered.Add(Capabilities.SessionsReply); }
        if (offered.Contains(Capabilities.SessionsAnswer) || offered.Contains(Capabilities.SessionsReply)) { offered.Add(Capabilities.SessionsProgress); }
        if (attachments is not null && replies?.TakesAttachments == true) { offered.Add(Capabilities.SessionsAttach); }
        // Large files come through the blob store of the relay this channel came through; an app on
        // this machine has no use for it, and sends them in chunks over its own pipe instead.
        if (attachments is not null && replies?.TakesAttachments == true && channel is SecureChannel && attachments.BlobBytes > 0) { offered.Add(Capabilities.SessionsAttachBlob); }
        if (attachments is not null && replies?.TakesAttachments == true && Direct(channel)) { offered.Add(Capabilities.SessionsAttachDirect); }
        if (stops?.Available == true) { offered.Add(Capabilities.SessionsStop); }
        // Web Push is for devices through the relay; an app on this machine has its own toasts.
        if (push is not null && channel is SecureChannel) { offered.Add(Capabilities.Push); }
        if (usage is not null) { offered.Add(UsageProtocol.Capability); }
        if (usage?.Shares == true) { offered.Add(UsageProtocol.SharedCapability); }
        if (launcher?.Available == true) { offered.Add(LaunchProtocol.Capability); }
        if (TrustAnswers?.Available == true && channel is SecureChannel) { offered.Add(TrustProtocol.Capability); }
        if (answers?.Available == true && channel is SecureChannel peer && EscapedApprovals?.Offers(peer.PeerId) == true) { offered.Add(EscapedApproval.Capability); }
        if (Updates?.Offered(channel) == true) { offered.Add(UpdateProtocol.Capability); }
        if (Restore is not null) { offered.Add(RestoreProtocol.Capability); }
        // Transfers are signed by a device through the relay, and go through its blob store.
        if (transfers is not null && channel is SecureChannel)
        {
            if (launcher?.Available == true && transfers.Receives) { offered.Add(TransferProtocol.Capability); }
            if (transfers.Serves) { offered.Add(TransferProtocol.ServeCapability); }
        }
        return offered;
    }

    async Task<ReplyResult> ReplyAsync(IProtocolChannel channel, JsonElement data)
    {
        // As for answers: a request without an id or request UUID fails as a request, and
        // anything that can be read gets an outcome.
        if (data.ValueKind != JsonValueKind.Object) { throw new ArgumentException("session.reply needs an id, a request and text"); }
        var request = data.Deserialize<ReplyRequest>(ProtocolJson.Options) ?? throw new ArgumentException("session.reply needs an id, a request and text");
        var (watch, progress) = Watch(request.Progress);
        if (replies is null) { return new ReplyResult(AnswerOutcome.Refused, "this host does not take replies; reply at the machine"); }
        var takeBack = BeginTakeBack(request.Id, request.Request);
        var result = await replies.ReplyAsync(request, channel.PeerId, watch: watch);
        if (result.Outcome is AnswerOutcome.Delivered or AnswerOutcome.Unconfirmed)
        {
            push?.Replied(channel.PeerId, request.Id);
            TakeBack(takeBack, answer: false);
        }
        return progress ? result with { Timings = watch.Timings } : result;
    }

    // Only this machine's own pipe is direct: anything through the relay keeps the 256 KiB cap
    // in chunks, which keeps the relay's bulk lane cheap (design §4.6).
    static bool Direct(IProtocolChannel channel) => channel is AgentBridge.Local.LocalChannel;

    // attachment.*: the files a reply will carry, held for the device that sends them. A check
    // that fails fails the request, with the reason.
    object Attach(IProtocolChannel channel, string op, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) { throw new ArgumentException($"{op} needs an attachment"); }
        return op switch
        {
            Ops.AttachmentStart => attachments!.Start(channel.PeerId, data.Deserialize<AttachmentStart>(ProtocolJson.Options)!, Direct(channel)),
            Ops.AttachmentChunk => attachments!.Chunk(channel.PeerId, data.Deserialize<AttachmentChunk>(ProtocolJson.Options)!),
            _ => new AttachmentDiscarded(attachments!.Discard(channel.PeerId, data.Deserialize<AttachmentDiscard>(ProtocolJson.Options)!.Attachment)),
        };
    }

    async Task<StopResult> StopAsync(JsonElement data)
    {
        // As for answers: a request without an id or request UUID fails as a request, and
        // anything that can be read gets an outcome.
        if (data.ValueKind != JsonValueKind.Object) { throw new ArgumentException("session.stop needs an id and a request"); }
        var request = data.Deserialize<StopRequest>(ProtocolJson.Options) ?? throw new ArgumentException("session.stop needs an id and a request");
        if (stops is null) { return new StopResult(StopOutcome.Refused, "this host does not end sessions; end it at the machine"); }
        return await stops.StopAsync(request);
    }

async Task<LaunchResult> LaunchAsync(IProtocolChannel channel, JsonElement data)
    {
        // As for answers: a request without a request UUID fails as a request, and anything
        // that can be read gets an outcome. The whole request is bounded, prompt and all.
        if (data.ValueKind != JsonValueKind.Object) { throw new ArgumentException("session.launch needs a request"); }
        if (System.Text.Encoding.UTF8.GetByteCount(data.GetRawText()) > LaunchProtocol.MaxRequest) { throw new ArgumentException($"session.launch is larger than {LaunchProtocol.MaxRequest / 1024} KiB"); }
        var request = data.Deserialize<LaunchRequest>(ProtocolJson.Options) ?? throw new ArgumentException("session.launch needs a request");
        // A transfer is asked for by a device through the relay, which signs it; this machine's
        // own app has no key the other machine knows.
        if (request.Transfer is not null && (channel is not SecureChannel || transfers is null))
        {
            return new LaunchResult(LaunchOutcome.Refused, "a session from another machine is brought in through the relay, from the phone or browser app");
        }
        var result = await launcher!.LaunchAsync(request, channel.PeerId);
        return result.Trust is null ? result : result with { Trust = ForPeer(channel, result.Trust) };
    }

    // A folder-trust question as this caller may answer it: the machine says whether it can be
    // answered at all, and this says whether by this device. Only a device a person approved at
    // the machine, through the relay; never this machine's pipe, which agents and the MCP server
    // reach. So a card never offers buttons that would only be refused.
    TrustQuestion ForPeer(IProtocolChannel channel, TrustQuestion question)
    {
        if (!question.Answerable) { return question; }
        string? why = null;
        if (TrustAnswers is null || channel is not SecureChannel secure)
        {
            why = "Answer at the machine, or from a phone, browser or Windows app through the relay.";
        }
        else if (TrustAnswers.NotAPerson(secure.PeerId) is { } who)
        {
            why = $"Answer at the machine: {who}.";
        }
        else if (launcher!.TrustDevices.Count > 0 && !launcher.TrustDevices.Contains(secure.PeerId))
        {
            why = LaunchService.NotApproved(secure.PeerId);
        }
        return why is null ? question : question with { Answerable = false, AtMachine = why };
    }

    static string? String(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal sealed class Feed(SessionSubscription subscription, bool patch, Subscriber state, TimeProvider time, TimeSpan quiet)
    {
        // Put in the subscription's own queue, so what was held goes in order with everything else:
        // Tick when the quiet interval is up, Refocused when the client shows another session.
        static readonly object Tick = new(), Refocused = new();
        readonly CancellationTokenSource stop = new();
        readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // With patch: each session as this channel last sent it, and the seq it went with. Only what
        // got through: an event that could not be sent leaves the client on the one before, which
        // the next patch then goes on from.
        readonly Dictionary<string, (long Seq, JsonObject Json)> sent = new(StringComparer.Ordinal);
        // With focus: the latest change of each session the client does not show, until the quiet
        // interval is up, and the latest revision read before its first held change (Floor). That
        // change was made after Floor, so nothing sent meanwhile says a later revision: a client
        // that resumed from one would never be sent the change held here.
        readonly Dictionary<string, (SessionEvent Event, long Floor)> held = new(StringComparer.Ordinal);
        // Each session as this channel was last sent it, patch or not: what Urgent compares with.
        readonly Dictionary<string, Session> shown = new(StringComparer.Ordinal);
        readonly Lock timerGate = new();
        ITimer? timer;
        DateTimeOffset quietUntil = DateTimeOffset.MinValue;
        // The latest revision read from the subscription: everything up to it is sent or held.
        long read;
        int heldCount;
        // Past this subscription's sessions.synced: upserts from here on are live changes.
        bool live;
        int started;

        public SessionSubscription Subscription => subscription;

        // Sessions whose change waits for the quiet interval; tests read it.
        internal int Held => Volatile.Read(ref heldCount);

        public void Refocus() => subscription.Channel.Writer.TryWrite(new SessionEvent("", Refocused));

        // Sends the subscription's events, in order, until the channel ends or it is
        // replaced. The channel numbers them: seq continues across subscriptions.
        public async Task RunAsync(IProtocolChannel channel, Action<string> log)
        {
            if (Interlocked.Exchange(ref started, 1) == 1) { return; }
            try
            {
                await foreach (var ev in subscription.Events.ReadAllAsync(stop.Token))
                {
                    if (ReferenceEquals(ev.Data, Tick) || ReferenceEquals(ev.Data, Refocused))
                    {
                        await ReleaseAsync(channel, log, all: ReferenceEquals(ev.Data, Tick));
                        continue;
                    }
                    var floor = read;
                    if (ev.Revision is { } revision && revision > read) { read = revision; }
                    if (ev.Data is SessionsSynced synced)
                    {
                        live = true;
                        if (synced.Revision > read) { read = synced.Revision; }
                    }
                    if (live && ev.Data is Session session && Quiet(session.Id) && !Urgent(session))
                    {
                        var now = time.GetUtcNow();
                        // The first change after a quiet spell goes at once; the next ones wait for the
                        // interval's end and go together, each session's latest only.
                        if (held.Count > 0 || now < quietUntil)
                        {
                            Hold(session.Id, ev, floor, now);
                            continue;
                        }
                        quietUntil = now + quiet;
                    }
                    // What this sends of a session supersedes what was held of it: a session opened
                    // since it was held, its newer change read before the Refocused marker. Released
                    // after, the older state would have gone back over the newer one.
                    if (ev.Data is SessionRemoved removed && held.Remove(removed.Id)) { Volatile.Write(ref heldCount, held.Count); }
                    if (live && ev.Data is Session superseding && held.Remove(superseding.Id)) { Volatile.Write(ref heldCount, held.Count); }
                    await SendAsync(channel, held.Count == 0 ? ev : ev with { Revision = Cap(ev.Revision) }, log);
                }
            }
            catch (OperationCanceledException) { }
            // The channel has gone; End, waiting on Closed, removes the feed.
            catch (IOException) { }
            finally
            {
                lock (timerGate)
                {
                    timer?.Dispose();
                    timer = null;
                }
                subscription.Dispose();
                done.TrySetResult();
            }
        }

        // Held only once the channel has said what it shows; the open session, while on screen, never.
        bool Quiet(string id) => Volatile.Read(ref state.Focus) is { } focus && !(focus.Visible && focus.Open == id);

        // What a person must see at once, on screen or not: a question asked, answered or changed
        // (its id, what it asks, its decision's choices, answerAtMachine), a session starting to
        // wait or ask, or ending. Held with the rest, a phone on another session showed a new
        // approval up to 12 s late (#697). Sent, it replaces what was held of the session.
        bool Urgent(Session now)
        {
            // Not sent on this subscription: new, or left out of a resumed snapshot, where the client
            // may still hold a question this change answers. Nothing to compare with, so at once,
            // once per session per subscription.
            if (!shown.TryGetValue(now.Id, out var before)) { return true; }
            if (now.Status != before.Status && now.Status is SessionStatus.Waiting or SessionStatus.Ending) { return true; }
            if (now.WaitingOn is not null && now.WaitingOn != before.WaitingOn) { return true; }
            if (ReferenceEquals(now.Question, before.Question)) { return false; }
            if (now.Question is null || before.Question is null) { return true; }
            // Rebuilt on each read, so compared as sent: Fields is a list, which a record compares
            // by reference. Only while a question is up, which is rare.
            return JsonSerializer.Serialize(now.Question, ProtocolJson.Options) != JsonSerializer.Serialize(before.Question, ProtocolJson.Options);
        }

        long? Cap(long? revision) => revision is { } r ? Math.Min(r, held.Values.Min(h => h.Floor)) : null;

        void Hold(string id, SessionEvent ev, long floor, DateTimeOffset now)
        {
            held[id] = (ev, held.TryGetValue(id, out var before) ? before.Floor : floor);
            Volatile.Write(ref heldCount, held.Count);
            lock (timerGate)
            {
                var due = quietUntil - now;
                timer ??= time.CreateTimer(_ => subscription.Channel.Writer.TryWrite(new SessionEvent("", Tick)), null,
                    due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
        }

        // At the interval's end everything held goes; on a refocus, only what is no longer quiet.
        async Task ReleaseAsync(IProtocolChannel channel, Action<string> log, bool all)
        {
            if (all)
            {
                lock (timerGate)
                {
                    timer?.Dispose();
                    timer = null;
                }
            }
            var going = held.Where(h => all || !Quiet(h.Key)).OrderBy(h => h.Value.Floor).Select(h => h.Key).ToList();
            foreach (var id in going)
            {
                var ev = held[id].Event;
                held.Remove(id);
                Volatile.Write(ref heldCount, held.Count);
                // The last to go leaves nothing read unsent, so it can say so.
                var revision = ev.Revision is { } r ? held.Count == 0 ? Math.Max(r, read) : Cap(r) : null;
                await SendAsync(channel, ev with { Revision = revision }, log);
            }
            if (all && going.Count > 0) { quietUntil = time.GetUtcNow() + quiet; }
        }

        async Task SendAsync(IProtocolChannel channel, SessionEvent ev, Action<string> log)
        {
            var (name, data, id, json) = Shape(ev);
            if (name is null) { return; }
            try
            {
                var seq = await channel.SendEventAsync(name, data, stop.Token);
                if (id is not null && json is not null) { sent[id] = (seq, json); }
                if (ev.Data is Session session) { shown[session.Id] = session; }
                else if (ev.Data is SessionRemoved removed) { shown.Remove(removed.Id); }
            }
            catch (MessageTooLargeException e)
            {
                // Every session has been through SessionBudget.Fit, so this is a bug.
                // The size is checked before encrypting, so the channel is intact and
                // the session's next change can still get through.
                log($"skipped a {name} too large to send ({e.Size} bytes)");
            }
        }
        // The event as this client asked for it: to one that asked for patches, a live upsert is a
        // session.patch on what it last sent (null name: nothing it reads changed), and a removal
        // carries the revision. Json: the session as the client will hold it once sent.
        (string? Name, object Data, string? Id, JsonObject? Json) Shape(SessionEvent ev)
        {
            if (!patch) { return (ev.Name, ev.Data, null, null); }
            switch (ev.Data)
            {
                case Session session:
                    var json = SessionPatches.Json(session);
                    if (!live) { return (ev.Name, ev.Data, session.Id, json); }
                    var next = sent.TryGetValue(session.Id, out var last)
                        ? SessionPatches.Next(last.Json, last.Seq, json, ev.Revision)
                        : SessionPatches.Whole(json, ev.Revision);
                    return next is null ? (null, ev.Data, null, null) : (Events.SessionPatch, next, session.Id, json);
                case SessionRemoved removed:
                    sent.Remove(removed.Id);
                    return (ev.Name, removed with { Revision = ev.Revision }, null, null);
                case SessionsSynced:
                    live = true;
                    return (ev.Name, ev.Data, null, null);
                default:
                    return (ev.Name, ev.Data, null, null);
            }
        }

        public async Task StopAsync()
        {
            stop.Cancel();
            // Replaced before its snapshot started (the response never left): nothing runs.
            if (Interlocked.Exchange(ref started, 1) == 0)
            {
                subscription.Dispose();
                done.TrySetResult();
            }
            await done.Task;
        }
    }
}
