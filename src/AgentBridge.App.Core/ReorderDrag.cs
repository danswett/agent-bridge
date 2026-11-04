namespace AgentBridge.App.Core;

// One card as the list lays it out when a drag starts (#592), in the list's own coordinates, which
// scrolling doesn't move: its top, the height of the group header drawn at its top (0 for none),
// and its extent down to the next card's top (its height and the space after it).
public sealed record ReorderCard(string Key, bool Pinned, bool Fixed, double Top, double Header, double Extent)
{
    public double CardTop => Top + Header;
    public double CardHeight => Extent - Header;
}

// Where a held card would land: a place among the other cards of a group.
public readonly record struct ReorderTarget(bool Pinned, int Index);

// The list as drawn with the gap at one target: how far each other card has moved from where it was
// laid out, where each header and the gap are, and the slot a group the held card left empty keeps.
public sealed record ReorderLayout(
    ReorderTarget Target,
    IReadOnlyDictionary<string, double> Shifts,
    double? PinnedHeader,
    double? SessionsHeader,
    double GapTop,
    double GapHeight,
    double? Placeholder);

// A held card's drag through the list (ui.md §5.3 Reorder, §6.1 Moving): the places it can land in,
// the one nearest it, and where the cards it passes, the gap and the headers are then. Kept apart
// from the window, which only measures the cards and moves what this says.
//
// The list is one column: Pinned's header, its cards, Sessions' header, its cards, with no headers
// when nothing is pinned. The held card leaves its slot and a gap one card tall opens where it would
// land; the cards between ease one slot, and a header moves only when the gap crosses it. With
// nothing pinned there is no Pinned to drop into. A group the held card leaves empty keeps its slot,
// so nothing moves under it ("Drop here to keep it pinned"). Rows a machine hasn't finished sending
// sit at the top of Sessions with no place yet: nothing lands among them.
public sealed class ReorderDrag
{
    // How much nearer another place must be before the gap moves to it, so it doesn't flicker on a
    // boundary.
    public const double Hysteresis = 1;

    readonly ReorderCard first;
    readonly List<ReorderCard> pinned;
    readonly List<ReorderCard> rest;
    readonly double pinnedHeader;
    readonly double sessionsHeader;

    public ReorderDrag(IReadOnlyList<ReorderCard> cards, string held)
    {
        if (cards.Count == 0) { throw new ArgumentException("a drag needs the list's cards", nameof(cards)); }
        Held = cards.FirstOrDefault(c => c.Key == held) ?? throw new ArgumentException($"no card {held} in the list", nameof(held));
        first = cards[0];
        Grouped = cards.Any(c => c.Pinned);
        pinned = [.. cards.Where(c => c.Pinned && c != Held)];
        rest = [.. cards.Where(c => !c.Pinned && c != Held)];
        pinnedHeader = cards.FirstOrDefault(c => c.Pinned)?.Header ?? 0;
        sessionsHeader = cards.FirstOrDefault(c => !c.Pinned)?.Header ?? 0;
        Origin = new ReorderTarget(Held.Pinned, cards.TakeWhile(c => c != Held).Count(c => c.Pinned == Held.Pinned));
    }

    public ReorderCard Held { get; }
    // Anything pinned: two groups, each under its header.
    public bool Grouped { get; }
    // Where the held card came from.
    public ReorderTarget Origin { get; }
    public double GapHeight => Held.CardHeight;

    // Pinned exists only once something is pinned (the held card included); Sessions while anything
    // is unpinned.
    public bool HasGroup(bool pinnedGroup) => pinnedGroup ? Grouped : rest.Count > 0 || !Held.Pinned;

    List<ReorderCard> Others(bool pinnedGroup) => pinnedGroup ? pinned : rest;

    // The other cards of a group, as drawn: what a target's index counts in.
    public IReadOnlyList<string> Group(bool pinnedGroup) => [.. Others(pinnedGroup).Select(c => c.Key)];

    // The first place in a group a card can land: below any rows that have no place yet.
    int First(bool pinnedGroup) => Others(pinnedGroup).TakeWhile(c => c.Fixed).Count();

    public IEnumerable<ReorderTarget> Targets =>
        new[] { true, false }.Where(HasGroup).SelectMany(g => Enumerable.Range(First(g), Others(g).Count - First(g) + 1).Select(i => new ReorderTarget(g, i)));

    public bool Allows(ReorderTarget t) => HasGroup(t.Pinned) && t.Index >= First(t.Pinned) && t.Index <= Others(t.Pinned).Count;

    // Its place as said aloud: position N of M in its group, the held card counted.
    public (int Position, int Of) Place(ReorderTarget t) => (t.Index + 1, Others(t.Pinned).Count + 1);

    // A drop there pins or unpins the held card.
    public bool Crosses(ReorderTarget t) => t.Pinned != Held.Pinned;

    IEnumerable<bool> Groups => new[] { true, false }.Where(HasGroup);

