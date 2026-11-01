using System.Collections.ObjectModel;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.App.Core;

// One entry of the machine picker over the cards: every machine, or one of them.
public sealed class MachineChoice : Observable
{
    string label;

    public MachineChoice(string? key, string label)
    {
        Key = key;
        this.label = label;
    }

    // Null for every machine.
    public string? Key { get; }
    public string Label { get => label; internal set => Set(ref label, value); }
    public override string ToString() => label;
}

// The window's state: the session cards of every machine, in the person's order as on the PWA
// (SessionOrder, #164); usage across machines; settings; and what the connection to this
// machine's host says. One timer moves relative times on and raises toasts when they come due.
public sealed partial class MainViewModel : Observable
{
    readonly Machine local;
    readonly List<Machine> machines = [];
    readonly Dictionary<Machine, Action> unhook = [];
    readonly Dictionary<string, SessionCardViewModel> byId = new(StringComparer.Ordinal);
    readonly Dictionary<SessionCardViewModel, Machine> owner = [];
    readonly HashSet<Machine> baselined = [];
    readonly Dictionary<Machine, string> capabilities = [];
    Fleet? fleet;
    MachineChoice? selected;
    int ticks;

    public MainViewModel(HostConnection host, CardContext cards, UsageViewModel usage, SettingsViewModel? settings = null, ToastPlanner? toasts = null, SessionOrder? order = null)
        : this(new Machine(Machine.LocalKey, host, cards, toasts), usage, settings, order) { }

    public MainViewModel(Machine local, UsageViewModel usage, SettingsViewModel? settings = null, SessionOrder? order = null)
    {
        this.local = local;
        Usage = usage;
        Settings = settings;
        Order = order ?? new SessionOrder(null);
        toldSort = Order.Sort;
        Machines.Add(new MachineChoice(null, "All machines"));
        selected = Machines[0];
        Add(local);
    }

    public ObservableCollection<SessionCardViewModel> Sessions { get; } = [];
    public UsageViewModel Usage { get; }
    public SettingsViewModel? Settings { get; }
    public Machine Local => local;
    public IReadOnlyList<Machine> AllMachines => machines;
    // Every machine's cards, whatever the picker shows.
    public IEnumerable<SessionCardViewModel> AllCards => byId.Values;

    // The picker: every machine, then each one. Shown once there is more than one machine.
    public ObservableCollection<MachineChoice> Machines { get; } = [];
    public bool ShowMachines => machines.Count > 1;
    public MachineChoice? SelectedMachine
    {
        get => selected;
        set
        {
            if (value is null || !Set(ref selected, value)) { return; }
            Refilter();
            Raise(nameof(Empty));
            Raise(nameof(EmptyText));
        }
    }

    HostConnection host => local.Host;

    public bool Connected => host.State == LinkState.Connected;
    public bool Empty => Connected && Sessions.Count == 0;
    public string EmptyText => SelectedMachine?.Key is { } key && key != Machine.LocalKey
        ? $"No agent sessions on {MachineOf(key)?.Name ?? "that machine"} right now."
        : machines.Count > 1 && SelectedMachine?.Key is null
            ? "No agent sessions on any of your machines right now. Start Copilot CLI, Claude Code or Codex and it appears here."
            : "No agent sessions on this machine right now. Start Copilot CLI, Claude Code or Codex and it appears here.";
    public int Waiting => byId.Values.Count(c => c.Session.Question is not null);
    public string MachineName => host.Hello?.Machine.Name ?? Environment.MachineName;
    // This machine's host's relay device id, once it has said hello: never listed as another machine.
    // Kept while the pipe is down: the relay's list of machines goes on changing meanwhile.
    string? localId;
    public string? LocalMachineId => localId = host.Hello?.Machine.Id ?? host.Status?.Machine.Id ?? localId;

    // A line across the top when something stops the app from doing its job.
    public string? Banner => host.State switch
    {
        LinkState.NotRunning => "The Bridge Host is not running on this machine. Start it from Settings.",
        LinkState.Refused => $"The Bridge Host refused this app: {host.StateDetail}",
        LinkState.Connecting => "Connecting to the Bridge Host…",
        _ => ReadOnlyWhy,
    };
    public bool HasBanner => Banner is not null;

    // Why answers, replies and End session are off on this machine's cards, when its host says so.
    public string? ReadOnlyWhy => host.Status?.AgentHaBridge is { Length: > 0 } ha
        ? $"Answering, replying and ending sessions are off: {ha}. Agent Bridge only watches while agent-ha-bridge runs."
        : null;

