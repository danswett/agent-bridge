using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentBridge.Host.Core.Intake;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// manage.* (docs/protocol/v1-sessions.md, "Managing a machine"; #801 slice A): a person's phone, browser or Windows app
// restarts this host, or repairs an agent's hooks, through the relay, so nobody has to sign in to
// the machine for either. The rules are remote update's (UpdateService, #160), made stricter:
//   * through the relay only: every manage op is refused on the local pipe, whoever asks (LocalApi
//     refuses them before they reach here, and this refuses them again), so the #67 guard on
//     host.restart and host.stop is untouched and gains no way around it;
//   * only a device the relay lists as a person's (pwa or app), connected now, that signed the
//     command itself (design §5.5, SignedCommands) for this machine, at most two minutes before;
//     never the app beside this host, whose key an agent here could use as the same user;
//   * nothing on a read-only machine, reads included;
//   * a restart leaves only through #805's quiet lease (LifecycleCoordinator): it is refused at once
//     when the lease cannot be had, never forced, never carried out later (owner, Q-O7);
//   * at most 2 committed restarts per machine every 6 hours until the signed roster (#55);
//   * a repair only for an agent this installation set up (its ownership record names this
//     profile), and only ever the Settings repair, never a removal.
// Stop, config and the Home Assistant token are refused here until #55, before their data is read.
public sealed class ManageService
{
    // Connection's answer to Q-C1 on #886: a person's device is pwa or app, as for update and folder trust.
    static readonly HashSet<string> PersonKinds = new(["pwa", "app"], StringComparer.Ordinal);
    public const string ThroughTheRelay = "managing a machine from another device goes through the relay; on this machine, use the app's Settings";
    public const string FileName = "manage.json";
    public const string PendingFile = "manage-pending.json";
    public const int RestartsBeforeRoster = 2;
    public static readonly TimeSpan RestartWindowBeforeRoster = TimeSpan.FromHours(6);
    public const int RestartsWithRoster = 6;
    public static readonly TimeSpan RestartWindowWithRoster = TimeSpan.FromHours(1);
    // Each device may make this many presses of each kind in RateWindow, as update allows.
    public const int RateLimit = 3;
    public static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Skew = TransferProtocol.Skew;
    // A pending restart older than this when the next host starts did not end by its own restart.
    public static readonly TimeSpan PendingStale = TimeSpan.FromMinutes(30);
    // How long a committed restart waits for its reply to be sent before leaving regardless.
    public static readonly TimeSpan LeaveAnyway = TimeSpan.FromSeconds(3);
    // How long leaving waits for the owner's other devices to be told (Q-O3).
    public static readonly TimeSpan NoticeWait = TimeSpan.FromSeconds(3);
    const int RequestsKept = 32;
    const int DevicesKept = 64;

    readonly P256KeyPair self;
    readonly string folder;
    readonly Action<string> log;
    readonly TimeProvider time;
    readonly Lock gate = new();
    // Presses being handled now: one sent again meanwhile is answered busy, never acted on twice.
    readonly HashSet<string> inFlight = new(StringComparer.Ordinal);
    readonly Dictionary<(string Device, string Kind), Queue<DateTimeOffset>> pressed = [];
    readonly Dictionary<string, DateTimeOffset> refusalLogged = new(StringComparer.Ordinal);
    readonly Dictionary<IProtocolChannel, bool> subscribers = [];
    Saved saved = new();
    Func<IReadOnlyList<PresenceEntry>>? presence;
    ManagePending? committed;
    DepartureGuard? departure;
    int acquiring;
    int repairing;
    int leaving;
    Task? restartNotice;
    // What manage-pending.json says, and whether this host has handed over to a replacement.
    Pending? pending;
    bool handedOver;

    public ManageService(P256KeyPair self, string folder, Action<string>? log = null, TimeProvider? time = null)
    {
        this.self = self;
        this.folder = folder;
        this.log = log ?? (_ => { });
        this.time = time ?? TimeProvider.System;

        Load();
    }

    // permissions.remoteManage: the one switch that takes the feature back. The host gives its
    // settings, so what the switch reads is wired here, where tests hold it (a mutant that wired it
    // on regardless once survived in Program). Off until given: a host that forgets is managed by nobody.
    public HostSettings Settings { init => Enabled = () => value.RemoteManage; }
    internal Func<bool> Enabled { get; set; } = () => false;
    // Why this machine acts on nothing from another device now (delivery.readOnly, agent-ha-bridge).
    public Func<string?> ReadOnly { get; init; } = () => null;
    // The same, perhaps a moment old, for what hello offers only.
    public Func<string?>? OfferedReadOnly { get; init; }
    public LifecycleCoordinator? Lifecycle { get; init; }
    // The condition registered for RemoteRestart, refreshed here just before each commit; without
    // it the registered condition is never fresh and every commit is refused Ineligible.
    public RemoteRestartEligibility? Eligibility { get; init; }
    // host.restart's own handover (#239): the replacement starts once this host has fully stopped.
    public Action RestartHost { get; init; } = () => { };
    // Set once hook setup is ready, after the service is made.
    public IHookRepair? Hooks { get; set; }
    public IManageNotices? Notices { get; init; }
    // #55: until the signed roster is enforced, stop, config and the token are refused, and the
    // restart cap is RestartsBeforeRoster.
    public IRosterGate Roster { get; init; } = new NoRoster();
    public string MachineName { get; init; } = "this machine";