    public ReorderLayout Layout(ReorderTarget target)
    {
        var shifts = new Dictionary<string, double>(StringComparer.Ordinal);
        double? pinnedTop = null, sessionsTop = null, placeholder = null;
        double gapTop = 0;
        var y = first.Top;
        foreach (var g in Groups)
        {
            if (Grouped)
            {
                if (g) { pinnedTop = y; y += pinnedHeader; } else { sessionsTop = y; y += sessionsHeader; }
            }
            var others = Others(g);
            for (var i = 0; i <= others.Count; i++)
            {
                if (target.Pinned == g && target.Index == i) { gapTop = y; y += GapHeight; }
                if (i < others.Count) { shifts[others[i].Key] = y - others[i].CardTop; y += others[i].CardHeight; }
            }
            if (Keeps(g, target.Pinned)) { placeholder = y; y += GapHeight; }
        }
        return new ReorderLayout(target, shifts, pinnedTop, sessionsTop, gapTop, GapHeight, placeholder);
    }

    // The held card's own group left empty while the gap is in the other: it keeps a slot.
    bool Keeps(bool group, bool gapIn) => Others(group).Count == 0 && Held.Pinned == group && gapIn != group;

    // Each place's gap top in a group, the gap being there: everything above a place is laid out
    // the same whichever place in that group it is, so one pass gives them all.
    double[] GapTops(bool group)
    {
        var tops = new double[Others(group).Count + 1];
        var y = first.Top;
        foreach (var g in Groups)
        {
            if (Grouped) { y += g ? pinnedHeader : sessionsHeader; }
            var others = Others(g);
            for (var i = 0; i <= others.Count; i++)
            {
                if (g == group) { tops[i] = y; }
                if (i < others.Count) { y += others[i].CardHeight; }
            }
            if (Keeps(g, group)) { y += GapHeight; }
        }
        return tops;
    }

    // The place whose gap's centre is nearest the held card's centre; the current one unless another
    // is nearer by more than the hysteresis.
    public ReorderTarget Nearest(double centre, ReorderTarget current)
    {
        var best = current;
        var bestDistance = double.MaxValue;
        var currentDistance = double.MaxValue;
        foreach (var g in Groups)
        {
            var tops = GapTops(g);
            for (var i = First(g); i < tops.Length; i++)
            {
                var d = Math.Abs(tops[i] + (GapHeight / 2) - centre);
                if (current == new ReorderTarget(g, i)) { currentDistance = d; }
                if (d < bestDistance) { (best, bestDistance) = (new ReorderTarget(g, i), d); }
            }
        }
        return bestDistance < currentDistance - Hysteresis ? best : Allows(current) ? current : best;
    }

    // Auto-scroll (§6.1, Edges): within 10% of the list's height of its top or bottom (40 to 80),
    // 1000 × (depth ÷ zone)² a second, up (negative) or down, at full speed past the edge.
    public const double ScrollMax = 1000;

    public static double ScrollZone(double height) => Math.Clamp(height * 0.1, 40, 80);

    public static double ScrollSpeed(double pointer, double top, double height)
    {
        var zone = ScrollZone(height);
        double depth;
        if (pointer < top + zone) { depth = -Math.Min(top + zone - pointer, zone); }
        else if (pointer > top + height - zone) { depth = Math.Min(pointer - (top + height - zone), zone); }
        else { return 0; }
        return Math.Sign(depth) * ScrollMax * (depth / zone) * (depth / zone);
    }
}

// What a move says to a screen reader (ui.md §9, Moving a session), and the words a held card and
// the header it crosses show.
public static class ReorderWords
{
    public static string Group(bool pinned) => pinned ? "Pinned" : "Sessions";

    public static string PickedUp(string title, (int Position, int Of) at, bool pinned) => $"Picked up {title}, position {at.Position} of {at.Of} in {Group(pinned)}.";

    // While held, as its place changes. Across the other group's header it says what the drop will do.
    public static string Held((int Position, int Of) at, bool crossing, bool pinned) =>
        !crossing ? $"Position {at.Position} of {at.Of}."
        : pinned ? $"Pinned, position {at.Position} of {at.Of}. Drops as pinned."
        : $"Unpinned, position {at.Position} of {at.Of}. Drops as unpinned.";

    public static string Dropped((int Position, int Of) at, bool crossed, bool pinned) =>
        !crossed ? $"Moved to position {at.Position} of {at.Of} in {Group(pinned)}."
        : pinned ? $"Pinned. Position {at.Position} of {at.Of} in Pinned."
        : $"Unpinned. Position {at.Position} of {at.Of} in Sessions.";

    public static string Cancelled((int Position, int Of) at) => $"Cancelled. Back at position {at.Position} of {at.Of}.";

    // Alt+Up or Alt+Down at its group's end moves nothing.
    public static string End(bool up, bool pinned) => up ? $"Top of {Group(pinned)}." : $"End of {Group(pinned)}.";

    // The target header's cue, and the chip in place of the held card's age.
    public static string HeaderCue(bool pinned) => pinned ? " · drop to pin" : " · drop to unpin";
    public static string Chip(bool pinned) => pinned ? "Pin" : "Unpin";
    public const string Cancel = "Cancel";
    // The slot a group the held card leaves empty keeps, for dropping it back (#594: the owner's words).
    public static string Keep(bool pinned) => pinned ? "Drop here to keep it pinned" : "Drop here to unpin";
}
