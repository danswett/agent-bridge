using System.Collections.Concurrent;
using AgentBridge.Host.Core.Updates;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// The app's own updates (Velopack in the app; a fake in tests). An app update is a host update:
// both ship in one package with one version.
public interface IUpdater
{
    // Null when this copy was not installed by Velopack (a build folder): no updates then.
    string? InstalledVersion { get; }
    Task<string?> CheckAsync();
    // When the feed itself was last asked, where a check may be answered from the machine's
    // shared copy of it (#530): null when every check asks the feed.
    DateTimeOffset? DiscoveredAt => null;
    // The release notes of the release the last check found, when it has any.
    string? PendingNotes => null;
    Task DownloadAsync(Action<int> progress);
    // The release downloaded and ready to apply: the one the last check found, once downloaded.
    // Null when a later check (or a change of feed) has found another since, or none.
    string? Ready { get; }
    // Exits the app, replaces it, and starts the new version: only the release named, the one the
    // person or the updater chose, and only while it is the one ready. Anything else throws before
    // anything is replaced (a check or download meanwhile may have replaced it; r4487830884).
    void ApplyAndRestart(string version);
    // Where releases come from now (the feed, and Include pre-releases): what a check found, or a
    // download fetched, belongs to it.
    string Source => "";
}

// Whether the app itself starts at sign-in (into the notification area), so toasts keep coming
// after a restart. A Run entry in the app; a fake in tests.
public interface IAppAtSignIn
{
    bool Enabled { get; }
    void Set(bool on);
}

// The Settings page: this machine's host (running, version, start at sign-in, start, stop,
// restart, its log), its relay sign-in and name, what it lets other devices do, and the app's
// own preferences and updates.
public sealed class SettingsViewModel : Observable
{
    readonly HostConnection host;
    readonly HostManager manager;
    readonly IUpdater updater;
    readonly IAppAtSignIn appAtSignIn;
    readonly Func<AppPreferences> prefs;
    readonly Action<AppPreferences> savePrefs;
    string relayUrl;
    string machineName = "";
    bool nameEdited;
    string? error;
    bool busy;
    int busyActions;
    string updateStatus = "";
    string? updateVersion;
    // The release Settings itself saw downloaded and ready (its check, or its Restart to update),
    // whatever its status line says since (a Not now or a refusal clears that).
    string? readyHere;
    // Changes of source (Include pre-releases), and the one updateVersion was found on: a release
    // found on another source is not offered, however busy Settings was when the source changed.
    int sourceAt;
    int foundAt;
    string log = "";
    string agentInstallStatus = "";
    string agentSignInStatus = "";
    bool agentInstallInProgress;
    bool installRequestPending;
    string? installing;
    HostStatus? installStatusSeen;
    // Each row's last setup note, by agent: rows are rebuilt from every status, the notes are not.
    readonly Dictionary<string, AgentSetupNote> agentNotes = new(StringComparer.Ordinal);
    // Each hooks' last setup result, by the agent whose hooks they are (AgentReadinessRow.HooksOf):
    // Copilot CLI's serve Agency's row too.
    readonly Dictionary<string, AgentSetupNote> hookResults = new(StringComparer.Ordinal);
    // Counts the times the host was seen gone: a setup that ends after its host went (#705 review)
    // says nothing on the rows of the host that came back.
    int hostsGone;

    public SettingsViewModel(HostConnection host, HostManager manager, IUpdater updater, IAppAtSignIn appAtSignIn, Func<AppPreferences> prefs, Action<AppPreferences> savePrefs, string appVersion, string defaultRelay)
    {
        this.host = host;
        this.manager = manager;
        this.updater = updater;
        this.appAtSignIn = appAtSignIn;
        this.prefs = prefs;
        this.savePrefs = savePrefs;
        AppVersion = appVersion;
        relayUrl = prefs().Relay ?? defaultRelay;
        trust = TrustEditor.For(host);
        trust.Changed += Refresh;
        host.Changed += Refresh;
        Refresh();
    }

    public string AppVersion { get; }

    // The same page over another updater: the self-test's stand-in release, for its pictures of
    // the update row (Creative's acceptance on #719). The app itself never uses it.
    public SettingsViewModel WithUpdater(IUpdater other) => new(host, manager, other, appAtSignIn, prefs, savePrefs, AppVersion, relayUrl);

    // Done with such a page: it stops following the host.
    public void Detach()
    {
        trust.Changed -= Refresh;
        host.Changed -= Refresh;
    }
    public string InstalledText => updater.InstalledVersion is { } v ? $"Installed version {v}" : "Not installed (a development copy): updates are off";
    HostStatus? Status => host.Status;

    public string HostState => host.State switch
    {
        LinkState.Connected => $"Running (pid {Status?.ProcessId}, since {Status?.StartedAt.ToLocalTime():g})",
        LinkState.Refused => $"The host refused this app: {host.StateDetail}",
        LinkState.NotRunning => manager.HostInstalled ? "Not running" : "Not installed beside the app",
        _ => host.StateDetail is { } d ? $"Connecting… ({d})" : "Connecting…",
    };
    public bool HostRunning => host.State == LinkState.Connected;
    public string HostVersion => Status is { } s ? $"{s.Version}{(s.Dev ? " (dev)" : "")}" : "";
    public bool VersionSkew => Status is { } s && !string.Equals(s.Version, AppVersion.Split('+')[0], StringComparison.Ordinal);
    public string Profile => manager.Layout.Profile;
    public string? AgentHaBridge => Status?.AgentHaBridge;
    public bool HasAgentHaBridge => !string.IsNullOrEmpty(Status?.AgentHaBridge);
    public string? HomeAssistantRefused => Status?.HomeAssistant;
    public bool HasHomeAssistantRefused => !string.IsNullOrEmpty(Status?.HomeAssistant);
    public bool RestartRequired => Status?.RestartRequired == true;
    public bool ElevatedWarning => Status?.Elevated == true;

