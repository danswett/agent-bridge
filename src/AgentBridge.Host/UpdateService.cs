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
//
// Update now (#1159, docs/protocol/v1-update.md): update.install carrying { now: SignedCommand },
// an Install that skips the app's wait for a quiet machine. Its press passes the same signed-
// command gate as manage.* (ManageService.Gate, SignedPress), needs permissions.remoteManage on, is
// recorded durably before the app is asked (UpdateNowRecord), and is answered confirm, asking the
// app nothing, when the machine is busier than the person confirmed. busy is the host's own view of
// its sessions. Leaving is DepartAsync, the host's half of the app-only departure step #870 owns:
// it takes #805's lease as purpose Update, compares what is busy with the confirmation again with
// the lease held, announces, and commits with Update now's own allowance or a slot of the shared
// cap spent under ManageService's lock. The app calls it over the pipe (host.update.depart), the
// departure step #870's lane owns, wired here minimally to the design's contract.
public sealed class UpdateService
{
    // The relay's kinds for a person's device.
    static readonly HashSet<string> PersonKinds = new(["pwa", "app"], StringComparer.Ordinal);
    static readonly SignedPress.Words NowWords = new("update a machine now", "an Update now press");
    // The rate limiter's name for Update now: a kind of its own, so Install stays usable.
    const string NowKind = "now";
    public static readonly TimeSpan AskKept = UpdateProtocol.Lapses;
    public const int MaxAsks = 8;
    // Each device may have this many presses of each kind taken in RateWindow; the rest are
    // refused, saying when to try again. Every new press makes the app ask the feed (or answer
    // from a check a minute old) and an Install look at the machine's processes again.
    public const int RateLimit = 3;
    public static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(10);
    // Devices remembered for the limit and for the log; the oldest is let go past this.
    internal int DevicesKept { get; init; } = 64;
    public const string Full = "this machine has too many presses waiting for its app already; try again in a few minutes";
    public const string NoApp = $"the {Product.App} is not running on this machine, and it is what installs updates";
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
    readonly Dictionary<(string Peer, string Kind), Queue<DateTimeOffset>> pressed = [];
    readonly Dictionary<string, DateTimeOffset> refusalLogged = new(StringComparer.Ordinal);
    readonly Dictionary<IProtocolChannel, Subscriber> subscribers = [];
    Func<IReadOnlyList<PresenceEntry>>? presence;
    IProtocolChannel? app;
    UpdateState? reported;
    // Whether the app reporting says it carries Update now (UpdateReport.CarriesUpdateNow).
    bool carries;
    long version;
    // Update now: whether one is leaving now, and the busy last pushed for a session's change, with
    // when. The presses themselves are in the durable record (UpdateNowRecord).
    UpdateNowParts? now;
    UpdateNowRecord? record;
    int departing;
    string? busySent;
    bool busyPushPending;
    DateTimeOffset busyPushedAt;

    sealed record NowPress(SignedPress Signed, string Release, UpdateBusy? Accept);

    public UpdateService(string hostVersion, Action<string>? log = null, TimeProvider? time = null)
    {
        HostVersion = hostVersion;
        this.log = log ?? (_ => { });
        this.time = time ?? TimeProvider.System;
    }

    public string HostVersion { get; }
    // Why this machine acts on nothing from another device now, or null: hello's readOnly.
    public Func<string?> ReadOnly { get; init; } = () => null;
    // The same, perhaps a moment old (SharedAgentHaBridgeProbe), for what hello offers only:
    // everything that acts on an update asks ReadOnly. ReadOnly where not set.
    public Func<string?>? OfferedReadOnly { get; init; }
    // Raised when the asks change, so the host's status (which the app follows) is sent again.
    public event Action? AsksChanged;

    // Update now's parts (ManageComposition.UpdateNow): without them this host offers none, and a
    // press with now is refused unavailable.
    public UpdateNowParts? Now
    {
        get => now;
        init
        {
            now = value;
            if (value is null) { return; }
            record = new UpdateNowRecord(value.Folder, time.GetUtcNow(), log);
            value.Sessions.Changed += (_, _) => SessionsChanged();
        }
    }