    public void Attach(RelayClient? relay) => Volatile.Write(ref presence, relay is null ? null : () => relay.Presence);

    internal void AttachPresence(Func<IReadOnlyList<PresenceEntry>>? devices) => Volatile.Write(ref presence, devices);

    // The device id of the Windows app beside this host, which it gives when it reads the relay
    // account (host.relay.account, app only): its key may not manage this machine through the relay.
    public void NoteLocalApp(string? device)
    {
        if (!TransferProtocol.IsDeviceId(device)) { return; }
        lock (gate)
        {
            if (saved.AppDevices.Contains(device!)) { return; }
            saved.AppDevices.Add(device!);
            while (saved.AppDevices.Count > 4) { saved.AppDevices.RemoveAt(0); }
            Save();
        }
    }

    // What the last host left in manage-pending.json: how its restart ended.
    public void Start()
    {
        var path = Path.Combine(folder, PendingFile);
        Pending? left;
        try { left = File.Exists(path) ? JsonSerializer.Deserialize<Pending>(File.ReadAllText(path), Json) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            log($"manage: {PendingFile} could not be read ({e.Message}); it is let go");
            left = null;
        }
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (left is null || left.Request is null) { return; }
        var now = time.GetUtcNow();
        var took = now - left.At;
        // HandedOver is absent from a file an earlier version left, which handed over whenever it committed.
        var stopped = left.HandedOver == false;
        var done = left.Kind == "restart" && !stopped && took >= TimeSpan.Zero && took <= PendingStale;
        var outcome = done ? ManageOutcome.Done : ManageOutcome.Failed;
        var detail = done ? $"restarted in {Math.Max(1, (int)took.TotalSeconds)} s"
            : stopped ? "the host was stopped before it restarted, so it did not restart" : "the host stopped some other way";
        lock (gate)
        {
            Remember(new Entry(left.Request, left.Kind ?? "restart", outcome, done ? null : ManageReason.Changed, detail, left.By ?? "", now,
                left.Transition, done ? (int)took.TotalSeconds : null));
            Save();
        }
        Audit(left.Kind ?? "restart", left.By ?? "", left.Name, left.Request, outcome, null, detail);
    }

    // What hello offers this channel: manage on a relay channel from a person's device, where the
    // switch is on and the machine is not read-only; manage.hooks where hooks can be repaired.
    public IEnumerable<string> Offered(IProtocolChannel channel)
    {
        if (channel is not SecureChannel secure || !Enabled() || Lifecycle is null || (OfferedReadOnly ?? ReadOnly)() is not null) { yield break; }
        if (Who(secure.PeerId) is not { } who || !PersonKinds.Contains(who.Kind) || IsLocalApp(secure.PeerId)) { yield break; }
        yield return ManageProtocol.Capability;
        if (Hooks?.Available == true) { yield return ManageProtocol.HooksCapability; }
    }

    public async Task<object?> HandleAsync(IProtocolChannel channel, string op, JsonElement data)
    {
        if (channel is not SecureChannel secure) { throw new InvalidOperationException(ThroughTheRelay); }
        // Held until #55, refused before the data is read: a token sent anyway is never parsed.
        if (ManageProtocol.HeldUntilRoster.Contains(op))
        {
            return Refuse(secure.PeerId, op, null, ManageReason.NeedsRoster, "this needs the account's signed device roster (#55), which this machine does not have yet");
        }
        if (!Enabled()) { throw new InvalidOperationException("managing this machine from other devices is turned off here (permissions.remoteManage)"); }
        if (ReadOnly() is { } readOnly) { throw new InvalidOperationException($"this machine is read-only for other devices: {readOnly}"); }
        switch (op)
        {
            case ManageProtocol.Get:
                ReadAllowed(secure);
                return State();
            case ManageProtocol.Subscribe:
                ReadAllowed(secure);
                return Subscribe(channel);
            case ManageProtocol.Restart:
                return await RestartAsync(secure, data);
            case ManageProtocol.HooksRepair:
                return await RepairAsync(secure, data);
            default:
                return null;
        }
    }

    void ReadAllowed(SecureChannel secure)
    {
        if (Who(secure.PeerId) is not { } who || !PersonKinds.Contains(who.Kind))
        {
            throw new InvalidOperationException("only a person's phone, browser or Windows app can manage a machine");
        }
        // As for presses: the app beside this host reads it from its Settings, not through the relay.
        if (IsLocalApp(secure.PeerId)) { throw new InvalidOperationException(ThroughTheRelay); }
    }

