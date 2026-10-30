using System.Collections.ObjectModel;
using AgentBridge.Local;
using AgentBridge.Protocol;

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

// The window's state: the session cards of every machine, most recently updated first as on
// the PWA; usage across machines; settings; and what the connection to this machine's host
// says. One timer moves relative times on and raises toasts when they come due.
public sealed class MainViewModel : Observable
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

    public MainViewModel(HostConnection host, CardContext cards, UsageViewModel usage, SettingsViewModel? settings = null, ToastPlanner? toasts = null)
        : this(new Machine(Machine.LocalKey, host, cards, toasts), usage, settings) { }

    public MainViewModel(Machine local, UsageViewModel usage, SettingsViewModel? settings = null)
    {
        this.local = local;
        Usage = usage;
        Settings = settings;
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
    public string? LocalMachineId => host.Hello?.Machine.Id;

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

    // What the Sessions page says about the account's other machines, when there is something.
    public string? FleetNote => fleet?.Note;
    public bool HasFleetNote => FleetNote is not null;

    // Raised on the UI thread when this machine's host says hello or goes.
    public event Action? LocalChanged;

    internal void AttachFleet(Fleet f)
    {
        fleet = f;
        f.Changed += () =>
        {
            Raise(nameof(FleetNote));
            Raise(nameof(HasFleetNote));
        };
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
            m.Toasts?.Remove(card.Id);
        }
        unhook.Remove(m, out var undo);
        undo?.Invoke();
        machines.Remove(m);
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
    (string Machine, string Session)? pendingOpen;

    public void Open(string? sessionId) => Open(null, sessionId);

    public void Open(string? machineKey, string? sessionId)
    {
        if (sessionId is null) { return; }
        var key = CardKey(machineKey ?? Machine.LocalKey, sessionId);
        if (!byId.TryGetValue(key, out var card)) { pendingOpen = (machineKey ?? Machine.LocalKey, sessionId); return; }
        pendingOpen = null;
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
            if (Visible(card)) { Sessions.Insert(IndexFor(s), card); }
            RefreshLabels();
        }
        Reorder(card);
        if (m.Toasts is { } toasts)
        {
            // The first list after connecting is a baseline, not news.
            if (!baselined.Contains(m)) { toasts.Baseline([s]); }
            else { toasts.Upsert(s); }
        }
        Raise(nameof(Waiting));
        Raise(nameof(Empty));
        if (pendingOpen is { } p && p.Machine == m.Key && p.Session == s.Id) { Open(m.Key, s.Id); }
    }

    void OnRemoved(Machine m, string id)
    {
        if (!byId.Remove(CardKey(m.Key, id), out var card)) { return; }
        owner.Remove(card);
        card.Detach();
        Sessions.Remove(card);
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
        if (pendingOpen is { } p && p.Machine == m.Key && !byId.ContainsKey(CardKey(p.Machine, p.Session))) { pendingOpen = null; }
        RefreshLabels();
    }

    bool Shows(Machine m) => SelectedMachine?.Key is not { } key || key == m.Key;

    bool Visible(SessionCardViewModel card) => owner.TryGetValue(card, out var m) && Shows(m);

    // The picker changed: the cards it shows, in order.
    void Refilter()
    {
        Sessions.Clear();
        foreach (var c in byId.Values.Where(Visible).OrderBy(c => c, Comparer<SessionCardViewModel>.Create((a, b) => Before(a.Session, b.Session) ? -1 : Before(b.Session, a.Session) ? 1 : 0)))
        {
            Sessions.Add(c);
        }
        OnHeldChanged();
    }

    int IndexFor(Session s)
    {
        var i = 0;
        while (i < Sessions.Count && Before(Sessions[i].Session, s)) { i++; }
        return i;
    }

    // Most recently updated first; ties by id, so the order is stable.
    static bool Before(Session a, Session b) => a.UpdatedAt > b.UpdatedAt || (a.UpdatedAt == b.UpdatedAt && string.CompareOrdinal(a.Id, b.Id) < 0);

    // A card moves to its place, unless someone is typing in any card: moving it then would
    // take the words from under their fingers.
    void Reorder(SessionCardViewModel card)
    {
        if (Sessions.Any(c => c.Editing)) { return; }
        var from = Sessions.IndexOf(card);
        if (from < 0) { return; }
        var to = 0;
        foreach (var c in Sessions)
        {
            if (c != card && Before(c.Session, card.Session)) { to++; }
        }
        if (from != to) { Sessions.Move(from, to); }
    }

    // Every second, on the UI thread.
    public void Tick()
    {
        foreach (var m in machines) { m.Toasts?.Tick(); }
        if (++ticks % 5 == 0) { foreach (var c in byId.Values) { c.RefreshTimes(); } }
        if (ticks % 30 == 0) { Usage.Refresh(); }
    }
}
