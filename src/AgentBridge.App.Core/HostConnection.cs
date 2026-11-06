using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.App.Core;

public enum LinkState { Connecting, Connected, NotRunning, Refused }

// The app's one connection to this machine's host, over the local pipe: it connects (and
// reconnects whenever the host restarts), says hello, and keeps three subscriptions running:
// the sessions, the agents' usage and the host's own status. What it hears is applied on the
// UI thread, so view models never see a change from anywhere else.
// The host process a request went to has been replaced: it is not sent to the new one.
public sealed class HostRestartedException() : Exception($"the {Product.HostName} restarted");

public sealed class HostConnection : IAsyncDisposable
{
    readonly Func<CancellationToken, Task<IClientChannel>> connect;
    readonly IUiDispatcher ui;
    readonly Action<string> log;
    readonly CancellationTokenSource stop = new();
    TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task loop = Task.CompletedTask;
    // The connection with the host process it reaches, swapped whole so the two are never read
    // apart: Instance is null until that process is known.
    Current? current;
    long statusEvents;
    long usageEvents;
    long updateEvents;
    long connections;
    // The kinds of event a message was refused for, each logged once (#446).
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> refusedKinds = new(StringComparer.Ordinal);

    sealed record Current(IClientChannel Channel, string? Instance);

    public HostConnection(Func<CancellationToken, Task<LocalChannel>> connect, IUiDispatcher ui, Action<string>? log = null)
        : this(async ct => (IClientChannel)await connect(ct), ui, log) { }

    // Any machine's host: this one's over the pipe, another's over a channel through the relay.
    public HostConnection(Func<CancellationToken, Task<IClientChannel>> connect, IUiDispatcher ui, Action<string>? log = null)
    {
        this.connect = connect;
        this.ui = ui;
        this.log = log ?? (_ => { });
        // A resumed snapshot that can't be whole (v1-sessions.md, Resuming without a snapshot) is
        // asked for again in full, on the channel followed now; an older channel's ends with it.
        Sessions.SnapshotRefused += on =>
        {
            if (on is not null && ReferenceEquals(on, Volatile.Read(ref pumping))) { Resync(); }
        };
    }

    public static HostConnection ForProfile(string profileFolder, IUiDispatcher ui, Action<string>? log = null) =>
        new(ct => LocalClient.ConnectAsync(LocalEndpoint.For(profileFolder), TimeSpan.FromSeconds(2), ct), ui, log);