    async Task<object> RestartAsync(SecureChannel channel, JsonElement data)
    {
        var check = Check(channel, data, ManageProtocol.Restart, "restart");
        if (check.Verified is not { } v) { return check.Refusal!; }
        try { return await RestartVerifiedAsync(channel, v); }
        finally { lock (gate) { inFlight.Remove(v.Request); } }
    }

    async Task<object> RestartVerifiedAsync(SecureChannel channel, Verified v)
    {
        // Q-C4 (Connection, #886): the shared CommandBody carries args for the actions that define
        // them; a restart defines none, so a body with any is not this press.
        if (v.Body.Args is not null || v.Body.ContentSha256 is not null)
        {
            return Refused(v, "restart", ManageReason.BadArgs, "a restart takes no arguments");
        }
        var now = time.GetUtcNow();
        lock (gate)
        {
            // Once one restart has committed, another press joins it rather than leaving twice. It
            // leaves nothing itself: the committing press has armed its reply's leave and the
            // fallback, and one here could leave before that press's notice is even queued (Q-O3).
            if (committed is { } going)
            {
                var joined = Remember(new Entry(v.Request, "restart", ManageOutcome.Accepted, null, "joined a restart already under way", v.By, now, going.Transition, null));
                Save();
                return Result(joined) with { Transition = going.Transition };
            }
            if (RestartsLeft(now) is (0, var next))
            {
                return Refused(v, "restart", ManageReason.RateLimited,
                    $"the {RestartCap.Count} remote restarts this machine allows every {RestartCap.Window.TotalHours:0} hours are used; the next is allowed at {Clock(next!.Value)}");
            }
        }
        if (Lifecycle is not { } lifecycle) { return Refused(v, "restart", ManageReason.Unavailable, "this host cannot leave safely yet"); }
        if (Interlocked.Exchange(ref acquiring, 1) == 1) { return Refused(v, "restart", ManageReason.Busy, "another restart is being asked for right now"); }
        try
        {
            // No forced or "now" path (Q-O7): without the lease, the press is refused.
            var quiet = await lifecycle.TryAcquireQuiet(DeparturePurpose.RemoteRestart);
            if (quiet.Value is not { } lease) { return Refused(v, "restart", Reason(quiet.Reason), Words(quiet.Reason)); }
            var receipt = await lifecycle.AnnouncePlanned(lease);
            if (!receipt.Succeeded) { return Refused(v, "restart", Reason(receipt.Reason), Words(receipt.Reason)); }
            // Outside the fence: the read-only look may probe processes, which commit must not wait on.
            Eligibility?.Refresh();
            var departure = lifecycle.CommitDeparture(lease);
            if (departure.Guard is not { } guard) { return Refused(v, "restart", Reason(departure.Kind), Words(departure.Kind)); }
            var transition = guard.Transition.ToString();
            now = time.GetUtcNow();
            Entry accepted;
            lock (gate)
            {
                this.departure = guard;
                committed = new ManagePending(v.Request, "restart", v.By, now, transition);
                saved.Restarts.Add(now);
                accepted = Remember(new Entry(v.Request, "restart", ManageOutcome.Accepted, null, "restarting", v.By, now, transition, null));
                Save();
            }
            lock (gate) { WritePending(pending = new Pending(v.Request, "restart", v.By, v.Name, transition, !receipt.Value!.Legacy, now, handedOver)); }
            Audit("restart", v.By, v.Name, v.Request, ManageOutcome.Accepted, null, "restarting");
            Volatile.Write(ref restartNotice, Notify(v, "was restarted"));
            PushState();
            // After the reply has gone, so the person sees accepted before the channel closes. The host
            // is fenced from here on, so it leaves anyway if the reply cannot be sent (a dropped channel).
            // Inline on the timer's thread, so under a fake clock the host has left by the time Advance returns.
            _ = Task.Delay(LeaveAnyway, time).ContinueWith(_ => Leave(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return new ChannelReply(Result(accepted) with { Transition = transition }, _ => { Leave(); return Task.CompletedTask; });
        }
        finally { Volatile.Write(ref acquiring, 0); }
    }

    // Leaving happens once, whichever of the reply or the fallback gets here first.
    // The owner's other devices hear of it first (Q-O3, so a stolen device can't act silently): the
    // notice was only queued, and went with the host's memory when it left. NoticeWait bounds that;
    // one already sent (or no device to tell) leaves at once, inline.
    void Leave()
    {
        if (Interlocked.Exchange(ref leaving, 1) == 0) { _ = LeaveAsync(); }
    }

    async Task LeaveAsync()
    {
        try { await (Volatile.Read(ref restartNotice) ?? Task.CompletedTask).WaitAsync(NoticeWait, time); }
        // Whatever became of the notice, the host has committed and leaves.
        catch (Exception e)
        {
            log($"manage: the restart's notice to other devices was not confirmed sent ({e.GetType().Name}); leaving anyway");
        }
        RestartHost();
    }

    async Task<object> RepairAsync(SecureChannel channel, JsonElement data)
    {
        var check = Check(channel, data, ManageProtocol.HooksRepair, "repair");
        if (check.Verified is not { } v) { return check.Refusal!; }
        try { return await RepairVerifiedAsync(v); }
        finally { lock (gate) { inFlight.Remove(v.Request); } }
    }

    async Task<object> RepairVerifiedAsync(Verified v)
    {
        // Q-C4: a repair defines one argument, agent, and nothing travels beside it.
        if (v.Body.ContentSha256 is not null || v.Body.Args is { } given && (given.ValueKind != JsonValueKind.Object || given.EnumerateObject().Any(p => p.Name != "agent")))
        {
            return Refused(v, "repair", ManageReason.BadArgs, "a repair takes one argument, agent");
        }
        if (Hooks is not { Available: true } hooks) { return Refused(v, "repair", ManageReason.Unavailable, "hooks cannot be set up on this machine"); }
        ManageRepairArgs? args = null;
        try { args = v.Body.Args is { ValueKind: JsonValueKind.Object } a ? a.Deserialize<ManageRepairArgs>(ProtocolJson.Options) : null; }
        catch (JsonException) { }
        if (args?.Agent is not { } agent || !ManageProtocol.Agents.Contains(agent))
        {
            return Refused(v, "repair", ManageReason.InvalidValue, $"name one agent to repair: {string.Join(", ", ManageProtocol.Agents)}");
        }
        if (hooks.NotOwned(agent) is { } notOwned)
        {
            return Refused(v, "repair", ManageReason.Unavailable, $"{notOwned}; connecting an agent stays at the machine");
        }
        if (Interlocked.Exchange(ref repairing, 1) == 1) { return Refused(v, "repair", ManageReason.Busy, "a repair is already running on this machine"); }
        try
        {
            // Looked at again just before the repair runs, as a restart's commit does: the switch or
            // read-only may have changed while this press was checked, and turning the switch off
            // must take back a press already on its way.
            if (!Enabled()) { return Refused(v, "repair", ManageReason.Disabled, "managing this machine from other devices was turned off here (permissions.remoteManage); nothing was done"); }
            if (ReadOnly() is { } nowReadOnly) { return Refused(v, "repair", ManageReason.ReadOnly, $"this machine became read-only for other devices: {nowReadOnly}; nothing was done"); }
            PushState();
            ManageOutcome outcome;
            string detail;
            bool? restartRequired = null;
            try
            {
                var result = await hooks.RepairAsync(agent);
                outcome = result.Completed ? ManageOutcome.Done : ManageOutcome.Failed;
                detail = result.Detail;
                restartRequired = result.RestartRequired;
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                (outcome, detail) = (ManageOutcome.Failed, e.Message);
            }
            detail = Limits.KeepStart(detail.Trim(), ManageProtocol.Detail) ?? "";
            Entry entry;
            lock (gate)
            {
                entry = Remember(new Entry(v.Request, "repair", outcome, outcome == ManageOutcome.Failed ? ManageReason.Unavailable : null, detail, v.By, time.GetUtcNow(), null, null));
                Save();
            }
            Audit("repair", v.By, v.Name, v.Request, outcome, null, $"{agent}{(outcome == ManageOutcome.Done ? "" : ": failed")}");
            if (outcome == ManageOutcome.Done) { _ = Notify(v, $"had its {Titled(agent)} hooks repaired"); }
            return Result(entry) with { RestartRequired = restartRequired };
        }
        finally
        {
            Volatile.Write(ref repairing, 0);
            PushState();
        }
    }

    internal sealed record Verified(CommandBody Body, string Request, string Device, string By, string Name);
    internal sealed record Checked(Verified? Verified, ManageResult? Refusal);

    // Every rule a press must pass, in the order docs/protocol/v1-sessions.md ("Managing a machine") gives. A request
    // already answered gets that answer again, even past its lifetime, and never acts again; one
    // being handled now is busy. A press that passes is in flight until its caller lets it go.
    Checked Check(SecureChannel channel, JsonElement data, string op, string kind)
    {
        var peer = channel.PeerId;
        ManageCommand? request = null;
        try { request = data.ValueKind == JsonValueKind.Object ? data.Deserialize<ManageCommand>(ProtocolJson.Options) : null; }
        catch (JsonException) { }
        if (request?.Command is not { } command || Peek(command) is not { } peeked)
        {
            return new(null, Refuse(peer, op, null, ManageReason.BadSignature, "the command is malformed"));
        }
        // Verified as of its own signing time, so the MAC and every field are checked first; its
        // lifetime is checked below, after the replay check.
        var (body, problem) = SignedCommands.Verify(self, command, op, DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(peeked.IssuedAt, 0, 253402300799)));
        if (body is null) { return new(null, Refuse(peer, op, null, ManageReason.BadSignature, problem ?? "the command could not be verified")); }
        var requestId = Guid.Parse(body.Request).ToString();
        if (body.Principal.Device != peer) { return new(null, Refuse(peer, op, requestId, ManageReason.NotPeer, "the command must be signed by the device sending it")); }
        if (Who(peer) is not { } who) { return new(null, Refuse(peer, op, requestId, ManageReason.NotConnected, "the device asking is not connected to the relay now")); }
        if (!PersonKinds.Contains(who.Kind)) { return new(null, Refuse(peer, op, requestId, ManageReason.NotPerson, "only a person's phone, browser or Windows app can manage a machine")); }
        if (IsLocalApp(peer)) { return new(null, Refuse(peer, op, requestId, ManageReason.LocalDevice, "the app on this machine manages it from its Settings, not through the relay")); }
        if (ReadOnly() is { } readOnly) { return new(null, Refuse(peer, op, requestId, ManageReason.ReadOnly, $"this machine is read-only for other devices: {readOnly}")); }
        var by = $"{who.Kind} {peer[..8]}";
        var name = Limits.KeepStart(HostConfigFile.CleanName(who.Name), ManageProtocol.Name) ?? "";
        var verified = new Verified(body, requestId, peer, by, name);
        lock (gate)
        {
            if (saved.Requests.FirstOrDefault(e => e.Request == requestId) is { } taken) { return new(null, Result(taken)); }
            if (inFlight.Contains(requestId)) { return new(null, Refuse(peer, op, requestId, ManageReason.Busy, "this press is already being handled")); }
        }
        var now = time.GetUtcNow();
        var issued = DateTimeOffset.FromUnixTimeSeconds(body.IssuedAt);
        var expires = DateTimeOffset.FromUnixTimeSeconds(body.ExpiresAt);
        if (issued - Skew > now) { return new(null, Refuse(peer, op, requestId, ManageReason.Future, "the command was signed in the future; check the clocks of the two devices")); }
        if (expires < now) { return new(null, Refuse(peer, op, requestId, ManageReason.Expired, "the command has expired; press again")); }
        if (expires - issued > ManageProtocol.MaxLifetime) { return new(null, Refuse(peer, op, requestId, ManageReason.Expired, "a management command lasts at most 2 minutes")); }
        lock (gate)
        {
            // Again: the first press may have finished between the two looks.
            if (saved.Requests.FirstOrDefault(e => e.Request == requestId) is { } done) { return new(null, Result(done)); }
            if (inFlight.Contains(requestId)) { return new(null, Refuse(peer, op, requestId, ManageReason.Busy, "this press is already being handled")); }
            if (Limited(peer, kind, now) is { } again)
            {
                return new(null, Refuse(peer, op, requestId, ManageReason.RateLimited, $"too many presses of {Titled(kind)} from this device just now; try again {again}"));
            }
            inFlight.Add(requestId);
        }
        return new(verified, null);
    }

    static CommandBody? Peek(SignedCommand command)
    {
        try
        {
            return BlobFormat.FromBase64Url(command.Body) is { } bytes ? JsonSerializer.Deserialize<CommandBody>(bytes, ProtocolJson.Options) : null;
        }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException) { return null; }
    }

