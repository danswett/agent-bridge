using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

public enum SessionSort { Yours, Recent, Machine }

// One live session as the list sees it: which machine it is on, by the machine's key.
public sealed record OrderEntry(string Machine, Session Session)
{
    public string Key => SessionOrder.KeyOf(Machine, Session.Id);
}

// A session a host removed while the app was running, for the Ended group.
public sealed record EndedSession(string Machine, Session Session, DateTimeOffset EndedAt)
{
    public string Key => SessionOrder.KeyOf(Machine, Session.Id);
}

public sealed record Arrangement(IReadOnlyList<OrderEntry> Pinned, IReadOnlyList<OrderEntry> Sessions, IReadOnlyList<EndedSession> Ended);

// The order of the sessions list, kept on this device: the person's, not the host's (ui.md §1
// principle 2 and §6.1, #164; pwa/js/session-order.js is the same, rule for rule). Sorting by
// the last update moved a card on every tool call, reasoning line and background-count change;
// with several sessions running the list reshuffled every few seconds and a click could land on
// the wrong card.
//
//   * Your order (the default): a session never placed before goes to the top of the unpinned
//     rows once, newest first when several arrive together, then stays where it is. Nothing a
//     host sends moves a row; only a move does (Alt+Up/Down, a drag, the row menu).
//   * Pinned rows sit above the rest. Most recent: the old order. Machine: by machine name, then
//     your order. Each sorts within Pinned and within the rest; rows move only in Your order.
//   * A session a host removed is kept in memory as ended, for the Ended group, until the app
//     restarts. A machine that goes offline or out of view ends nothing.
//   * Unread: a row whose session updated since the row was last open. A session seen for the
//     first time is not news.
//
// It knows nothing of XAML: the list's markup is rewritten by #180, and this stays. A file that
// can't be read or written falls back to Most recent: an order that can't be kept would be lost
// at the next start.
public sealed class SessionOrder
{
    // Rows remembered whose sessions aren't shown now: enough for several busy machines, and
    // bounded so the file never grows without end.
    const int MaxKeys = 300;
    const int MaxEnded = 20;

    readonly string? path;
    readonly TimeProvider time;
    readonly State state;
    List<EndedSession> ended = [];
    // What the last Arrange showed, by group: moves are among the rows on screen, not among rows
    // remembered for sessions that aren't shown.
    List<string> shownPinned = [];
    List<string> shownSessions = [];
    // Rows shown whose machine hasn't sent its whole list: they have no place yet to move from or to.
    HashSet<string> pending = new(StringComparer.Ordinal);
    bool seenDirty;

    sealed class State
    {
        public SessionSort Sort { get; set; }
        public List<string> Pinned { get; set; } = [];
        public List<string> Order { get; set; } = [];
        public Dictionary<string, DateTimeOffset> Seen { get; set; } = [];
    }

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // path: where the order is kept, or null to keep it only while the app runs.
    public SessionOrder(string? path, TimeProvider? time = null)
    {
        this.path = path;
        this.time = time ?? TimeProvider.System;
        state = Load(path, out var usable);
        Usable = usable;
    }

    public static string KeyOf(string machine, string session) => $"{machine}\n{session}";

    // Whether the order can be kept. When it can't, the list is Most recent.
    public bool Usable { get; private set; }

    public SessionSort Sort
    {
        get => Usable ? state.Sort : SessionSort.Recent;
        set
        {
            if (state.Sort == value || !Enum.IsDefined(value)) { return; }
            state.Sort = value;
            Save();
        }
    }

