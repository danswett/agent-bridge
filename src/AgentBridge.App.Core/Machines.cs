using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.App.Core;

// One machine the app shows: this one, over the local pipe, or another of the account's,
// over a channel through the relay. Everything a card does goes to its own machine's host.
public sealed class Machine : IAsyncDisposable
{
    public const string LocalKey = "local";

    public Machine(string key, HostConnection host, CardContext cards, ToastPlanner? toasts = null)
    {
        Key = key;
        Host = host;
        Cards = cards;
        Toasts = toasts;
    }

    // "local" for this machine; another machine's relay device id.
    public string Key { get; }
    public bool IsLocal => Key == LocalKey;
    public HostConnection Host { get; }
    public CardContext Cards { get; }
    public Deliveries Deliveries => Cards.Deliveries;
    public ToastPlanner? Toasts { get; }
    // What the relay's presence calls it, until its host says hello.
    public string? PresenceName { get; set; }

    public string Name => Host.Hello?.Machine.Name ?? PresenceName ?? (IsLocal ? Environment.MachineName : "Another machine");

    public ValueTask DisposeAsync() => IsLocal ? ValueTask.CompletedTask : Host.DisposeAsync();
}

// Elsewhere: this app is open in another of the person's Windows sessions, which holds the
// relay link (the same device key can be connected once).
public enum FleetState { Off, SignedOut, Connecting, Connected, Refused, Elsewhere }

// A host the relay says is online for the account.
public sealed record FleetHost(string Id, string Name);

// The app's way to the account's other machines: the relay's presence, and a channel to any
// host in it. The relay in the app; a stand-in in tests and the demo.
public interface IFleetLink : IAsyncDisposable
{
    FleetState State { get; }
    string? Detail { get; }
    IReadOnlyList<FleetHost> Hosts { get; }
    // Changes when the account (or relay) the machines come from does: whatever was shown for
    // the one before goes, even while the new one is still connecting.
    int Account => 0;
    // Raised on any thread when the state or the hosts change.
    event Action? Changed;
    void Start();
    // Throws HostNotRunningException when the host cannot be reached now; the caller retries.
    Task<IClientChannel> OpenAsync(string hostId, CancellationToken ct);
    // The relay's blob store, for a reply's files over 256 KiB to these machines; none in a stand-in.
    IBlobStore? Blobs => null;
    // Session transfer (Transfers): what the account's machines other than target hand on, and a
    // command this app signs for one of them. None in a stand-in.
    Task<IReadOnlyList<TransferSource>> TransferSourcesAsync(string target, CancellationToken ct) => Task.FromResult<IReadOnlyList<TransferSource>>([]);
    SignedCommand? SignTransfer(TransferSource from, string session, string to) => null;
}

// The account's other machines, kept in step with the relay's presence: a machine for each
// host online, other than this machine's own (which the app reaches over its pipe), each with
// a connection that subscribes as the app's own does. A machine whose host leaves goes, with
// its cards; while the relay connection itself is down, every machine stays as it was and
// reconnects when it is back.
public sealed class Fleet : IAsyncDisposable
{
    readonly IFleetLink link;
    readonly MainViewModel main;
    readonly IUiDispatcher ui;
    readonly Func<FleetHost, Machine> create;
    readonly Dictionary<string, Machine> machines = new(StringComparer.Ordinal);
    bool disposed;
    int account;

    public Fleet(IFleetLink link, MainViewModel main, IUiDispatcher ui, Func<FleetHost, Machine> create)
    {
        this.link = link;
        this.main = main;
        this.ui = ui;
        this.create = create;
        link.Changed += () => ui.Post(Sync);
        main.LocalChanged += Sync;
        main.AttachFleet(this);
    }

    public FleetState State => link.State;
    public string? Detail => link.Detail;
    public int Count => machines.Count;

    // Raised on the UI thread when the link's state changes.
    public event Action? Changed;