    PresenceEntry? Who(string peer) => Volatile.Read(ref presence) is { } devices ? devices().FirstOrDefault(d => d.Id == peer) : null;

    bool IsLocalApp(string peer)
    {
        lock (gate) { return saved.AppDevices.Contains(peer); }
    }

    (int Count, TimeSpan Window) RestartCap => Roster.Enforcing ? (RestartsWithRoster, RestartWindowWithRoster) : (RestartsBeforeRoster, RestartWindowBeforeRoster);

    // Under the gate: how many restarts the cap allows now, and when the next frees up. Only
    // restarts that committed are counted: a refused press uses up nothing.
    (int Left, DateTimeOffset? NextAt) RestartsLeft(DateTimeOffset now)
    {
        var (count, window) = RestartCap;
        saved.Restarts.RemoveAll(t => now - t >= window);
        var left = Math.Max(0, count - saved.Restarts.Count);
        return (left, left == 0 ? saved.Restarts.Min() + window : null);
    }

    string? Limited(string peer, string kind, DateTimeOffset now)
    {
        if (!pressed.TryGetValue((peer, kind), out var times))
        {
            while (pressed.Count >= DevicesKept) { pressed.Remove(pressed.MinBy(p => p.Value.Count > 0 ? p.Value.Last() : DateTimeOffset.MinValue).Key); }
            pressed[(peer, kind)] = times = new Queue<DateTimeOffset>();
        }
        while (times.Count > 0 && now - times.Peek() >= RateWindow) { times.Dequeue(); }
        if (times.Count >= RateLimit)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling((times.Peek() + RateWindow - now).TotalMinutes));
            return $"in {minutes} minute{(minutes == 1 ? "" : "s")}";
        }
        times.Enqueue(now);
        return null;
    }

    ManageResult Refuse(string peer, string op, string? request, string reason, string detail)
    {
        bool say;
        lock (gate)
        {
            var now = time.GetUtcNow();
            say = !refusalLogged.TryGetValue(peer, out var at) || now - at >= TimeSpan.FromMinutes(1);
            if (say)
            {
                while (!refusalLogged.ContainsKey(peer) && refusalLogged.Count >= DevicesKept) { refusalLogged.Remove(refusalLogged.MinBy(p => p.Value).Key); }
                refusalLogged[peer] = now;
            }
        }
        if (say) { log($"manage {op[7..]} from {peer[..Math.Min(8, peer.Length)]} at {Clock(time.GetUtcNow(), seconds: true)}: refused: {reason}: {detail}"); }
        // No state here: a refusal before a person's press is verified would otherwise hand what
        // manage.get keeps from other machines, and from anyone while read-only or switched off,
        // to whoever sent it. Refused adds the state once the press is a verified person's.
        return new ManageResult { Request = request, Outcome = ManageOutcome.Refused, Reason = reason, Detail = Limits.KeepStart(detail, ManageProtocol.Detail) };
    }

    // A verified press that is refused: remembered, so the same press sent again gets the same answer.
    ManageResult Refused(Verified v, string kind, string reason, string detail)
    {
        var result = Refuse(v.Device, $"manage.{kind}", v.Request, reason, detail);
        lock (gate)
        {
            Remember(new Entry(v.Request, kind, ManageOutcome.Refused, reason, result.Detail, v.By, time.GetUtcNow(), null, null));
            Save();
        }
        return result with { State = State() };
    }

    ManageResult Result(Entry e) => new()
    {
        Request = e.Request,
        Outcome = e.Outcome,
        Reason = e.Reason,
        Detail = e.Detail,
        Transition = e.Transition,
        State = State(),
    };

    public ManageState State()
    {
        lock (gate)
        {
            var (left, next) = RestartsLeft(time.GetUtcNow());
            var last = saved.Requests.LastOrDefault();
            return new ManageState
            {
                Phase = committed is not null ? ManagePhase.Restarting : Volatile.Read(ref repairing) == 1 ? ManagePhase.Repairing : ManagePhase.Idle,
                Pending = committed,
                Restarts = new ManageRestarts(left, next),
                Last = last is null ? null : new ManageLast(last.Request, last.Kind, last.Outcome, last.By, last.At, last.Reason, last.TookSeconds),
            };
        }
    }

    ManageState Subscribe(IProtocolChannel channel)
    {
        lock (gate)
        {
            if (subscribers.TryAdd(channel, true))
            {
                _ = channel.Closed.ContinueWith(_ => { lock (gate) { subscribers.Remove(channel); } }, TaskScheduler.Default);
            }
        }
        return State();
    }

    void PushState()
    {
        IProtocolChannel[] to;
        lock (gate) { to = [.. subscribers.Keys]; }
        if (to.Length == 0) { return; }
        var state = State();
        foreach (var ch in to)
        {
            _ = Send(ch, state);
        }
        static async Task Send(IProtocolChannel ch, ManageState state)
        {
            try { await ch.SendEventAsync(ManageProtocol.Event, state); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
        }
    }

    // One line per outcome in the host's log: who (device kind, short id and name), what, when (in
    // Pacific time) and how it ended. Never a value, a token or a signed command.
    void Audit(string kind, string by, string? name, string request, ManageOutcome outcome, string? reason, string? detail) =>
        log($"manage {kind} by {by}{(string.IsNullOrEmpty(name) ? "" : $" \"{name}\"")} request {request[..Math.Min(8, request.Length)]} at {Clock(time.GetUtcNow(), seconds: true)}: " +
            $"{outcome.ToString().ToLowerInvariant()}{(reason is null ? "" : $": {reason}")}{(string.IsNullOrEmpty(detail) ? "" : $": {detail}")}");

    // The owner's other devices hear of it quietly (Q-O3): the machine, what, and who; no values.
    Task Notify(Verified v, string what) =>
        Notices?.Notify(v.Device, MachineName, $"{MachineName} {what} from {(v.Name.Length > 0 ? v.Name : "a device")} ({v.By}) at {Clock(time.GetUtcNow())}.") ?? Task.CompletedTask;

    // Called as this host hands over to its replacement, whoever asked for the restart. A committed
    // restart can still end without one: a stop that comes while it settles wins (SettledRestart),
    // and the next host, started by hand later, once reported that press as done.
    public void HandingOver()
    {
        lock (gate)
        {
            handedOver = true;
            if (pending is { HandedOver: false } written) { WritePending(pending = written with { HandedOver = true }); }
        }
    }

    void WritePending(Pending pending)
    {
        try { Atomic(Path.Combine(folder, PendingFile), JsonSerializer.Serialize(pending, Json)); }
        // The departure has committed and the host is fenced; it leaves whatever this says, and the
        // next host reports the press failed rather than done.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"manage: {PendingFile} could not be written ({e.Message})"); }
    }

    static string Reason(QuietBlockedReason? r) => r switch
    {
        QuietBlockedReason.Typing or QuietBlockedReason.DeliveryInFlight or QuietBlockedReason.JournalUncertain or QuietBlockedReason.ConditionFault => ManageReason.NotQuiet,
        QuietBlockedReason.LifecycleInProgress => ManageReason.Busy,
        QuietBlockedReason.AnnounceUnacked or QuietBlockedReason.AnnounceUnavailable => ManageReason.AnnounceFailed,
        QuietBlockedReason.Cancelled => ManageReason.Cancelled,
        _ => ManageReason.Changed,
    };

    static string Words(QuietBlockedReason? r) => r switch
    {
        QuietBlockedReason.Typing => "an answer or reply is being typed into a session right now; try again in a moment",
        QuietBlockedReason.DeliveryInFlight => "an answer, reply, launch or stop is under way; try again in a moment",
        QuietBlockedReason.JournalUncertain or QuietBlockedReason.ConditionFault => "the host cannot be sure nothing is being delivered, so it will not leave now",
        QuietBlockedReason.LifecycleInProgress => "another restart, stop or update is already under way",
        QuietBlockedReason.AnnounceUnacked or QuietBlockedReason.AnnounceUnavailable => "the relay did not acknowledge the planned restart, so the host stays",
        QuietBlockedReason.Cancelled => "the restart was cancelled",
        _ => "something changed while the host was getting ready to leave; nothing was done",
    };

    static string Reason(DepartureBlocker? k) => k switch
    {
        DepartureBlocker.AnnounceUnacked => ManageReason.AnnounceFailed,
        // LeaseExpired (#885) is "changed, try again", as the lease's own expiry is in Reason(QuietBlockedReason).
        DepartureBlocker.Ineligible or DepartureBlocker.LeaseExpired or DepartureBlocker.ChannelChanged or DepartureBlocker.AppRunning or DepartureBlocker.OtherHostRunning => ManageReason.Changed,
        _ => ManageReason.NotQuiet,
    };

    static string Words(DepartureBlocker? k) => k switch
    {
        DepartureBlocker.Typing => Words(QuietBlockedReason.Typing),
        DepartureBlocker.DeliveryInFlight or DepartureBlocker.TransferInFlight => Words(QuietBlockedReason.DeliveryInFlight),
        DepartureBlocker.AnnounceUnacked => Words(QuietBlockedReason.AnnounceUnacked),
        DepartureBlocker.Ineligible => "this machine can no longer be restarted from another device (it became read-only, or a sign-in is in progress); nothing was done",
        DepartureBlocker.JournalUncertain or DepartureBlocker.ConditionFault => Words(QuietBlockedReason.JournalUncertain),
        _ => Words(QuietBlockedReason.EligibilityChanged),
    };

    static string Titled(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // In Pacific time, as everything this project shows, whatever the machine's own zone.
    static readonly TimeZoneInfo Pacific = FindPacific();

    static TimeZoneInfo FindPacific()
    {
        foreach (var id in new[] { "America/Los_Angeles", "Pacific Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc;
    }

    internal static string Clock(DateTimeOffset at, bool seconds = false) =>
        TimeZoneInfo.ConvertTime(at, Pacific).ToString(seconds ? "yyyy-MM-dd HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture) + " PT";

    // ---- what is kept on disk, beside the device key ----

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    sealed record Entry(string Request, string Kind, ManageOutcome Outcome, string? Reason, string? Detail, string By, DateTimeOffset At, string? Transition, int? TookSeconds);

    sealed record Pending(string? Request, string? Kind, string? By, string? Name, string? Transition, bool Announced, DateTimeOffset At, bool? HandedOver = null);

    sealed class Saved
    {
        public int V { get; set; } = 1;
        public List<DateTimeOffset> Restarts { get; set; } = [];
        public List<Entry> Requests { get; set; } = [];
        public List<string> AppDevices { get; set; } = [];
    }

    // Under the gate. The latest entry for a request replaces any before it.
    Entry Remember(Entry e)
    {
        saved.Requests.RemoveAll(x => x.Request == e.Request);
        saved.Requests.Add(e);
        while (saved.Requests.Count > RequestsKept) { saved.Requests.RemoveAt(0); }
        return e;
    }

    void Load()
    {
        var path = Path.Combine(folder, FileName);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(path), Json) is { V: 1 } s)
            {
                s.Requests = [.. s.Requests.Where(e => e?.Request is not null).TakeLast(RequestsKept)];
                s.AppDevices = [.. s.AppDevices.Where(TransferProtocol.IsDeviceId).TakeLast(4)];
                saved = s;
            }
        }
        // Unreadable: start empty. The restart cap then counts from now, which errs towards one
        // more restart, and the presses answered before are forgotten: one sent again within its
        // two-minute lifetime is checked afresh (its rate limit and the quiet lease still apply).
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            log($"manage: {FileName} could not be read ({e.Message}); starting afresh");
        }
    }

    // Under the gate.
    void Save()
    {
        try { Atomic(Path.Combine(folder, FileName), JsonSerializer.Serialize(saved, Json)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"manage: {FileName} could not be written ({e.Message})"); }
    }

    static void Atomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, Encoding.UTF8);
        File.Move(temp, path, overwrite: true);
    }
}

