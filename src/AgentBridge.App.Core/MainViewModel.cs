using System.Collections.ObjectModel;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// The window's state: the session cards, most recently updated first as on the PWA; usage;
// settings; and what the connection to the host says. One timer moves relative times on and
// raises toasts when they come due.
public sealed class MainViewModel : Observable
{
    readonly HostConnection host;
    readonly CardContext cards;
    readonly ToastPlanner? toasts;
    readonly Dictionary<string, SessionCardViewModel> byId = new(StringComparer.Ordinal);
    bool baselined;
    string capabilities = "";
    int ticks;

    public MainViewModel(HostConnection host, CardContext cards, UsageViewModel usage, SettingsViewModel? settings = null, ToastPlanner? toasts = null)
    {
        this.host = host;
        this.cards = cards;
        this.toasts = toasts;
        Usage = usage;
        Settings = settings;
        host.Sessions.Upserted += OnUpsert;
        host.Sessions.Removed += OnRemoved;
        host.Sessions.Synced += Synced;
        host.Sessions.HeldChanged += OnHeldChanged;
        host.Changed += OnHostChanged;
        OnHostChanged();
    }

    public ObservableCollection<SessionCardViewModel> Sessions { get; } = [];
    public UsageViewModel Usage { get; }
    public SettingsViewModel? Settings { get; }

    public bool Connected => host.State == LinkState.Connected;
    public bool Empty => Connected && Sessions.Count == 0;
    public int Waiting => Sessions.Count(c => c.Session.Question is not null);
    public string MachineName => host.Hello?.Machine.Name ?? Environment.MachineName;

    // A line across the top when something stops the app from doing its job.
    public string? Banner => host.State switch
    {
        LinkState.NotRunning => "The Bridge Host is not running on this machine. Start it from Settings.",
        LinkState.Refused => $"The Bridge Host refused this app: {host.StateDetail}",
        LinkState.Connecting => "Connecting to the Bridge Host…",
        _ => ReadOnlyWhy,
    };
    public bool HasBanner => Banner is not null;

    // Why answers, replies and End session are off on every card, when the host says so.
    public string? ReadOnlyWhy => host.Status?.AgentHaBridge is { Length: > 0 } ha
        ? $"Answering, replying and ending sessions are off: {ha}. Agent Bridge only watches while agent-ha-bridge runs."
        : null;

    // What holds the host's discovery, as agent-ha-bridge's status line said it: while it holds,
    // a session that ended stays on its card, and nothing is offered for resume or reclaimed.
    public string? HeldWhy => Connected && host.Sessions.Held is { } held
        ? $"Discovery held: {held.Reason}. Sessions stay listed, and resuming them waits, until the host can account for every agent process."
        : null;
    public bool IsHeld => HeldWhy is not null;

    void OnHeldChanged()
    {
        Raise(nameof(HeldWhy));
        Raise(nameof(IsHeld));
    }

    // A toast or the tray asked for this session: the window shows it.
    public event Action<SessionCardViewModel>? OpenRequested;

    // A toast clicked with the app closed starts it before the host has sent its sessions; the
    // session asked for opens when it arrives.
    string? pendingOpen;

    public void Open(string? sessionId)
    {
        if (sessionId is null) { return; }
        if (!byId.TryGetValue(sessionId, out var card)) { pendingOpen = sessionId; return; }
        pendingOpen = null;
        card.Folded = false;
        OpenRequested?.Invoke(card);
    }

    void OnHostChanged()
    {
        Usage.Update(host.Usage);
        var caps = string.Join(",", host.Capabilities);
        if (caps != capabilities)
        {
            capabilities = caps;
            foreach (var c in Sessions) { c.CapabilitiesChanged(); }
        }
        if (host.State != LinkState.Connected) { baselined = false; }
        Raise(nameof(Connected));
        Raise(nameof(Empty));
        Raise(nameof(Banner));
        Raise(nameof(HasBanner));
        Raise(nameof(ReadOnlyWhy));
        Raise(nameof(MachineName));
        OnHeldChanged();
    }

    void OnUpsert(Session s)
    {
        if (byId.TryGetValue(s.Id, out var card)) { card.Update(s); }
        else
        {
            card = new SessionCardViewModel(s, cards);
            byId[s.Id] = card;
            Sessions.Insert(IndexFor(s), card);
        }
        Reorder(card);
        if (toasts is not null)
        {
            // The first list after connecting is a baseline, not news.
            if (!baselined) { toasts.Baseline([s]); }
            else { toasts.Upsert(s); }
        }
        Raise(nameof(Waiting));
        Raise(nameof(Empty));
        if (pendingOpen == s.Id) { Open(s.Id); }
    }

    void OnRemoved(string id)
    {
        if (!byId.Remove(id, out var card)) { return; }
        card.Detach();
        Sessions.Remove(card);
        toasts?.Remove(id);
        Raise(nameof(Waiting));
        Raise(nameof(Empty));
    }

    // Once the snapshot after connecting has been read, later upserts are news to toast.
    public void Synced()
    {
        baselined = true;
        cards.Folds.Keep(byId.Keys);
        // The session a toast asked for has ended since.
        if (pendingOpen is { } id && !byId.ContainsKey(id)) { pendingOpen = null; }
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
        toasts?.Tick();
        if (++ticks % 5 == 0) { foreach (var c in Sessions) { c.RefreshTimes(); } }
        if (ticks % 30 == 0) { Usage.Refresh(); }
    }
}