    // What the Sessions page says about the other machines, when there is something to say.
    // Nothing while this machine's own host is not there: the banner already says so, and the
    // account comes from it.
    public string? Note => !main.Connected ? null : link.State switch
    {
        FleetState.Off => null,
        FleetState.SignedOut => "Your other machines appear here once this machine's Bridge Host is signed in to the relay (Settings).",
        FleetState.Connecting => $"Connecting to your other machines…{(Detail is { Length: > 0 } d ? $" ({d})" : "")}",
        FleetState.Refused => $"The relay refused this app: {Detail}. Sign this machine's Bridge Host in again in Settings.",
        FleetState.Elsewhere => Detail,
        _ => null,
    };

    public void Start() => link.Start();

    public Task<IReadOnlyList<TransferSource>> TransferSourcesAsync(string target, CancellationToken ct) => link.TransferSourcesAsync(target, ct);
    public SignedCommand? SignTransfer(TransferSource from, string session, string to) => link.SignTransfer(from, session, to);

    // This machine's own host reached through the relay, as a device reaches it, for what the pipe
    // does not carry: a session transfer is only ever launched on a channel through the relay
    // (v1-transfer.md), whose peer is the device that signed it. Made when first needed, and only
    // for sending: it follows no sessions, which the pipe shows. Null while this machine's id or
    // the relay is not known.
    Machine? loopback;
    string? loopbackFor;

    // Raised on the UI thread when the loopback's connection changes (what it offers, chiefly).
    public event Action? LoopbackChanged;

    // The loopback if made, without making it.
    public Machine? CurrentLoopback => loopback;

    public Machine? Loopback()
    {
        if (disposed || main.LocalMachineId is not { } me || link.State != FleetState.Connected) { return null; }
        if (loopback is not null && loopbackFor == me) { return loopback; }
        DropLoopback();
        var conn = new HostConnection(ct => link.OpenAsync(me, ct), ui) { Subscribes = false, RetryDelay = TimeSpan.FromSeconds(5) };
        conn.Changed += () => LoopbackChanged?.Invoke();
        loopback = new Machine(Machine.LocalKey, conn, new CardContext { Deliveries = new Deliveries(conn, ui), Folds = new FoldStore(null), Capabilities = () => conn.Capabilities, Host = conn });
        loopbackFor = me;
        conn.Start();
        return loopback;
    }

    void DropLoopback()
    {
        if (loopback is not { } was) { return; }
        loopback = null;
        loopbackFor = null;
        _ = was.Host.DisposeAsync().AsTask();
        LoopbackChanged?.Invoke();
    }

    // On the UI thread.
    void Sync()
    {
        if (disposed) { return; }
        Changed?.Invoke();
        // Without this machine's own id the list could take its host for another machine's.
        if (main.LocalMachineId is not { } me) { return; }
        // One consistent reading of the link: its account and state, the hosts, and the account and
        // state again. The link changes them from its own thread (connecting, dropping, switching),
        // each change announced, so a pass that saw one under it does nothing and the next pass,
        // already on its way, reads again. Otherwise an empty list read just before a reconnect
        // published its presence would be taken, with the state read after, for every machine gone.
        var now = link.Account;
        var state = link.State;
        var hosts = link.Hosts.Where(h => h.Id != me).ToDictionary(h => h.Id, StringComparer.Ordinal);
        if (link.Account != now || link.State != state) { return; }
        foreach (var (id, h) in hosts)
        {
            if (machines.TryGetValue(id, out var known)) { known.PresenceName = h.Name; continue; }
            var m = create(h);
            m.PresenceName = h.Name;
            machines[id] = m;
            main.Add(m);
            m.Host.Start();
        }
        // Only a relay connection that is down for now, to the same account, keeps every machine as
        // it was. Otherwise the list is the answer: presence while connected, and none when there is
        // no account to find them through (signed out, refused), the account is another one now, or
        // another copy of the app shows them instead.
        var switched = now != account;
        account = now;
        if (switched) { DropLoopback(); }
        if (state == FleetState.Connecting && !switched) { return; }
        foreach (var id in machines.Keys.Where(id => !hosts.ContainsKey(id)).ToList())
        {
            machines.Remove(id, out var gone);
            main.Remove(gone!.Key);
            _ = gone.DisposeAsync().AsTask();
        }
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        if (loopback is { } lb) { loopback = null; await lb.Host.DisposeAsync(); }
        foreach (var m in machines.Values) { await m.DisposeAsync(); }
        machines.Clear();
        await link.DisposeAsync();
    }
}