    public IReadOnlyList<AgentReadinessRow> Agents
    {
        get
        {
            if (!HostRunning || !host.Can(HostOps.AgentReadinessCapability) || Status?.Agents is not { } report) { return []; }
            AgentReadinessRow[] rows = [.. report.Agents.Select(a => new AgentReadinessRow(a, CanManageAgents, CanInstallAgents, agentNotes.GetValueOrDefault(a.Id),
                shared: hookResults.GetValueOrDefault(AgentReadinessRow.HooksOf(a.Id)), install: InstallOf(a.Id), installOffered: InstallOffered, canSignIn: CanSignInAgents))];
            // Never on a host that is read-only or beside agent-ha-bridge: the row's own words say why
            // there (Review migration, Read-only), and the card would offer what can't be done.
            var mayOffer = Status?.ReadOnly is null && Status?.AgentHaBridge is null;
            var dismissed = prefs().AgentOffersDismissed;
            foreach (var row in rows)
            {
                row.Offered = mayOffer && row.CanBeOffered && dismissed?.GetValueOrDefault(row.Id) != row.Version;
            }
            // What the rows say in other words is logged once each, so the raw text is still there
            // to diagnose, never on a row.
            foreach (var row in rows)
            {
                foreach (var raw in row.Raw())
                {
                    bool fresh;
                    lock (loggedRaw) { fresh = loggedRaw.Add(raw); }
                    if (fresh) { LogTo($"agents: {row.Id}: {raw}"); }
                }
            }
            return rows;
        }
    }
    // "Get started": none while the host can't report agents, so it never guesses.
    public IReadOnlyList<SetupStep> SetupSteps => !HostRunning ? SetupGuide.Steps(false, [], false)
        : Agents is { Count: > 0 } rows ? SetupGuide.Steps(true, rows, Status?.Account is not null) : [];
    public bool ShowSetupSteps => SetupSteps is { Count: > 0 } steps && !steps.All(s => s.Settled);
    // Where the app's log is (AppLog in the app).
    public Action<string> LogTo { get; init; } = _ => { };
    readonly HashSet<string> loggedRaw = new(StringComparer.Ordinal);
    // The host offers CLI installation here: none on a read-only host, beside agent-ha-bridge, or once
    // an installer's cleanup couldn't be confirmed, so no Install CLI there at all.
    bool InstallOffered => HostRunning && host.Can(HostOps.AgentInstallCapability)
        && Status?.AgentInstallation?.Blocked != true
        && Status?.ReadOnly is null && Status?.AgentHaBridge is null;
    // An agent's installation as its row says it: the app's request until the host's progress says.
    AgentInstallProgress? InstallOf(string agent) =>
        installRequestPending && installing == agent ? new(agent, true, "")
        : Status?.AgentInstallation is { } progress && progress.Agent == agent ? progress : null;
    public bool CanManageAgents => HostRunning && host.Can(HostOps.AgentSetupCapability) && !Busy && !AgentInstallInProgress
        && Status?.ReadOnly is null && Status?.AgentHaBridge is null;
    public bool CanInstallAgents => InstallOffered && !Busy && !AgentInstallInProgress;

    // Agents installed after the bridge, each with a card at the top of Settings and a grey dot on the
    // way in (#231). The offer is never a toast, a push or a count, and is nothing Needs you shows.
    public IReadOnlyList<AgentOffer> AgentOffers => [.. Agents.Where(a => a.Offered).Select(a => new AgentOffer(a, CanManageAgents))];
    public bool HasAgentOffers => Agents.Any(a => a.Offered);
    // The Settings item's name while a card waits, so the dot is in it too (ui.md §9).
    public string AgentsNavName => HasAgentOffers ? "Settings, new agent found" : "Settings";

    // Not now: this agent's card and the dot are hidden at the version it has now, across restarts.
    // The row keeps its own Set up, and a new version is a new offer.
    public void DismissAgentOffer(string agent)
    {
        if (Agents.FirstOrDefault(a => a.Id == agent)?.Version is { } version) { Dismiss(agent, version); }
    }

    void Dismiss(string agent, string version)
    {
        var next = new Dictionary<string, string>(prefs().AgentOffersDismissed ?? new Dictionary<string, string>(), StringComparer.Ordinal) { [agent] = version };
        // Not Save: PreferencesChanged is for what the update ticks and the cards' capabilities follow.
        savePrefs(prefs() with { AgentOffersDismissed = next });
        RaiseAll();
    }
    public string AgentInstallStatus { get => agentInstallStatus; private set => Set(ref agentInstallStatus, value); }
    public bool AgentInstallInProgress => agentInstallInProgress || Status?.AgentInstallation?.InProgress == true;
    public bool CanSignInAgents => HostRunning && host.Can(HostOps.AgentSignInCapability) && !Busy && !AgentInstallInProgress
        && Status?.ReadOnly is null && Status?.AgentHaBridge is null;
    public string AgentSignInStatus { get => agentSignInStatus; private set => Set(ref agentSignInStatus, value); }
    public bool CanCheckAgents => HostRunning && host.Can(HostOps.AgentReadinessCapability) && !Busy;
    public string AgentsState => !HostRunning ? "Connect to this machine's bridge to check its agents."
        : !host.Can(HostOps.AgentReadinessCapability) ? "This host does not report agent readiness. Update the app and restart the host."
        : Status?.Agents is not { } report ? "Checking installed agents..."
        : $"Checked at {report.CheckedAt.ToLocalTime():g}. Detection alone does not verify sign-in, hook trust or a new session."
            + (Status.ReadOnly is { } why ? $" This host is read-only: {why}." : "");

    public string AccountText => Status?.Account is { } a ? $"Signed in as {a.Login}" : "Not signed in: this machine's sessions are only in this app";
    public string RelayText => Status?.Relay is not { } r ? "" : r.State switch
    {
        RelayState.Connected => $"Connected to {Host(r.Url)}",
        RelayState.Connecting => $"Connecting to {Host(r.Url)}{(r.Detail is { } d ? $": {d}" : "")}",
        RelayState.Refused => r.Detail ?? "The relay refused this device; sign in again",
        RelayState.Off => "Not using the relay",
        _ => "Not signed in",
    };
    public bool SignInPending => Status?.SignIn.State == SignInState.Pending;
    public string? SignInCode => Status?.SignIn.UserCode;
    public string? SignInUri => Status?.SignIn.VerificationUri;
    public string? SignInError => Status?.SignIn.State == SignInState.Failed ? Status.SignIn.Error : null;
    public bool CanSignIn => HostRunning && !SignInPending && Status?.Relay.State != RelayState.Off;

    public string RelayUrl { get => relayUrl; set => Set(ref relayUrl, value); }

    public string MachineName
    {
        get => machineName;
        set { if (Set(ref machineName, value)) { nameEdited = true; } }
    }

    public bool RemoteApproval
    {
        get => Status?.Settings.RemoteApproval ?? true;
        set { if (Status is { } s && s.Settings.RemoteApproval != value) { _ = RunAsync(() => host.CallAsync(HostOps.Settings, new SettingsRequest(RemoteApproval: value))); } }
    }

    // permissions.phoneSignIn (#972): a person's device may run Copilot's own device-code sign-in
    // here. Off unless turned on; applies when the host restarts.
    public bool PhoneSignIn
    {
        get => Status?.Settings.PhoneSignIn ?? false;
        set { if (Status is { } s && s.Settings.PhoneSignIn != value) { _ = RunAsync(() => host.CallAsync(HostOps.Settings, new SettingsRequest(PhoneSignIn: value))); } }
    }