    // How long the departure waits for the quiet lease each time, and how many times it tries
    // (about a minute in all, docs/protocol/v1-update.md, Going, step 3).
    internal TimeSpan DepartWait { get; init; } = TimeSpan.FromSeconds(10);
    internal int DepartTries { get; init; } = 6;
    // For tests: runs just before the departure's commit, the press and its restart slot already
    // recorded spent, where a host that stops (a crash) leaves both spent and nothing departed.
    internal Action? BeforeCommit { get; set; }
    // busy is sent again for a session's change at most this often.
    public static readonly TimeSpan BusyEvery = TimeSpan.FromSeconds(2);

    public void Attach(RelayClient? relay) => Volatile.Write(ref presence, relay is null ? null : () => relay.Presence);

    // For tests: who the relay says is connected.
    internal void AttachPresence(Func<IReadOnlyList<PresenceEntry>>? devices) => Volatile.Write(ref presence, devices);

    // hello's update: through the relay, on a machine that is not read-only.
    public bool Offered(IProtocolChannel channel) => channel is SecureChannel && (OfferedReadOnly ?? ReadOnly)() is null;

    // The presses waiting for the app. None on a machine that has become read-only since they
    // were taken: they are let go, not handed to the app. ReadOnly is uncached (it scans for
    // agent-ha-bridge) and every status reads Asks, so it is asked only when a press is waiting.
    public IReadOnlyList<UpdateAsk> Asks
    {
        get
        {
            lock (gate)
            {
                Prune();
                if (asks.Count == 0) { return []; }
            }
            var readOnly = ReadOnly() is not null;
            lock (gate)
            {
                Prune();
                if (readOnly) { asks.Clear(); }
                return [.. asks];
            }
        }
    }

    public UpdateState State()
    {
        UpdateState state;
        lock (gate) { state = StateLocked(); }
        return Decorate(state);
    }

    UpdateState StateLocked()
    {
        if (app is not null && reported is { } r) { return r; }
        // No app: what this host knows, and how the last update the app reported ended.
        return new UpdateState { Current = HostVersion, Installer = false, Unavailable = NoApp, Last = reported?.Last };
    }

    // Outside the gate: what this host adds to the app's state for Update now, from its own view.
    // canUpdateNow with busy, or, with an update ready, why not. Read-only here is the answer hello
    // offers from (perhaps a moment old); a press reads it afresh.
    UpdateState Decorate(UpdateState s)
    {
        if (now is not { } parts) { return s; }
        var (why, at) = WhyNot(s, parts, OfferedReadOnly ?? ReadOnly);
        if (why is null) { return s with { CanUpdateNow = true, Busy = Busy(parts).Shown() }; }
        return s.Available is null ? s : s with { UpdateNowWhy = why, UpdateNowAt = at };
    }

    // Why Update now can't be pressed now, as UpdateNowWhy's code (each the same word as the press's
    // refusal code), or null when it can: the switch on, the machine not read-only, the app running,
    // installed and new enough, no agent sign-in under way, no other Update now downloading or
    // leaving, and a restart slot free, its own or the shared cap's (settled Q7).
    (string? Why, DateTimeOffset? At) WhyNot(UpdateState s, UpdateNowParts parts, Func<string?> readOnly)
    {
        if (!parts.Manage.RemoteManageOn) { return (UpdateNowWhy.Disabled, null); }
        if (readOnly() is not null) { return (UpdateNowWhy.ReadOnly, null); }
        if (!s.Installer) { return (UpdateNowWhy.Unavailable, null); }
        if (!Volatile.Read(ref carries)) { return (UpdateNowWhy.OldApp, null); }
        if (parts.Manage.SignInPending) { return (UpdateNowWhy.SignIn, null); }
        if (UnderWay(s)) { return (UpdateNowWhy.Busy, null); }
        var (free, next) = parts.Manage.UpdateNowSlot();
        return free ? (null, null) : (UpdateNowWhy.RestartCap, next);
    }

