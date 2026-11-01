using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// update.* (docs/protocol/v1-update.md, #51): another of the person's devices sees this machine's
// updates and asks for a check, or for the latest release to be installed now. The Windows app
// beside this host does the work, as it does its automatic updates (AutoUpdater, with the same
// idle rules: it installs once no session has a turn running or a question waiting); the host
// carries what the app reports (host.update.report) to the devices, and their presses to the app
// (HostStatus.UpdateAsks), and decides who may press:
//   * a device through the relay only: never this machine's pipe, which agents, the MCP server
//     and the Home Assistant connector reach (an app here updates from its own Settings);
//   * a device the relay lists as the PWA or the Windows app, never a machine, which is where an
//     agent acts. The relay's kind is what a device says it is, so something holding the
//     account's token could claim "pwa"; what it could then do is what the app does unasked
//     anyway: install the release from this machine's own feed once no session is busy;
//   * never on a read-only machine (delivery.readOnly, or agent-ha-bridge running), which serves
//     no update.* at all, as #112 withdrew Home Assistant's Install there.
// Refusing while another profile runs from the same installation is the app's to decide (it
// knows the package's processes), and comes back as the outcome.
public sealed class UpdateService
{
    // The relay's kinds for a person's device.
    static readonly HashSet<string> PersonKinds = new(["pwa", "app"], StringComparer.Ordinal);
    public static readonly TimeSpan AskKept = TimeSpan.FromMinutes(10);
    public const int MaxAsks = 8;
    // Each device may have this many presses of each kind taken in RateWindow; the rest are
    // refused, saying when to try again. Every new press makes the app ask the feed (or answer
    // from a check a minute old) and an Install look at the machine's processes again.
    public const int RateLimit = 3;
    public static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(10);
    // Devices remembered for the limit and for the log; the oldest is let go past this.
    internal int DevicesKept { get; init; } = 64;
    public const string Full = "this machine has too many presses waiting for its app already; try again in a few minutes";
    public const string NoApp = "the Agent Bridge app is not running on this machine, and it is what installs updates";
    public const string ThroughTheRelay = "updates are asked for from another device, through the relay; on this machine, use the app's Settings";

    readonly Action<string> log;
    readonly TimeProvider time;
    readonly object gate = new();
    readonly List<UpdateAsk> asks = [];
    // Presses taken, by request, for AskKept: one sent again after the app took it is the same
    // press, not a new one.
    readonly Dictionary<string, DateTimeOffset> taken = new(StringComparer.Ordinal);
    // Each device's presses taken in RateWindow, by kind, and when each device's refusal was last
    // logged.
    readonly Dictionary<(string Peer, UpdateAskKind Kind), Queue<DateTimeOffset>> pressed = [];
    readonly Dictionary<string, DateTimeOffset> refusalLogged = new(StringComparer.Ordinal);
    readonly Dictionary<IProtocolChannel, Subscriber> subscribers = [];
    Func<IReadOnlyList<PresenceEntry>>? presence;
    IProtocolChannel? app;
    UpdateState? reported;
    long version;

    public UpdateService(string hostVersion, Action<string>? log = null, TimeProvider? time = null)
    {
        HostVersion = hostVersion;
        this.log = log ?? (_ => { });
        this.time = time ?? TimeProvider.System;
    }

    public string HostVersion { get; }
    // Why this machine acts on nothing from another device now, or null: hello's readOnly.
    public Func<string?> ReadOnly { get; init; } = () => null;
    // Raised when the asks change, so the host's status (which the app follows) is sent again.
    public event Action? AsksChanged;

    public void Attach(RelayClient? relay) => Volatile.Write(ref presence, relay is null ? null : () => relay.Presence);

    // For tests: who the relay says is connected.
    internal void AttachPresence(Func<IReadOnlyList<PresenceEntry>>? devices) => Volatile.Write(ref presence, devices);

    // hello's update: through the relay, on a machine that is not read-only.
    public bool Offered(IProtocolChannel channel) => channel is SecureChannel && ReadOnly() is null;

    public IReadOnlyList<UpdateAsk> Asks
    {
        get { lock (gate) { Prune(); return [.. asks]; } }
    }

    public UpdateState State()
    {
        lock (gate) { return StateLocked(); }
    }