    // Who can sign Copilot in here (#972 slice 2): under the switch, the devices the machine's roster
    // pins, read-only, only while the running host has sign-in from devices on (the host says them
    // only then): the switch's own value changes at once, the host's at its next start, and until then
    // the devices listed can still sign in. No Add, no Remove and no disabled button for either yet
    // (Creative on #972, 6700914747; ui.md §6.9). Nobody while the roster can't be read.
    public bool ShowSignInDevices => HostRunning && Status?.SignInDevices is not null;
    public IReadOnlyList<SignInDeviceRow> SignInDevices => Status?.SignInDevices is { State: SignInDeviceStates.Ready } listed
        ? [.. listed.Devices.Select(SignInDeviceRow.Of)] : [];
    public string SignInDevicesEmpty => Status?.SignInDevices?.State == SignInDeviceStates.Unreadable ? "Can't read the device list on this machine."
        : SignInDevices.Count == 0 ? "No devices can sign Copilot in here yet." : "";
    public string SignInDevicesNote => Status?.SignInDevices?.State == SignInDeviceStates.Unreadable ? ""
        : SignInDevices.Count == 0 ? "Adding a device comes in a later update."
        : "Removing a single device comes later. To stop them all, turn this off.";
    // launch.remoteFolderTrust: a person may answer a launched agent's folder-trust question from
    // their devices, each approved here (launch.remoteFolderTrustDevices). Both apply at once.
    // Turned on with none approved yet, this app is approved with it: the way most people
    // will answer, and a setting that lets no device answer would look broken.
    // While changes are on their way, what was last asked for, so a switch turned on and straight
    // back off is not taken for no change (the host's status still says off) and lost.
    bool? trustWanted;

    public bool RemoteFolderTrust
    {
        get => trustWanted ?? trust.Settings?.RemoteFolderTrust ?? false;
        set
        {
            if (Status is null || RemoteFolderTrust == value) { return; }
            trustWanted = value;
            _ = SwitchAsync(value);
        }
    }

    async Task SwitchAsync(bool value)
    {
        Interlocked.Increment(ref switching);
        try
        {
            await TrustSettingsAsync(now => new SettingsRequest(RemoteFolderTrust: value,
                RemoteFolderTrustDevices: value && (now.RemoteFolderTrustDevices ?? []).Count == 0 && fleet?.DeviceId is { } me ? [me] : null));
        }
        finally { Interlocked.Decrement(ref switching); }
        // Its boxes were held while it was on its way.
        DrawDevices();
    }

    // Changes to who may answer go one at a time through the editor the Launch page uses too, each
    // made from the host's list as it is then (TrustEditor).
    readonly TrustEditor trust;
    int pendingEdits;

    async Task TrustSettingsAsync(Func<HostSettingsView, SettingsRequest?> change)
    {
        Interlocked.Increment(ref pendingEdits);
        try
        {
            await RunAsync(() => trust.EditAsync(change));
            // Not made: the switch shows what the host has again.
            if (Error is not null) { trustWanted = null; }
        }
        finally
        {
            // The last one in: the switch and every device row show what the host has now,
            // which redraws held back while changes were on their way did not.
            if (Interlocked.Decrement(ref pendingEdits) == 0) { Refresh(); }
        }
    }

    // The devices that may answer: this app, the account's people's devices the relay lists now
    // (the PWA on a phone or browser, this app on another machine), and any approved that are not
    // connected now. Each with whether it is approved, which a person changes here.
    public IReadOnlyList<TrustDevice> TrustDevices { get; private set; } = [];
    public bool ShowTrustDevices => RemoteFolderTrust && TrustDevices.Count > 0;

    // Who may approve an approval with hidden characters from a device (#803), and why, shown either
    // way: devices listed in config.json are otherwise invisible here, and ones inherited from
    // folder trust approve only while it is on. Empty for a host from before it.
    public string EscapedApprovalText => Status?.Settings is not { EscapedApproval: { } policy } s ? ""
        : policy != EscapedApproval.Confirm
            ? "Approvals with hidden characters: Approve only at this machine (permissions.escapedApproval). Deny goes from anywhere."
        : s.EscapedApprovalDevices is { } listed
            ? listed.Count == 0
                ? "Approvals with hidden characters: no device may approve them (permissions.escapedApprovalDevices is empty in config.json), so Approve only at this machine. Deny goes from anywhere."
                : $"Approvals with hidden characters: approved after a second confirmation from {listed.Count} device(s) listed in config.json (permissions.escapedApprovalDevices): {string.Join(", ", listed.Select(id => id[..Math.Min(8, id.Length)]))}. Remove one there."
        : RemoteFolderTrust
            ? "Approvals with hidden characters: approved after a second confirmation from the devices approved above for folder-trust questions."
            : "Approvals with hidden characters: Approve only at this machine while folder-trust questions are answered only here. Deny goes from anywhere.";

    Fleet? fleet;

    // The account's devices come from the relay link, made after this page.
    internal void AttachFleet(Fleet f)
    {
        fleet = f;
        f.Changed += DrawDevices;
        DrawDevices();
    }

    void DrawDevices()
    {
        var approved = trust.Settings?.RemoteFolderTrustDevices ?? [];
        var me = fleet?.DeviceId;
        var rows = new List<(string Id, string Label)>();
        if (me is not null) { rows.Add((me, $"This app ({Environment.MachineName})")); }
        foreach (var p in fleet?.People ?? [])
        {
            if (p.Id != me && rows.All(r => r.Id != p.Id)) { rows.Add((p.Id, p.Name is { Length: > 0 } n ? n : "A device")); }
        }
        foreach (var id in approved.Where(id => rows.All(r => r.Id != id))) { rows.Add((id, "A device not connected now")); }

        var next = rows.Select(r => (r.Id, Label: $"{r.Label} · {r.Id[..Math.Min(8, r.Id.Length)]}", Approved: approved.Contains(r.Id))).ToList();
        // The same rows, kept: a presence update does not recreate the checkboxes on screen.
        if (next.Count == TrustDevices.Count && next.Zip(TrustDevices).All(p => p.First.Id == p.Second.Id && p.First.Label == p.Second.Label))
        {
            // While a change to a box is on its way, it shows what was asked for, not a status from
            // before; the others show the host's word meanwhile (one drawn before the host's status
            // came would otherwise stay wrong until every change was in).
            foreach (var (row, d) in next.Zip(TrustDevices)) { if (!Held(d.Id)) { d.Show(row.Approved); } }
        }
        else
        {
            // A device joining or leaving while changes are on their way: a box being changed keeps
            // what was asked for, as above; the rest show what the host says.
            var asked = TrustDevices.Where(d => Held(d.Id)).ToDictionary(d => d.Id, d => d.Approved);
            TrustDevices = [.. next.Select(r => new TrustDevice(this, r.Id, r.Label, asked.TryGetValue(r.Id, out var a) ? a : r.Approved))];
            Raise(nameof(TrustDevices));
        }
        Raise(nameof(ShowTrustDevices));
        Raise(nameof(EscapedApprovalText));
    }

    // Boxes with a change of their own on its way, by device id.
    readonly ConcurrentDictionary<string, int> asking = new(StringComparer.Ordinal);
    int switching;

    // Whether a box shows what was asked rather than the host's word: its own change is on its way,
    // or the switch's (turned on, it approves this app), or a change from the Launch page is.
    bool Held(string id) => (asking.TryGetValue(id, out var n) && n > 0) || Volatile.Read(ref switching) > 0
        || (Volatile.Read(ref pendingEdits) == 0 && trust.Pending);

