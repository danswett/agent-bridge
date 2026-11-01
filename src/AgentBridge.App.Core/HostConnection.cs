using System.Text.Json;
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
public sealed class HostRestartedException() : Exception("the Bridge Host restarted");

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

    sealed record Current(IClientChannel Channel, string? Instance);

    public HostConnection(Func<CancellationToken, Task<LocalChannel>> connect, IUiDispatcher ui, Action<string>? log = null)
        : this(async ct => (IClientChannel)await connect(ct), ui, log) { }

    // Any machine's host: this one's over the pipe, another's over a channel through the relay.
    public HostConnection(Func<CancellationToken, Task<IClientChannel>> connect, IUiDispatcher ui, Action<string>? log = null)
    {
        this.connect = connect;
        this.ui = ui;
        this.log = log ?? (_ => { });
    }

    public static HostConnection ForProfile(string profileFolder, IUiDispatcher ui, Action<string>? log = null) =>
        new(ct => LocalClient.ConnectAsync(LocalEndpoint.For(profileFolder), TimeSpan.FromSeconds(2), ct), ui, log);

    // How long to wait between attempts while the host is not there. Tests shorten it.
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);
    // How often an open connection asks the host again what it offers. Tests shorten it.
    public TimeSpan CapabilityRefresh { get; init; } = TimeSpan.FromSeconds(10);
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

    public bool Can(string capability) => Capabilities.Contains(capability);

    public void Start() => loop = Task.Run(() => RunAsync(stop.Token));

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
    public Task<JsonElement> CallAsync(string op, object? data, TimeSpan? timeout, bool knownHost, string? onlyTo, out string? instance, CancellationToken ct = default)
    {
        var now = Volatile.Read(ref current);
        if (now is null || !now.Channel.IsOpen) { throw new HostNotRunningException("not connected to the Bridge Host"); }
        if (knownHost && now.Instance is null) { throw new HostNotRunningException("still connecting to the Bridge Host"); }
        if (onlyTo is not null && now.Instance != onlyTo) { throw new HostRestartedException(); }
        instance = now.Instance;
        return now.Channel.CallAsync(op, data, timeout, ct);
    }

    public async Task<T> CallAsync<T>(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default) =>
        (await CallAsync(op, data, timeout, ct)).Deserialize<T>(ProtocolJson.Options) ?? throw new HostErrorException($"{op} returned nothing");

    async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            IClientChannel? ch = null;
            try
            {
                ch = await connect(ct);
                var hello = await ch.HelloAsync(ct);
                // A gap in this connection's events: its own signal, so a wait abandoned when the
                // connection ends cannot swallow the next connection's.
                // Not disposed: the pump may still release it as the connection ends, and a
                // SemaphoreSlim whose wait handle was never asked for holds nothing to free.
                var gaps = new SemaphoreSlim(0);
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
                    var usage = await ch.CallAsync<UsageReport>(UsageProtocol.Subscribe, UsageAsk(hello), ct: ct);
                    ui.Post(() => { if (Interlocked.Read(ref usageEvents) == heardUsage) { Usage = usage; } });
                }
                // A host that reports no usage now (usage.publish off) has no figure to keep showing.
                else { ui.Post(() => Usage = UsageReport.Empty); }
                var heardUpdate = Interlocked.Read(ref updateEvents);
                if (Subscribes && hello.Capabilities.Contains(UpdateProtocol.Capability))
                {
                    var update = await ch.CallAsync<UpdateState>(UpdateProtocol.Subscribe, ct: ct);
                    ui.Post(() => { if (Interlocked.Read(ref updateEvents) == heardUpdate) { Update = update; } });
                }
                else { ui.Post(() => Update = null); }
                if (Subscribes) { await SubscribeSessionsAsync(ch, ct); }
                ui.Post(() => SetState(LinkState.Connected, null));
                Volatile.Read(ref connected).TrySetResult();
                log($"connected to the host ({hello.Machine.Name}, {hello.Machine.HostVersion})");
                // The host's capabilities change without a new connection (agent-ha-bridge
                // starting or stopping takes answering away or gives it back), and hello is
                // computed afresh each time, so it is asked again now and then. The gap wait is
                // kept across turns: a fresh one each time would leave the old one to swallow
                // the next gap.
                Task? again = null;
                while (true)
                {
                    again ??= gaps.WaitAsync(ct);
                    var tick = Task.Delay(CapabilityRefresh, ct);
                    var done = await Task.WhenAny(ch.Closed, again, pump, tick);
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
                    else if (done == tick) { await RefreshAsync(ch, ct); }
                    else { break; }
                }
                log($"connection to the host ended: {ch.CloseReason?.Message}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (LocalRefusedException e) { ui.Post(() => SetState(LinkState.Refused, e.Message)); }
            catch (HostNotRunningException e) { ui.Post(() => SetState(LinkState.NotRunning, e.Message)); }
            catch (Exception e) when (e is IOException or HostErrorException or JsonException or TimeoutException or OperationCanceledException)
            {
                log($"host connection failed: {e.Message}");
                ui.Post(() => SetState(LinkState.Connecting, e.Message));
            }
            finally
            {
                Volatile.Write(ref current, null);
                if (ch is not null) { await ch.DisposeAsync(); }
                if (Volatile.Read(ref connected).Task.IsCompleted) { Volatile.Write(ref connected, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)); }
            }
            ui.Post(() =>
            {
                if (State == LinkState.Connected) { SetState(LinkState.Connecting, "the host went away; reconnecting"); }
            });
            try { await Task.Delay(RetryDelay, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    async Task SubscribeSessionsAsync(IClientChannel ch, CancellationToken ct)
    {
        // The snapshot is marked before the request goes, so no upsert of it is missed.
        ui.Post(Sessions.BeginSnapshot);
        await ch.CallAsync<SubscribeResult>(Ops.SessionsSubscribe, ct: ct);
    }

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
        var hello = await ch.HelloAsync(ct);
        ui.Post(() =>
        {
            if (Hello is not null && !Hello.Capabilities.SequenceEqual(hello.Capabilities)) { Hello = hello; Changed?.Invoke(); }
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
            var usage = await ch.CallAsync<UsageReport>(UsageProtocol.Get, UsageAsk(hello), ct: ct);
            ui.Post(() => { if (Interlocked.Read(ref usageEvents) == heardUsage) { Usage = usage; Changed?.Invoke(); } });
        }
        if (hello.Capabilities.Contains(UpdateProtocol.Capability))
        {
            // Subscribed again rather than read: a host first seen without updates was followed
            // only from a refresh, and subscribing where it already is changes nothing there.
            var heardUpdate = Interlocked.Read(ref updateEvents);
            var update = await ch.CallAsync<UpdateState>(UpdateProtocol.Subscribe, ct: ct);
            ui.Post(() => { if (Interlocked.Read(ref updateEvents) == heardUpdate) { Update = update; Changed?.Invoke(); } });
        }
        else { ui.Post(() => { if (Update is not null) { Update = null; Changed?.Invoke(); } }); }
        await SubscribeSessionsAsync(ch, ct);
    }
    // What the host offers now, and its status (whose agent-ha-bridge note the host works out
    // when asked rather than announcing). Applied only when something changed.
    async Task RefreshAsync(IClientChannel ch, CancellationToken ct)
    {
        var hello = await ch.HelloAsync(ct);
        HostStatus? status = hello.Capabilities.Contains(HostOps.Capability) ? await ch.CallAsync<HostStatus>(HostOps.Status, ct: ct) : null;
        // Updates offered again (agent-ha-bridge stopped there): followed from now. Subscribing
        // again where they already are changes nothing at the host.
        UpdateState? update = null;
        var heardUpdate = Interlocked.Read(ref updateEvents);
        if (Subscribes && hello.Capabilities.Contains(UpdateProtocol.Capability) && Hello?.Capabilities.Contains(UpdateProtocol.Capability) != true)
        {
            update = await ch.CallAsync<UpdateState>(UpdateProtocol.Subscribe, ct: ct);
        }
        ui.Post(() =>
        {
            var changed = false;
            if (Hello is not null && !Hello.Capabilities.SequenceEqual(hello.Capabilities)) { Hello = hello; changed = true; }
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
        try
        {
            await foreach (var ev in ch.Events.ReadAllAsync(ct))
            {
                // A gap means the buffer dropped events while the UI was slow: the sessions
                // are subscribed again, which resends them all. Each connection numbers its
                // events from 1, so a first event above 1 is a gap too.
                if (ev.Seq != last + 1) { gaps.Release(); }
                last = ev.Seq;
                Apply(ev);
            }
        }
        catch (OperationCanceledException) { }
    }

    void Apply(ChannelEvent ev)
    {
        switch (ev.Name)
        {
            case Events.SessionUpsert when ev.Data.Deserialize<Session>(ProtocolJson.Options) is { } s:
                ui.Post(() => Sessions.Upsert(s));
                break;
            case Events.SessionRemove when ev.Data.Deserialize<SessionRemoved>(ProtocolJson.Options) is { } r:
                ui.Post(() => Sessions.Remove(r.Id));
                break;
            case Events.SessionsSynced:
                {
                    var held = ev.Data.ValueKind == JsonValueKind.Object ? ev.Data.Deserialize<SessionsSynced>(ProtocolJson.Options)?.DiscoveryHeld : null;
                    ui.Post(() => Sessions.EndSnapshot(held));
                    break;
                }
            case Events.DiscoveryChanged when ev.Data.Deserialize<DiscoveryChanged>(ProtocolJson.Options) is { } d:
                ui.Post(() => Sessions.SetHeld(d.DiscoveryHeld));
                break;
            case UsageProtocol.Update when ev.Data.Deserialize<UsageReport>(ProtocolJson.Options) is { } u:
                Interlocked.Increment(ref usageEvents);
                ui.Post(() => { Usage = u; Changed?.Invoke(); });
                break;
            case UpdateProtocol.Event when ev.Data.Deserialize<UpdateState>(ProtocolJson.Options) is { } up:
                Interlocked.Increment(ref updateEvents);
                ui.Post(() => { Update = up; Changed?.Invoke(); });
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

    public void SetHeld(DiscoveryHeld? held)
    {
        if (held?.Reason == Held?.Reason && (held?.Agents ?? []).SequenceEqual(Held?.Agents ?? [])) { return; }
        Held = held;
        HeldChanged?.Invoke();
    }

    public void BeginSnapshot()
    {
        snapshot = new HashSet<string>(StringComparer.Ordinal);
        Complete = false;
    }

    // Whether the list is the host's whole snapshot: false until a subscribe's sessions.synced, and
    // again from each new subscribe (a reconnect, a gap) until its own. An incomplete list may be
    // missing a busy session.
    public bool Complete { get; private set; }

    public void Upsert(Session s)
    {
        snapshot?.Add(s.Id);
        sessions[s.Id] = s;
        Upserted?.Invoke(s);
    }

    public void Remove(string id)
    {
        if (sessions.Remove(id)) { Removed?.Invoke(id); }
    }

    public void EndSnapshot() => EndSnapshot(null);

    public void EndSnapshot(DiscoveryHeld? held)
    {
        if (snapshot is not { } seen) { return; }
        snapshot = null;
        Complete = true;
        foreach (var id in sessions.Keys.Where(id => !seen.Contains(id)).ToList()) { Remove(id); }
        SetHeld(held);
        Synced?.Invoke();
    }
}