// When the relay link should look at this machine's host's account again: the host signed in
// to another account or relay, came back, or finished a sign-in (even to the same account and
// relay, which is how a person brings the other machines back after another copy of the app
// took the connection over). A poke only makes the link read the account; it reconnects only if
// it needs to.
public sealed class LocalAccountWatch
{
    (string?, string?) account;
    bool up;
    bool signingIn;

    public LocalAccountWatch(HostStatus? status, bool connected) => (account, up, signingIn) = (Of(status), connected, status?.SignIn.State == SignInState.Pending);

    static (string?, string?) Of(HostStatus? s) => (s?.Account?.AccountId, s?.Relay.Url);

    // On each change of the host connection: whether to poke the link.
    public bool Next(HostStatus? status, bool connected)
    {
        var now = Of(status);
        var back = !up && connected;
        var signing = status?.SignIn.State == SignInState.Pending;
        var signedIn = signingIn && !signing && status?.SignIn.State != SignInState.Failed && status?.Account is not null;
        (up, signingIn) = (connected, signing);
        if (now == account && !back && !signedIn) { return false; }
        account = now;
        return true;
    }
}

// The fleet through the relay, as a device of its own (kind "app") on the account this
// machine's host signed in to: the host hands the app its account (host.relay.account, for the
// app alone), and the app proves its own key, so it needs no second sign-in and never shares
// the host's identity. It serves nothing; it only opens channels, as the PWA does.
public sealed class RelayFleetLink : IFleetLink
{
    static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    readonly Func<CancellationToken, Task<RelayAccount?>> account;
    readonly P256KeyPair key;
    readonly string name;
    readonly Action<string> log;
    readonly Func<bool> claim;
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim poke = new(0);
    Task loop = Task.CompletedTask;
    volatile Current? current;
    volatile StateNow state = new(FleetState.SignedOut, null);

    sealed record Current(RelayClient Relay, ChannelHub Hub);
    sealed record StateNow(FleetState State, string? Detail);

    // claim: whether this copy of the app may hold the relay link, which one copy per person may
    // (App.xaml.cs keeps a lock across Windows sessions); asked again at each recheck until it may.
    public RelayFleetLink(Func<CancellationToken, Task<RelayAccount?>> account, P256KeyPair key, string name, Action<string>? log = null, Func<bool>? claim = null)
    {
        this.account = account;
        this.key = key;
        this.name = name;
        this.log = log ?? (_ => { });
        this.claim = claim ?? (() => true);
        Blobs = new RelayBlobStore(() => current?.Relay);
    }

    public IBlobStore Blobs { get; }

    public const string ElsewhereNote = "Your other machines are shown by this app in another of your Windows sessions. Close it there and they appear here.";
    public const string ReplacedNote = "Another copy of this app, with this app's key, took over the connection to your other machines. They come back here when this machine's Bridge Host signs in again or restarts, or when this app is opened again.";

    // How long to wait for a sign-in that has not happened, before asking again.
    public TimeSpan Recheck { get; init; } = TimeSpan.FromSeconds(30);

    public string DeviceId => Wire.DeviceId(key.Public);
    public FleetState State => state.State;
    public string? Detail => state.Detail;
    public event Action? Changed;

    int accountGeneration;
    RelayAccount? tried;
    public int Account => Volatile.Read(ref accountGeneration);

    public IReadOnlyList<FleetHost> Hosts => current is { } c
        ? [.. c.Relay.Presence.Where(p => p.Kind == "host" && p.Id != c.Relay.DeviceId).Select(p => new FleetHost(p.Id, p.Name))]
        : [];

    public void Start() => loop = Task.Run(() => RunAsync(stop.Token));

    // Something may have changed what the account is (this machine's host signed in): look
    // again now rather than at the next recheck.
    public void Poke() => poke.Release();

    void Set(FleetState s, string? detail)
    {
        if (state.State == s && state.Detail == detail) { return; }
        state = new StateNow(s, detail);
        Changed?.Invoke();
    }

