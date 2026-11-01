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

    // The sort menu's choice: Your order, Most recent, Machine. Where the order can't be kept,
    // it reads Most recent whatever was picked.
    public int SortIndex
    {
        get => (int)Order.Sort;
        set
        {
            if (value < 0 || value >= SortChoices.Count) { return; }
            Order.Sort = (SessionSort)value;
            Rearrange();
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
        Rearrange();
        return true;
    }

    public void SetPinned(SessionCardViewModel card, bool pinned)
    {
        Order.SetPinned(KeyOf(card), pinned);
        Rearrange();
    }

    // A drag: the card with key `dragged` goes before or after `target`.
    public bool Place(string dragged, SessionCardViewModel target, bool after)
    {
        if (!Order.Place(dragged, KeyOf(target), after)) { return false; }
        Rearrange();
        return true;
    }

    void Hook(SessionCardViewModel card)
    {
        card.Row = new RowActions(this, card);
        // Opened, its news has been seen.
        card.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionCardViewModel.Folded) && !card.Folded && owner.ContainsKey(card))
            {
                Order.MarkSeen(KeyOf(card), card.Session.UpdatedAt);
                card.Unread = false;
            }
        };
    }

    // The cards shown, in order, with the group headers, the dots and the Ended group. Rows move
    // only when the arrangement says so, and never while someone types in any card: moving one
    // then would take the words from under their fingers (in Most recent, where upserts move rows).
    void Rearrange()
    {
        var a = Arranged();
        var want = a.Pinned.Concat(a.Sessions).Select(e => byId[e.Key]).ToList();
        var hold = Sessions.Any(c => c.Editing);
        foreach (var gone in Sessions.Except(want).ToList()) { Sessions.Remove(gone); }
        for (var i = 0; i < want.Count; i++)
        {
            var c = want[i];
            var at = Sessions.IndexOf(c);
            if (at < 0) { Sessions.Insert(Math.Min(i, Sessions.Count), c); }
            else if (at != i && !hold) { Sessions.Move(at, i); }
        }
        for (var i = 0; i < want.Count; i++)
        {
            var c = want[i];
            c.Pinned = i < a.Pinned.Count;
            c.GroupHeader = a.Pinned.Count == 0 ? null : i == 0 ? "Pinned" : i == a.Pinned.Count ? "Sessions" : null;
            var key = KeyOf(c);
            if (!c.Folded) { Order.MarkSeen(key, c.Session.UpdatedAt); }
            c.Unread = Order.Unread(key, c.Session.UpdatedAt);
        }
        SyncEnded(a.Ended);
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
    public bool CanDrag => main.Order.Sort == SessionSort.Yours;
    public bool MoveUp() => main.Move(card, -1);
    public bool MoveDown() => main.Move(card, 1);
    public void TogglePin() => main.SetPinned(card, !card.Pinned);
    public bool Drop(string dragged, bool after) => main.Place(dragged, card, after);
}
