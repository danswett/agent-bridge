using System.Collections.ObjectModel;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// One row of the Machines page (#180, #213): every machine on the account, this one first, then
// the others connected, then those offline. Its anatomy is ui.md §6.1's: the name on line 1 with
// the status trailing it, at most two facts on line 2 (never an id, never a sentence: a read-only
// host's reason is "View only" with the reason in its tooltip), and the machine's update, if
// any, inside its own row. Its buttons share one style and one column, under the text.
public sealed class MachineRowViewModel : Observable
{
    string name = "";
    string facts = "";
    string status = "";
    string? viewOnlyWhy;
    string? heldWhy;
    bool canShowSessions;
    bool canCheckHere;
    MachineUpdateViewModel? update;
    MachineDiagnosisViewModel? diagnosis;

    internal MachineRowViewModel(string key, OfflineMachineViewModel? offline = null)
    {
        Key = key;
        Offline = offline;
    }

    // The machine's key (Machine.Key), or "offline:" and the relay's id for one offline.
    public string Key { get; }
    public string Name
    {
        get => name;
        internal set
        {
            if (!Set(ref name, value)) { return; }
            Raise(nameof(ShowSessionsName));
            Raise(nameof(CheckHereName));
        }
    }
    public string Facts { get => facts; internal set => Set(ref facts, value); }
    public string Status { get => status; internal set { if (Set(ref status, value)) { RaiseTile(); } } }
    int sessions;
    string? seenAgo;
    bool offersLaunch;
    // What the tile reads from the fields above (#1147): see MachineTile.
    internal void Tile(int count, string? seen, bool launch)
    {
        (sessions, seenAgo, offersLaunch) = (count, seen, launch);
        RaiseTile();
    }
    void RaiseTile()
    {
        Raise(nameof(HealthText));
        Raise(nameof(HealthIcon));
        Raise(nameof(RunningText));
        Raise(nameof(CanLaunchHere));
        Raise(nameof(LaunchWhy));
        Raise(nameof(HasLaunchWhy));
        Raise(nameof(ShowTile));
    }
    public string HealthText => MachineTile.Health(status == "Offline", seenAgo).Text;
    public string HealthIcon => MachineTile.Health(status == "Offline", seenAgo).Icon;
    public string RunningText => MachineTile.Running(sessions);
    public bool CanLaunchHere => !IsOffline && offersLaunch && MachineTile.CanLaunchHere(status, ViewOnly, Held);
    public string? LaunchWhy => MachineTile.LaunchWhy(status, ViewOnly, Held);
    public bool HasLaunchWhy => LaunchWhy is not null;
    // An offline machine shows its header and Forget only.
    public bool ShowTile => !IsOffline && status != "Offline";
    public bool ShowHealth => !IsOffline;
    public string LaunchHereName => $"Launch a session on {name}";
    public bool ViewOnly => viewOnlyWhy is not null;
    // Why the machine takes no answers, replies or End session from here: the host's words.
    public string? ViewOnlyWhy
    {
        get => viewOnlyWhy;
        internal set { if (Set(ref viewOnlyWhy, value)) { RaiseTile(); Raise(nameof(ViewOnly)); Raise(nameof(FactsWhy)); } }
    }
    // What holds the machine's discovery, once the hold has lasted two minutes: the host's words
    // (#788, #1064). Its row is the only place the app says so.
    public string? HeldWhy
    {
        get => heldWhy;
        internal set { if (Set(ref heldWhy, value)) { RaiseTile(); Raise(nameof(Held)); Raise(nameof(FactsWhy)); } }
    }
    public bool Held => heldWhy is not null;
    // The reasons behind line 2's flag, never on the row (ui.md §6.6): its tooltip and its
    // accessible description. Discovery held takes View only's place on the row, as in the PWA,
    // so with both, both reasons are said, each named.
    public string? FactsWhy => (heldWhy, viewOnlyWhy) switch
    {
        ({ } held, { } viewOnly) => $"View only: {viewOnly} · Discovery held: {held}",
        ({ } held, null) => $"Discovery held: {held}",
        _ => viewOnlyWhy,
    };
    public bool CanShowSessions { get => canShowSessions; internal set => Set(ref canShowSessions, value); }
    // This machine's own Check: Settings' check, shown there with its outcome and install.
    public bool CanCheckHere { get => canCheckHere; internal set => Set(ref canCheckHere, value); }
    public MachineUpdateViewModel? Update
    {
        get => update;
        internal set { if (Set(ref update, value)) { Raise(nameof(HasUpdate)); } }
    }
    public bool HasUpdate => update is not null;
    // Diagnose (#802): another machine's, while it is connected; never this one's.
    public MachineDiagnosisViewModel? Diagnosis
    {
        get => diagnosis;
        internal set { if (Set(ref diagnosis, value)) { Raise(nameof(HasDiagnosis)); } }
    }
    public bool HasDiagnosis => diagnosis is not null;
    public OfflineMachineViewModel? Offline { get; }
    public bool IsOffline => Offline is not null;
    public string ShowSessionsName => $"Show the sessions on {name}";
    public string CheckHereName => $"Check {name} for updates in Settings";
}

public sealed partial class MainViewModel
{
    public ObservableCollection<MachineRowViewModel> MachineRows { get; } = [];

    // One line heading the page: text only, no health figure until one is reported.
    public string FleetSummary => MachineTile.Fleet(MachineRows.Count, MachineRows.Count(r => r.Status is "Online" or "This machine"));