    internal async Task ApproveAsync(TrustDevice device, bool on)
    {
        if (Status is not null)
        {
            asking.AddOrUpdate(device.Id, 1, (_, n) => n + 1);
            try
            {
                await TrustSettingsAsync(now =>
                {
                    var devices = now.RemoteFolderTrustDevices ?? [];
                    if (devices.Contains(device.Id) == on) { return null; }
                    return new SettingsRequest(RemoteFolderTrustDevices: on ? [.. devices, device.Id] : [.. devices.Where(d => d != device.Id)]);
                });
            }
            finally { asking.AddOrUpdate(device.Id, 0, (_, n) => n - 1); }
        }
        // What the host says now, whatever the checkbox was left showing.
        DrawDevices();
        device.Reraise();
    }

    // devBox.keepAwake, offered on a Dev Box only (the host's status has DevBox only there): the host
    // skips or delays the Dev Box's pending stop-on-disconnect, so it does not hibernate under the
    // sessions it runs. Applies at once.
    public bool ShowDevBox => Status?.DevBox is not null;

    // While changes are on their way, and until the host's status says what it last acknowledged,
    // the value asked for (1 on, 0 off, -1 none): otherwise a switch turned on and straight back
    // off, or off again after the reply but before its status event, is taken for no change and
    // left on. Sent one at a time, each with what was asked last.
    int keepAwakeWanted = -1;
    int keepAwakePending;
    readonly SemaphoreSlim keepAwakeGate = new(1, 1);

    public bool DevBoxKeepAwake
    {
        get => Volatile.Read(ref keepAwakeWanted) is var w and >= 0 ? w == 1 : Status?.Settings.DevBoxKeepAwake ?? false;
        set
        {
            if (Status?.DevBox is null || DevBoxKeepAwake == value) { return; }
            Volatile.Write(ref keepAwakeWanted, value ? 1 : 0);
            Interlocked.Increment(ref keepAwakePending);
            _ = RunAsync(async () =>
            {
                await keepAwakeGate.WaitAsync();
                SettingsResult? saved = null;
                try
                {
                    if (Volatile.Read(ref keepAwakeWanted) is var w and >= 0)
                    {
                        saved = await host.CallAsync<SettingsResult>(HostOps.Settings, new SettingsRequest(DevBoxKeepAwake: w == 1));
                    }
                }
                finally
                {
                    keepAwakeGate.Release();
                    // The last one in: the switch shows what the host acknowledged until its status
                    // says so too, or the host's word when nothing was acknowledged.
                    if (Interlocked.Decrement(ref keepAwakePending) == 0)
                    {
                        Volatile.Write(ref keepAwakeWanted, saved is null ? -1 : saved.Settings.DevBoxKeepAwake ? 1 : 0);
                        CaughtUp();
                    }
                }
            });
        }
    }

    // The host's status now says what it acknowledged: from here the switch follows the host. A
    // status that disagrees, with nothing on its way, is either an older one arriving late or a
    // change made elsewhere (another app, a person's terminal), which only the host can say: it is
    // read again, once per status, as TrustEditor does. Waiting for a status that agrees could
    // wait for ever after a missed event, the switch showing on while the host is off, and turning
    // it on then discarded as no change.
    HostStatus? keepAwakeChecked;

    void CaughtUp()
    {
        var s = Status;
        if (s is null || Volatile.Read(ref keepAwakePending) > 0 || Volatile.Read(ref keepAwakeWanted) is not (var w and >= 0)) { return; }
        if ((w == 1) == s.Settings.DevBoxKeepAwake)
        {
            Volatile.Write(ref keepAwakeWanted, -1);
            return;
        }
        if (ReferenceEquals(s, keepAwakeChecked)) { return; }
        keepAwakeChecked = s;
        _ = RereadKeepAwakeAsync();
    }

