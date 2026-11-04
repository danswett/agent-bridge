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
            if (value < 0 || value >= SortChoices.Count) { return; }
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

    // A drag's drop (#592): the card goes to `index` among the other cards of the group it is dropped
    // in, pinned or not, as one change; `drawn`, those cards' keys as the drag drew them.
    public bool MoveTo(SessionCardViewModel card, bool pinned, int index, IReadOnlyList<string>? drawn = null)
    {
        if (!Order.MoveTo(KeyOf(card), pinned, index, drawn)) { return false; }
        // The dropped card keeps focus, as after a keyboard or menu move, so the next Alt+Up/Down moves it.
        card.WantsRowFocus = true;
        Rearrange(chosen: true);
        return true;
    }

    public bool IsFixed(SessionCardViewModel card) => Order.IsFixed(KeyOf(card));

    // The card a person holds in a drag (#592). While one is, the list's own changes (a session
    // arriving, ending or moving in Most recent) apply to the model at once but reach Sessions, and
    // so the screen, only once it is let go: a row appearing or going under the held card would move
    // the place it is about to land in. Its own session ending is said (HeldGone), so the drag ends.
    public SessionCardViewModel? Held
    {
        get => held;
        set
        {
            if (held == value) { return; }
            held = value;
            if (held is null && heldChanges)
            {
                heldChanges = false;
                Rearrange();
            }
        }
    }
    SessionCardViewModel? held;
    bool heldChanges;
    public event Action<SessionCardViewModel>? HeldGone;

    // A card leaving the list, at once unless one is held.
    void Unlist(SessionCardViewModel card)
    {
        if (held is null) { Sessions.Remove(card); return; }
        heldChanges = true;
        if (card == held) { HeldGone?.Invoke(card); }
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
        if (held is not null && !chosen)
        {
            heldChanges = true;
            if (!Visible(held)) { HeldGone?.Invoke(held); }
            return;
        }
        var sort = Order.Sort;
        var a = Arranged();
        var want = a.Pinned.Concat(a.Sessions).Select(e => byId[e.Key]).ToList();
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
            if (c == open) { Order.MarkSeen(key, c.Session.UpdatedAt); }
            c.Unread = Order.Unread(key, c.Session.UpdatedAt);
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
        settled: k => MachineOf(k)?.Host.Sessions.Complete != false);

    // "ended 3m ago" moves on with the clock.
    void RefreshEnded() => SyncEnded(Arranged().Ended);
}

public sealed record EndedRow(string Key, string Title, string Meta);

// What a card's menu, keyboard and drag do to its place in the list (#164).
public sealed class RowActions(MainViewModel main, SessionCardViewModel card)
{
    public string Key => main.KeyOf(card);
    public bool CanMoveUp => main.CanMove(card, -1);
    public bool CanMoveDown => main.CanMove(card, 1);
    public bool CanDrag => main.Order.Sort == SessionSort.Yours && !main.IsFixed(card);
    public bool MoveUp() => main.Move(card, -1);
    public bool MoveDown() => main.Move(card, 1);
    public void TogglePin() => main.SetPinned(card, !card.Pinned);
    public bool MoveTo(bool pinned, int index, IReadOnlyList<string>? drawn = null) => main.MoveTo(card, pinned, index, drawn);
}
