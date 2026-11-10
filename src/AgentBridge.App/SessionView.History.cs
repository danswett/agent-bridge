using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
    readonly System.Collections.ObjectModel.ObservableCollection<HistoryRow> historyShown = [];
    readonly Dictionary<string, HistoryDisclosure> openPrompts = [];

    // The session shown is another: its turns replace the last one's, and it opens at its end.
    // The same session shown again (the view re-attached) keeps what it has and does not scroll.
    void WatchHistory()
    {
        UnwatchHistory();
        history = Card?.Stored;
        if (shownHistory != Card?.Id) { shownHistory = Card?.Id; pinEnd = true; openPrompts.Clear(); newMessages = 0; }
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
        var rows = HistoryDraw.Rows(turns, Card?.UserMessage, openPrompts);
        var line = history is null ? null : HistoryDraw.Line(history.State, rows.Count, !string.IsNullOrEmpty(Card?.UserMessage), history.Start);
        var scroll = ConversationScroll;
        // Measured before the rows change: the pane collapsing under the view must not read as scrolled back.
        var atEnd = pinEnd || scroll is { ScrollableHeight: > 0 } && scroll.VerticalOffset >= scroll.ScrollableHeight - ConversationFollow.Slack;
        var offset = scroll?.VerticalOffset ?? 0;
        var arrived = HistoryRowSync.Appended(historyShown, rows);
        var changed = HistoryRowSync.Apply(historyShown, rows);
        // Scrolled back, a person is never moved; they are told instead (#1739).
        if (arrived > 0 && !atEnd) { newMessages += arrived; }
        ShowNewMessages();
        // Nothing moved when no row changed, and a scroller left from another session says nothing of where this one is.
        if (changed) { KeepPlace(atEnd, offset); }
        HistoryLine.Text = line ?? "";
        HistoryLine.Visibility = line is null ? Visibility.Collapsed : Visibility.Visible;
        HistoryRetry.Visibility = history?.State == HistoryLoad.Failed ? Visibility.Visible : Visibility.Collapsed;
    }

    // The person is scrolling: the view is theirs from here, and stays where they leave it.
    internal void ReleasePin() { pinEnd = false; pinReleases++; }

    // Content changed in the conversation can reflow it, so the place is put back once layout has settled:
    // the end if the view is pinned or was at the end, else the offset it was at (#1635). A scrolled-back
    // view is only put back if it was moved up; the scroller already holds still what is inserted above it.
    // Skipped if another session was opened, or the person took the view, meanwhile.
    void KeepPlace(bool atEnd, double offset)
    {
        var (session, releases) = (shownHistory, pinReleases);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (session != shownHistory || releases != pinReleases || ConversationScroll is not { } scroll) { return; }
            HistoryColumn.UpdateLayout();
            var target = atEnd || pinEnd ? scroll.ScrollableHeight : Math.Min(offset, scroll.ScrollableHeight);
            var off = atEnd || pinEnd ? Math.Abs(scroll.VerticalOffset - target) > 0.5 : scroll.VerticalOffset < target - 0.5;
            if (off) { scroll.ChangeView(null, target, null, disableAnimation: true); }
        });
    }

    void OnHistoryRetry(object sender, RoutedEventArgs e) => _ = history?.LoadOlderAsync();

    // Stored turns that arrived while the view was scrolled back, until it is back at the end.
    int newMessages;

    internal void ShowNewMessages()
    {
        if (newMessages > 0 && (pinEnd || follow.AtEnd)) { newMessages = 0; }
        NewMessages.Content = newMessages == 1 ? "1 new message" : $"{newMessages} new messages";
        NewMessages.Visibility = newMessages > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnNewMessages(object sender, RoutedEventArgs e)
    {
        newMessages = 0;
        ShowNewMessages();
        pinEnd = true;
        KeepPlace(true, 0);
    }
}