// Repairing an agent's hooks, as Settings' Repair does (#670).
public interface IHookRepair
{
    bool Available { get; }
    // Null when this installation set the agent up; otherwise why it may not be repaired remotely.
    string? NotOwned(string agent);
    Task<AgentSetupResult> RepairAsync(string agent);
}

public sealed class AgentSetupHookRepair(AgentSetupService setup, IntakeState.AgentHomes homes, string profile) : IHookRepair
{
    public bool Available => true;

    public string? NotOwned(string agent) => HookOwnership.NotOwned(agent switch
    {
        "claude" => homes.Claude,
        "codex" => homes.Codex,
        _ => homes.Copilot,
    }, profile);

    // install is Settings' Set up / Repair; remove is never sent from here.
    public Task<AgentSetupResult> RepairAsync(string agent) => setup.ApplyAsync(new AgentSetupRequest(agent, "install"));
}

// Whether this profile owns an agent's hook setup: its agent-bridge-owner.json, version 1, names
// this profile's folder. The same record AgentReadinessService reads; a setup that never completed
// is still owned, and repairing it is what Repair is for.
public static class HookOwnership
{
    public const string FileName = "agent-bridge-owner.json";

    public static string? NotOwned(string home, string profile)
    {
        try
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(Path.Combine(home, FileName)); }
            catch (Exception absent) when (absent is FileNotFoundException or DirectoryNotFoundException) { return "this agent was never set up by this machine's app"; }
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var number) || number != 1
                || !root.TryGetProperty("profile", out var owner) || owner.ValueKind != JsonValueKind.String
                || owner.GetString() is not { } path || !Path.IsPathFullyQualified(path))
            {
                return "this agent's hook ownership record is not recognized";
            }
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile)), comparison)
                ? null : "another bridge profile owns this agent's hook setup";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return "this agent's hook ownership could not be checked";
        }
    }
}