    // Another Update now downloading or leaving: one the app has not taken yet, one it carries out
    // and is not waiting with (a press waiting for a quiet moment, or held for a usage check, is
    // replaced by a new one, decision 3), or the departure itself.
    bool UnderWay(UpdateState s)
    {
        if (Volatile.Read(ref departing) == 1) { return true; }
        if (Carrying(s)) { return true; }
        lock (gate)
        {
            Prune();
            return asks.Any(a => a.Now is not null);
        }
    }

    // The app carrying out an Update now it has taken, by what it reports.
    static bool Carrying(UpdateState s) => s.RequestNow && s.Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Installing;

    static UpdateBusyView Busy(UpdateNowParts parts) => UpdateBusyView.Of(parts.Sessions.Snapshot(), parts.Sessions.Held is not null);

    // A session changed: busy is sent again if it is not what was last sent, at most every BusyEvery.
    void SessionsChanged()
    {
        TimeSpan wait;
        lock (gate)
        {
            if (subscribers.Count == 0 || busyPushPending) { return; }
            busyPushPending = true;
            wait = busyPushedAt + BusyEvery - time.GetUtcNow();
        }
        if (wait <= TimeSpan.Zero) { BusyPush(); return; }
        _ = Task.Delay(wait, time).ContinueWith(_ => BusyPush(), TaskScheduler.Default);
    }

    void BusyPush()
    {
        string busy;
        try { busy = JsonSerializer.Serialize(State().Busy, ProtocolJson.Options); }
        catch (Exception e)
        {
            // Never left pending: a look at the state that fails once must not stop every later
            // push of busy. Nor thrown into the session source that raised the change.
            lock (gate) { busyPushPending = false; }
            log($"update now: busy could not be read for subscribers ({e.GetType().Name})");
            return;
        }
        lock (gate)
        {
            busyPushPending = false;
            busyPushedAt = time.GetUtcNow();
            if (busy == busySent) { return; }
            busySent = busy;
            version++;
        }
        Push();
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
            Volatile.Write(ref carries, report.CarriesUpdateNow);
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
            Volatile.Write(ref carries, false);
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
        if (kind == UpdateAskKind.Install && command?.Now is { } signed) { return AskNow(peer, command, signed); }
        if (!UpdateProtocol.IsRequest(command?.Request)) { throw new ArgumentException($"{name} needs a request (a UUID)"); }
        var request = Guid.Parse(command!.Request!).ToString();
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
            else if (added && Limited(peer, kind.ToString()) is { } again)
            {
                tooMany = $"too many presses of {(kind == UpdateAskKind.Install ? "Install" : "Check")} from this device just now; try again {again}";
                added = false;
            }
            else if (added)
            {
                var now = time.GetUtcNow();
                asks.Add(new UpdateAsk(request, kind, by, now));
                pressed[(peer, kind.ToString())].Enqueue(now);
            }
            state = StateLocked();
        }
        if (tooMany is not null) { return Refused(peer, name, tooMany); }
        if (added)
        {
            log($"update {(kind == UpdateAskKind.Install ? "install" : "check")} asked for by {by}");
            AsksChanged?.Invoke();
        }
        return new UpdateCommandResult(UpdateAck.Accepted) { State = Decorate(state) };
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

    UpdateCommandResult RefusedNow(string peer, string code, string why) => Refused(peer, $"{UpdateProtocol.Install} now", why) with { Code = code };

