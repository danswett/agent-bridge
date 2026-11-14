namespace AgentBridge.App;

public sealed partial class SessionView
{
    long historyFrameChecks, historyTicks, historyDrawAttempts, historyPrepended;

    // Read-only snapshots distinguish an end pin from a stalled older-drawing scheduler (#2483).
    internal object HistoryDrawingTrace()
    {
        var turns = history?.Turns ?? [];
        var first = historyShown.FirstOrDefault()?.Id;
        var firstIndex = -1;
        for (var i = 0; i < turns.Count; i++)
        {
            if (turns[i].Id == first) { firstIndex = i; break; }
        }
        return new
        {
            session = Card?.Id, shownHistory, rowsOf, liveTurn = Card?.LiveTurn,
            loaded = IsLoaded, attached, state = history?.State.ToString(),
            retained = turns.Count, drawn = historyShown.Count, historyVisible,
            first, firstIndex, last = historyShown.LastOrDefault()?.Id,
            pinEnd, pinReleases, scrolling, panning, keyTarget, anchoring,
            firstFramePending = historyFirstFrame is not null,
            timerRunning = historyDraw?.IsRunning == true, historyDrawQueued,
            historyFrameChecks, historyTicks, historyDrawAttempts, historyPrepended,
            offset = ConversationScroll?.VerticalOffset, extent = ConversationScroll?.ScrollableHeight,
        };
    }
}
