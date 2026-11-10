namespace AgentBridge.App.Core;

// Brings the rows on screen to the rows wanted by changing only what differs (#1635). Replacing the whole
// list collapses the History pane above the conversation for a frame, and the scroller clamps to the top.
public static class HistoryRowSync
{
    // True when the rows on screen changed.
    public static bool Apply(IList<HistoryRow> shown, IReadOnlyList<HistoryRow> wanted)
    {
        var head = 0;
        while (head < shown.Count && head < wanted.Count && Same(shown[head], wanted[head])) { head++; }
        var tail = 0;
        while (tail < shown.Count - head && tail < wanted.Count - head
            && Same(shown[shown.Count - 1 - tail], wanted[wanted.Count - 1 - tail])) { tail++; }

        var drop = shown.Count - head - tail;
        var add = wanted.Count - head - tail;
        for (var i = 0; i < Math.Min(drop, add); i++) { shown[head + i] = wanted[head + i]; }
        for (var i = add; i < drop; i++) { shown.RemoveAt(head + add); }
        for (var i = drop; i < add; i++) { shown.Insert(head + i, wanted[head + i]); }
        return drop > 0 || add > 0;
    }

    // How many rows come after the last one on screen: the messages that arrived under a person who is
    // reading further up (#1739). Older pages, edits and another session's rows are none.
    public static int Appended(IReadOnlyList<HistoryRow> shown, IReadOnlyList<HistoryRow> wanted)
    {
        if (shown.Count == 0) { return 0; }
        var last = shown[^1].Id;
        for (var i = wanted.Count - 1; i >= 0; i--)
        {
            if (wanted[i].Id == last) { return wanted.Count - 1 - i; }
        }
        return 0;
    }

    // Blocks are compared by what they say: each draw builds new block objects for the same turn.
    static bool Same(HistoryRow a, HistoryRow b)
    {
        if (a.Id != b.Id || !ReferenceEquals(a.Disclosure, b.Disclosure) || a.Prompt != b.Prompt || a.Note != b.Note || a.Blocks.Count != b.Blocks.Count) { return false; }
        for (var i = 0; i < a.Blocks.Count; i++)
        {
            if (a.Blocks[i].Text != b.Blocks[i].Text) { return false; }
        }
        return true;
    }
}