    // How long to wait between attempts while the host is not there. Tests shorten it.
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);
    // How long to wait after the host refused this device (Refusal), unless RetryNow says
    // something changed sooner; null waits RetryDelay, as for any other failure. Another
    // machine's host that had not approved this device was asked every 5 s for hours, each time
    // a channel handshake for an answer that could only change at that machine (#602).
    public TimeSpan? RefusedRetryDelay { get; init; }
    // What the waits between attempts are timed by. Tests use a fake.
    public TimeProvider Time { get; init; } = TimeProvider.System;
    // How often an open connection asks the host again what it offers. Tests shorten it.
    public TimeSpan CapabilityRefresh { get; init; } = TimeSpan.FromSeconds(10);
    // The same, of a host that says when it changes (hello.changed): only in case a change was
    // missed. Every app asked every 10 s, ~36 hellos a minute at a host from other machines (#640).
    public TimeSpan ChangesFallback { get; init; } = TimeSpan.FromMinutes(5);
    // Whether it follows the host's sessions and usage. A connection only for sending (this
    // machine's host reached through the relay, for a transfer) does not: the pipe shows them.
    public bool Subscribes { get; init; } = true;

    // Read on the UI thread.
    public LinkState State { get; private set; } = LinkState.Connecting;
    // Which host process the current connection reaches (its pid and start), set before the
    // connection counts as connected, on any thread: a delivery whose outcome was lost can
    // tell whether the host that may have acted on it is still the one listening.
    public string? HostInstance => Volatile.Read(ref current)?.Instance;
    // Which connection this is, counting from 1: a new one after every reconnect, the same host
    // run or not.
    public long ConnectionNumber => Interlocked.Read(ref connections);
    // The host's static key, as the current channel's handshake proved it: only through the relay.
    // What a command this app signs for that machine is signed for.
    public byte[]? PeerKey => Volatile.Read(ref current)?.Channel is RelayClientChannel { Channel.PeerStatic: { } k } ? [.. k] : null;
    public string? StateDetail { get; private set; }
    public HelloResult? Hello { get; private set; }
    public IReadOnlyList<string> Capabilities => Hello?.Capabilities ?? [];
    public HostStatus? Status { get; private set; }
    public UsageReport Usage { get; private set; } = UsageReport.Empty;
    // Another machine's updates (update.subscribe, #51), through the relay: null where the host
    // offers none (this machine's own pipe, a read-only host, an older host).
    public UpdateState? Update { get; private set; }
    public SessionStore Sessions { get; } = new();

    // Raised on the UI thread: the link, hello, status or usage changed.
    public event Action? Changed;
    // Raised on the UI thread: signin.code or signin.done (#800, AuthState), by name, with its data
    // as sent. They come only to the channels in a flow, and are not kept here: a code is the
    // sheet's for as long as it is open, and no one else's.
    public event Action<string, JsonElement>? AgentSignIn;

    public bool Can(string capability) => Capabilities.Contains(capability);

    // Raised on the UI thread when this connection's events skip some (a gap), before anything
    // after the gap is applied: its sessions may be missing changes until the snapshot the recovery
    // asks for is whole. That snapshot begins only once the recovery has read hello, status, usage
    // and updates again, so until then the list still reads as complete (#427).
    public event Action? Gapped;

    public void Start() => loop = Task.Run(() => RunAsync(stop.Token));

    // The current connection's gap signal, while there is one.
    SemaphoreSlim? resync;
    // The current connection's hello.changed signal, while there is one.
    SemaphoreSlim? helloChanged;
    // Whether the host last said it sends hello.changed: if so, asking every CapabilityRefresh is
    // not needed.
    volatile bool announces;
    // The channel whose events are being followed now.
    IClientChannel? pumping;

    // Reads everything again on the connection there is, as after a gap in its events, the
    // sessions with a new snapshot; nothing without one (the next connection subscribes afresh).
    // For a machine back in the relay's presence whose channel outlived its absence (#269). One
    // pending is enough: a recovery reads everything.
    public void Resync()
    {
        if (Volatile.Read(ref resync) is { CurrentCount: 0 } s) { s.Release(); }
    }

    // Set by RetryNow, cleared as each attempt starts: a change seen during an attempt that ends
    // refused still cuts the wait after it short.
    int poked;
    // Ends the wait after a refusal early. Its continuations run asynchronously, so the attempt a
    // RetryNow starts is not run inline on the caller's (UI) thread.
    TaskCompletionSource? wake;

    // Something that could change a refusal happened (the account's machines changed, this one came
    // back online, the person opened it): after a refusal, try again now rather than at the end of
    // RefusedRetryDelay. Never sooner than RetryDelay after the last attempt, so a roster that
    // changes often costs no more than a failure that is not a refusal. Nothing otherwise.
    public void RetryNow()
    {
        Volatile.Write(ref poked, 1);
        Volatile.Read(ref wake)?.TrySetResult();
    }

    // Waits until there is a connection, for those that have something to send.
    public Task WhenConnectedAsync(CancellationToken ct = default) => Volatile.Read(ref connected).Task.WaitAsync(ct);

    // Sends a request on the current connection. Throws HostNotRunningException without one,
    // IOException when the connection goes under it, HostErrorException when the host fails it.
    public Task<JsonElement> CallAsync(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default) =>
        CallAsync(op, data, timeout, knownHost: false, onlyTo: null, out _, ct);

    // The same, saying which host process it went to (instance), read with the connection it
    // goes on. knownHost: send nothing until that process is known, for an op that must never
    // reach a restarted host a second time. onlyTo: send only to that process, else throw
    // HostRestartedException, checked against the very connection the call would go on.
    // progress: hears the host's progress frames for it (IClientChannel.RequestAsync), on the
    // receive path.
    public Task<JsonElement> CallAsync(string op, object? data, TimeSpan? timeout, bool knownHost, string? onlyTo, out string? instance, CancellationToken ct = default, Action<JsonElement>? progress = null)
    {
        var now = Volatile.Read(ref current);
        if (now is null || !now.Channel.IsOpen) { throw new HostNotRunningException($"not connected to the {Product.HostName}"); }
        if (knownHost && now.Instance is null) { throw new HostNotRunningException($"still connecting to the {Product.HostName}"); }
        if (onlyTo is not null && now.Instance != onlyTo) { throw new HostRestartedException(); }
        instance = now.Instance;
        return now.Channel.CallAsync(op, data, timeout, ct, progress);
    }

    // A line for the app's log (the one this connection was given), for what the app measures of
    // its own deliveries (Deliveries' timing marks, #600). Queued, and written in order by one task
    // of its own: a delivery finishes on the UI thread, and the app's log opens, appends to and
    // locks a file there, which held up input and painting whenever the disk was slow (#625,
    // Copilot 4478421982). What is still queued is written when the connection is let go.
    internal void Log(string line)
    {
        if (!logged.Writer.TryWrite(line)) { return; }
        lock (logGate) { logging ??= Task.Run(WriteLogAsync); }
    }

    readonly System.Threading.Channels.Channel<string> logged =
        System.Threading.Channels.Channel.CreateUnbounded<string>(new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
    readonly object logGate = new();
    Task? logging;

    async Task WriteLogAsync()
    {
        await foreach (var line in logged.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { log(line); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
        }
    }

    public async Task<T> CallAsync<T>(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default) =>
        (await CallAsync(op, data, timeout, ct)).Deserialize<T>(ProtocolJson.Options) ?? throw new HostErrorException($"{op} returned nothing");

    // A host record (a session, launch options, a launch's outcome), read as the spec says a client
    // reads one (Received, v1-sessions.md "Malformed records"): a field it cannot read is absent,
    // unless it is one the record is filed or decided on, which refuses it (MalformedRecordException).
    public async Task<T> ReadAsync<T>(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default) where T : class =>
        Received.Read<T>(await CallAsync(op, data, timeout, ct));

    public async Task RefreshStatusAsync(CancellationToken ct = default)
    {
        var now = Volatile.Read(ref current);
        if (now is null || !now.Channel.IsOpen || now.Instance is null) { throw new HostNotRunningException($"not connected to the {Product.HostName}"); }
        var heard = Interlocked.Read(ref statusEvents);
        var status = await now.Channel.CallAsync<HostStatus>(HostOps.Status, ct: ct);
        ui.Post(() =>
        {
            if (!ReferenceEquals(Volatile.Read(ref current), now) || Interlocked.Read(ref statusEvents) != heard) { return; }
            Status = status;
            Changed?.Invoke();
        });
    }

    // The attempt under way (connecting, or connected), which Reconnect cancels; null between
    // attempts.
    CancellationTokenSource? attempt;

    // Ends the attempt under way, connecting or connected, and lets the loop start another, as when
    // the connection fails: for a link that is not Connected while its host answers on its pipe
    // (#800: an app sat ~20 h believing its live host was not running). The one loop goes on, so
    // attempts never overlap, and calling it again, or between attempts, does no more than once.
    public void Reconnect()
    {
        if (Volatile.Read(ref attempt) is { } now)
        {
            try { now.Cancel(); }
            // Ended and disposed meanwhile: the next attempt starts anyway.
            catch (ObjectDisposedException) { }
        }
        RetryNow();
    }

    async Task RunAsync(CancellationToken stopping)
    {
        // What the host last refused this device with, while every attempt since has been refused
        // the same way: logged once, not at every attempt.
        string? refusal = null;
        while (!stopping.IsCancellationRequested)
        {
            IClientChannel? ch = null;
            HelloResult? said = null;
            string? refusedNow = null;
            Volatile.Write(ref poked, 0);
            var mine = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            Volatile.Write(ref attempt, mine);
            var ct = mine.Token;
            try
            {
                ch = await connect(ct);
                var hello = await ch.HelloAsync(ct, changes: true);
                said = hello;
                // A gap in this connection's events: its own signal, so a wait abandoned when the
                // connection ends cannot swallow the next connection's.
                // Not disposed: the pump may still release it as the connection ends, and a
                // SemaphoreSlim whose wait handle was never asked for holds nothing to free.
                var gaps = new SemaphoreSlim(0);
                Volatile.Write(ref resync, gaps);
                var changed = new SemaphoreSlim(0);
                Volatile.Write(ref helloChanged, changed);
                announces = Announces(hello);
                Volatile.Write(ref pumping, ch);
                var pump = Task.Run(() => PumpAsync(ch, gaps, ct), ct);
                Volatile.Write(ref current, new Current(ch, null));
                ui.Post(() => Hello = hello);
                // An event can overtake the subscribe's result on its way to the UI thread (they
                // arrive on different threads); the result is applied only if no event came.
                var heardStatus = Interlocked.Read(ref statusEvents);
                var connectionNumber = Interlocked.Increment(ref connections);
                // Which run of the host this is: what it says itself (hello's instance), else this
                // machine's by its pid and start, else each connection counts as a new run, which is
                // never wrong, only cautious (a lost launch or reply is then not confirmed, never sent
                // again).
                var instance = hello.Machine.Instance ?? $"connection {connectionNumber}";
                if (hello.Capabilities.Contains(HostOps.Capability))
                {
                    var status = await ch.CallAsync<HostStatus>(HostOps.Subscribe, ct: ct);
                    instance = hello.Machine.Instance ?? $"{status.ProcessId}@{status.StartedAt:O}";
                    ui.Post(() => { if (Interlocked.Read(ref statusEvents) == heardStatus) { Status = status; } });
                }
                Volatile.Write(ref current, new Current(ch, instance));
                var heardUsage = Interlocked.Read(ref usageEvents);
                if (!Subscribes) { }
                else if (hello.Capabilities.Contains(UsageProtocol.Capability))
                {
                    var usage = await ExtraAsync<UsageReport>(ch, UsageProtocol.Subscribe, UsageAsk(hello), ct);
                    if (usage is not null) { ui.Post(() => { if (Interlocked.Read(ref usageEvents) == heardUsage) { Usage = usage; } }); }
                }
                // A host that reports no usage now (usage.publish off) has no figure to keep showing.
                else { ui.Post(() => Usage = UsageReport.Empty); }
                var heardUpdate = Interlocked.Read(ref updateEvents);
                if (Subscribes && hello.Capabilities.Contains(UpdateProtocol.Capability))
                {
                    var update = await ExtraAsync<UpdateState>(ch, UpdateProtocol.Subscribe, null, ct);
                    if (update is not null) { ui.Post(() => { if (Interlocked.Read(ref updateEvents) == heardUpdate) { Update = update; } }); }
                }
                else { ui.Post(() => Update = null); }
                if (Subscribes) { await SubscribeSessionsAsync(ch, hello, ct); }
                ui.Post(() => SetState(LinkState.Connected, null));
                Volatile.Read(ref connected).TrySetResult();
                log($"connected to the host ({hello.Machine.Name}, {hello.Machine.HostVersion})");
                // The host's capabilities change without a new connection (agent-ha-bridge
                // starting or stopping takes answering away or gives it back), and hello is
                // computed afresh each time, so it is asked again when the host says it changed
                // (hello.changed), and now and then besides: every CapabilityRefresh of a host that
                // doesn't say, every ChangesFallback of one that does. The gap and change waits are
                // kept across turns: a fresh one each time would leave the old one to swallow the
                // next signal.
                Task? again = null;
                Task? heard = null;
                while (true)
                {
                    again ??= gaps.WaitAsync(ct);
                    heard ??= changed.WaitAsync(ct);
                    var tick = Task.Delay(announces ? ChangesFallback : CapabilityRefresh, ct);
                    var done = await Task.WhenAny(ch.Closed, again, pump, tick, heard);
                    // Stopped: every wait above completes at once, cancelled. Without this, a gap
                    // wait that won was taken for a gap, and a connection that follows no sessions
                    // (the relay loopback) recovers by doing nothing, so the loop spun a thread at
                    // full speed and DisposeAsync never returned (#129).
                    ct.ThrowIfCancellationRequested();
                    if (done == again)
                    {
                        again = null;
                        await RecoverAsync(ch, hello, ct);
                    }
                    else if (done == heard)
                    {
                        heard = null;
                        await RefreshAsync(ch, ct);
                    }
                    else if (done == tick) { await RefreshAsync(ch, ct); }
                    else { break; }
                }
                log($"connection to the host ended: {ch.CloseReason?.Message}");
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
            // Reconnect: whatever the cancellation surfaced as.
            catch (Exception e) when (mine.IsCancellationRequested && !stopping.IsCancellationRequested && e is not OutOfMemoryException)
            {
                log("host connection: the attempt under way was ended to reconnect");
                ui.Post(() => SetState(LinkState.Connecting, "reconnecting"));
            }
            catch (LocalRefusedException e) { ui.Post(() => SetState(LinkState.Refused, e.Message)); }
            catch (HostNotRunningException e) { ui.Post(() => SetState(LinkState.NotRunning, e.Message)); }
            catch (HostErrorException e) when (Refusal(said))
            {
                refusedNow = e.Message;
                if (refusal != e.Message) { log($"the host refused this device: {e.Message} (asking again in {Describe(RefusedRetryDelay ?? RetryDelay)}, or once something changes)"); }
                ui.Post(() => SetState(LinkState.Refused, e.Message));
            }
            catch (Exception e) when (e is IOException or HostErrorException or JsonException or TimeoutException or OperationCanceledException)
            {
                log($"host connection failed: {e.Message}");
                ui.Post(() => SetState(LinkState.Connecting, e.Message));
            }
            finally
            {
                Volatile.Write(ref resync, null);
                Volatile.Write(ref helloChanged, null);
                Volatile.Write(ref current, null);
                if (ch is not null) { await ch.DisposeAsync(); }
                Interlocked.CompareExchange(ref attempt, null, mine);
                mine.Dispose();
                if (Volatile.Read(ref connected).Task.IsCompleted) { Volatile.Write(ref connected, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)); }
            }
            refusal = refusedNow;
            ui.Post(() =>
            {
                if (State == LinkState.Connected) { SetState(LinkState.Connecting, "the host went away; reconnecting"); }
            });
            try { await WaitAsync(refusedNow is not null, stopping); }
            catch (OperationCanceledException) { break; }
        }
    }

    // A refusal is the host's own answer, not a lost connection: an error in reply to hello (the
    // one op every peer may ask), or to what follows a hello that offered this device nothing to
    // read (no sessions.read: a host that has not approved it, as agent-executor's does). It stays
    // until someone changes something at that machine, so it is asked again only after
    // RefusedRetryDelay or a RetryNow. The protocol has no error code for it (v1-sessions.md
    // "Channel framing"), so it is told by when it came rather than by its words.
    static bool Refusal(HelloResult? said) => said is null || !said.Capabilities.Contains(Protocol.Capabilities.SessionsRead);

    async Task WaitAsync(bool refused, CancellationToken ct)
    {
        if (!refused || RefusedRetryDelay is not { } longer || Volatile.Read(ref poked) == 1)
        {
            await Task.Delay(RetryDelay, Time, ct);
            return;
        }
        var started = Time.GetTimestamp();
        var woken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref wake, woken);
        using var cut = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            // A RetryNow between the attempt's end and the wait's start set poked without a wait to end.
            if (Volatile.Read(ref poked) == 1) { woken.TrySetResult(); }
            var done = await Task.WhenAny(Task.Delay(longer, Time, cut.Token), woken.Task);
            // Ends the long delay's timer when woken, rather than leaving it for minutes.
            cut.Cancel();
            ct.ThrowIfCancellationRequested();
            if (done != woken.Task) { return; }
            var left = RetryDelay - Time.GetElapsedTime(started);
            if (left > TimeSpan.Zero) { await Task.Delay(left, Time, ct); }
        }
        finally { Interlocked.CompareExchange(ref wake, null, woken); }
    }

    static string Describe(TimeSpan delay) => delay >= TimeSpan.FromMinutes(1) ? $"{delay.TotalMinutes:0.#} min" : $"{delay.TotalSeconds:0.#} s";

    // Usage and update state ride along with the sessions: a reading of either the app cannot read
    // is refused, and the connection goes on with the one it had. Thrown, it ended the connection
    // before the sessions were subscribed, and every connection after read the same thing: the app
    // reconnected for good and showed no sessions (#446).
    async Task<T?> ExtraAsync<T>(IClientChannel ch, string op, object? data, CancellationToken ct) where T : class
    {
        try { return await ch.ReadAsync<T>(op, data, ct: ct); }
        catch (MalformedRecordException e)
        {
            Refused(op, e);
            return null;
        }
    }

    async Task SubscribeSessionsAsync(IClientChannel ch, HelloResult hello, CancellationToken ct)
    {
        // The snapshot is marked before the request goes, so no upsert of it is missed. Only a
        // sessions.synced received after the response, on this channel, ends it (SessionStore).
        // Of a host that offers patches, they are asked for, and a list held whole from this run of
        // the host is resumed rather than sent again (#601). A host that offers none is asked
        // exactly as before. The resume point is read here, off the UI thread, and may be a moment
        // old: an older revision is still true, and a list made not whole meanwhile is caught when
        // the resumed snapshot ends (SessionStore.Finish), which then asks again in full.
        var patch = hello.Capabilities.Contains(AgentBridge.Protocol.Capabilities.SessionsPatch);
        var instance = hello.Machine.Instance;
        var request = patch ? new SubscribeRequest { Patch = true, Since = Sessions.ResumeFrom(instance) } : null;
        PostSessions(ch, () => Sessions.BeginSnapshot(ch, patch ? instance : null));
        // Its result is read loosely, only for resumed: read strictly, a result the app could not
        // read ended the connection, and every reconnect asked again (#446).
        var (result, before) = await ch.CallOrderedAsync(Ops.SessionsSubscribe, request, ct: ct);
        var resumed = request?.Since is not null && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("resumed", out var r) && r.ValueKind == JsonValueKind.True;
        var asked = request?.Since is not null;
        PostSessions(ch, () => Sessions.Answered(ch, before, resumed, asked));
    }

    // What the host tells hello.changed of: its read-only reason can change on its own (a host
    // read-only by setting that agent-ha-bridge starts beside), and was dropped when only the
    // capabilities were compared.
    static bool Differs(HelloResult was, HelloResult now) =>
        was.Machine.ReadOnly != now.Machine.ReadOnly || !was.Capabilities.SequenceEqual(now.Capabilities);

    // Whether the host sends hello.changed on this connection (it was asked with every hello), so
    // asking every CapabilityRefresh is not needed. Not this machine's own host (host.*): a refresh
    // there also reads host.status, whose agent-ha-bridge note and restart-required change without
    // hello changing (a host read-only by setting, a config edited by hand), and the pipe is cheap.
    static bool Announces(HelloResult hello) =>
        hello.Capabilities.Contains(AgentBridge.Protocol.Capabilities.HelloChanged) && !hello.Capabilities.Contains(HostOps.Capability);

    // The account's other machines' readings too, where the host keeps them (usage.shared, #172).
    static UsageRequest UsageAsk(HelloResult hello) => new() { Shared = hello.Capabilities.Contains(UsageProtocol.SharedCapability) };

    // Events were dropped, and any of them may have been the host's status or usage, not
    // only sessions: all three are read again, each applied only if no newer event of its
    // kind arrived meanwhile, as when connecting.
    async Task RecoverAsync(IClientChannel ch, HelloResult first, CancellationToken ct)
    {
        if (!Subscribes) { return; }
        // What the host offers now, not at the connection's first hello: updates offered since
        // (agent-ha-bridge stopped there) are read again, and ones withdrawn are not asked for.
        var hello = await ch.HelloAsync(ct, changes: true);
        announces = Announces(hello);
        ui.Post(() =>
        {
            if (Hello is not null && Differs(Hello, hello)) { Hello = hello; Changed?.Invoke(); }
        });
        if (hello.Capabilities.Contains(HostOps.Capability))
        {
            var heardStatus = Interlocked.Read(ref statusEvents);
            var status = await ch.CallAsync<HostStatus>(HostOps.Status, ct: ct);
            ui.Post(() => { if (Interlocked.Read(ref statusEvents) == heardStatus) { Status = status; Changed?.Invoke(); } });
        }
        if (hello.Capabilities.Contains(UsageProtocol.Capability))
        {
            var heardUsage = Interlocked.Read(ref usageEvents);
            var usage = await ExtraAsync<UsageReport>(ch, UsageProtocol.Get, UsageAsk(hello), ct);
            if (usage is not null) { ui.Post(() => { if (Interlocked.Read(ref usageEvents) == heardUsage) { Usage = usage; Changed?.Invoke(); } }); }
        }
        if (hello.Capabilities.Contains(UpdateProtocol.Capability))
        {
            // Subscribed again rather than read: a host first seen without updates was followed
            // only from a refresh, and subscribing where it already is changes nothing there.
            var heardUpdate = Interlocked.Read(ref updateEvents);
            var update = await ExtraAsync<UpdateState>(ch, UpdateProtocol.Subscribe, null, ct);
            if (update is not null) { ui.Post(() => { if (Interlocked.Read(ref updateEvents) == heardUpdate) { Update = update; Changed?.Invoke(); } }); }
        }
        else { ui.Post(() => { if (Update is not null) { Update = null; Changed?.Invoke(); } }); }
        await SubscribeSessionsAsync(ch, hello, ct);
    }
    // What the host offers now, and its status (whose agent-ha-bridge note the host works out
    // when asked rather than announcing). Applied only when something changed.
    async Task RefreshAsync(IClientChannel ch, CancellationToken ct)
    {
        var hello = await ch.HelloAsync(ct, changes: true);
        announces = Announces(hello);
        HostStatus? status = hello.Capabilities.Contains(HostOps.Capability) ? await ch.CallAsync<HostStatus>(HostOps.Status, ct: ct) : null;
        // Updates offered again (agent-ha-bridge stopped there): followed from now. Subscribing
        // again where they already are changes nothing at the host.
        UpdateState? update = null;
        var heardUpdate = Interlocked.Read(ref updateEvents);
        if (Subscribes && hello.Capabilities.Contains(UpdateProtocol.Capability) && Hello?.Capabilities.Contains(UpdateProtocol.Capability) != true)
        {
            update = await ExtraAsync<UpdateState>(ch, UpdateProtocol.Subscribe, null, ct);
        }
        ui.Post(() =>
        {
            var changed = false;
            if (Hello is not null && Differs(Hello, hello)) { Hello = hello; changed = true; }
            if (update is not null && Interlocked.Read(ref updateEvents) == heardUpdate) { Update = update; changed = true; }
            if (status is not null && Status is { } now && (now.AgentHaBridge != status.AgentHaBridge || now.RestartRequired != status.RestartRequired))
            {
                Status = status;
                changed = true;
            }
            if (changed) { Changed?.Invoke(); }
        });
    }

    async Task PumpAsync(IClientChannel ch, SemaphoreSlim gaps, CancellationToken ct)
    {
        long last = 0;
        // Each session as this channel last gave it, the JSON as received and the seq it came with:
        // what a session.patch applies to (v1-sessions.md, Patches). Read and written on the pump
        // alone. A patch's base names one event of this channel, so an entry from an earlier
        // subscription on it is never mistaken for the one a later patch is on.
        var held = new Dictionary<string, (long Seq, JsonObject Json)>(StringComparer.Ordinal);
        try
        {
            await foreach (var ev in ch.Events.ReadAllAsync(ct))
            {
                // A gap means the buffer dropped events while the UI was slow: the sessions
                // are subscribed again, which resends them all. Each connection numbers its
                // events from 1, so a first event above 1 is a gap too.
                // Before anything of the event is applied: the snapshot under way is no longer the
                // whole of anything, so nothing ends it, not even this event when it is the
                // sessions.synced (#427).
                if (ev.Seq != last + 1)
                {
                    // An event numbered again or back (a host that is broken) may carry an older state
                    // than the one held: the list is not whole after it, and is never resumed from.
                    if (ev.Seq <= last) { RefuseOn(ch); }
                    Resubscribe(ch, ev.Seq, gaps);
                }
                last = ev.Seq;
                Apply(ch, ev, held, gaps);
            }
        }
        catch (OperationCanceledException) { }
    }

    // The channel's sessions may be missing changes from at on: they are subscribed again.
    void Resubscribe(IClientChannel ch, long at, SemaphoreSlim gaps)
    {
        PostSessions(ch, () =>
        {
            Sessions.AbandonSnapshot(ch, at);
            Gapped?.Invoke();
        });
        gaps.Release();
    }

    // One message the app cannot read is refused, and the pump goes on (#446). Before, the
    // JsonException ended the pump and the connection with it, and the snapshot after the
    // reconnect sent the same record again: one malformed session kept the app reconnecting for
    // good, its other sessions frozen. Logged once per kind of event, without the record itself.
    void Apply(IClientChannel ch, ChannelEvent ev, Dictionary<string, (long Seq, JsonObject Json)> held, SemaphoreSlim gaps)
    {
        try { ApplyOne(ch, ev, held, gaps); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or NotSupportedException) { Refused(ev.Name, e); }
    }

    // Said once for each kind of message, by the type of failure and where it was, never with the
    // message's text: a converter's message quotes the value it could not read (UtcTimeConverter
    // the time string it was given), and what a host sends is not the log's to keep.
    void Refused(string kind, Exception e)
    {
        if (!refusedKinds.TryAdd(kind, 0)) { return; }
        var where = e is JsonException { Path: { } path } ? $" at {path}" : "";
        log($"refused a {kind} the app could not read ({e.GetType().Name}{where}); any more are refused without a word");
    }

    // A session, from an upsert or a patch applied (revision: the patch's). Applied only while its
    // channel is the one followed: an old channel's pump can still be draining after its
    // replacement's snapshot is whole, and its older state would overwrite the newer one with
    // nothing to resend it, a resumed snapshot sending only what changed since.
    Session Upserted(IClientChannel ch, JsonElement data, long seq, long? revision)
    {
        Session s;
        bool exact;
        try { s = ReceivedLimits.Fit(Received.Read<Session>(data, out exact)); }
        catch (JsonException)
        {
            // A session the app could not read may be the busy one: the list is not
            // the whole word on whether this host may be restarted.
            RefuseOn(ch);
            throw;
        }
        // Its attention (#800), which the protocol's record doesn't carry yet, kept beside it.
        AuthState.Attach(s, data);
        PostSessions(ch, () =>
        {
            Sessions.Upsert(s, exact, ch, seq);
            Sessions.Advance(ch, revision);
        });
        return s;
    }

    bool Followed(IClientChannel ch) => ReferenceEquals(Volatile.Read(ref pumping), ch);

    // Checked when the UI applies the post, not when the pump queues it: reconnect may
    // replace the channel while this action waits on a busy dispatcher (#547).
    void PostSessions(IClientChannel ch, Action apply) => ui.Post(() => { if (Followed(ch)) { apply(); } });

    // Something on that channel was refused: the list is not whole. Not an old channel's, whose
    // events no longer change the list: it would leave the list of the channel that replaced it
    // unsure and never resumed from, with nothing on that channel to ask for it again.
    void RefuseOn(IClientChannel ch) => PostSessions(ch, Sessions.Refuse);

    // Host records are read as the spec says (Received): a field that cannot be read is absent,
    // unless the record is filed or decided on by it. The host's own status, a local-pipe record
    // the app alone reads, stays strict: its process id and start say which host it is.
    void ApplyOne(IClientChannel ch, ChannelEvent ev, Dictionary<string, (long Seq, JsonObject Json)> held, SemaphoreSlim gaps)
    {
        switch (ev.Name)
        {
            case Events.SessionUpsert:
                {
                    // Read first, which refuses one the app can't read (an id that is not text, or
                    // holds a lone surrogate) as before; then kept as received, the JSON and not what
                    // the app made of it, as what a patch on it applies to.
                    var s = Upserted(ch, ev.Data, ev.Seq, revision: null);
                    held[s.Id] = (ev.Seq, JsonObject.Create(ev.Data)!);
                    break;
                }
            case Events.SessionPatch:
                {
                    SessionPatch p;
                    JsonObject json;
                    try
                    {
                        p = Received.Read<SessionPatch>(ev.Data);
                        var had = held.TryGetValue(p.Id, out var h);
                        json = SessionPatches.Apply(had ? h.Json : null, had ? h.Seq : null, p);
                    }
                    catch (Exception e) when (e is JsonException or SessionPatchException or InvalidOperationException)
                    {
                        // Dropped whole, and the list is no longer the host's: asked for again in
                        // full, as after a gap, before anything after it is shown (v1-sessions.md,
                        // A patch the client cannot apply).
                        Refused(ev.Name, e);
                        RefuseOn(ch);
                        Resubscribe(ch, ev.Seq, gaps);
                        break;
                    }
                    held[p.Id] = (ev.Seq, json);
                    // Read exactly as an upsert of the same moment would be.
                    Upserted(ch, JsonSerializer.SerializeToElement(json), ev.Seq, p.Revision);
                    break;
                }
            case Events.SessionRemove:
                {
                    SessionRemoved r;
                    try { r = Received.Read<SessionRemoved>(ev.Data); }
                    catch (JsonException)
                    {
                        // A session still listed may have ended: the list is not the host's whole
                        // list, so nothing resumes from it.
                        RefuseOn(ch);
                        throw;
                    }
                    held.Remove(r.Id);
                    PostSessions(ch, () =>
                    {
                        Sessions.Remove(r.Id);
                        Sessions.Advance(ch, r.Revision);
                    });
                    break;
                }
            case Events.SessionsSynced:
                {
                    // Read like any other record: one the app cannot read ends no snapshot, so it
                    // removes no session and lifts no discovery hold. A resumed one is then asked
                    // for again in full: its live set is what drops the sessions that ended.
                    var seq = ev.Seq;
                    SessionsSynced synced;
                    try { synced = Received.Read<SessionsSynced>(ev.Data); }
                    catch (JsonException)
                    {
                        PostSessions(ch, () => Sessions.RefuseSynced(ch, seq));
                        throw;
                    }
                    PostSessions(ch, () => Sessions.EndSnapshot(synced.DiscoveryHeld, ch, seq, synced.Revision, synced.Live));
                    break;
                }
            case Events.DiscoveryChanged:
                {
                    var d = Received.Read<DiscoveryChanged>(ev.Data);
                    PostSessions(ch, () => Sessions.SetHeld(d.DiscoveryHeld, ch));
                    break;
                }
            case AuthState.CodeEvent or AuthState.DoneEvent:
                {
                    var name = ev.Name;
                    var data = ev.Data.Clone();
                    PostSessions(ch, () => AgentSignIn?.Invoke(name, data));
                    break;
                }
            case UsageProtocol.Update:
                {
                    var u = Received.Read<UsageReport>(ev.Data);
                    Interlocked.Increment(ref usageEvents);
                    ui.Post(() => { Usage = u; Changed?.Invoke(); });
                    break;
                }
            case UpdateProtocol.Event:
                {
                    var up = Received.Read<UpdateState>(ev.Data);
                    Interlocked.Increment(ref updateEvents);
                    ui.Post(() => { Update = up; Changed?.Invoke(); });
                    break;
                }
            // Asked again rather than applied: RefreshAsync reads the status and offered updates
            // with it, as on a tick. One pending is enough, and only for the channel followed now.
            case Events.HelloChanged:
                if (ReferenceEquals(Volatile.Read(ref pumping), ch) && Volatile.Read(ref helloChanged) is { CurrentCount: 0 } signal) { signal.Release(); }
                break;
            case HostOps.StatusEvent when ev.Data.Deserialize<HostStatus>(ProtocolJson.Options) is { } h:
                Interlocked.Increment(ref statusEvents);
                ui.Post(() => { Status = h; Changed?.Invoke(); });
                break;
        }
    }

    void SetState(LinkState state, string? detail)
    {
        State = state;
        StateDetail = detail;
        if (state != LinkState.Connected) { Hello = null; }
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await loop; } catch (Exception) { }
        logged.Writer.TryComplete();
        Task? writing;
        lock (logGate) { writing = logging; }
        if (writing is not null) { try { await writing; } catch (Exception) { } }
    }
}