    // What holds each host's discovery, as agent-ha-bridge's status line said it: while it holds,
    // a session that ended stays on its card, and nothing is offered for resume or reclaimed.
    public string? HeldWhy
    {
        get
        {
            var lines = machines.Where(m => m.Host.State == LinkState.Connected && m.Host.Sessions.Held is not null && Shows(m))
                .Select(m => $"{(machines.Count > 1 ? $"{m.Name}: " : "")}Discovery held: {m.Host.Sessions.Held!.Reason}. Sessions stay listed, and resuming them waits, until the host can account for every agent process.")
                .ToList();
            return lines.Count > 0 ? string.Join("\n", lines) : null;
        }
    }
    public bool IsHeld => HeldWhy is not null;

    // The account's machines that are offline (the relay's roster), each with a two-press Forget.
    public ObservableCollection<OfflineMachineViewModel> OfflineMachines { get; } = [];
    public bool ShowOfflineMachines => OfflineMachines.Count > 0;

    // On the UI thread, from the fleet: entries for machines still offline are kept (an arm with
    // them), a machine no longer listed goes, and a new one is added. fresh: the list is another
    // account's (or relay's), so nothing is kept, an arm least of all.
    internal void SetOffline(IReadOnlyList<KnownMachine> known, Func<string, CancellationToken, Task<string?>> forget, TimeProvider time, bool fresh = false)
    {
        if (fresh) { OfflineMachines.Clear(); }
        var now = known.ToDictionary(k => k.Id, StringComparer.Ordinal);
        foreach (var gone in OfflineMachines.Where(o => !now.ContainsKey(o.Id)).ToList()) { OfflineMachines.Remove(gone); }
        for (var i = 0; i < known.Count; i++)
        {
            var at = OfflineMachines.ToList().FindIndex(o => o.Id == known[i].Id);
            if (at < 0) { OfflineMachines.Insert(i, new OfflineMachineViewModel(known[i], forget, time)); continue; }
            OfflineMachines[at].Update(known[i]);
            if (at != i) { OfflineMachines.Move(at, i); }
        }
        Raise(nameof(ShowOfflineMachines));
    }

    // The account's other machines whose updates can be seen and asked for (#51): those whose
    // host offers update.* (a read-only one offers none), each with Check and Install.
    public ObservableCollection<MachineUpdateViewModel> MachineUpdates { get; } = [];
    public bool ShowMachineUpdates => MachineUpdates.Count > 0;
    public bool CanCheckAll => MachineUpdates.Any(u => u.CanCheck) || CheckLocal is not null;
    // This machine's own check, for Check all: its updater's, which Settings shows. Set by the app.
    public Action? CheckLocal { get; set; }
    public TimeProvider Time { get; init; } = TimeProvider.System;

    // Check all: every machine listed, and this one.
    public void CheckAll()
    {
        CheckLocal?.Invoke();
        foreach (var u in MachineUpdates.Where(u => u.CanCheck).ToList()) { u.Check(); }
    }

    void SyncUpdate(Machine m)
    {
        var row = MachineUpdates.FirstOrDefault(u => u.Key == m.Key);
        var shown = !m.IsLocal && machines.Contains(m) && m.Host.State == LinkState.Connected && m.Host.Can(UpdateProtocol.Capability) && m.Host.Update is not null;
        if (!shown)
        {
            if (row is null) { return; }
            MachineUpdates.Remove(row);
        }
        else if (row is null)
        {
            MachineUpdates.Add(new MachineUpdateViewModel(m, Time));
        }
        else { row.Changed(); }
        Raise(nameof(ShowMachineUpdates));
        Raise(nameof(CanCheckAll));
    }

    // What the Sessions page says about the account's other machines, when there is something.
    public string? FleetNote => fleet?.Note;
    public bool HasFleetNote => FleetNote is not null;

    // Raised on the UI thread when a machine comes or goes, or what one offers may have changed.
    public event Action? MachinesUpdated;

    // Raised on the UI thread when this machine's host says hello or goes.
    public event Action? LocalChanged;
    // Another machine gone, by its key, after its cards and toasts.
    public event Action<string>? MachineRemoved;

    internal Fleet? Fleet => fleet;

    internal void AttachFleet(Fleet f)
    {
        fleet = f;
        void Note()
        {
            Raise(nameof(FleetNote));
            Raise(nameof(HasFleetNote));
        }
        f.Changed += Note;
        Settings?.AttachFleet(f);
        // Now too: a link that starts signed out has no change to announce, and the note it
        // has would otherwise wait for an unrelated update.
        Note();
    }

