using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

// The stored turns above the latest exchange (#292 W2a, ui.md §6.4 History): drawn read-only from the
// session's SessionHistory, oldest first. A session opens at its end, the latest reply (#1646), and the
// view stays at the end as turns and replies arrive until the person scrolls (#1635); a person scrolled
// back keeps their offset.
public sealed partial class SessionView
{
    SessionHistory? history;
    // From opening a session until the person scrolls: whatever grows the conversation keeps it at its end.
    bool pinEnd;
    int pinReleases;
    string? shownHistory;
    string? rowsOf;
    int historyVisible = 1;
    bool historyDrawQueued;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? historyDraw;
    EventHandler<object>? historyFirstFrame;
    readonly System.Collections.ObjectModel.ObservableCollection<HistoryRow> historyShown = [];
    readonly Dictionary<string, HistoryDisclosure> openPrompts = [];
    double historyOffset;
    bool historyUp;

    // The session shown is another: its turns replace the last one's, and it opens at its end.
    // The same session shown again (the view re-attached) keeps what it has and does not scroll.
    void WatchHistory()
    {
        UnwatchHistory();
        history = Card?.Stored;
        if (shownHistory != Card?.Id) { shownHistory = Card?.Id; pinEnd = true; openPrompts.Clear(); historyVisible = 1; }
        if (history is null) { ShowHistory(); return; }
        history.Changed += OnHistoryChanged;
        ShowHistory();
        _ = history.LoadNewestAsync();
    }

    // Capabilities arriving (the host's hello) is when a first page that could not be asked for is asked.
    void RetryHistory()
    {
        if (history is { State: HistoryLoad.Idle }) { _ = history.LoadNewestAsync(); }
    }

    void UnwatchHistory()
    {
        historyDraw?.Stop();
        if (historyFirstFrame is not null) { CompositionTarget.Rendering -= historyFirstFrame; historyFirstFrame = null; }
        if (history is not null) { history.Changed -= OnHistoryChanged; }
        history = null;
    }

    // Raised on whichever thread finished the load.
    void OnHistoryChanged() => DispatcherQueue.TryEnqueue(ShowHistory);

