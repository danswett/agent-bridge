using System.Collections.ObjectModel;

namespace AgentBridge.App.Core;

// The cards' order (#164, ui.md §6.1): the person's, kept on this device by SessionOrder. Rows
// move only when the person moves them, never on an upsert; pinned rows sit above the rest; the
// sessions a host removed fold into Ended · N. Kept apart from the rest of MainViewModel, whose
// markup #180 rewrites.
public sealed partial class MainViewModel
{
    public static IReadOnlyList<string> SortChoices { get; } = ["Your order", "Most recent", "Machine"];

    public SessionOrder Order { get; }
    public IReadOnlyList<SessionGroup> Groups => Order.Groups.Items;
    public IReadOnlyDictionary<string, int> GroupCounts => Order.Groups.Counts();

    public string CreateGroup(string name)
    {
        var id = Order.Groups.Create(name);
        Rearrange(chosen: true);
        return id;
    }

    public void RenameGroup(string id, string name) { Order.Groups.Rename(id, name); Rearrange(chosen: true); }
    public void DeleteGroup(string id) { Order.Groups.Delete(id); Rearrange(chosen: true); }
    public void MoveToGroup(SessionCardViewModel card, string id) { Order.Groups.SetGroup(KeyOf(card), id); Rearrange(chosen: true); }
    public void RemoveFromGroup(SessionCardViewModel card) { Order.Groups.SetGroup(KeyOf(card), null); Rearrange(chosen: true); }

    public bool CanSort(int index) => index >= 0 && index < SortChoices.Count
        && (index != (int)SessionSort.Yours || Order.Usable);

    // The sort the picker was last told of: a failed save anywhere (a pin, a drop, a host's list)
    // turns the list to Most recent, and the next arrangement says so.
    SessionSort toldSort;

    // The sort menu's choice: Your order, Most recent, Machine. Where the order can't be kept,
    // it reads Most recent whatever was picked.
    public int SortIndex
    {
        get => (int)Order.Sort;
        set
        {
            if (!CanSort(value)) { return; }
            Order.Sort = (SessionSort)value;
            Rearrange(chosen: true);
            toldSort = Order.Sort;
            Raise();
        }
    }

    // Sessions a host removed while the app was running, the most recently ended first.
    public ObservableCollection<EndedRow> Ended { get; } = [];
    public bool HasEnded => Ended.Count > 0;
    public string EndedHeader => $"Ended · {Ended.Count}";

    public string KeyOf(SessionCardViewModel card) => CardKey(owner.TryGetValue(card, out var m) ? m.Key : Machine.LocalKey, card.Id);

    public bool CanMove(SessionCardViewModel card, int delta) => Order.CanMove(KeyOf(card), delta);

    public bool Move(SessionCardViewModel card, int delta)
    {
        if (!Order.Move(KeyOf(card), delta)) { return false; }
        Rearrange(chosen: true);
        return true;
    }

    public void SetPinned(SessionCardViewModel card, bool pinned)
    {
        Order.SetPinned(KeyOf(card), pinned);
        Rearrange(chosen: true);
    }

    // A drag: the card with key `dragged` goes before or after `target`.
    public bool Place(string dragged, SessionCardViewModel target, bool after)
    {
        if (!Order.Place(dragged, KeyOf(target), after)) { return false; }
        // The dragged card keeps focus, as after a keyboard or menu move, so the next Alt+Up/Down moves it.
        if (byId.TryGetValue(dragged, out var moved)) { moved.WantsRowFocus = true; }
        Rearrange(chosen: true);
        return true;
    }

    // Most recent's batches (#1858): activity moves rows together, five seconds apart at most, and
    // not while the person points at a row, has focus in one, or types.
    RecentBatch? recent;
    DateTimeOffset? recentDue;
    readonly HashSet<(SessionCardViewModel Card, RowHold Kind)> holds = [];

    bool Held => holds.Any(h => Sessions.Contains(h.Card)) || Sessions.Any(c => c.Editing);

    // The pointer is over a row, or focus is in it (a view's call, as the pointer and focus come and
    // go). The last hold to end applies what waited, once five seconds have passed since the last batch.
    public void Hold(SessionCardViewModel card, RowHold kind, bool on)
    {
        if (!(on ? holds.Add((card, kind)) : holds.Remove((card, kind)))) { return; }
        if (!on) { Release(); }
    }

    void Release()
    {
        if (Order.Sort == SessionSort.Recent && !Held) { Rearrange(); }
    }