    void OnHeldChanged()
    {
        Raise(nameof(HeldWhy));
        Raise(nameof(IsHeld));
    }

    public Machine? MachineOf(string? key) => machines.FirstOrDefault(m => m.Key == (key ?? Machine.LocalKey));

    // Adds a machine and its cards; on the UI thread.
    public void Add(Machine m)
    {
        if (machines.Contains(m)) { return; }
        machines.Add(m);
        var sessions = m.Host.Sessions;
        Action<Session> upsert = s => OnUpsert(m, s);
        Action<string> removed = id => OnRemoved(m, id);
        Action synced = () => Synced(m);
        Action changed = () => OnHostChanged(m);
        sessions.Upserted += upsert;
        sessions.Removed += removed;
        sessions.Synced += synced;
        sessions.HeldChanged += OnHeldChanged;
        m.Host.Changed += changed;
        unhook[m] = () =>
        {
            sessions.Upserted -= upsert;
            sessions.Removed -= removed;
            sessions.Synced -= synced;
            sessions.HeldChanged -= OnHeldChanged;
            m.Host.Changed -= changed;
        };
        Machines.Add(new MachineChoice(m.Key, m.Name));
        foreach (var s in sessions.All) { OnUpsert(m, s); }
        MachinesChanged();
        OnHostChanged(m);
    }

    // A machine gone from the account's presence: its cards and toasts go with it.
    public void Remove(string key)
    {
        if (MachineOf(key) is not { IsLocal: false } m) { return; }
        foreach (var (id, card) in byId.Where(p => owner[p.Value] == m).ToList())
        {
            byId.Remove(id);
            owner.Remove(card);
            card.Detach();
            Sessions.Remove(card);
        }
        Rearrange();
        m.Toasts?.MachineGone();
        MachineRemoved?.Invoke(key);
        // A launch's open stays: the machine may be back in a moment, with the session it started.
        pendingOpens.RemoveWhere(p => p.Machine == key);
        unhook.Remove(m, out var undo);
        undo?.Invoke();
        machines.Remove(m);
        SyncUpdate(m);
        baselined.Remove(m);
        capabilities.Remove(m);
        if (Machines.FirstOrDefault(c => c.Key == key) is { } choice)
        {
            if (SelectedMachine == choice) { SelectedMachine = Machines[0]; }
            Machines.Remove(choice);
        }
        MachinesChanged();
        Raise(nameof(Waiting));
        Raise(nameof(Empty));
        OnHeldChanged();
        UpdateUsage();
    }

    void MachinesChanged()
    {
        MachinesUpdated?.Invoke();
        Raise(nameof(ShowMachines));
        Raise(nameof(EmptyText));
        RefreshLabels();
        // Cards say which machine they are on once there is more than one.
        foreach (var c in byId.Values) { c.CapabilitiesChanged(); }
    }

    void RefreshLabels()
    {
        foreach (var choice in Machines)
        {
            if (choice.Key is null) { choice.Label = "All machines"; continue; }
            if (MachineOf(choice.Key) is not { } m) { continue; }
            var count = byId.Values.Count(c => owner[c] == m);
            var state = m.Host.State == LinkState.Connected ? Words.Plural(count, "session", "sessions") : "connecting…";
            choice.Label = $"{m.Name}{(m.IsLocal ? " (this machine)" : "")} · {state}";
        }
    }

    // A toast or the tray asked for this session: the window shows it.
    public event Action<SessionCardViewModel>? OpenRequested;

    // A toast clicked with the app closed starts it before the host has sent its sessions; the
    // session asked for opens when it arrives.
    // Sessions asked to open before their cards arrived: several, since launches on two machines
    // can both start before either card comes. A toast's is dropped when the machine's next list
    // lacks it (the session ended); a launch's waits for its session to register, which can come
    // after such a list (a folder-trust question answered at the machine).
    readonly HashSet<(string Machine, string Session)> pendingOpens = [];
    readonly HashSet<(string Machine, string Session)> launchOpens = [];

    public void Open(string? sessionId) => Open(null, sessionId);

    public void Open(string? machineKey, string? sessionId, bool untilItArrives = false)
    {
        if (sessionId is null) { return; }
        var wanted = (machineKey ?? Machine.LocalKey, sessionId);
        if (!byId.TryGetValue(CardKey(wanted.Item1, sessionId), out var card)) { (untilItArrives ? launchOpens : pendingOpens).Add(wanted); return; }
        pendingOpens.Remove(wanted);
        launchOpens.Remove(wanted);
        // A card the picker hides is shown: it is what was asked for.
        if (!Visible(card)) { SelectedMachine = Machines[0]; }
        card.Folded = false;
        OpenRequested?.Invoke(card);
    }