    // Called whenever a machine, its sessions, its update or the offline list may have changed.
    void SyncMachineRows()
    {
        var want = new List<MachineRowViewModel>();
        var listedOffline = OfflineMachines.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in restarting.Keys.Where(k => machines.All(m => m.Key != k)).ToList()) { restarting.Remove(gone); }
        var restartingRows = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in machines.OrderByDescending(m => m.IsLocal))
        {
            var leaving = Restarting(m);
            if (leaving) { restartingRows.Add(m.Key); }
            // One kept offline with its rows (#269) that the roster lists is shown by its offline
            // row, which has its Forget; one the roster does not know says so itself.
            if (m.Offline && listedOffline.Contains(m.Key)) { continue; }
            var row = MachineRows.FirstOrDefault(r => r.Key == m.Key) ?? new MachineRowViewModel(m.Key);
            var connected = m.Host.State == LinkState.Connected;
            var count = byId.Values.Count(c => owner[c] == m);
            row.Name = m.Name;
            row.ViewOnlyWhy = ViewOnlyWhy(m);
            // Only once the hold has lasted HeldRowAfter (#1064); read from the host's store.
            row.HeldWhy = HeldLong(m, connected);
            row.Facts = m.OfflineSince is { } since ? $"Last seen {Words.Ago(since, Time.GetUtcNow())}"
                : string.Join(" · ", new[] { connected ? Words.Plural(count, "session", "sessions") : null, row.Held ? "Discovery held" : row.ViewOnly ? "View only" : null }.Where(s => s is not null));
            row.Status = leaving ? UpdateNowWords.Restarting : m.Offline ? "Offline" : !connected ? "Connecting" : m.IsLocal ? "This machine" : "Online";
            row.Tile(count, m.OfflineSince is { } off ? Words.Ago(off, Time.GetUtcNow()) : null, m.Host.Can(LaunchProtocol.Capability));
            row.CanShowSessions = connected && !m.Offline && machines.Count > 1;
            row.CanCheckHere = m.IsLocal && CheckLocal is not null;
            row.Update = MachineUpdates.FirstOrDefault(u => u.Key == m.Key);
            // This machine's host is reached over the local pipe, which never serves diagnostics.
            if (m.IsLocal || m.Offline || !connected) { row.Diagnosis?.Close(); row.Diagnosis = null; }
            else if (row.Diagnosis?.Machine != m) { row.Diagnosis = new MachineDiagnosisViewModel(m, Time); }
            else { row.Diagnosis?.Refresh(); }
            want.Add(row);
        }
        foreach (var o in OfflineMachines)
        {
            var key = $"offline:{o.Id}";
            var row = MachineRows.FirstOrDefault(r => r.Key == key && r.Offline == o) ?? new MachineRowViewModel(key, o);
            row.Name = o.Name;
            row.Status = restartingRows.Contains(o.Id) ? UpdateNowWords.Restarting : o.Status;
            want.Add(row);
        }
        foreach (var gone in MachineRows.Except(want).ToList()) { MachineRows.Remove(gone); }
        for (var i = 0; i < want.Count; i++)
        {
            var at = MachineRows.IndexOf(want[i]);
            if (at < 0) { MachineRows.Insert(i, want[i]); }
            else if (at != i) { MachineRows.Move(at, i); }
        }
        Raise(nameof(FleetSummary));
    }

    // Null while Installing is connected; the grace starts only when that channel goes away.
    readonly Dictionary<string, DateTimeOffset?> restarting = new(StringComparer.Ordinal);

    bool Restarting(Machine m)
    {
        if (m.IsLocal) { return false; }
        if (!m.Offline && m.Host.State == LinkState.Connected)
        {
            if (m.Host.Update is { Phase: UpdatePhase.Installing, RequestNow: true })
            {
                restarting[m.Key] = null;
                return true;
            }
            restarting.Remove(m.Key);
            return false;
        }
        if (!restarting.TryGetValue(m.Key, out var left)) { return false; }
        var now = Time.GetUtcNow();
        if (left is null) { restarting[m.Key] = left = now; }
        if (now - left.Value < UpdateNowWords.RestartShown) { return true; }
        restarting.Remove(m.Key);
        return false;
    }

    // How long a discovery hold must last before its machine's row says so: a hold that starts and
    // ends inside that is routine and changes nothing on screen (ui.md §6.6, #1064).
    // (a settable, so the window's self-test need not wait the two minutes out).
    public TimeSpan HeldRowAfter { get; set; } = TimeSpan.FromMinutes(2);

    // When each machine's current hold was first seen here, by key: per hold, so one that clears
    // and returns counts from its own start. Read on the Tick, which draws the row once it is due.
    readonly Dictionary<string, DateTimeOffset> heldSince = new(StringComparer.Ordinal);

    // The host's reason while the machine's hold has lasted HeldRowAfter, else null.
    string? HeldLong(Machine m, bool connected)
    {
        if (!connected || m.Host.Sessions.Held is not { } held) { heldSince.Remove(m.Key); return null; }
        var now = Time.GetUtcNow();
        if (!heldSince.TryGetValue(m.Key, out var since)) { heldSince[m.Key] = since = now; }
        return now - since >= HeldRowAfter ? held.Reason : null;
    }

    // Why a machine is view only, as its host says (and, for this one, agent-ha-bridge beside it);
    // null when it isn't.
    string? ViewOnlyWhy(Machine m)
    {
        var why = m.IsLocal ? ReadOnlyWhy ?? m.Host.Hello?.Machine.ReadOnly : m.Host.Hello?.Machine.ReadOnly;
        return string.IsNullOrEmpty(why) ? null : why;
    }
}
