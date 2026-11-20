using AgentBridge.App.Core;
using Microsoft.UI.Xaml;

namespace AgentBridge.App;

public sealed partial class SessionView
{
    internal SessionRecovery? Recovery { get; set; }
    SessionPosition? restoreReading;
    bool applyingReading;

    void RecoverReading()
    {
        restoreReading = Recovery?.Current is { } saved && saved.Key == Card?.Row?.Key ? saved.Position : null;
        pinEnd = restoreReading?.AtEnd ?? true;
    }

    void SaveReading()
    {
        if (!attached || restoreReading is not null || ConversationScroll is not { } scroll
            || Card?.Row?.Key is not { } key || shownHistory != key || Recovery?.Current?.Key != key) { return; }
        var turn = ReadingHistory();
        Recovery.Remember(new(key, new(scroll.VerticalOffset, pinEnd || scroll.VerticalOffset >= scroll.ScrollableHeight - ConversationFollow.Slack,
            anchor, turn?.Id, turn?.Top ?? 0, turn is null && Card?.ResponseTruncated == false ? Card?.Session.Activity.ResponseTurn : null)));
    }

    // History arrives asynchronously and is drawn in small batches. Keep the saved anchor
    // until its row/block is laid out, instead of replacing it with startup's empty offset.
    bool RestoreReading()
    {
        if (restoreReading is not { } saved) { return false; }
        if (applyingReading || shownHistory != Card?.Row?.Key || ConversationScroll is not { ViewportHeight: > 0 } scroll || !ConversationList.IsLoaded) { return true; }
        // A live block needs the same live, untruncated reply: cutting its prefix renumbers the blocks.
        if (!saved.AtEnd && saved.Turn is null && (string.IsNullOrEmpty(saved.ResponseTurn) || saved.ResponseTurn != Card?.Session.Activity.ResponseTurn
            || saved.ResponseTurn != Card?.LiveTurn || Card?.ResponseTruncated == true))
        { restoreReading = null; anchor = null; pinEnd = true; return false; }
        if (history is { State: HistoryLoad.Loading } or { State: HistoryLoad.Idle, HasOlder: false, Start: null }) { return true; }
        applyingReading = true;
        try
        {
            var target = saved.AtEnd ? scroll.ScrollableHeight : Math.Clamp(saved.Offset, 0, scroll.ScrollableHeight);
            if (!saved.AtEnd && saved.Turn is { } id)
            {
                var index = historyShown.ToList().FindIndex(r => r.Id == id);
                if (index >= 0)
                {
                    if (HistoryRows.GetOrCreateElement(index) is not FrameworkElement row) { return true; }
                    HistoryColumn.UpdateLayout();
                    target = Math.Clamp(scroll.VerticalOffset + Top(row, scroll) - saved.TurnTop, 0, scroll.ScrollableHeight);
                }
                else if (historyShown.FirstOrDefault() is { } first && history?.Turns.TakeWhile(t => t.Id != first.Id).Any(t => t.Id == id) == true) { DrawEarlier(); return true; }
                else if (history is { HasOlder: true, State: HistoryLoad.Idle }) { _ = history.LoadOlderAsync(); return true; }
            }
            else if (!saved.AtEnd && saved.Block is { Index: >= 0 } block)
            {
                if (block.Index >= blocks.Count) { return true; }
                ScrollProgrammatically(() => ConversationList.ScrollIntoView(blocks[block.Index]));
                ConversationList.UpdateLayout();
                if (ConversationList.ContainerFromIndex(block.Index) is not FrameworkElement container) { return true; }
                target = block.OffsetFor(scroll.VerticalOffset, Top(container, scroll), scroll.ScrollableHeight);
            }
            restoreReading = null;
            pinEnd = saved.AtEnd;
            anchor = saved.Block;
            Anchoring(scroll, target);
        }
        finally { applyingReading = false; }
        return true;
    }
}