    UpdateState StateLocked()
    {
        if (app is not null && reported is { } r) { return r; }
        // No app: what this host knows, and how the last update the app reported ended.
        return new UpdateState { Current = HostVersion, Installer = false, Unavailable = NoApp, Last = reported?.Last };
    }

    // host.update.report, from the app on this machine's pipe (LocalApi lets nothing else send it).
    public object Report(IProtocolChannel channel, UpdateReport report)
    {
        var took = false;
        lock (gate)
        {
            if (app != channel)
            {
                app = channel;
                _ = channel.Closed.ContinueWith(_ => Gone(channel), TaskScheduler.Default);
            }
            reported = UpdateProtocol.Fit(report.State);
            foreach (var request in report.Taken ?? [])
            {
                if (asks.RemoveAll(a => a.Request == request) == 0) { continue; }
                took = true;
                this.taken[request] = time.GetUtcNow();
            }
            version++;
        }
        if (took) { AsksChanged?.Invoke(); }
        Push();
        return new { ok = true };
    }

    void Gone(IProtocolChannel channel)
    {
        lock (gate)
        {
            if (app != channel) { return; }
            app = null;
            version++;
        }
        Push();
    }

    public Task<object?> HandleAsync(IProtocolChannel channel, string op, JsonElement data)
    {
        // Through the relay only, and only where the machine acts on other devices at all: a
        // read-only one serves no update.*, reading included, as hello offers none.
        if (channel is not SecureChannel secure) { throw new InvalidOperationException(ThroughTheRelay); }
        if (ReadOnly() is { } readOnly) { throw new InvalidOperationException($"this machine is read-only for other devices: {readOnly}"); }
        return op switch
        {
            UpdateProtocol.Get => Task.FromResult<object?>(State()),
            UpdateProtocol.Subscribe => Task.FromResult<object?>(Subscribe(channel)),
            UpdateProtocol.Check => Task.FromResult<object?>(Ask(secure.PeerId, UpdateAskKind.Check, data)),
            UpdateProtocol.Install => Task.FromResult<object?>(Ask(secure.PeerId, UpdateAskKind.Install, data)),
            _ => Task.FromResult<object?>(null),
        };
    }

    UpdateCommandResult Ask(string peer, UpdateAskKind kind, JsonElement data)
    {
        UpdateCommand? command = null;
        try { command = data.ValueKind == JsonValueKind.Object ? data.Deserialize<UpdateCommand>(ProtocolJson.Options) : null; }
        catch (JsonException) { }
        var name = kind == UpdateAskKind.Install ? UpdateProtocol.Install : UpdateProtocol.Check;
        if (!UpdateProtocol.IsRequest(command?.Request)) { throw new ArgumentException($"{name} needs a request (a UUID)"); }
        var request = Guid.Parse(command!.Request).ToString();
        var who = Volatile.Read(ref presence) is { } devices ? devices().FirstOrDefault(d => d.Id == peer) : null;
        if (who is null) { return Refused(peer, name, "the device asking is not connected to the relay now"); }
        if (!PersonKinds.Contains(who.Kind)) { return Refused(peer, name, "only a person's phone, browser or Windows app can ask a machine to update"); }
        var by = $"{who.Kind} {peer[..8]}";
        bool added;
        UpdateState state;
        string? tooMany = null;
        lock (gate)
        {
            Prune();
            if (app is null || reported is null) { return Refused(peer, name, NoApp); }
            if (!reported.Installer) { return Refused(peer, name, reported.Unavailable ?? "this copy of the app was not installed by its updater, so it cannot update"); }
            // A resend of the same press is the same ask, whether or not the app has taken it.
            added = asks.All(a => a.Request != request) && !taken.ContainsKey(request);
            // Full: refused, rather than letting go of a press already answered "accepted".
            if (added && asks.Count >= MaxAsks)
            {
                tooMany = Full;
                added = false;
            }
            else if (added && Limited(peer, kind) is { } again)
            {
                tooMany = $"too many presses of {(kind == UpdateAskKind.Install ? "Install" : "Check")} from this device just now; try again {again}";
                added = false;
            }
            else if (added)
            {
                var now = time.GetUtcNow();
                asks.Add(new UpdateAsk(request, kind, by, now));
                pressed[(peer, kind)].Enqueue(now);
            }
            state = StateLocked();
        }
        if (tooMany is not null) { return Refused(peer, name, tooMany); }
        if (added)
        {
            log($"update {(kind == UpdateAskKind.Install ? "install" : "check")} asked for by {by}");
            AsksChanged?.Invoke();
        }
        return new UpdateCommandResult(UpdateAck.Accepted) { State = state };
    }