    // An Update now press (docs/protocol/v1-update.md, "The press" and "What still refuses"): the
    // request only inside the signed command; the shared signed-command gate; a request this host
    // has recorded gets that press's answer and is never acted on again; the command's lifetime;
    // Update now's own rate limit; then, recorded before anything else, its args, the switch, the
    // app, a sign-in, another Update now, the restart slots, the release shown, and what is busy
    // against what the person confirmed. Only then is the app asked.
    UpdateCommandResult AskNow(string peer, UpdateCommand command, SignedCommand signed)
    {
        if (now is not { } parts || record is null) { return RefusedNow(peer, ManageReason.Unavailable, "this machine's host does not offer Update now"); }
        // A client never sends both (security review, Low): one that did is caught here, rather than
        // turned into a plain Install by a host that doesn't know now.
        if (command.Request is not null) { return RefusedNow(peer, ManageReason.BadArgs, "an Update now carries its request only inside the signed command"); }
        var check = parts.Manage.Gate(peer, signed, UpdateProtocol.NowAction, NowWords);
        if (check.Press is not { } press) { return RefusedNow(peer, check.Code!, check.Detail!); }
        var at = time.GetUtcNow();
        UpdateNowRecord.Entry? known;
        bool knows;
        lock (gate) { (known, knows) = (record.Find(press.Request, at), record.Knows(press.IssuedAt)); }
        if (known is not null) { return Again(peer, known); }
        if (!knows)
        {
            return RefusedNow(peer, ManageReason.Changed, "this machine could not read its record of presses, so one signed before now is not acted on; press again");
        }
        if (SignedPress.Lifetime(press.Body, at, NowWords) is { } lapsed) { return RefusedNow(peer, lapsed.Code, lapsed.Detail); }
        string? again;
        lock (gate)
        {
            known = record.Find(press.Request, at);
            again = known is null ? Limited(peer, NowKind) : null;
            if (known is null && again is null)
            {
                pressed[(peer, NowKind)].Enqueue(at);
                // Written through before anything else, so the press is never acted on twice, even by a
                // host that has restarted since.
                knows = record.Add(new UpdateNowRecord.Entry(press.Request, press.IssuedAt, press.By), at);
            }
        }
        if (known is not null) { return Again(peer, known); }
        if (again is not null) { return RefusedNow(peer, ManageReason.RateLimited, $"too many presses of Update now from this device just now; try again {again}"); }
        if (!knows) { return RefusedNow(peer, ManageReason.Unavailable, "this machine could not record the press, so it was not acted on"); }
        var result = DecideNow(peer, parts, press, signed);
        lock (gate) { record.Answer(press.Request, result.Outcome.ToString().ToLowerInvariant(), result.Code, result.Reason); }
        return result;
    }

    // A press recorded already is the same press and is never acted on again. One this host is still
    // deciding is refused busy, and one a host before this one was deciding when it stopped is
    // refused changed; any other gets the answer it had.
    UpdateCommandResult Again(string peer, UpdateNowRecord.Entry e)
    {
        if (e.Outcome is null)
        {
            return record!.Earlier(e.Request)
                ? RefusedNow(peer, ManageReason.Changed, "this press was being handled when the bridge here restarted, and is not acted on again; press again")
                : RefusedNow(peer, ManageReason.Busy, "this press is already being handled");
        }
        return new(Enum.TryParse<UpdateAck>(e.Outcome, ignoreCase: true, out var outcome) ? outcome : UpdateAck.Refused, e.Reason) { Code = e.Code, State = State() };
    }