    async Task RereadKeepAwakeAsync()
    {
        await keepAwakeGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref keepAwakePending) > 0) { return; }
            var now = (await host.CallAsync<HostStatus>(HostOps.Status)).Settings.DevBoxKeepAwake;
            // A change asked for meanwhile is what the switch shows; its own reply settles it.
            if (Volatile.Read(ref keepAwakePending) == 0) { Volatile.Write(ref keepAwakeWanted, Status?.Settings.DevBoxKeepAwake == now ? -1 : now ? 1 : 0); }
        }
        // Not read: what was acknowledged stands until a status agrees or the next is checked.
        catch (Exception e) when (e is not OutOfMemoryException) { }
        finally { keepAwakeGate.Release(); }
        Raise(nameof(DevBoxKeepAwake));
        // A status that came during the read was let pass, and the read may be older than it.
        CaughtUp();
    }

    public string DevBoxText => Status?.DevBox is not { } d ? ""
        : !d.KeepAwake ? "Off: this host does not keep the Dev Box awake. If its pool stops it on disconnect, it hibernates once your last session has been gone for the pool's grace period, sessions running here or not, unless agent-ha-bridge keeps it awake."
        : d.Last is not { } last ? $"On: the first pass runs shortly, then one every {d.IntervalMinutes} minutes."
        : $"On, every {d.IntervalMinutes} minutes. Last pass at {last.At.ToLocalTime():t}: {last.Outcome}, {last.Detail}.{(d.NextAt is { } next ? $" Next at {next.ToLocalTime():t}." : "")}";

    // What a person should act on: the Dev Box agent's settings could not be used, or the last pass
    // failed (az not signed in, say), could not move the stop, or stood back with no evidence that
    // agent-ha-bridge keeps it awake, so the Dev Box may hibernate.
    public string? DevBoxAlarm => Status?.DevBox is not { KeepAwake: true } d ? null
        : d.Problem is { } problem ? $"This Dev Box's agent settings could not be used, so it cannot be kept awake and may hibernate when you disconnect: {problem}"
        : d.Last is { Alarming: true } last ? $"The last pass did not keep this Dev Box awake ({last.Outcome}), so it may hibernate when you disconnect: {last.Detail}" : null;

    // Each setter acts only on a change: a binding refreshing pushes the same value back.
    public bool StartAtSignIn
    {
        get { try { return manager.StartsAtSignIn; } catch (Exception) { return false; } }
        set { if (value != StartAtSignIn) { _ = RunAsync(() => { manager.SetStartAtSignIn(value); return Task.CompletedTask; }); } }
    }

    public bool AppStartsAtSignIn
    {
        get => appAtSignIn.Enabled;
        set { if (value != appAtSignIn.Enabled) { _ = RunAsync(() => { appAtSignIn.Set(value); return Task.CompletedTask; }); } }
    }

    public bool ShowDetail { get => prefs().ShowDetail; set { if (value != ShowDetail) { Save(prefs() with { ShowDetail = value }); } } }
    public bool Toasts { get => prefs().Toasts; set { if (value != Toasts) { Save(prefs() with { Toasts = value }); } } }
    public bool FinishedToasts { get => prefs().FinishedToasts; set { if (value != FinishedToasts) { Save(prefs() with { FinishedToasts = value }); } } }
    public bool Prereleases
    {
        get => prefs().Prereleases;
        set
        {
            if (value == Prereleases) { return; }
            // Another source: the updater drops what it found and downloaded as soon as it is next
            // read, and says nothing (VelopackUpdater.Manager), so what a check here found or saw
            // ready on the old one, and its Restart to update, go: now, or once Settings is idle.
            sourceAt++;
            Reconcile();
            // The shared updater told of the new source before anything hears of the change: the
            // app's tick on PreferencesChanged would otherwise check and download under the old
            // source's mark, which the next tick then threw away (Copilot, r4488279975). Said to
            // the devices and the banner too, rather than at the next check.
            Save(prefs() with { Prereleases = value }, () => (updater as ObservedUpdater)?.SourceChanged());
        }
    }
    public bool AutoUpdate { get => prefs().AutoUpdate; set { if (value != AutoUpdate) { Save(prefs() with { AutoUpdate = value }); } } }
    // When an update is ready (#689, 6674007815; Product's names, 6674016765): the options in the
    // order Settings lists them, Restart to update first. Kept for this machine and profile; the
    // AutoUpdater reads it at its next look (the app asks for one at once).
    public int WhenReadyIndex
    {
        get => prefs().WhenReady == UpdateWhenReady.InstallBetweenTurns ? 1 : 0;
        set
        {
            var chosen = value == 1 ? UpdateWhenReady.InstallBetweenTurns : UpdateWhenReady.RestartToUpdate;
            if (value is 0 or 1 && chosen != prefs().WhenReady) { Save(prefs() with { WhenUpdateReady = chosen }); }
        }
    }
    // What the option chosen does, in one sentence (ui.md §8).
    public string WhenReadyNote => prefs().WhenReady == UpdateWhenReady.InstallBetweenTurns
        ? WhenReadyBetweenTurns
        : WhenReadyRestart;
    public const string WhenReadyRestart = "Nothing restarts until you choose Restart to update, here or on the banner at the top of the app.";
    public const string WhenReadyBetweenTurns = "Installs when no session here has a turn running or a question waiting; after 6 hours, even mid-turn, but never while a question waits.";

    public string? Error { get => error; private set { if (Set(ref error, value)) { Raise(nameof(HasError)); } } }
    public bool HasError => error is not null;
    public bool Busy
    {
        get => busy;
        private set { if (Set(ref busy, value)) { Raise(nameof(CanCheckAgents)); Raise(nameof(CanManageAgents)); Raise(nameof(CanInstallAgents)); Raise(nameof(CanSignInAgents)); Raise(nameof(Agents)); RaiseOffers(); Raise(nameof(UpdateStatus)); } }
    }
    // The card's Set up follows what the row's would (CanManageAgents), so it is drawn again wherever the rows are.
    void RaiseOffers() { Raise(nameof(AgentOffers)); Raise(nameof(HasAgentOffers)); Raise(nameof(AgentsNavName)); }
    public string UpdateStatus
    {
        // A release the app downloaded in the background is said here whenever Settings itself is
        // doing nothing about updates (#516): what an earlier check here said ("Up to date", say)
        // is older than that download, so it gives way. The banner above every pane owns the offer
        // and a failed restart of it while it shows them: nothing is said twice on one screen
        // (Creative, 6677102646, correcting 6676653935). Without the banner, a restart refused
        // here is said here, once, in its words. A check GitHub refused or limited since stays
        // here, after the offer when Settings says it (6676653935).
        get
        {
            if (Busy) { return updateStatus; }
            var banner = shared is { Shown: true } open ? open : null;
            // Dropped only when the banner says it, or a release downloaded since makes it old news.
            var failed = banner is { Failed: true } || refusedHere is not { } refused
                || (Downloaded is { } now && now != refused.Version) ? null : refused.Sentence;
            // With nothing ready, the failure, then what Settings learned since (a release gone
            // stale, say).
            if (Downloaded is not { } ready) { return Said(failed, updateStatus); }
            var offer = failed ?? (banner?.Version == ready || banner is { Failed: true } ? null : Ready(ready));
            return Said(offer, discoveryNote);
        }
        private set => Set(ref updateStatus, value);
    }
    static string Said(params string?[] lines) => string.Join(" ", lines.Where(s => s is { Length: > 0 }));
    // A restart pressed here that did not install, and the release it was for: said here unless
    // the banner says it, until the next press, check or change of source.
    (string Sentence, string Version)? refusedHere;
    // What the release discovery said when a check here could not ask GitHub (#709), until the
    // next check here or a change of source.
    string? discoveryNote;
    // The banner's restart, which Settings' Restart to update is too (ShareRestart).
    AppUpdateViewModel? shared;
    // Settings' own Restart to update under way: its button shows its ring and says so (Creative,
    // 6676653935), as the banner's does.
    public bool RestartingHere { get => restartingHere; private set { if (Set(ref restartingHere, value)) { Raise(nameof(RestartStatus)); } } }
    bool restartingHere;
    public string RestartStatus => restartingHere ? "Restarting\u2026" : "";

    // Settings' Restart to update is the banner's (App): one restart, with one state, whichever
    // control starts it. The banner shows it restarting and how it ended; Settings' status line
    // says the same words.
    public void ShareRestart(AppUpdateViewModel banner)
    {
        shared = banner;
        banner.PropertyChanged += (_, _) => Raise(nameof(UpdateStatus));
        RestartDownloaded = banner.RestartAsync;
    }
    // A release found by a check here, or downloaded in the background: Restart to update shows.
    public bool UpdateAvailable => updater.InstalledVersion is not null && (updateVersion ?? Downloaded) is not null;
    // The release downloaded, in the background or by Settings: still offered after an install of
    // it fails, for the person to try again, as the banner's is (AutoUpdater.InstallAsync).
    string? Downloaded => updater.InstalledVersion is not null ? updater.Ready : null;
    static string Ready(string version) => AppUpdateViewModel.Ready(version);
    string UpToDate => $"Up to date ({updater.InstalledVersion}).";
    // The app's Restart to update for the release downloaded (AutoUpdater.RestartToUpdateAsync),
    // given the confirmation to ask; unset, Settings stops the host and applies it itself. Null
    // from it: handed to Velopack; AutoUpdater.NotNow: the person chose not to; else why not.
    public Func<Func<string, Task<bool>>?, Task<string?>>? RestartDownloaded { get; set; }

    // The shared updater changed (a download in the background finished, say).
    public void UpdatesChanged()
    {
        Reconcile();
        Raise(nameof(UpdateStatus));
        Raise(nameof(UpdateAvailable));
        // Changed on its own (a release downloaded in the background, or gone): said to a screen
        // reader once (ui.md §9.1 A6). Settings' own progress and results are its own actions'.
        // While Settings is busy with something else, it is said once Settings is idle (RunAsync):
        // recorded as seen then without being said, a release downloaded meanwhile was never
        // announced (Copilot, 9039771d). Never while Settings' own check or Restart to update runs.
        if (Busy) { if (ownUpdateActions == 0) { announceWhenIdle = true; } }
        else { Announce(); }
    }

    void Announce()
    {
        if (UpdateStatus is var now && now != lastStatus)
        {
            lastStatus = now;
            if (now.Length > 0) { UpdateStatusAnnounced?.Invoke(); }
        }
    }

    // A change on its own came while Settings was busy, to be said once it is idle; and Settings'
    // own update actions under way, whose progress and results are never announced as such.
    bool announceWhenIdle;
    int ownUpdateActions;
    Task RunUpdateAsync(Func<Task> act) => RunAsync(async () =>
    {
        ownUpdateActions++;
        announceWhenIdle = false;
        try { await act(); }
        finally { ownUpdateActions--; }
    });

    // The update line changed without the person doing anything here: SettingsView raises its
    // polite live region's LiveRegionChanged.
    public event Action? UpdateStatusAnnounced;
    string lastStatus = "";

    // A release Settings saw ready, which the shared updater no longer has downloaded (a check in
    // the background replaced it, its download not done): neither it nor its Restart to update is
    // offered any more. Done once Settings is idle, so its own progress is never overwritten. A
    // release a check here found and that was never downloaded stays available while it is still
    // what the last check found; a later one (in the background) finding another, or nothing
    // newer, makes it old news too, whether or not that one's download then failed.
    void Reconcile()
    {
        if (Busy) { return; }
        if (foundAt != sourceAt)
        {
            foundAt = sourceAt;
            // Everything a check here said is the old source's, "Up to date" included: kept, it
            // claimed the new source current without ever asking it (Copilot, ac778916).
            (updateVersion, readyHere, updateStatus, discoveryNote, refusedHere) = (null, null, "", null, null);
            return;
        }
        if (readyHere is null)
        {
            if (updateVersion is { } found && updater is ObservedUpdater { Found: var newest } && newest != found && updater.Ready != found)
            {
                updateVersion = null;
                updateStatus = $"Version {found} is no longer the newest; check for updates again.";
            }
            // "Up to date" from a check here, under a background download since gone: a newer
            // release found since is said, never hidden behind it (Copilot, ac778916).
            else if (updateVersion is null && Downloaded is null && updater is ObservedUpdater { Found: { } since } && updateStatus == UpToDate)
            {
                updateStatus = $"Version {since} has been found since; check for updates again.";
            }
            return;
        }
        if (readyHere is not { } said || updater.Ready == said) { return; }
        readyHere = null;
        if (updateVersion != said) { return; }
        updateVersion = null;
        updateStatus = $"Version {said} is no longer the newest; check for updates again.";
    }
    public string Log { get => log; private set => Set(ref log, value); }

    public event Action? PreferencesChanged;

    // before: what must follow the saving and come before anyone is told.
    void Save(AppPreferences next, Action? before = null)
    {
        savePrefs(next);
        before?.Invoke();
        RaiseAll();
        PreferencesChanged?.Invoke();
    }

    public void Refresh()
    {
        // A setup's result is about the host that ran it: a host gone (stopped, restarted as it said
        // to) takes the rows' notes with it.
        if (!HostRunning) { agentNotes.Clear(); hookResults.Clear(); hostsGone++; }
        if (!installRequestPending && HostRunning && Status is { } status && !ReferenceEquals(status, installStatusSeen))
        {
            installStatusSeen = status;
            agentInstallInProgress = status.AgentInstallation?.InProgress == true;
            if (status.AgentInstallation is { } installation) { AgentInstallStatus = installation.Detail; }
        }
        if (!nameEdited) { machineName = Status?.Settings.Name ?? Status?.Machine.Name ?? ""; }
        if (Volatile.Read(ref pendingEdits) == 0) { trustWanted = null; }
        CaughtUp();
        DrawDevices();
        RaiseAll();
        OfferGuide();
    }

    // Slice 2 of #384: a first install opens on the Get started list once, the first time the host
    // reports agents and none has its hooks connected (AppPreferences.GuidePending, set only by a
    // first run, so an upgrader is never pulled to Settings). The app asks for it only when its window
    // is to be shown: not the demo, a development copy, the self-test or a start at sign-in, which
    // leaves it pending for the first launch that shows the window.
    public bool OpenGuideAtFirstLaunch { get; init; }
    public event Action? GuideWanted;
    void OfferGuide()
    {
        if (!OpenGuideAtFirstLaunch || !prefs().GuidePending || !HostRunning || Agents.Count == 0) { return; }
        var steps = SetupSteps;
        if (steps.Any(s => s.Id == "hooks" && s.State == SetupStepState.Done)) { return; }
        GuideWanted?.Invoke();
        savePrefs(prefs() with { GuidePending = false });
    }

    public Task StartHostAsync() => RunAsync(() => manager.StartAsync());
    public Task StopHostAsync() => RunAsync(() => manager.StopAsync());

    // Through the logon task or launcher when the host is installed beside the app, so the
    // new one runs as it does at sign-in (and the task's restart-on-failure watches it again);
    // a host the app cannot start (run from the command line, say) restarts itself.
    public Task RestartHostAsync() => RunAsync(async () =>
    {
        if (manager.HostInstalled) { await manager.RestartAsync(); }
        else if (host.State == LinkState.Connected) { await host.CallAsync(HostOps.Restart); }
        else { throw new InvalidOperationException($"the host is not beside the app ({manager.Layout.HostExe})"); }
    });

    public Task SignInAsync() => RunAsync(async () =>
    {
        var url = RelayUrl.Trim();
        savePrefs(prefs() with { Relay = url });
        await host.CallAsync<SignInStatus>(HostOps.SignIn, new SignInRequest(url.Length > 0 ? url : null), TimeSpan.FromSeconds(40));
    });

    public Task CancelSignInAsync() => RunAsync(() => host.CallAsync(HostOps.SignInCancel));

    public Task SaveNameAsync() => RunAsync(async () =>
    {
        await host.CallAsync(HostOps.Settings, new SettingsRequest(Name: MachineName));
        nameEdited = false;
    });

    public void RefreshLog() => Log = manager.LogTail();

    public Task CheckAgentsAsync() => RunAsync(async () =>
    {
        if (!HostRunning || !host.Can(HostOps.AgentReadinessCapability)) { throw new InvalidOperationException(AgentsState); }
        // A fresh check can wait for one active scan before its own four bounded probes.
        var fresh = await host.CallAsync<AgentReadinessReport>(HostOps.AgentsRefresh, timeout: TimeSpan.FromMinutes(1));
        // Fresh evidence replaces what the last setups said of each row, but for what the host can't
        // say (AgentReadinessRow.Keeps): a removal that left the owned record, which keeps Set up
        // (#705 review).
        // Kept for every row on those hooks, Agency's with Copilot CLI's, and the row that ran it keeps
        // its words.
        foreach (var (hooks, result) in hookResults.ToList())
        {
            if (fresh?.Agents.Any(a => AgentReadinessRow.HooksOf(a.Id) == hooks && new AgentReadinessRow(a).Keeps(result)) != true) { hookResults.Remove(hooks); }
        }
        foreach (var (id, note) in agentNotes.ToList())
        {
            if (hookResults.GetValueOrDefault(AgentReadinessRow.HooksOf(id)) != note) { agentNotes.Remove(id); }
        }
        await host.RefreshStatusAsync();
    });

    public Task SetUpAgentAsync(string agent, bool remove = false) => RunAsync(async () =>
    {
        if (!HostRunning || !host.Can(HostOps.AgentSetupCapability))
        {
            throw new InvalidOperationException("This host does not support agent setup. Update the app and restart the host.");
        }
        if (Status?.ReadOnly is not null || Status?.AgentHaBridge is not null)
        {
            throw new InvalidOperationException($"Agent setup is unavailable: {Status.ReadOnly ?? Status.AgentHaBridge}");
        }
        var verb = Agents.FirstOrDefault(a => a.Id == agent)?.SetUpVerb;
        // Agency's removal takes Copilot CLI's hooks, whose card it is that must not come back.
        var offered = AgentReadinessRow.HooksOf(agent);
        var version = Agents.FirstOrDefault(a => a.Id == offered)?.Version;
        var gone = hostsGone;
        Note(agent, new(remove ? "Removing…" : verb == "Repair" ? "Repairing…" : "Setting up…", Tone.Progress));
        AgentSetupResult result;
        try
        {
            result = await host.CallAsync<AgentSetupResult>(HostOps.AgentsSetup,
                new AgentSetupRequest(agent, remove ? "remove" : "install"), SetupTimeout);
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            // A host that never answers may have changed some of the hooks: the pipe's timeout is a
            // TimeoutException, a cancellation an OperationCanceledException (#705 review). A refusal's
            // own text names paths, so the row says why from a known set and the log keeps the text.
            LogTo($"agents: {agent}: {(remove ? "removal" : "setup")} failed: {e.Message}");
            if (gone == hostsGone) { Note(agent, new(e is TimeoutException or OperationCanceledException ? AgentWords.TookTooLong : AgentWords.SetupFailed(remove, verb, e.Message), Tone.Bad)); }
            throw;
        }
        if (result.Detail.Length > 0) { LogTo($"agents: {agent}: {(remove ? "removal" : "setup")}: {result.Detail}"); }
        if (gone == hostsGone) { Note(agent, new(AgentWords.SetupResult(remove, verb, result), result.Completed ? Tone.Ok : Tone.Bad, Removed: remove && result.Completed)); }
        // Removing the connection is the person's choice: the agent is not offered back at this version.
        if (remove && result.Completed && version is not null) { Dismiss(offered, version); }
        await host.RefreshStatusAsync();
        // A setup or removal that failed can have changed some hooks too (a removal that stopped
        // part-way leaves a setup that didn't finish), so the evidence is checked afresh either way;
        // the cached readiness would hide Repair until another scan (#705 review). After a failure,
        // the failure is what's said, not a scan's error.
        if (host.Can(HostOps.AgentReadinessCapability))
        {
            try
            {
                await host.CallAsync<AgentReadinessReport>(HostOps.AgentsRefresh, timeout: TimeSpan.FromMinutes(1));
                await host.RefreshStatusAsync();
            }
            catch (Exception e) when (!result.Completed && e is not (OutOfMemoryException or StackOverflowException)) { }
        }
        if (!result.Completed) { throw new InvalidOperationException(result.Detail); }
    });

    public Task InstallAgentAsync(string agent) => RunAsync(async () =>
    {
        if (AgentInstallInProgress) { throw new InvalidOperationException("A CLI installation is still running. Cancel it or wait for its result before installing another CLI."); }
        if (!HostRunning || !host.Can(HostOps.AgentInstallCapability))
        {
            throw new InvalidOperationException("This host does not support CLI installation. Update the app and restart the host.");
        }
        if (Status?.ReadOnly is not null || Status?.AgentHaBridge is not null)
        {
            throw new InvalidOperationException($"CLI installation is unavailable: {Status.ReadOnly ?? Status.AgentHaBridge}");
        }
        AgentInstallStatus = "Installing the CLI and required runtimes with Windows Package Manager...";
        agentInstallInProgress = true;
        installRequestPending = true;
        installing = agent;
        installStatusSeen = Status;
        RaiseAll();
        AgentInstallResult result;
        var reported = false;
        try
        {
            result = await host.CallAsync<AgentInstallResult>(HostOps.AgentsInstall,
                new AgentInstallRequest(agent, AcceptAgreements: true), TimeSpan.FromMinutes(11));
            AgentInstallStatus = result.Detail;
            reported = true;
        }
        finally
        {
            installRequestPending = false;
            agentInstallInProgress = !reported;
            if (!reported) { AgentInstallStatus = "The installation result is unavailable. It may still be running; reconnect to recover progress and cancellation before retrying."; }
            Refresh();
        }
        if (host.Can(HostOps.AgentReadinessCapability))
        {
            await host.CallAsync<AgentReadinessReport>(HostOps.AgentsRefresh, timeout: TimeSpan.FromMinutes(1));
            await host.RefreshStatusAsync();
        }
        if (!result.Completed) { throw new InvalidOperationException(result.Detail); }
    });

    public Task CancelAgentInstallAsync() => RunAsync(async () =>
    {
        if (!AgentInstallInProgress) { return; }
        await host.CallAsync(HostOps.AgentsInstallCancel);
    });

    public Task SignInAgentAsync(string agent) => RunAsync(async () =>
    {
        if (!HostRunning || !host.Can(HostOps.AgentSignInCapability))
        {
            throw new InvalidOperationException("This host does not support CLI sign-in. Update the app and restart the host.");
        }
        if (Status?.ReadOnly is not null || Status?.AgentHaBridge is not null)
        {
            throw new InvalidOperationException($"CLI sign-in is unavailable: {Status.ReadOnly ?? Status.AgentHaBridge}");
        }
        AgentSignInStatus = "Opening the CLI's own sign-in flow...";
        var reported = false;
        try
        {
            var result = await host.CallAsync<AgentSignInResult>(HostOps.AgentsSignIn,
                new AgentSignInRequest(agent, Confirmed: true), TimeSpan.FromSeconds(30));
            AgentSignInStatus = result.Detail;
            reported = true;
            if (!result.Opened) { throw new InvalidOperationException(result.Detail); }
        }
        finally
        {
            if (!reported) { AgentSignInStatus = "The sign-in window's result is unavailable. Check your desktop before retrying; account access remains unverified."; }
        }
    });

    // How long a setup may take at the host before its row says it took too long (tests shorten it).
    internal TimeSpan SetupTimeout { get; set; } = TimeSpan.FromMinutes(4);

    void Note(string agent, AgentSetupNote note)
    {
        agentNotes[agent] = note;
        if (note.Tone != Tone.Progress) { hookResults[AgentReadinessRow.HooksOf(agent)] = note; }
        Raise(nameof(Agents));
        RaiseOffers();
    }

    public Task CheckForUpdatesAsync() => RunUpdateAsync(async () =>
    {
        if (updater.InstalledVersion is null) { UpdateStatus = "Updates are off in a development copy."; return; }
        UpdateStatus = "Checking…";
        (discoveryNote, refusedHere) = (null, null);
        // Its answer is for the source as it began: one changed meanwhile drops it (Reconcile).
        var at = sourceAt;
        // GitHub limiting or unreachable is a wait, said as the status with when the next check
        // is (#530), not an error to act on; after a release ready, it follows that offer.
        try { updateVersion = await updater.CheckAsync(); }
        catch (ReleaseDiscoveryException e) { UpdateStatus = e.Message; discoveryNote = e.Message; return; }
        UpdateStatus = updateVersion is not { } v ? UpToDate
            : updater.Ready == v ? Ready(v)
            : $"Version {v} is available.";
        readyHere = updateVersion is { } found && updater.Ready == found ? found : null;
        foundAt = at;
        Raise(nameof(UpdateAvailable));
    });

    // Restart to update: the release downloaded in the background, or the one a check here found
    // (downloaded first). confirm(warning) is asked last, after the download and just before the
    // host stops, if a session here is waiting on the person or has a turn running then. The host
    // is stopped first so nothing in the app's folder is in use while it is replaced; the new
    // version starts it again.
    public Task InstallUpdateAsync(Func<string, Task<bool>>? confirm = null) => RunUpdateAsync(async () =>
    {
        refusedHere = null;
        updateVersion = updater.Ready ?? updateVersion;
        if (updateVersion is null) { return; }
        // A download that fails is the update failing, for the account's devices too, unless the
        // automatic update is installing meanwhile: its outcome is the one to report.
        if (updater.Ready != updateVersion)
        {
            UpdateStatus = $"Downloading {updateVersion}…";
            // Said to a screen reader at most every quarter, not at every percent (ui.md §9.1 A6).
            var quarter = 0;
            try
            {
                await updater.DownloadAsync(p =>
                {
                    UpdateStatus = $"Downloading {updateVersion}… {p}%";
                    if (p / 25 > quarter) { quarter = p / 25; UpdateStatusAnnounced?.Invoke(); }
                });
            }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
            {
                var why = e is DownloadHeldException ? e.Message : $"downloading {updateVersion} failed: {e.Message}";
                if (!manager.UpdateUnderWay) { AutoUpdater.Failed(prefs, savePrefs, updater, updateVersion, why, recorded: false); }
                UpdateStatus = "";
                // The next step, in one sentence: when the background tries again, or the person.
                throw new InvalidOperationException(updater is ObservedUpdater { RetryAt: { } at } && prefs().AutoUpdate
                    ? $"The update didn't download. {Product.Name} will try again at {at.ToLocalTime():t}."
                    : "The update didn't download; choose Restart to update to try again.", e);
            }
        }
        // Another check (the automatic one shares this updater) may have found a different release
        // since: the host is stopped only for the one downloaded.
        if (updater.Ready != updateVersion)
        {
            updateVersion = null;
            throw new InvalidOperationException("the update found has changed since; check for updates again");
        }
        readyHere = updateVersion;
        foundAt = sourceAt;
        UpdateStatus = "Stopping the host to update it…";
        // Through the app's own updater when it has one (AutoUpdater.RestartToUpdateAsync): the
        // banner's path, which refuses while another profile runs from this installation, asks
        // confirm itself, and reports how it went to the account's devices.
        if (RestartDownloaded is { } restart)
        {
            string? why;
            RestartingHere = true;
            try
            {
                why = await restart(confirm is null ? null : async warning =>
                {
                    UpdateStatus = "";
                    RestartingHere = false;
                    var go = await confirm(warning);
                    if (go) { UpdateStatus = "Stopping the host to update it…"; }
                    RestartingHere = go;
                    return go;
                });
            }
            finally { RestartingHere = false; }
            if (why is null) { return; }
            UpdateStatus = "";
            if (why == AutoUpdater.NotNow) { return; }
            // A status, never an error (Creative, 6676653935): the banner's failed state says it
            // while the banner shows, and this line does when it doesn't (UpdateStatus).
            refusedHere = (AppUpdateViewModel.DidNotInstall(why, updater.Ready is not null), updateVersion!);
            return;
        }
        if (RestartWarning.For(host) is { } warning && confirm is not null)
        {
            UpdateStatus = "";
            if (!await confirm(warning)) { return; }
            UpdateStatus = "Stopping the host to update it…";
        }
        try
        {
            await manager.StopAndApplyAsync(() => AutoUpdater.Record(prefs, savePrefs, updater), () =>
            {
                UpdateStatus = "Restarting…";
                updater.ApplyAndRestart(updateVersion!);
            });
        }
        // The automatic update got there first, and is installing (and recorded) it.
        catch (UpdateUnderWayException)
        {
            UpdateStatus = "";
            throw;
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            // Still this version: the host stopped for it is back (HostManager), and the failure is
            // shown, and reported to the account's devices.
            AutoUpdater.Failed(prefs, savePrefs, updater, updateVersion, e.Message);
            updateVersion = null;
            UpdateStatus = "";
            throw new InvalidOperationException(AppUpdateViewModel.DidNotInstall(e.Message, updater.Ready is not null), e);
        }
    });

    // Every action here ends up behind an async void click handler, where an escaping exception
    // ends the app (and every unsent draft on its cards). So whatever goes wrong (the pipe, Task
    // Scheduler, the registry, the network under an update check) is shown, not thrown.
    async Task RunAsync(Func<Task> act)
    {
        Error = null;
        busyActions++;
        Busy = true;
        try { await act(); }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            Error = e is OperationCanceledException ? "it took too long; try again" : e.Message;
        }
        finally
        {
            Busy = --busyActions > 0;
            // What changed in the updater while Settings was at work, once it no longer is.
            Reconcile();
            RaiseAll();
            if (!Busy && announceWhenIdle) { announceWhenIdle = false; Announce(); }
            else if (!Busy) { lastStatus = UpdateStatus; }
        }
    }

    static string Host(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url ?? "the relay";
}