    async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            if (!claim())
            {
                Set(FleetState.Elsewhere, ElsewhereNote);
                await WaitAsync(Recheck, ct);
                continue;
            }
            RelayAccount? a;
            try { a = await account(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                Set(FleetState.Connecting, e.Message);
                await WaitAsync(Recheck, ct);
                continue;
            }
            if (a is null || !Uri.TryCreate(a.Relay, UriKind.Absolute, out var relayUri))
            {
                Set(FleetState.SignedOut, null);
                await WaitAsync(Recheck, ct);
                continue;
            }
            // Another account or relay than the one tried last: what was shown for that one goes
            // now, not when (or if) this one connects.
            if (tried is { } was && (was.Relay != a.Relay || was.AccountId != a.AccountId))
            {
                Interlocked.Increment(ref accountGeneration);
                Changed?.Invoke();
            }
            tried = a;
            var switched = false;
            var replaced = false;
            try
            {
                Set(FleetState.Connecting, null);
                await using var relay = await RelayClient.ConnectAsync(relayUri, a.Token, key, name, "app", ct);
                var hub = new ChannelHub(relay, key, name, "app") { JsonOptions = ProtocolJson.Options };
                relay.PresenceChanged += _ => Changed?.Invoke();
                current = new Current(relay, hub);
                Set(FleetState.Connected, $"as {relay.Login}");
                log($"connected to the relay as {relay.Login} ({Wire.DeviceId(key.Public)[..8]})");
                backoff = TimeSpan.FromSeconds(1);
                switched = await HoldAsync(relay, a, ct);
                replaced = !switched && (int?)relay.CloseStatus == CloseCodes.Replaced;
                log(switched ? "this machine's host is on another account or relay now: following it" : $"the relay connection ended: {(int?)relay.CloseStatus} {relay.CloseReason}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (RelayRefusedException e) when (e.Code is CloseCodes.AuthFailed or CloseCodes.ProofFailed)
            {
                log($"the relay refused the app: {e.Message}");
                Set(FleetState.Refused, "the account's sign-in is no longer accepted");
                current = null;
                Changed?.Invoke();
                await WaitAsync(Recheck, ct);
                continue;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                log($"the relay connection failed: {e.Message}");
                Set(FleetState.Connecting, e.Message);
            }
            // Not Connected before the hosts go: an empty list while Connected says every
            // machine left, and Fleet would drop their cards over a relay blip.
            if (replaced) { Set(FleetState.Elsewhere, ReplacedNote); }
            else if (state.State == FleetState.Connected) { Set(FleetState.Connecting, "reconnecting"); }
            current = null;
            Changed?.Invoke();
            if (switched) { continue; }
            if (replaced)
            {
                // Another copy of the app with this key connected (this person's other Windows
                // session): reconnecting would take it back, and that copy would do the same, each
                // evicting the other every second. As the host does, wait to be asked: a poke, when
                // this machine's host signs in again or comes back.
                log("another copy of this app took over its relay connection; not reconnecting until this machine's host signs in again or restarts");
                try { await poke.WaitAsync(ct); }
                catch (OperationCanceledException) { break; }
                continue;
            }
            await WaitAsync(backoff, ct);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    // While connected, until the relay connection ends (false) or this machine's host is signed
    // in to another account or relay (true). A poke is only a hint that it may be: the account
    // is read again and compared.
    async Task<bool> HoldAsync(RelayClient relay, RelayAccount now, CancellationToken ct)
    {
        while (true)
        {
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var poked = poke.WaitAsync(wake.Token);
            var first = await Task.WhenAny(relay.Completion, poked);
            if (first != poked)
            {
                // Cancel the losing wait, so it cannot take a later poke meant for the next one.
                wake.Cancel();
                try { await poked; } catch (OperationCanceledException) { }
                return false;
            }
            ct.ThrowIfCancellationRequested();
            RelayAccount? then;
            try { then = await account(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Not knowing is not a change: keep the connection that works.
                log($"could not read this machine's account again: {e.Message}");
                continue;
            }
            if (then is null || then.Relay != now.Relay || then.AccountId != now.AccountId) { return true; }
        }
    }

    async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await poke.WaitAsync(delay, ct); }
        catch (OperationCanceledException) { }
    }

    public async Task<IClientChannel> OpenAsync(string hostId, CancellationToken ct)
    {
        if (current is not { } c) { throw new HostNotRunningException("not connected to the relay"); }
        if (!c.Relay.Presence.Any(p => p.Id == hostId)) { throw new HostNotRunningException("the machine is offline"); }
        try
        {
            // A host too old to name its run in hello is known by nothing better than this channel:
            // the relay's presence can arrive out of order, so when it joined could name the run
            // before a restart, and a lost launch would go to the new one too.
            return new RelayClientChannel(c.Hub, await c.Hub.OpenAsync(hostId, TimeSpan.FromSeconds(15)).WaitAsync(ct));
        }
        // Whatever ends a handshake (ChannelHub.EndsChannel: a malformed introduction too) is the
        // machine not answering for now; the connection loop tries again rather than stopping.
        catch (Exception e) when (e is TimeoutException or IOException or System.Net.WebSockets.WebSocketException || ChannelHub.EndsChannel(e))
        {
            throw new HostNotRunningException($"the machine did not answer through the relay ({e.Message})", e);
        }
    }

    // Every host in the relay's presence but the target (this machine's own included: it can be a
    // source too), asked at once, each over a short channel of its own.
    public async Task<IReadOnlyList<TransferSource>> TransferSourcesAsync(string target, CancellationToken ct)
    {
        if (current is not { } c) { return []; }
        var hosts = c.Relay.Presence.Where(p => p.Kind == "host" && p.Id != target && p.Id != c.Relay.DeviceId).ToList();
        var found = await Task.WhenAll(hosts.Select(async h =>
        {
            try
            {
                await using var ch = await OpenAsync(h.Id, ct);
                return await Transfers.AskAsync(ch, h.Id, h.Name, SourceOptionsTimeout, ct);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                log($"{h.Name} offered no sessions to bring over: {e.Message}");
                return null;
            }
        }));
        return [.. found.OfType<TransferSource>()];
    }

    // launch.options can take a minute on a slow machine (LaunchViewModel.OptionsTimeout).
    public TimeSpan SourceOptionsTimeout { get; init; } = TimeSpan.FromSeconds(90);

    // Signed with this app's own device key, for the machine the session is on.
    public SignedCommand SignTransfer(TransferSource from, string session, string to) =>
        Transfers.Sign(key, from.Key, session, to, DateTimeOffset.UtcNow, Guid.NewGuid().ToString());

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await loop; } catch (Exception) { }
    }
}