    static string CardKey(string machine, string session) => $"{machine}\n{session}";

    void OnHostChanged(Machine m)
    {
        if (m == local) { LocalChanged?.Invoke(); }
        UpdateUsage();
        var caps = string.Join(",", m.Host.Capabilities);
        if (!capabilities.TryGetValue(m, out var before) || caps != before)
        {
            capabilities[m] = caps;
            foreach (var (c, _) in owner.Where(p => p.Value == m)) { c.CapabilitiesChanged(); }
        }
        if (m.Host.State != LinkState.Connected) { baselined.Remove(m); }
        SyncUpdate(m);
        MachinesUpdated?.Invoke();
        RefreshLabels();
        Raise(nameof(Connected));
        Raise(nameof(Empty));
        Raise(nameof(EmptyText));
        Raise(nameof(Banner));
        Raise(nameof(HasBanner));
        Raise(nameof(ReadOnlyWhy));
        Raise(nameof(MachineName));
        Raise(nameof(FleetNote));
        Raise(nameof(HasFleetNote));
        OnHeldChanged();
    }

    void UpdateUsage() => Usage.Update(machines.Count == 1 ? local.Host.Usage : UsageMerge.Merge(machines.Select(m => m.Host.Usage)));

    void OnUpsert(Machine m, Session s)
    {
        var key = CardKey(m.Key, s.Id);
        if (byId.TryGetValue(key, out var card)) { card.Update(s); }
        else
        {
            card = new SessionCardViewModel(s, m.Cards);
            byId[key] = card;
            owner[card] = m;
            Hook(card);
            RefreshLabels();
        }
        Rearrange();
        if (m.Toasts is { } toasts)
        {
            // The first list after connecting is a baseline, not news.
            if (!baselined.Contains(m)) { toasts.Baseline([s]); }
            else { toasts.Upsert(s); }
        }
        Raise(nameof(Waiting));
        Raise(nameof(Empty));
        if (pendingOpens.Contains((m.Key, s.Id)) || launchOpens.Contains((m.Key, s.Id))) { Open(m.Key, s.Id); }
    }

    void OnRemoved(Machine m, string id)
    {
        if (!byId.Remove(CardKey(m.Key, id), out var card)) { return; }
        owner.Remove(card);
        card.Detach();
        Sessions.Remove(card);
        Order.Ended(m.Key, card.Session);
        Rearrange();
        m.Toasts?.Remove(id);
        RefreshLabels();
        Raise(nameof(Waiting));
        Raise(nameof(Empty));
    }

    // Once the snapshot after connecting has been read, later upserts are news to toast.
    public void Synced() => Synced(local);

    void Synced(Machine m)
    {
        baselined.Add(m);
        m.Cards.Folds.Keep(owner.Where(p => p.Value == m).Select(p => p.Key.Id));
        // The session a toast asked for has ended since.
        pendingOpens.RemoveWhere(p => p.Machine == m.Key && !byId.ContainsKey(CardKey(p.Machine, p.Session)));
        Order.Synced(m.Key, owner.Where(p => p.Value == m).Select(p => p.Key.Id).ToHashSet(StringComparer.Ordinal));
        Rearrange();
        RefreshLabels();
    }

    bool Shows(Machine m) => SelectedMachine?.Key is not { } key || key == m.Key;

    bool Visible(SessionCardViewModel card) => owner.TryGetValue(card, out var m) && Shows(m);

    // The picker changed: the cards it shows, in order.
    void Refilter()
    {
        Rearrange();
        OnHeldChanged();
    }

    // Every second, on the UI thread.
    public void Tick()
    {
        foreach (var m in machines) { m.Toasts?.Tick(); }
        foreach (var o in OfflineMachines) { o.Tick(); }
        if (++ticks % 5 == 0)
        {
            foreach (var c in byId.Values) { c.RefreshTimes(); }
            RefreshEnded();
            Order.Flush();
            // A flush that failed turned the list to Most recent: arrange it so, and tell the picker.
            if (Order.Sort != toldSort) { Rearrange(); }
        }
        if (ticks % 30 == 0)
        {
            Usage.Refresh();
            foreach (var u in MachineUpdates) { u.Changed(); }
        }
    }
}