// One device that can sign Copilot in on this machine (#972): its name, "(this device)" for this
// app's own entry, then its kind and platform ("Phone · iOS"). Never its id, key or presence.
public sealed record SignInDeviceRow(string Name, string Detail)
{
    public static SignInDeviceRow Of(SignInDevice d) => new(d.ThisDevice == true ? $"{d.Name} (this device)" : d.Name,
        string.Join(" · ", new[] { Kind(d.Kind), d.Platform }.Where(s => s.Length > 0)));

    static string Kind(string kind) => kind switch { "phone" => "Phone", "pwa" => "Browser", "app" => "App", _ => "" };
}

// One device on the Settings page that may be approved to answer folder-trust questions.
public sealed class TrustDevice(SettingsViewModel page, string id, string label, bool approved) : Observable
{
    bool approved = approved;

    public string Id { get; } = id;
    public string Label { get; } = label;

    // Shown as asked at once, and every change sent, so ticking and unticking quickly ends as
    // unticked rather than the second click being taken for no change.
    public bool Approved
    {
        get => approved;
        set
        {
            if (value == approved) { return; }
            approved = value;
            Raise();
            _ = page.ApproveAsync(this, value);
        }
    }

    internal void Reraise() => Raise(nameof(Approved));

    internal void Show(bool now)
    {
        if (approved == now) { return; }
        approved = now;
        Raise(nameof(Approved));
    }
}