// The sessions as the host last described them, kept by the snapshot rules of
// sessions.subscribe: the upserts after a subscribe replace the whole list at sessions.synced.
public sealed class SessionStore
{
    readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    HashSet<string>? snapshot;

    public IReadOnlyCollection<Session> All => sessions.Values;
    public Session? Get(string id) => sessions.GetValueOrDefault(id);
    // What holds the host's discovery, while anything does (sessions.synced, discovery.changed).
    public DiscoveryHeld? Held { get; private set; }

    public event Action<Session>? Upserted;
    public event Action<string>? Removed;
    // A snapshot has been read in full: the list is the host's.
    public event Action? Synced;
    public event Action? HeldChanged;

    public void SetHeld(DiscoveryHeld? held, object? on = null)
    {
        // A discovery.changed read after a sessions.synced that still waits on its subscribe's
        // answer is the newer word: finishing that snapshot later must not put the synced's older
        // hold back, which hid a newly reported hold until the next change.
        if (ReferenceEquals(on, snapshotOn) && early is { } e) { early = e with { Held = held }; }
        ApplyHeld(held);
    }

    void ApplyHeld(DiscoveryHeld? held)
    {
        if (held?.Reason == Held?.Reason && (held?.Agents ?? []).SequenceEqual(Held?.Agents ?? [])) { return; }
        Held = held;
        HeldChanged?.Invoke();
    }

