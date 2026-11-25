namespace AgentBridge.App.Core;

// Most recent re-sorts in batches (#1858, #1578 S3b): pwa/js/recent-order.js, rule for rule, so both
// clients agree. Activity moves rows together, no sooner than Interval after the last batch. While
// the person holds the list (a pointer over a row, focus in one, typing) nothing moves, except a
// session or question that has just arrived: that row alone goes to the top of its group.
// Pinned and the rest are separate groups. A change of sort, or a move the person made, shows the
// order as it is and starts afresh; a row that ended leaves at once.
public sealed class RecentBatch(TimeProvider time)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    SessionSort? sort;
    List<string> shownPinned = [];
    List<string> shownRest = [];
    Dictionary<string, string?> questions = new(StringComparer.Ordinal);
    DateTimeOffset? last;

    // How long until what is pending may move, or null when nothing waits (or a hold does the waiting).
    public TimeSpan? Wait { get; private set; }
    // The rows this arrangement moved: the ones that arrived, or every one a batch placed.
    public IReadOnlySet<string> Moved { get; private set; } = new HashSet<string>();

    // target: the order the sort asks for. held: someone is typing, pointing or has focus in the list.
    // fresh: the person moved, pinned or sorted, which shows the target as it is.
    public Arrangement Arrange(Arrangement target, SessionSort by, bool held, bool fresh = false)
    {
        Wait = null;
        var moved = new HashSet<string>(StringComparer.Ordinal);
        Moved = moved;
        var previous = questions;
        questions = target.Pinned.Concat(target.Sessions).ToDictionary(e => e.Key, e => e.Session.Question?.Id, StringComparer.Ordinal);
        var reset = fresh || sort != by;
        sort = by;
        var shown = target;
        if (by == SessionSort.Recent && !reset)
        {
            var pinned = Hold(target.Pinned, shownPinned, previous, moved);
            var rest = Hold(target.Sessions, shownRest, previous, moved);
            shown = target with { Pinned = pinned, Sessions = rest };
            var pending = Differ(target.Pinned, [.. pinned.Select(e => e.Key)]).Count + Differ(target.Sessions, [.. rest.Select(e => e.Key)]).Count > 0;
            var now = time.GetUtcNow();
            var remaining = last is { } at ? at + Interval - now : TimeSpan.Zero;
            if (pending && !held && remaining <= TimeSpan.Zero && moved.Count == 0)
            {
                shown = target;
                moved.UnionWith(Differ(target.Pinned, shownPinned));
                moved.UnionWith(Differ(target.Sessions, shownRest));
                last = now;
            }
            else if (pending && !held) { Wait = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero; }
        }
        else { last = null; }
        shownPinned = [.. shown.Pinned.Select(e => e.Key)];
        shownRest = [.. shown.Sessions.Select(e => e.Key)];
        return shown;
    }

    // The group as last shown, with the rows that arrived put first and the rest left in their places.
    static List<OrderEntry> Hold(IReadOnlyList<OrderEntry> target, List<string> shown, Dictionary<string, string?> previous, HashSet<string> moved)
    {
        var entries = target.ToDictionary(e => e.Key, StringComparer.Ordinal);
        var onShow = shown.ToHashSet(StringComparer.Ordinal);
        var arrived = target.Where(e => !onShow.Contains(e.Key)
            || (e.Session.Question?.Id is { Length: > 0 } q && !(previous.TryGetValue(e.Key, out var was) && was == q))).ToList();
        moved.UnionWith(arrived.Select(e => e.Key));
        return [.. arrived, .. shown.Where(k => entries.ContainsKey(k) && !moved.Contains(k)).Select(k => entries[k])];
    }

    // The rows of target that are not where `shown` has them.
    static List<string> Differ(IReadOnlyList<OrderEntry> target, IReadOnlyList<string> shown) =>
        [.. target.Where((e, i) => i >= shown.Count || shown[i] != e.Key).Select(e => e.Key)];
}