// Every machine's usage as one list (pwa/js/usage.js, mergeUsage): one row per allowance (an
// agent and its account), carrying the freshest reading any machine has. A reading outranks a
// failure however new, since a failure is stamped with when the attempt was made; a failure
// newer than the reading is still the latest word on why nothing fresher exists.
public static class UsageMerge
{
    public static UsageReport Merge(IEnumerable<UsageReport> reports)
    {
        var slots = new Dictionary<string, (AgentUsage? Reading, AgentUsage? Failure)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var a in reports.SelectMany(r => r.Agents))
        {
            var key = $"{a.Agent}\0{a.AccountKey ?? (a.Account ?? "").ToLowerInvariant()}";
            if (!slots.TryGetValue(key, out var slot)) { order.Add(key); }
            if (a.Windows.Count > 0)
            {
                if (slot.Reading is null || slot.Reading.MeasuredAt < a.MeasuredAt) { slot.Reading = a; }
            }
            else if (slot.Failure is null || slot.Failure.MeasuredAt < a.MeasuredAt) { slot.Failure = a; }
            slots[key] = slot;
        }
        var entries = order.Select(k => slots[k]).Select(s => s.Reading is not { } r ? s.Failure!
            : s.Failure is { Error: { Length: > 0 } why } f && f.MeasuredAt > r.MeasuredAt ? r with { Error = why } : r).ToList();
        // Two rows of one agent with no account to name either (Claude names none) would look
        // the same: a few characters of the key keep them apart, only when there is something
        // to keep apart.
        var perAgent = entries.GroupBy(e => e.Agent).ToDictionary(g => g.Key, g => g.Count());
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i] is { Account: null, AccountKey: { Length: >= 4 } k } e && perAgent[e.Agent] > 1) { entries[i] = e with { Account = $"#{k[..4]}" }; }
        }
        return new UsageReport(entries);
    }
}