    // Which subscription the snapshot under way belongs to: the channel it was asked on, and, once
    // its response has come, the seq of the last event received before it. Only a sessions.synced
    // on that channel with a higher seq ends it (#427). A host stops one subscription before
    // answering the next, so a synced before the response is an earlier subscription's, which
    // ended this one early with half its sessions; one from an earlier channel ends nothing now.
    object? snapshotOn;
    long? snapshotAfter;
    // A synced on the right channel that was read before the response was known (Unreadable: one
    // the app could not read, which a resumed snapshot cannot end with).
    readonly record struct Early(long Seq, DiscoveryHeld? Held, long Revision, IReadOnlyList<string>? Live, bool Unreadable = false);
    Early? early;
    // Upserts on that channel read before the response was known: counted towards the snapshot
    // once it is, if they came after it. An earlier subscription's upsert counted, and a session
    // that ended before the new snapshot was taken stayed on the list for good.
    List<(long Seq, string Id)>? earlyIds;
    // The run of the host the snapshot under way is asked of (hello's machine.instance; null for a
    // host that offers no patches), and whether its subscribe resumed: only the sessions changed
    // since follow, and its synced's live set says which are left.
    string? snapshotInstance;
    bool snapshotResumed;
    // Whether its subscribe gave since: a synced with live then, its answer not saying it resumed,
    // can't be told for a full snapshot's, which would drop every session it didn't send again.
    bool snapshotAsked;

