using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

// The stored turns above the latest exchange (#292 W2a, ui.md §6.4 History): drawn read-only from the
// session's SessionHistory, oldest first. The pane opens at the latest exchange once, when the turns
// are first drawn; after that nothing here moves the view.
public sealed partial class SessionView
{
    SessionHistory? history;
    bool openAtLatest;
    string? shownHistory;

    // The session shown is another: its turns replace the last one's, and its pane opens at its latest.
    // The same session shown again (the view re-attached) keeps what it has and does not scroll.
    void WatchHistory()
    {
        UnwatchHistory();
        history = Card?.Stored;
        if (shownHistory != Card?.Id) { shownHistory = Card?.Id; openAtLatest = true; }
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
        if (history is not null) { history.Changed -= OnHistoryChanged; }
        history = null;
    }

    // Raised on whichever thread finished the load.
    void OnHistoryChanged() => DispatcherQueue.TryEnqueue(ShowHistory);

    void ShowHistory()
    {
        var turns = history?.Turns ?? [];
        var rows = HistoryDraw.Rows(turns, Card?.UserMessage);
        var line = history is null ? null : HistoryDraw.Line(history.State, rows.Count, !string.IsNullOrEmpty(Card?.UserMessage), history.Start);
        HistoryRows.ItemsSource = rows;
        HistoryLine.Text = line ?? "";
        HistoryLine.Visibility = line is null ? Visibility.Collapsed : Visibility.Visible;
        HistoryRetry.Visibility = history?.State == HistoryLoad.Failed ? Visibility.Visible : Visibility.Collapsed;
        if (rows.Count > 0 && openAtLatest) { OpenAtLatest(); }
    }

    // Once: the latest exchange at the top of the view, the stored turns above it. Cleared first, so no
    // later draw (a page of older turns, a retry, a live turn) moves the view.
    void OpenAtLatest()
    {
        openAtLatest = false;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            HistoryColumn.UpdateLayout();
            Conversation.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
        });
    }

    void OnHistoryRetry(object sender, RoutedEventArgs e) => _ = history?.LoadOlderAsync();
}