// The quiet notice to the owner's other devices (Q-O3): never to the device that acted. The task
// ends once every device has been sent it, or will not be; a restart waits on it, bounded, to leave.
public interface IManageNotices
{
    Task Notify(string exceptDevice, string machineName, string body);
}

// #55: whether the account's signed device roster is enforced here. Nothing implements it yet, so
// stop, config and the token stay refused and the restart cap stays at its pre-roster value.
public interface IRosterGate
{
    bool Enforcing { get; }
}

public sealed class NoRoster : IRosterGate
{
    public bool Enforcing => false;
}

// #800 (Connection's): a sign-in an agent is running for a person. Its flow lives in the host's
// AgentSignInService and a restart cancels it (Connection's answer to Q-C8 on #886), so a remote
// restart refuses while one could be pending: the default blocks whenever the sign-in entrypoint is
// turned on. Narrowing that to "a flow is active" waits for Connection's read-only seam.
public interface IAgentSignInDeparture
{
    bool BlocksDeparture { get; }
}

public sealed class RefuseWhileSignInEnabled(Func<bool> signInEnabled) : IAgentSignInDeparture
{
    public bool BlocksDeparture => signInEnabled();
}

// The host's own: SessionService binds it to its #800 sign-in entrypoint when given its
// ManageService, so the check is wired where tests hold it (a mutant that left it unwired once
// survived in Program). Until bound, nothing is pending.
sealed class BoundSignIn : IAgentSignInDeparture
{
    Func<bool> on = () => false;
    public void Bind(Func<bool> signInEnabled) => Volatile.Write(ref on, signInEnabled);
    public bool BlocksDeparture => Volatile.Read(ref on)();
}