    void Hook(SessionCardViewModel card)
    {
        card.Row = new RowActions(this, card);
        // Its question asked, answered or rebuilt for new capabilities (Settings too): the lane follows.
        card.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionCardViewModel.Question) && owner.ContainsKey(card)) { SyncNeedsYou(); }
            if (e.PropertyName == nameof(SessionCardViewModel.Editing) && !card.Editing && owner.ContainsKey(card)) { Release(); }
        };
    }

    // Every second, from Tick: what waited for its five seconds moves.
    void ReleaseRecent()
    {
        if (recentDue is { } due && Time.GetUtcNow() >= due) { Rearrange(); }
    }

    // The cards shown, in order, with the group headers, the dots and the Ended group. Rows move
    // only when the arrangement says so, and an update never moves one while someone types in any
    // card: moving it then would take the words from under their fingers (in Most recent, where
    // updates move rows). chosen: the person moved, pinned or sorted, which moves rows anyway.
    void Rearrange(bool chosen = false)
    {
        var sort = Order.Sort;
        // In Most recent the batches decide what moves while someone types: a row that arrives still does.
        recent ??= new RecentBatch(Time);
        var a = recent.Arrange(Arranged(), sort, Held, fresh: chosen);
        recentDue = recent.Wait is { } wait ? Time.GetUtcNow() + wait : null;
        var want = a.Pinned.Concat(a.Sessions).Select(e => byId[e.Key]).ToList();
        var hold = !chosen && sort != SessionSort.Recent && Sessions.Any(c => c.Editing);
        foreach (var gone in Sessions.Except(want).ToList()) { Sessions.Remove(gone); }
        for (var i = 0; i < want.Count; i++)
        {
            var c = want[i];
            var at = Sessions.IndexOf(c);
            if (at < 0) { Sessions.Insert(Math.Min(i, Sessions.Count), c); }
            else if (at != i && !hold) { Sessions.Move(at, i); }
        }
        var pins = a.Pinned.Select(e => byId[e.Key]).ToHashSet();
        foreach (var c in want)
        {
            var key = KeyOf(c);
            // A row that moves keeps focus, as after a keyboard or menu move.
            if (recent.Moved.Contains(key) && holds.Contains((c, RowHold.Focus))) { c.WantsRowFocus = true; }
            c.Pinned = pins.Contains(c);
            // The row open in the session pane sees its news as it comes (ui.md §6.1, Unread).
            if (c == open) { Order.MarkSeen(key, c.Session.Activity?.ResponseTurn); }
            c.Unread = Order.Unread(key, c.Session.Activity?.ResponseTurn);
        }
        // Headings by the order on screen, which a held update leaves as it was.
        bool pinnedSeen = false, restSeen = false;
        foreach (var c in Sessions)
        {
            c.GroupHeader = pins.Count == 0 ? null
                : c.Pinned ? (pinnedSeen ? null : "Pinned")
                : (restSeen ? null : "Sessions");
            if (c.Pinned) { pinnedSeen = true; } else { restSeen = true; }
        }
        SyncEnded(a.Ended);
        Raise(nameof(Groups));
        Raise(nameof(GroupCounts));
        // Saving failed (the file can't be written), here or in the change before: the list is Most
        // recent now, and says so.
        if (Order.Sort != toldSort)
        {
            toldSort = Order.Sort;
            Raise(nameof(SortIndex));
        }
        if (Order.Sort != sort) { Rearrange(chosen); }
    }
    void SyncEnded(IReadOnlyList<EndedSession> ended)
    {
        var now = Time.GetUtcNow();
        var several = machines.Count > 1;
        var rows = ended.Select(e => new EndedRow(e.Key, string.IsNullOrEmpty(e.Session.Title) ? "Untitled session" : e.Session.Title,
            string.Join(" · ", new[] { several ? MachineOf(e.Machine)?.Name : null, $"ended {Words.Ago(e.EndedAt, now)}" }.Where(s => !string.IsNullOrEmpty(s))))).ToList();
        if (rows.SequenceEqual(Ended)) { return; }
        var had = Ended.Count;
        Ended.Clear();
        foreach (var r in rows) { Ended.Add(r); }
        if (had != Ended.Count)
        {
            Raise(nameof(HasEnded));
            Raise(nameof(EndedHeader));
        }
    }

    // A machine's sessions are placed once it has sent its whole list (SessionOrder.Arrange).
    Arrangement Arranged() => Order.Arrange(
        byId.Values.Where(Visible).Select(c => new OrderEntry(owner[c].Key, c.Session)).ToList(),
        nameOf: k => MachineOf(k)?.Name,
        shows: k => MachineOf(k) is { } m && Shows(m),
        settled: k => MachineOf(k)?.Host.Sessions.Complete != false,
        knownLive: byId.Values.Select(c => new OrderEntry(owner[c].Key, c.Session)).ToList());

    // "ended 3m ago" moves on with the clock.
    void RefreshEnded() => SyncEnded(Arranged().Ended);
}

public sealed record EndedRow(string Key, string Title, string Meta);

// What holds Most recent still while it lasts (#1858); typing is the card's Editing.
public enum RowHold { Pointer, Focus }

// What a card's menu, keyboard and drag do to its place in the list (#164).
public sealed class RowActions(MainViewModel main, SessionCardViewModel card)
{
    public string Key => main.KeyOf(card);
    public bool CanMoveUp => main.CanMove(card, -1);
    public bool CanMoveDown => main.CanMove(card, 1);
    public bool CanDrag => main.Order.Sort == SessionSort.Yours;
    public bool MoveUp() => main.Move(card, -1);
    public bool MoveDown() => main.Move(card, 1);
    public void TogglePin() => main.SetPinned(card, !card.Pinned);
    public bool Drop(string dragged, bool after) => main.Place(dragged, card, after);
    public void Hold(RowHold kind, bool on) => main.Hold(card, kind, on);
}
