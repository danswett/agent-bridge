namespace AgentBridge.App.Core;

// The machine tabs over the list (ui.md §6.1, #290): which show and which wait under More.
// Shown: the tabs' indexes in their order. More: the rest, in the same order.
public sealed record MachineTabSplit(IReadOnlyList<int> Shown, IReadOnlyList<int> More);

// The tabs once scrolled sideways under a thin scrollbar, so a machine past the pane's edge was
// hard to find and harder to reach. Now they never scroll: every tab that fits shows, in its own
// order, and the rest are listed under More. The chosen tab always shows, wherever it is in the
// order, so its indicator does too; after it the tabs keep their order, All first, until one
// doesn't fit. A tab is never moved: one chosen from More takes its own place among those shown.
// It knows nothing of XAML, so its rules run on Linux CI.
public static class MachineTabFit
{
    // widths: each tab's natural width, already capped by the longest a tab's name may be
    // (DesignLayout.MachineTab). room: the bar's width. more: More's width, and gap the space
    // between two tabs and before More.
    public static MachineTabSplit Split(IReadOnlyList<double> widths, int selected, double room, double more, double gap = 0)
    {
        var all = Enumerable.Range(0, widths.Count).ToList();
        if (widths.Sum() + gap * Math.Max(0, widths.Count - 1) <= room) { return new(all, []); }

        // Something waits under More: it takes its own width first.
        var left = room - more;
        var shown = new SortedSet<int>();
        var order = selected >= 0 && selected < widths.Count ? all.Where(i => i != selected).Prepend(selected) : all;
        foreach (var i in order)
        {
            var need = widths[i] + gap;
            // The chosen tab shows even where nothing fits beside More: it is the one being looked at.
            if (need > left && !(i == selected && shown.Count == 0)) { break; }
            shown.Add(i);
            left -= need;
        }
        return new([.. shown], [.. all.Where(i => !shown.Contains(i))]);
    }
}