    // Where a subscribe can resume from (v1-sessions.md, Resuming without a snapshot): the run of
    // the host the list came from and the revision it is whole at, while it is whole. Written on
    // the UI thread, read by the connection as it subscribes, so held as one record.
    sealed record ResumePoint(string? Instance, long Revision, bool Whole);
    ResumePoint? resume;
    // The channel whose patches and removals move the revision: the one whose synced made the list
    // whole, until a gap in its events. What came before a gap all arrived, so the revision held
    // then is still true; what came after it may not have.
    object? tracking;

    public void BeginSnapshot() => BeginSnapshot(null);

    public void BeginSnapshot(object? on) => BeginSnapshot(on, null);

    // on: the channel the subscribe goes on; null for a snapshot whose synced is taken whenever it
    // comes. instance: the run of the host it is asked of, to resume from later.
    public void BeginSnapshot(object? on, string? instance)
    {
        snapshot = new HashSet<string>(StringComparer.Ordinal);
        snapshotOn = on;
        snapshotAfter = on is null ? long.MinValue : null;
        early = null;
        earlyIds = null;
        snapshotInstance = instance;
        snapshotResumed = false;
        snapshotAsked = false;
        tracking = null;
        Complete = false;
        refused = false;
        Snapshots++;
        SnapshotBegun?.Invoke();
    }