    void ShowHistory()
    {
        var turns = history?.Turns ?? [];
        var from = Math.Max(0, turns.Count - historyVisible);
        // A live arrival extends what is already drawn instead of dropping its oldest visible turn.
        if (rowsOf == shownHistory && historyShown.FirstOrDefault() is { } first)
        {
            for (var i = 0; i < from; i++) { if (turns[i].Id == first.Id) { from = i; break; } }
        }
        var rows = HistoryDraw.Rows(turns.Skip(from).ToArray(), Card?.UserMessage, openPrompts);
        historyVisible = Math.Max(1, turns.Count - from);
        var line = history is null ? null : HistoryDraw.Line(history.State, rows.Count, !string.IsNullOrEmpty(Card?.UserMessage), history.Start);
        var scroll = ConversationScroll;
        // Measured before the rows change: the pane collapsing under the view must not read as scrolled back.
        var atEnd = pinEnd || !panning && !scrolling && keyTarget is null && scroll is { ScrollableHeight: > 0 } && scroll.VerticalOffset >= scroll.ScrollableHeight - ConversationFollow.Slack;
        var offset = scroll?.VerticalOffset ?? 0;
        var reading = atEnd ? null : ReadingHistory();
        var changed = HistoryRowSync.Apply(historyShown, rows);
        rowsOf = shownHistory;
        // Nothing moved when no row changed, and a scroller left from another session says nothing of where this one is.
        if (changed) { if (atEnd) { pinEnd = true; anchor = null; } KeepPlace(atEnd, offset, reading); }
        HistoryLine.Text = line ?? "";
        HistoryLine.Visibility = line is null ? Visibility.Collapsed : Visibility.Visible;
        HistoryRetry.Visibility = history?.State == HistoryLoad.Failed ? Visibility.Visible : Visibility.Collapsed;
        HistoryEarlier.Visibility = Ui.Show(history?.HasOlder == true && history.State != HistoryLoad.Failed);
        HistoryEarlier.Content = history?.State == HistoryLoad.Loading ? "Loading earlier messages…" : "Load earlier messages";
        if (from > 0)
        {
            if (historyDraw is null)
            {
                historyDraw = DispatcherQueue.CreateTimer();
                historyDraw.Interval = TimeSpan.FromMilliseconds(16);
                historyDraw.Tick += (_, _) =>
                {
                    if (historyDrawQueued) { return; }
                    var session = shownHistory;
                    historyDrawQueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                    {
                        historyDrawQueued = false;
                        if (session == shownHistory) { DrawEarlier(); }
                    });
                };
            }
            if (!historyDraw.IsRunning && historyFirstFrame is null)
            {
                // Older work waits for the latest exchange's first frame before competing with input.
                historyFirstFrame = (_, _) =>
                {
                    if (ConversationScroll is not { } s || pinEnd && s.VerticalOffset < s.ScrollableHeight - 1.5) { return; }
                    CompositionTarget.Rendering -= historyFirstFrame;
                    historyFirstFrame = null;
                    historyDraw.Start();
                };
                CompositionTarget.Rendering += historyFirstFrame;
            }
        }
        else { historyDraw?.Stop(); }
    }

    void DrawEarlier()
    {
        if (!IsLoaded || scrolling && anchoring is null || panning || keyTarget is not null) { return; }
        var turns = history?.Turns ?? [];
        var first = historyShown.FirstOrDefault();
        var end = first is null ? turns.Count - 1 : 0;
        while (first is not null && end < turns.Count && turns[end].Id != first.Id) { end++; }
        if (end <= 0 || end == turns.Count) { historyDraw?.Stop(); return; }
        var from = Math.Max(0, end - 2);
        var rows = HistoryDraw.Rows(turns.Skip(from).Take(end - from).ToArray(), null, openPrompts);
        var scroll = ConversationScroll;
        var atEnd = pinEnd || scroll is { ScrollableHeight: > 0 } && scroll.VerticalOffset >= scroll.ScrollableHeight - ConversationFollow.Slack;
        var offset = scroll?.VerticalOffset ?? 0;
        var extent = scroll?.ScrollableHeight ?? 0;
        var reading = atEnd ? null : ReadingHistory();
        if (atEnd) { anchor = null; }
        for (var i = rows.Count - 1; i >= 0; i--) { historyShown.Insert(0, rows[i]); }
        historyVisible = turns.Count - from;
        KeepPlace(atEnd, offset, reading, extent);
        if (atEnd) { KeepPlace(true, 0); }
        if (from == 0) { historyDraw?.Stop(); }
    }

    (string Id, double Top)? ReadingHistory()
    {
        if (ConversationScroll is not { } scroll) { return null; }
        for (var i = 0; i < historyShown.Count; i++)
        {
            if (HistoryRows.TryGetElement(i) is FrameworkElement row && Top(row, scroll) + row.ActualHeight > 0 && Top(row, scroll) < scroll.ViewportHeight)
            {
                return (historyShown[i].Id, Top(row, scroll));
            }
        }
        return null;
    }

    void HistoryScrolled(bool intermediate)
    {
        if (ConversationScroll is not { } scroll) { return; }
        if (scroll.VerticalOffset != historyOffset) { historyUp = scroll.VerticalOffset < historyOffset; }
        historyOffset = scroll.VerticalOffset;
        if (!intermediate && !pinEnd && historyUp && scroll.VerticalOffset <= scroll.ViewportHeight) { LoadEarlier(); }
        if (!intermediate) { historyUp = false; }
    }

    void LoadEarlier()
    {
        if (history is not { HasOlder: true, State: HistoryLoad.Idle }) { return; }
        ReleasePin();
        _ = history.LoadOlderAsync();
    }

    void OnHistoryEarlier(object sender, RoutedEventArgs e) => LoadEarlier();

    // The person is scrolling: the view is theirs from here, and stays where they leave it.
    internal void ReleasePin() { pinEnd = false; anchoring = null; pinReleases++; }

    // Content changed in the conversation can reflow it, so the place is put back once layout has settled:
    // the end if pinned, else the stored turn's screen position through a prepend (#1593).
    // Without a visible stored turn, keep the previous offset if the layout moved it up (#1635).
    // Skipped if another session was opened, or the person took the view, meanwhile.
    void KeepPlace(bool atEnd, double offset, (string Id, double Top)? reading = null, double? earlierExtent = null)
    {
        if (latestUsed) { atEnd = latestPinned; }
        var (session, releases) = (shownHistory, pinReleases);
        void Restore()
        {
            if (session != shownHistory || releases != pinReleases || ConversationScroll is not { } scroll) { return; }
            HistoryColumn.UpdateLayout();
            var target = atEnd || pinEnd ? scroll.ScrollableHeight : Math.Min(offset + (earlierExtent is { } extent ? scroll.ScrollableHeight - extent : 0), scroll.ScrollableHeight);
            if (!atEnd && !pinEnd && reading is { } kept)
            {
                var index = historyShown.ToList().FindIndex(r => r.Id == kept.Id);
                if (index >= 0 && HistoryRows.GetOrCreateElement(index) is FrameworkElement row)
                {
                    HistoryColumn.UpdateLayout();
                    target = Math.Clamp(scroll.VerticalOffset + Top(row, scroll) - kept.Top, 0, scroll.ScrollableHeight);
                }
            }
            var off = reading is not null || atEnd || pinEnd ? Math.Abs(scroll.VerticalOffset - target) > 0.5 : scroll.VerticalOffset < target - 0.5;
            if (off) { Anchoring(scroll, target); }
        }
        // Compensate a prepend before input can land between insertion and correction.
        if (earlierExtent is not null) { Restore(); }
        else { DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Restore); }
    }

    void OnHistoryRetry(object sender, RoutedEventArgs e) => _ = history?.LoadOlderAsync();
}
