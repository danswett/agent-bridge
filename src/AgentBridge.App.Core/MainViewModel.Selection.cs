namespace AgentBridge.App.Core;

// The session open in the session pane (ui.md §5.2, §6, #180): the selected row. It is what
// "open" means for unread (§6.1), what a toast, the tray, the search box or a launch opens, and it
// survives a resize across the breakpoints, since the panes only show it differently. A session
// that ends, or whose machine goes, is no longer selected.
public sealed partial class MainViewModel
{
    SessionCardViewModel? open;

    public SessionCardViewModel? Selected
    {
        get => open;
        set
        {
            if (value is not null && !owner.ContainsKey(value)) { return; }
            var before = open;
            if (!Set(ref open, value)) { return; }
            if (before is not null) { before.Selected = false; }
            if (value is null) { return; }
            value.Selected = true;
            Order.MarkSeen(KeyOf(value), value.Session.UpdatedAt);
            value.Unread = false;
        }
    }

    // A card going: if it was the one open, nothing is.
    void Deselect(SessionCardViewModel card)
    {
        if (card == open) { Selected = null; }
    }
}