    // sessions.subscribe's since for that run of the host, or null when the list is not whole
    // from it. Read on any thread.
    public SubscribeSince? ResumeFrom(string? instance) =>
        Volatile.Read(ref resume) is { Whole: true, Instance: { } i, Revision: >= 0 and var r } && instance is not null && i == instance
            ? new SubscribeSince { Instance = i, Revision = r }
            : null;

    // A patch or removal on that channel brought the list to revision.
    public void Advance(object on, long? revision)
    {
        if (revision is not { } at || tracking is null || !ReferenceEquals(on, tracking) || resume is not { Whole: true } r) { return; }
        Volatile.Write(ref resume, r with { Revision = at });
    }

    // A resumed snapshot that can't be whole was dropped: the connection asks for the sessions again
    // on that channel, and, the list no longer whole, in full.
    public event Action<object?>? SnapshotRefused;

    // The subscribe on that channel was answered after the event numbered before (-1: none).
    public void Answered(object on, long before) => Answered(on, before, resumed: false);

    public void Answered(object on, long before, bool resumed) => Answered(on, before, resumed, asked: resumed);

    // resumed: the host said so, to a subscribe that gave since (asked).
    public void Answered(object on, long before, bool resumed, bool asked)
    {
        if (snapshot is null || !ReferenceEquals(on, snapshotOn) || snapshotAfter is not null) { return; }
        snapshotAfter = before;
        snapshotResumed = resumed;
        snapshotAsked = asked;
        foreach (var (seq, id) in earlyIds ?? []) { if (seq > before) { snapshot.Add(id); } }
        earlyIds = null;
        if (early is { } e)
        {
            early = null;
            if (e.Seq <= before) { }
            else if (e.Unreadable) { if (resumed) { Reject(); } }
            else { Finish(e.Held, e.Revision, e.Live); }
        }
    }

