namespace AgentBridge.App.Core;

// The session open in the session pane (ui.md §5.2, §6, #180): the selected row. It is what
// "open" means for unread (§6.1), what a toast, the tray, the search box or a launch opens, and it
// survives a resize across the breakpoints, since the panes only show it differently. A session
// that ends, or whose machine goes, is no longer selected.
public sealed partial class MainViewModel
{
    SessionCardViewModel? open;
    public SessionRecovery? Recovery { get; }
    SessionReading? pendingReading;
    bool restoreSelection;
    bool restoringSelection;

    public SessionCardViewModel? Selected
    {
        get => open;
        set
        {
            if (value is not null && !owner.ContainsKey(value)) { return; }
            if (value == open && (value is null || !restoreSelection)) { return; }
            if (!restoringSelection)
            {
                restoreSelection = false;
                pendingReading = null;
                if (value != open || value is not null && Recovery?.Current?.Key != KeyOf(value)) { Recovery?.Remember(value is null ? null : new(KeyOf(value), null)); }
            }
            var before = open;
            if (!Set(ref open, value)) { return; }
            if (before is not null) { before.Selected = false; }
            if (value is null) { return; }
            value.Selected = true;
            Order.MarkSeen(KeyOf(value), value.Session.Activity?.ResponseTurn);
            value.Unread = false;
        }
    }

    void RestoreSelection(Machine machine)
    {
        if (!restoreSelection || Recovery is null) { return; }
        SessionCardViewModel? next;
        SessionReading? reading = null;
        if (pendingReading is { } saved && byId.TryGetValue(saved.Key, out next) && owner[next].Host.Sessions.Complete)
        {
            reading = saved;
            pendingReading = null;
            restoreSelection = false;
        }
        else
        {
            // A different host may still be connecting. Its saved choice waits without
            // leaving an already available local session blank.
            if (pendingReading?.Key.StartsWith(machine.Key + "\n", StringComparison.Ordinal) == true) { pendingReading = null; }
            next = open ?? Sessions.FirstOrDefault(c => owner[c].Host.Sessions.Complete);
            if (next is null) { return; }
            if (pendingReading is null) { restoreSelection = false; }
        }
        if (reading is not null || pendingReading is null) { Recovery.Remember(reading ?? new(KeyOf(next), null)); }
        if (next == open) { return; }
        restoringSelection = true;
        try { Selected = next; }
        finally { restoringSelection = false; }
    }

    // A card going: if it was the one open, nothing is.
    void Deselect(SessionCardViewModel card)
    {
        if (card == open) { Selected = null; }
    }
}