// The registered condition a remote restart's CommitDeparture checks again: the machine still not
// read-only, the switch still on, and no #800 sign-in it could cut off. #805 runs IsBlocking under
// the commit's exclusion, where it must not block or do I/O, and the read-only look can probe for
// agent-ha-bridge's process. So the sources are read by Refresh, outside the fence, just before the
// commit, and IsBlocking reads only that snapshot. One never refreshed, or older than MaxAge, blocks.
public sealed class RemoteRestartEligibility(Func<string?> readOnly, Func<bool> enabled, IAgentSignInDeparture signIns, TimeProvider? time = null) : IDepartureCondition
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(5);
    readonly TimeProvider time = time ?? TimeProvider.System;
    sealed record Snapshot(bool Eligible, long At);
    Snapshot? last;

    // The host's: the switch is its settings' permissions.remoteManage, and #800's sign-in is
    // bound by SessionService (BindSignIn).
    public static RemoteRestartEligibility For(HostSettings settings, Func<string?> readOnly, TimeProvider? time = null) =>
        new(readOnly, () => settings.RemoteManage, new BoundSignIn(), time);

    internal void BindSignIn(Func<bool> signInEnabled) => (signIns as BoundSignIn)?.Bind(signInEnabled);

    public DepartureBlocker Kind => DepartureBlocker.Ineligible;

    public void Refresh()
    {
        var at = time.GetTimestamp();
        bool eligible;
        try { eligible = readOnly() is null && enabled() && !signIns.BlocksDeparture; }
        catch { eligible = false; }
        Volatile.Write(ref last, new Snapshot(eligible, at));
    }

    public bool IsBlocking() => Volatile.Read(ref last) is not { Eligible: true } s || time.GetElapsedTime(s.At) > MaxAge;
}