    // Events were lost on that channel at seq: the snapshot under way, if its response came before
    // them or is not known yet, can't be whole, so nothing ends it. The next subscribe begins
    // another. Lost before its response, they were the earlier subscription's, which it covers.
    public void AbandonSnapshot(object on, long seq)
    {
        // The revision stops moving with what this channel sends after the gap.
        if (ReferenceEquals(on, tracking)) { tracking = null; }
        if (snapshot is null || !ReferenceEquals(on, snapshotOn) || seq <= snapshotAfter) { return; }
        snapshot = null;
        early = null;
        earlyIds = null;
    }

    // How many snapshots have begun: one that began after a moment (Machine.Unconfirm) and has
    // completed is the host's word on the list since then, not what was kept from before it.
    public long Snapshots { get; private set; }
    public event Action? SnapshotBegun;

    // Whether the list is the host's whole snapshot: false until a subscribe's sessions.synced, and
    // again from each new subscribe (a reconnect, a gap) until its own. An incomplete list may be
    // missing a busy session.
    public bool Complete { get; private set; }

    // Sessions read past a field the app could not read, and whether a session.upsert has been
    // refused since the snapshot began (Received, #446). What they show is the spec's reading,
    // not evidence: a session whose status was filled in as idle may be working.
    readonly HashSet<string> unsure = new(StringComparer.Ordinal);
    bool refused;