    static State Load(string? path, out bool usable)
    {
        usable = true;
        State? s = null;
        try
        {
            if (path is not null && File.Exists(path)) { s = JsonSerializer.Deserialize<State>(File.ReadAllText(path), Json); }
        }
        catch (JsonException) { s = null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { usable = false; }
        s ??= new State();
        s.Pinned = (s.Pinned ?? []).Where(k => k is not null).Distinct(StringComparer.Ordinal).ToList();
        var pins = s.Pinned.ToHashSet(StringComparer.Ordinal);
        s.Order = (s.Order ?? []).Where(k => k is not null && !pins.Contains(k)).Distinct(StringComparer.Ordinal).ToList();
        s.Seen = new Dictionary<string, DateTimeOffset>(s.Seen ?? [], StringComparer.Ordinal);
        if (!Enum.IsDefined(s.Sort)) { s.Sort = SessionSort.Yours; }
        return s;
    }

    public bool IsPinned(string key) => state.Pinned.Contains(key, StringComparer.Ordinal);

    // Last updated first; ties by id, then machine, so the order is stable.
    static int Newer(OrderEntry a, OrderEntry b)
    {
        var t = b.Session.UpdatedAt.CompareTo(a.Session.UpdatedAt);
        if (t != 0) { return t; }
        var id = string.CompareOrdinal(a.Session.Id, b.Session.Id);
        return id != 0 ? id : string.CompareOrdinal(a.Machine, b.Machine);
    }

    // entries: every live session shown. nameOf(machine): a machine's name, for the Machine sort.
    // shows(machine): whether a machine is shown, for the Ended group. settled(machine): whether a
    // machine has sent its whole list. Its sessions are placed only then, all together, newest
    // first: a snapshot arrives one session at a time, and placing each as it came would put them
    // in reverse. Until then they show at the top, newest first.
    public Arrangement Arrange(IReadOnlyList<OrderEntry> entries, Func<string, string?>? nameOf = null, Func<string, bool>? shows = null, Func<string, bool>? settled = null)
    {
        PlaceNew(entries.Where(e => settled?.Invoke(e.Machine) ?? true).ToList(), entries);
        var placed = state.Pinned.Concat(state.Order).ToHashSet(StringComparer.Ordinal);
        var unplaced = entries.Where(e => !placed.Contains(e.Key)).ToList();
        unplaced.Sort(Newer);
        pending = unplaced.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var k in unplaced.Select(e => e.Key).Concat(state.Pinned).Concat(state.Order)) { rank.TryAdd(k, rank.Count); }
        int Yours(OrderEntry a, OrderEntry b) => rank[a.Key].CompareTo(rank[b.Key]);
        Comparison<OrderEntry> by = Sort switch
        {
            SessionSort.Recent => Newer,
            SessionSort.Machine => (a, b) =>
            {
                var n = string.Compare(nameOf?.Invoke(a.Machine) ?? "", nameOf?.Invoke(b.Machine) ?? "", StringComparison.CurrentCultureIgnoreCase);
                if (n != 0) { return n; }
                var m = string.CompareOrdinal(a.Machine, b.Machine);
                return m != 0 ? m : Yours(a, b);
            },
            _ => Yours,
        };
        var pins = state.Pinned.ToHashSet(StringComparer.Ordinal);
        var pinned = entries.Where(e => pins.Contains(e.Key)).ToList();
        var rest = entries.Where(e => !pins.Contains(e.Key)).ToList();
        pinned.Sort(by);
        rest.Sort(by);
        shownPinned = pinned.Select(e => e.Key).ToList();
        shownSessions = rest.Select(e => e.Key).ToList();
        var live = entries.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var gone = ended.Where(e => (shows?.Invoke(e.Machine) ?? true) && !live.Contains(e.Key)).ToList();
        return new Arrangement(pinned, rest, gone);
    }

    // Sessions never placed go to the top of the unpinned rows, once; a session seen for the
    // first time starts read. all: every session shown, which the trim never forgets.
    void PlaceNew(IReadOnlyList<OrderEntry> entries, IReadOnlyList<OrderEntry> all)
    {
        var placed = state.Pinned.Concat(state.Order).ToHashSet(StringComparer.Ordinal);
        var fresh = entries.Where(e => !placed.Contains(e.Key)).ToList();
        fresh.Sort(Newer);
        var changed = fresh.Count > 0;
        state.Order.InsertRange(0, fresh.Select(e => e.Key).Distinct(StringComparer.Ordinal));
        foreach (var e in all)
        {
            if (state.Seen.TryAdd(e.Key, e.Session.UpdatedAt)) { changed = true; }
        }
        if (state.Order.Count + state.Pinned.Count > MaxKeys)
        {
            var live = all.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
            for (var i = state.Order.Count - 1; i >= 0 && state.Order.Count + state.Pinned.Count > MaxKeys; i--)
            {
                if (live.Contains(state.Order[i])) { continue; }
                state.Seen.Remove(state.Order[i]);
                state.Order.RemoveAt(i);
            }
        }
        if (changed) { Save(); }
    }

