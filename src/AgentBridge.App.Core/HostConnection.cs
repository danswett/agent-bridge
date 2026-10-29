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
public sealed class HostConnection : IAsyncDisposable
{
    readonly Func<CancellationToken, Task<LocalChannel>> connect;
    readonly IUiDispatcher ui;
    readonly Action<string> log;
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim resubscribe = new(0);
    TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task loop = Task.CompletedTask;
    LocalChannel? channel;
    long statusEvents;
    long usageEvents;

    public HostConnection(Func<CancellationToken, Task<LocalChannel>> connect, IUiDispatcher ui, Action<string>? log = null)
    {
        this.connect = connect;
        this.ui = ui;
        this.log = log ?? (_ => { });
    }

    public static HostConnection ForProfile(string profileFolder, IUiDispatcher ui, Action<string>? log = null) =>
        new(ct => LocalClient.ConnectAsync(LocalEndpoint.For(profileFolder), TimeSpan.FromSeconds(2), ct), ui, log);

    // How long to wait between attempts while the host is not there. Tests shorten it.
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    // Read on the UI thread.
    public LinkState State { get; private set; } = LinkState.Connecting;
    public string? StateDetail { get; private set; }
    public HelloResult? Hello { get; private set; }
    public IReadOnlyList<string> Capabilities => Hello?.Capabilities ?? [];
    public HostStatus? Status { get; private set; }
    public UsageReport Usage { get; private set; } = UsageReport.Empty;
    public SessionStore Sessions { get; } = new();

    // Raised on the UI thread: the link, hello, status or usage changed.
    public event Action? Changed;

    public bool Can(string capability) => Capabilities.Contains(capability);

    public void Start() => loop = Task.Run(() => RunAsync(stop.Token));

    // Waits until there is a connection, for those that have something to send.
    public Task WhenConnectedAsync(CancellationToken ct = default) => Volatile.Read(ref connected).Task.WaitAsync(ct);

    // Sends a request on the current connection. Throws HostNotRunningException without one,
    // IOException when the connection goes under it, HostErrorException when the host fails it.
    public Task<JsonElement> CallAsync(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var ch = Volatile.Read(ref channel);
        if (ch is null || !ch.IsOpen) { throw new HostNotRunningException("not connected to the Bridge Host"); }
        return ch.CallAsync(op, data, timeout, ct);
    }

    public async Task<T> CallAsync<T>(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default) =>
        (await CallAsync(op, data, timeout, ct)).Deserialize<T>(ProtocolJson.Options) ?? throw new HostErrorException($"{op} returned nothing");

    async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            LocalChannel? ch = null;
            try
            {
                ch = await connect(ct);
                var hello = await ch.HelloAsync(ct);
                var pump = Task.Run(() => PumpAsync(ch, ct), ct);
                Volatile.Write(ref channel, ch);
                ui.Post(() => Hello = hello);
                // An event can overtake the subscribe's result on its way to the UI thread (they
                // arrive on different threads); the result is applied only if no event came.
                var heard = Interlocked.Read(ref statusEvents);
                if (hello.Capabilities.Contains(HostOps.Capability))
                {
                    var status = await ch.CallAsync<HostStatus>(HostOps.Subscribe, ct: ct);
                    ui.Post(() => { if (Interlocked.Read(ref statusEvents) == heard) { Status = status; } });
                }
                heard = Interlocked.Read(ref usageEvents);
                if (hello.Capabilities.Contains(UsageProtocol.Capability))
                {
                    var usage = await ch.CallAsync<UsageReport>(UsageProtocol.Subscribe, ct: ct);
                    ui.Post(() => { if (Interlocked.Read(ref usageEvents) == heard) { Usage = usage; } });
                }
                await SubscribeSessionsAsync(ch, ct);
                ui.Post(() => SetState(LinkState.Connected, null));
                Volatile.Read(ref connected).TrySetResult();
                log($"connected to the host ({hello.Machine.Name}, {hello.Machine.HostVersion})");
                while (true)
                {
                    var closed = ch.Closed;
                    var again = resubscribe.WaitAsync(ct);
                    if (await Task.WhenAny(closed, again, pump) != again) { break; }
                    await SubscribeSessionsAsync(ch, ct);
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
                Volatile.Write(ref channel, null);
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

    async Task SubscribeSessionsAsync(LocalChannel ch, CancellationToken ct)
    {
        // The snapshot is marked before the request goes, so no upsert of it is missed.
        ui.Post(Sessions.BeginSnapshot);
        await ch.CallAsync<SubscribeResult>(Ops.SessionsSubscribe, ct: ct);
    }

    async Task PumpAsync(LocalChannel ch, CancellationToken ct)
    {
        long last = 0;
        try
        {
            await foreach (var ev in ch.Events.ReadAllAsync(ct))
            {
                // A gap means the buffer dropped events while the UI was slow: the sessions
                // are subscribed again, which resends them all.
                if (last != 0 && ev.Seq != last + 1) { resubscribe.Release(); }
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
                ui.Post(Sessions.EndSnapshot);
                break;
            case UsageProtocol.Update when ev.Data.Deserialize<UsageReport>(ProtocolJson.Options) is { } u:
                Interlocked.Increment(ref usageEvents);
                ui.Post(() => { Usage = u; Changed?.Invoke(); });
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

    public event Action<Session>? Upserted;
    public event Action<string>? Removed;
    // A snapshot has been read in full: the list is the host's.
    public event Action? Synced;

    public void BeginSnapshot() => snapshot = new HashSet<string>(StringComparer.Ordinal);

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

    public void EndSnapshot()
    {
        if (snapshot is not { } seen) { return; }
        snapshot = null;
        foreach (var id in sessions.Keys.Where(id => !seen.Contains(id)).ToList()) { Remove(id); }
        Synced?.Invoke();
    }
}
