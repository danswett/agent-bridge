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

    // Headers with no row after them (the list ends in empty or folded groups): the list's end carries them.
    public ObservableCollection<GroupHeader> TrailingGroupHeaders { get; } = [];
    public bool HasTrailingGroupHeaders => TrailingGroupHeaders.Count > 0;

    public void SetGroupCollapsed(string id, bool collapsed) { Order.SetGroupCollapsed(id, collapsed); Rearrange(chosen: true); }

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

    // Not in a folded group: its neighbours there are not listed, and a move would step over rows nobody sees.
    public bool CanMove(SessionCardViewModel card, int delta) => !Folded(card) && Order.CanMove(KeyOf(card), delta);

    public bool Move(SessionCardViewModel card, int delta)
    {
        if (!CanMove(card, delta) || !Order.Move(KeyOf(card), delta)) { return false; }
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

    void Hook(SessionCardViewModel card)
    {
        card.Row = new RowActions(this, card);
        // Its question asked, answered or rebuilt for new capabilities (Settings too): the lane follows.
        card.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionCardViewModel.Question) && owner.ContainsKey(card)) { SyncNeedsYou(); }
        };
    }

    // The cards shown, in order, with the group headers, the dots and the Ended group. Rows move
    // only when the arrangement says so, and an update never moves one while someone types in any
    // card: moving it then would take the words from under their fingers (in Most recent, where
    // updates move rows). chosen: the person moved, pinned or sorted, which moves rows anyway.
    void Rearrange(bool chosen = false)
    {
        var sort = Order.Sort;
        var a = Arranged();
        var (want, headers, trailing) = Sectioned(a);
        var hold = !chosen && Sessions.Any(c => c.Editing);
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
            c.Pinned = pins.Contains(c);
            var key = KeyOf(c);
            // The row open in the session pane sees its news as it comes (ui.md §6.1, Unread).
            if (c == open) { Order.MarkSeen(key, c.Session.Activity?.ResponseTurn); }
            c.Unread = Order.Unread(key, c.Session.Activity?.ResponseTurn);
        }
        // Headings by the order on screen, which a held update leaves as it was. "Sessions" names the rows in no group, once
        // anything is pinned or any group exists.
        bool pinnedSeen = false, restSeen = false;
        var sectioned = pins.Count > 0 || Groups.Count > 0;
        foreach (var c in Sessions)
        {
            var ungrouped = !c.Pinned && Order.Groups.GroupOf(KeyOf(c)) is null;
            c.GroupHeader = !sectioned ? null
                : c.Pinned ? (pinnedSeen ? null : "Pinned")
                : ungrouped && !restSeen ? "Sessions" : null;
            if (c.Pinned) { pinnedSeen = true; } else if (ungrouped) { restSeen = true; }
            if (!hold) { c.GroupHeaders = headers.GetValueOrDefault(c) ?? []; }  // a held update leaves them where they were, as it leaves the rows
        }
        if (!hold && !TrailingGroupHeaders.SequenceEqual(trailing))
        {
            TrailingGroupHeaders.Clear();
            foreach (var h in trailing) { TrailingGroupHeaders.Add(h); }
            Raise(nameof(HasTrailingGroupHeaders));
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
    // Pinned, then each group's rows, then the rows in no group (ui.md Groups). A collapsed group lists only the rows that need you and
    // the session that is open; its header, and an empty group's, sits over the next row listed, or ends up in `trailing`.
    (List<SessionCardViewModel> Rows, Dictionary<SessionCardViewModel, List<GroupHeader>> Headers, List<GroupHeader> Trailing) Sectioned(Arrangement a)
    {
        var rows = a.Pinned.Select(e => byId[e.Key]).ToList();
        var rest = a.Sessions.Select(e => byId[e.Key]).ToList();
        var headers = new Dictionary<SessionCardViewModel, List<GroupHeader>>();
        var waiting = new List<GroupHeader>();
        void List(SessionCardViewModel c)
        {
            rows.Add(c);
            if (waiting.Count > 0) { headers[c] = [.. waiting]; waiting.Clear(); }
        }
        foreach (var g in Order.Groups.Items)
        {
            var members = rest.Where(c => Order.Groups.GroupOf(KeyOf(c)) == g.Id).ToList();
            var folded = Order.IsGroupCollapsed(g.Id);
            waiting.Add(new GroupHeader(g.Id, g.Name, members.Count, members.Count(c => c.Session.Question is not null), !folded, SetGroupCollapsed));
            foreach (var c in members.Where(c => !folded || c.Session.Question is not null || c == open)) { List(c); }
        }
        foreach (var c in rest.Where(c => Order.Groups.GroupOf(KeyOf(c)) is null)) { List(c); }
        return (rows, headers, waiting);
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
        settled: k => MachineOf(k)?.Host.Sessions.Complete != false);

    // "ended 3m ago" moves on with the clock.
    void RefreshEnded() => SyncEnded(Arranged().Ended);
}

public sealed record EndedRow(string Key, string Title, string Meta);

// What a card's menu, keyboard and drag do to its place in the list (#164).
public sealed record GroupChoice(string Id, string Name, int Count, bool Current);

public sealed class RowActions(MainViewModel main, SessionCardViewModel card)
{
    public string Key => main.KeyOf(card);
    public bool CanMoveUp => main.CanMove(card, -1);
    public bool CanMoveDown => main.CanMove(card, 1);
    public bool CanDrag => main.Order.Sort == SessionSort.Yours;
    public bool MoveUp() => main.Move(card, -1);
    public bool MoveDown() => main.Move(card, 1);
    public void TogglePin() => main.SetPinned(card, !card.Pinned);

    // The row menu's groups (G5b): every group with its count and whether this row is in it.
    public IReadOnlyList<GroupChoice> GroupChoices
    {
        get
        {
            var mine = main.Order.Groups.GroupOf(Key);
            var counts = main.GroupCounts;
            return [.. main.Groups.Select(g => new GroupChoice(g.Id, g.Name, counts.GetValueOrDefault(g.Id), g.Id == mine))];
        }
    }
    public bool InGroup => main.Order.Groups.GroupOf(Key) is not null;
    public bool CanRemoveFromGroup => InGroup;
    public void MoveToGroup(string id) => main.MoveToGroup(card, id);
    public void RemoveFromGroup() => main.RemoveFromGroup(card);
    // Why a new group can't be made with this name, in words for the dialog; null when it can.
    public string? NewGroupProblem(string name)
    {
        if (main.Groups.Count >= 32) { return "At most 32 groups can be kept."; }
        try { main.Order.Groups.ValidateName(name); return null; }
        catch (ArgumentException e) { return e.Message; }
    }
    // Makes the group and moves this session into it; throws ArgumentException for a name NewGroupProblem refuses.
    public string NewGroup(string name)
    {
        if (NewGroupProblem(name) is { } problem) { throw new ArgumentException(problem, nameof(name)); }
        var id = main.CreateGroup(name);
        main.MoveToGroup(card, id);
        return id;
    }
    public bool Drop(string dragged, bool after) => main.Place(dragged, card, after);
}