    List<string>? GroupOf(string key) => shownPinned.Contains(key, StringComparer.Ordinal) ? shownPinned : shownSessions.Contains(key, StringComparer.Ordinal) ? shownSessions : null;

    // Whether a row on screen can move by one (-1 up, 1 down) within its group: not a row its machine
    // hasn't finished sending, nor past one.
    public bool CanMove(string key, int delta)
    {
        if (Sort != SessionSort.Yours || GroupOf(key) is not { } g || pending.Contains(key)) { return false; }
        var i = g.IndexOf(key) + delta;
        return i >= 0 && i < g.Count && !pending.Contains(g[i]);
    }

    // Moves a row past its neighbour on screen. Rows remembered for sessions not shown keep
    // their places relative to each other.
    public bool Move(string key, int delta)
    {
        if (!CanMove(key, delta)) { return false; }
        var g = GroupOf(key)!;
        Insert(key, g[g.IndexOf(key) + delta], delta > 0);
        return true;
    }

    // A drag: the row goes before (or after) the target row, and into the target's group, so a
    // row dragged among the pinned ones is pinned.
    public bool Place(string key, string target, bool after)
    {
        if (Sort != SessionSort.Yours || key == target || GroupOf(key) is null || GroupOf(target) is null || pending.Contains(key) || pending.Contains(target)) { return false; }
        Insert(key, target, after);
        return true;
    }

    void Insert(string key, string target, bool after)
    {
        Drop(key);
        var list = state.Pinned.Contains(target, StringComparer.Ordinal) ? state.Pinned : state.Order;
        var at = list.IndexOf(target);
        list.Insert(at < 0 ? list.Count : after ? at + 1 : at, key);
        Save();
    }

    void Drop(string key)
    {
        state.Pinned.Remove(key);
        state.Order.Remove(key);
    }

    // A pinned row goes to the foot of the pinned ones, nearest where it was; an unpinned one to
    // the top of the rest.
    public void SetPinned(string key, bool pinned)
    {
        if (IsPinned(key) == pinned) { return; }
        Drop(key);
        if (pinned) { state.Pinned.Add(key); } else { state.Order.Insert(0, key); }
        Save();
    }

    public bool Unread(string key, DateTimeOffset updatedAt) => state.Seen.TryGetValue(key, out var seen) && updatedAt > seen;

    // The row is open on screen: what it shows now has been seen. Kept with the next save, or by
    // Flush: an open card's every update would otherwise rewrite the file on the UI thread.
    public void MarkSeen(string key, DateTimeOffset updatedAt)
    {
        if (state.Seen.TryGetValue(key, out var seen) && seen >= updatedAt) { return; }
        state.Seen[key] = updatedAt;
        seenDirty = true;
    }

    // Writes what MarkSeen noted, if anything: every few seconds and when the app quits.
    public void Flush()
    {
        if (seenDirty) { Save(); }
    }

    // A session a host removed has ended.
    public void Ended(string machine, Session session)
    {
        var key = KeyOf(machine, session.Id);
        Drop(key);
        state.Seen.Remove(key);
        Save();
        ended = [new EndedSession(machine, session, time.GetUtcNow()), .. ended.Where(e => e.Key != key)];
        if (ended.Count > MaxEnded) { ended.RemoveRange(MaxEnded, ended.Count - MaxEnded); }
    }

    // A machine's complete list: rows remembered for its sessions that aren't in it ended while
    // the app wasn't looking, and are forgotten.
    public void Synced(string machine, IReadOnlySet<string> liveIds)
    {
        var prefix = KeyOf(machine, "");
        bool Gone(string k) => k.StartsWith(prefix, StringComparison.Ordinal) && !liveIds.Contains(k[prefix.Length..]);
        var changed = state.Order.RemoveAll(Gone) + state.Pinned.RemoveAll(Gone) > 0;
        foreach (var k in state.Seen.Keys.Where(Gone).ToList()) { state.Seen.Remove(k); changed = true; }
        if (changed) { Save(); }
    }

    void Save()
    {
        seenDirty = false;
        if (!Usable || path is null) { return; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(state, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An order that can't be kept would be lost at the next start, so the list says Most
            // recent rather than pretend.
            Usable = false;
        }
    }
}