    // Whether anything in the list was read past or refused: the list is not the host's whole word
    // on which sessions are busy.
    public bool Unsure => refused || unsure.Count > 0;

    public void Upsert(Session s) => Upsert(s, exact: true);

    public void Upsert(Session s, bool exact) => Upsert(s, exact, null, 0);

    // One read on a channel at seq: it counts towards the snapshot under way only if it came after
    // that snapshot's subscribe was answered, on its channel (on: null counts it whatever).
    public void Upsert(Session s, bool exact, object? on, long seq)
    {
        if (snapshot is not null)
        {
            if (on is null || snapshotOn is null) { snapshot.Add(s.Id); }
            else if (ReferenceEquals(on, snapshotOn))
            {
                if (snapshotAfter is not { } after) { (earlyIds ??= []).Add((seq, s.Id)); }
                else if (seq > after) { snapshot.Add(s.Id); }
            }
        }
        sessions[s.Id] = s;
        if (exact) { unsure.Remove(s.Id); } else { unsure.Add(s.Id); }
        Upserted?.Invoke(s);
    }

    // A session.upsert the app could not read, or a session.patch or session.remove it could not
    // read or apply: the list is not the host's whole word on which sessions are busy, nor whole to
    // resume from until a full snapshot.
    public void Refuse()
    {
        refused = true;
        if (resume is { Whole: true } r) { Volatile.Write(ref resume, r with { Whole = false }); }
    }

    public void Remove(string id)
    {
        unsure.Remove(id);
        if (sessions.Remove(id)) { Removed?.Invoke(id); }
    }

    public void EndSnapshot() => EndSnapshot(null);

    public void EndSnapshot(DiscoveryHeld? held)
    {
        if (snapshot is null) { return; }
        Finish(held, null, null);
    }

    // A sessions.synced read on a channel at seq.
    public void EndSnapshot(DiscoveryHeld? held, object on, long seq) => EndSnapshot(held, on, seq, 0, null);

    // With the synced's revision, and its live set, which ends a resumed snapshot.
    public void EndSnapshot(DiscoveryHeld? held, object on, long seq, long revision, IReadOnlyList<string>? live)
    {
        if (snapshot is null || !ReferenceEquals(on, snapshotOn)) { return; }
        if (snapshotAfter is not { } after)
        {
            if (early is not { } e || seq > e.Seq) { early = new(seq, held, revision, live); }
            return;
        }
        if (seq > after) { Finish(held, revision, live); }
    }

    // A sessions.synced on that channel at seq the app could not read. It ends no snapshot, as
    // before; a resumed one can't be whole without its live set, so it is asked for again in full.
    public void RefuseSynced(object on, long seq)
    {
        if (snapshot is null || !ReferenceEquals(on, snapshotOn)) { return; }
        if (snapshotAfter is not { } after)
        {
            if (early is not { } e || seq > e.Seq) { early = new(seq, null, 0, null, Unreadable: true); }
            return;
        }
        if (seq > after && snapshotResumed) { Reject(); }
    }

    // The snapshot under way can't be whole: dropped, and the list not resumed from again.
    void Reject()
    {
        var on = snapshotOn;
        snapshot = null;
        early = null;
        earlyIds = null;
        Refuse();
        SnapshotRefused?.Invoke(on);
    }

    void Finish(DiscoveryHeld? held, long? revision, IReadOnlyList<string>? live)
    {
        if (snapshot is not { } seen) { return; }
        if (!snapshotResumed && snapshotAsked && live is not null)
        {
            Reject();
            return;
        }
        if (snapshotResumed)
        {
            // Every live id must be one sent since the response or held from before, and the list
            // must have stayed whole since resuming was asked for: a session the host thinks the
            // app has, but it dropped, would otherwise be missing for good (v1-sessions.md, When
            // the list is whole again).
            if (live is null || refused || resume is not { Whole: true } || live.Any(id => !seen.Contains(id) && !sessions.ContainsKey(id)))
            {
                Reject();
                return;
            }
            // The live ids, and any session upserted on this channel after the response: one that
            // started after the synced, read before the subscribe's answer reached the UI thread
            // (early), is not in live, and dropping it left it missing until it changed again.
            seen = new HashSet<string>(live.Concat(seen), StringComparer.Ordinal);
        }
        snapshot = null;
        Complete = true;
        foreach (var id in sessions.Keys.Where(id => !seen.Contains(id)).ToList()) { Remove(id); }
        if (revision is { } at)
        {
            Volatile.Write(ref resume, new ResumePoint(snapshotInstance, at, Whole: !refused));
            tracking = snapshotOn;
        }
        ApplyHeld(held);
        Synced?.Invoke();
    }
}