    UpdateCommandResult DecideNow(string peer, UpdateNowParts parts, SignedPress press, SignedCommand signed)
    {
        var (args, problem) = UpdateNowRules.Read(press.Body);
        if (args is null) { return RefusedNow(peer, ManageReason.BadArgs, problem!); }
        UpdateState state;
        lock (gate) { state = StateLocked(); }
        // Each the same word as updateNowWhy, read afresh: read-only by the acting answer.
        var (why, next) = WhyNot(state, parts, ReadOnly);
        if (why is not null)
        {
            return RefusedNow(peer, why, why switch
            {
                UpdateNowWhy.Disabled => "managing this machine from other devices is turned off here (permissions.remoteManage)",
                UpdateNowWhy.ReadOnly => "this machine is read-only for other devices",
                UpdateNowWhy.Unavailable => state.Unavailable ?? NoApp,
                UpdateNowWhy.OldApp => "the app on this machine is too old to carry out Update now; Install still works",
                UpdateNowWhy.SignIn => "an agent sign-in is under way on this machine, and a restart would cancel it",
                UpdateNowWhy.Busy => "another Update now is already under way on this machine",
                _ => $"Update now has used its restarts for the last 6 hours; the next is allowed at {ManageService.Clock(next ?? time.GetUtcNow())}",
            });
        }
        if (state.Available != args.Release)
        {
            return RefusedNow(peer, UpdateCode.ReleaseChanged, "the release offered here is no longer the one the person was shown");
        }
        // The early answer, from the snapshot now: the same comparison is made again when the
        // host acts, with the lease held (DepartAsync).
        if (Busy(parts).CoveredBy(args.Accept) != UpdateBusyView.Cover.Covered)
        {
            log($"update now from {press.By}: more is busy than the person confirmed; asked to confirm again");
            return new UpdateCommandResult(UpdateAck.Confirm, "more is busy here than the confirmation covered") { State = State() };
        }
        var until = press.IssuedAt + UpdateProtocol.NowLife;
        string? refused = null;
        lock (gate)
        {
            Prune();
            // Again with the ask added in the same breath: two presses passing WhyNot together are
            // never both handed to the app.
            if (asks.Count >= MaxAsks) { refused = Full; }
            else if (Volatile.Read(ref departing) == 1 || asks.Any(a => a.Now is not null) || Carrying(StateLocked())) { refused = "another Update now is already under way on this machine"; }
            else
            {
                // Its signed command recorded before the app can have it, so its departure can be
                // verified again, by this host or one that restarts meanwhile.
                record!.Answer(press.Request, "accepted", null, null, signed);
                asks.Add(new UpdateAsk(press.Request, UpdateAskKind.Install, press.By, time.GetUtcNow(), new UpdateNowAsk(args.Release!, args.Accept, until)));
            }
        }
        if (refused is not null) { return RefusedNow(peer, ManageReason.Busy, refused); }
        log($"update now asked for by {press.By}{(press.Name.Length > 0 ? $" \"{press.Name}\"" : "")} for {args.Release}");
        AsksChanged?.Invoke();
        return new UpdateCommandResult(UpdateAck.Accepted) { State = State() };
    }

    // The host's half of an Update now's departure (docs/protocol/v1-update.md, Going, step 3): the
    // app-only op host.update.depart ({ request }, DepartForAppAsync) calls it once the download
    // is checked; #870's lane owns that step's contract, wired here minimally to the design's. The press must be one this host, or one before it,
    // verified and handed to the app (its signed command, from UpdateNowRecord, verified again),
    // within its 15 minutes, and not gone already. #805's lease is taken as purpose Update, with the person's
    // press (so it may announce the old way, Legacy, as a remote restart does), waiting DepartWait,
    // up to DepartTries times while an answer, reply or stop is typed or delivered, never cutting
    // one; then, with the lease held, what is busy is compared with the confirmation before
    // announcing and again just before committing, with the life read again; the Update purpose's
    // registered conditions are read again; and the commit spends Update now's own slot or a shared
    // one under ManageService's lock. Anything failing ends it there, the host serving on: waiting
    // (with its waiting code, the download kept) when the machine is busier than confirmed, else
    // refused with a code. The lease adds no idle test: what is busy is this consumer's own check.
    public async Task<UpdateDeparture> DepartAsync(string request, CancellationToken cancellationToken = default)
    {
        if (now is not { } parts || record is null) { return UpdateDeparture.Refused(ManageReason.Unavailable); }
        if (!UpdateProtocol.IsRequest(request)) { return UpdateDeparture.Refused(ManageReason.BadArgs); }
        request = Guid.Parse(request).ToString();
        // One departure at a time, taken before the record is read, so a press refused or gone by
        // the time a second call for it gets here is seen done.
        if (Interlocked.Exchange(ref departing, 1) == 1) { return UpdateDeparture.Refused(ManageReason.Busy); }
        UpdateDeparture result;
        try
        {
            UpdateNowRecord.Entry? entry;
            lock (gate) { entry = record.Find(request, time.GetUtcNow()); }
            if (entry is not { Outcome: "accepted", Command: { } command, Done: false }
                || parts.Manage.Reverify(command, UpdateProtocol.NowAction, entry.By) is not { } signed
                || signed.Request != request
                || UpdateNowRules.Read(signed.Body).Args is not { } args)
            {
                return UpdateDeparture.Refused(ManageReason.Changed);
            }
            Changed();
            // Spent on disk before the lease is even asked for: whatever this attempt comes to, a
            // host that restarts or crashes from here on finds the press spent, so it never departs
            // twice, and one whose spending can't be recorded departs with nothing. Only a press
            // sent back to waiting is given back, and if that can't be recorded it stays spent
            // (the update is offered again for a new press).
            if (!record.Consume(request)) { return UpdateDeparture.Refused(ManageReason.Unavailable); }
            result = await LeaveAsync(parts, new NowPress(signed, args.Release!, args.Accept), cancellationToken);
            if (result.Outcome == UpdateDepartureOutcome.Waiting && !record.Restore(request))
            {
                result = UpdateDeparture.Refused(ManageReason.Unavailable);
            }
        }
        finally { Volatile.Write(ref departing, 0); }
        log($"update now {request[..8]}: {result.Outcome.ToString().ToLowerInvariant()}{(result.Code is { } c ? $": {c}" : "")}");
        Changed();
        return result;
    }

    // host.update.depart (LocalApi lets only the app send it): DepartAsync, answered on the pipe.
    // A request that isn't a UUID is the app's mistake, an error rather than an outcome.
    public async Task<UpdateDepartResult> DepartForAppAsync(string request)
    {
        if (!UpdateProtocol.IsRequest(request)) { throw new ArgumentException($"{HostOps.UpdateDepart} needs the press's request (a UUID)"); }
        var d = await DepartAsync(request);
        return new UpdateDepartResult(d.Outcome switch
        {
            UpdateDepartureOutcome.Committed => UpdateDepartOutcome.Committed,
            UpdateDepartureOutcome.Waiting => UpdateDepartOutcome.Waiting,
            _ => UpdateDepartOutcome.Refused,
        }, d.Code, d.Transition);
    }

    // Something Decorate reads changed outside the app's report: subscribers are sent the state again.
    void Changed()
    {
        lock (gate) { version++; }
        Push();
    }

    async Task<UpdateDeparture> LeaveAsync(UpdateNowParts parts, NowPress press, CancellationToken cancellationToken)
    {
        var ends = press.Signed.IssuedAt + UpdateProtocol.NowLife;
        bool Lapsed() => time.GetUtcNow() >= ends;
        // Its 15 minutes over: dropped before any lease, cap slot or sign-in is touched.
        if (Lapsed()) { return UpdateDeparture.Refused(ManageReason.Expired); }
        UpdateState state;
        lock (gate) { state = StateLocked(); }
        if (state.Available != press.Release) { return UpdateDeparture.Refused(UpdateCode.ReleaseChanged); }
        if (Ineligible(parts) is { } early) { return UpdateDeparture.Refused(early); }
        QuietLease? lease = null;
        for (var i = 0; i < DepartTries && lease is null; i++)
        {
            if (Lapsed()) { return UpdateDeparture.Refused(ManageReason.Expired); }
            var quiet = await parts.Lifecycle.TryAcquireQuiet(DepartWait, DeparturePurpose.Update, press.Signed, cancellationToken);
            lease = quiet.Value;
            // Only something being typed or delivered is waited out; anything else ends it now.
            if (lease is null && quiet.Reason is not (QuietBlockedReason.Typing or QuietBlockedReason.DeliveryInFlight or QuietBlockedReason.JournalUncertain))
            {
                return UpdateDeparture.Refused(ManageService.Reason(quiet.Reason));
            }
        }
        if (lease is not { } held) { return UpdateDeparture.Refused(ManageReason.NotQuiet); }
        var committed = false;
        try
        {
            if (Busy(parts).CoveredBy(press.Accept) is var before and not UpdateBusyView.Cover.Covered)
            {
                return UpdateDeparture.Waiting(UpdateBusyView.WaitingCode(before));
            }
            var receipt = await parts.Lifecycle.AnnouncePlanned(held, cancellationToken);
            if (receipt.Value is null) { return UpdateDeparture.Refused(ManageService.Reason(receipt.Reason)); }
            // Outside the fence: read-only may probe processes, and other profiles' hosts are listed.
            // Before the last look at what is busy and at the press's life, so neither can change
            // unseen while these run.
            parts.Eligibility.Refresh();
            parts.OtherHosts.Refresh();
            UpdateDeparture? late = null;
            var (departure, own, recorded) = parts.Manage.CommitUpdateNow(() =>
            {
                // Just before committing, under the cap's lock: a turn, question or approval that
                // came since, or a press past its 15 minutes, commits nothing and spends no slot.
                if (Busy(parts).CoveredBy(press.Accept) is var after and not UpdateBusyView.Cover.Covered)
                {
                    late = UpdateDeparture.Waiting(UpdateBusyView.WaitingCode(after));
                    return null;
                }
                if (Lapsed())
                {
                    late = UpdateDeparture.Refused(ManageReason.Expired);
                    return null;
                }
                BeforeCommit?.Invoke();
                return parts.Lifecycle.CommitDeparture(held);
            });
            if (late is not null) { return late; }
            // The restart slot could not be recorded: nothing was consumed or committed.
            if (!recorded) { return UpdateDeparture.Refused(ManageReason.Unavailable); }
            if (departure is null) { return UpdateDeparture.Refused(UpdateCode.RestartCap); }
            if (departure.Guard is not { } guard)
            {
                return UpdateDeparture.Refused(departure.Kind switch
                {
                    DepartureBlocker.Ineligible => Ineligible(parts) ?? ManageReason.Changed,
                    DepartureBlocker.OtherHostRunning => UpdateCode.OtherProfile,
                    var k => ManageService.Reason(k),
                });
            }
            committed = true;
            // Legacy sends no planned state, so nothing outside traces this restart to an update:
            // the host's own log does (SRE, settled decision 2).
            log($"restart for update: {state.Current ?? HostVersion} to {press.Release}, asked from {press.Signed.By}{(receipt.Value.Legacy ? "" : " (announced)")}" +
                $"{(own ? "" : ", a slot of the shared restart cap")}");
            _ = parts.Manage.NotifyUpdate(press.Signed, $"was updated now to {press.Release}");
            return UpdateDeparture.Committed(guard.Transition.ToString());
        }
        finally
        {
            // Not committed: the lease goes (a refused announcement or commit has let it go already).
            if (!committed) { held.Dispose(); }
        }
    }

    // The Update purpose's eligibility read now, as the press's code: the switch, read-only (the
    // acting answer) and a sign-in; null while eligible.
    string? Ineligible(UpdateNowParts parts) =>
        !parts.Manage.RemoteManageOn ? ManageReason.Disabled
        : ReadOnly() is not null ? ManageReason.ReadOnly
        : parts.Manage.SignInPending ? UpdateCode.SignIn
        : null;

    // Under the gate: when this device may press this kind again, in words, or null if it may now.
    string? Limited(string peer, string kind)
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
    // as does an Update now's past its life, and so does the memory of one it took. While the app
    // reporting doesn't say it carries Update now (gone, or an older one), Update now's asks go
    // too: one that doesn't read now would take the ask for a plain Install.
    void Prune()
    {
        var now = time.GetUtcNow();
        var carrying = Volatile.Read(ref carries);
        asks.RemoveAll(a => now - a.At > AskKept || a.Now is { } n && (now >= n.Until || !carrying));
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
        return new ChannelReply(Decorate(state), ch =>
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
            await channel.SendEventAsync(UpdateProtocol.Event, Decorate(state));
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