    UpdateCommandResult Refused(string peer, string op, string why)
    {
        // At most one line a minute for each device: a device pressing over and over fills no log.
        bool say;
        lock (gate)
        {
            var now = time.GetUtcNow();
            say = !refusalLogged.TryGetValue(peer, out var at) || now - at >= TimeSpan.FromMinutes(1);
            if (say)
            {
                if (!refusalLogged.ContainsKey(peer))
                {
                    while (refusalLogged.Count >= DevicesKept) { refusalLogged.Remove(refusalLogged.MinBy(p => p.Value).Key); }
                }
                refusalLogged[peer] = now;
            }
        }
        if (say) { log($"{op} refused from {peer[..Math.Min(8, peer.Length)]}: {why}"); }
        return new UpdateCommandResult(UpdateAck.Refused, why) { State = State() };
    }

    // Under the gate: when this device may press this kind again, in words, or null if it may now.
    string? Limited(string peer, UpdateAskKind kind)
    {
        var now = time.GetUtcNow();
        if (!pressed.TryGetValue((peer, kind), out var times))
        {
            // Room made first, so the entry let go is never this one: the one that pressed least
            // recently (or not at all within the window).
            while (pressed.Count >= DevicesKept) { pressed.Remove(pressed.MinBy(p => p.Value.Count > 0 ? p.Value.Last() : DateTimeOffset.MinValue).Key); }
            pressed[(peer, kind)] = times = new Queue<DateTimeOffset>();
        }
        while (times.Count > 0 && now - times.Peek() >= RateWindow) { times.Dequeue(); }
        if (times.Count < RateLimit) { return null; }
        var minutes = Math.Max(1, (int)Math.Ceiling((times.Peek() + RateWindow - now).TotalMinutes));
        return $"in {minutes} minute{(minutes == 1 ? "" : "s")}";
    }

    // Under the gate: an ask the app has not taken in AskKept (it was not running, or hung) goes,
    // and so does the memory of one it took.
    void Prune()
    {
        var now = time.GetUtcNow();
        asks.RemoveAll(a => now - a.At > AskKept);
        foreach (var (request, at) in taken.Where(t => now - t.Value > AskKept).ToList()) { taken.Remove(request); }
    }

    // As usage.subscribe: the state now, then update.state whenever it changes, nothing ahead of
    // the response.
    object Subscribe(IProtocolChannel channel)
    {
        Subscriber s;
        UpdateState state;
        lock (gate)
        {
            if (!subscribers.TryGetValue(channel, out s!))
            {
                subscribers[channel] = s = new Subscriber();
                _ = channel.Closed.ContinueWith(_ => { lock (gate) { subscribers.Remove(channel); } }, TaskScheduler.Default);
            }
            state = StateLocked();
            s.Sent = version;
            s.Open = false;
        }
        return new ChannelReply(state, ch =>
        {
            lock (gate) { s.Open = true; }
            return PushAsync(ch, s);
        });
    }

    void Push()
    {
        List<(IProtocolChannel, Subscriber)> open;
        lock (gate) { open = [.. subscribers.Select(kv => (kv.Key, kv.Value))]; }
        foreach (var (ch, s) in open) { _ = PushAsync(ch, s); }
    }

    async Task PushAsync(IProtocolChannel channel, Subscriber s)
    {
        await s.Gate.WaitAsync();
        try
        {
            UpdateState state;
            long at;
            lock (gate)
            {
                if (!s.Open || version <= s.Sent) { return; }
                state = StateLocked();
                at = version;
            }
            // A machine that became read-only since the device subscribed serves it nothing more,
            // as it answers no update.get; what changed meanwhile is sent if it stops being.
            if (!channel.IsOpen || ReadOnly() is not null) { return; }
            await channel.SendEventAsync(UpdateProtocol.Event, state);
            lock (gate) { s.Sent = at; }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally { s.Gate.Release(); }
    }

    sealed class Subscriber
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public long Sent;
        public bool Open;
    }
}
