using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

// Your messages (ui.md §6.4, #255): a bubble at the right of the conversation's column, at most 85%
// of it, as the PWA has it, whatever else the conversation holds. The column once took the width of
// its widest block, so beside a short reply your short message sat at the left of a wide pane, and
// the reply box, under it, was as narrow as its placeholder. The column and the reply box are centred
// in the pane, as the PWA's are, and stay centred as a divider is dragged; their scrollers are the
// pane's width, so the scroll bar is at the pane's edge, not the column's.
static partial class SelfTest
{
    static async Task CheckYourMessages(MainWindow window, MainViewModel main, Action<bool, string> check, string outFile, string launchedPrompt)
    {
        var layout = window.Sessions.Layout!;
        var readingMax = (double)Application.Current.Resources["AbLayoutReadingMax"];
        // The launched session's one-line prompt and short "Done" reply, and the demo's longer message
        // that wraps in a narrow column under a long reply.
        var launched = main.AllCards.FirstOrDefault(c => c.UserMessage == launchedPrompt);
        if (launched is null || !await Until(() => launched.HasResponse, TimeSpan.FromSeconds(10))) { check(false, "finds the launched session's message and reply"); return; }
        var cards = new[] { launched, main.AllCards.First(c => c.Id == DelegatingId) };
        yoursId = launched.Id;

        var wrong = new List<string>();
        var dragged = new List<string>();
        foreach (var (width, mode) in new[] { (1440.0, PaneMode.Three), (1200.0, PaneMode.Two), (400.0, PaneMode.One) })
        {
            Resize(window, width, 900);
            await Until(() => layout.Mode == mode, TimeSpan.FromSeconds(10));
            foreach (var card in cards)
            {
                var view = await Choose(window, main, card.Id, "YourMessage");
                layout.Tab = SessionTab.Conversation;
                var scroll = view is null ? null : ConversationScroller(view);
                var column = scroll is null ? null : Find<StackPanel>(scroll).FirstOrDefault(s => s.Name == "Conversation");
                var bubble = view is null ? null : ById<Border>(view, "YourMessage");
                if (scroll is null || column is null || bubble is null || !await Until(() => bubble.ActualWidth > 0 && scroll.ViewportWidth > 0, TimeSpan.FromSeconds(5)))
                {
                    wrong.Add($"{card.Id} at {width}: not drawn");
                    continue;
                }
                await Task.Delay(300);
                if (Misplaced(view!, scroll, column, bubble, readingMax) is { } misplaced) { wrong.Add($"{card.Id} at {width}: {misplaced}"); }
            }
            // A divider dragged: the column follows the pane's width as it changes, not once let go.
            // The list narrowed to its narrowest, then halfway back, so the session stays wider than the
            // measure and the column moves.
            if (width is 1440.0 && await Choose(window, main, launched.Id, "YourMessage") is { } shown
                && ConversationScroller(shown) is { } scroll2 && Find<StackPanel>(scroll2).FirstOrDefault(s => s.Name == "Conversation") is { } column2
                && ById<Border>(shown, "YourMessage") is { } bubble2)
            {
                var divider = window.Sessions.ListDividerView;
                var x = divider.TransformToVisual(null).TransformPoint(default).X;
                var before = scroll2.ViewportWidth;
                divider.Begin(x);
                foreach (var by in new[] { -(DesignLayout.List - DesignLayout.ListMin), -(DesignLayout.List - DesignLayout.ListMin) / 2 })
                {
                    divider.Move(x + by);
                    // Measured once the pane has taken the dragged width, rather than after a fixed wait.
                    await Until(() => Math.Abs(scroll2.ViewportWidth - (before - by)) <= 1, TimeSpan.FromSeconds(5));
                    var misplaced = Misplaced(shown, scroll2, column2, bubble2, readingMax);
                    if (Math.Abs(scroll2.ViewportWidth - (before - by)) > 1 || misplaced is not null)
                    {
                        dragged.Add($"{by:0} px: pane {scroll2.ViewportWidth:0} of {before - by:0}{(misplaced is not null ? $", {misplaced}" : "")}");
                    }
                }
                divider.End();
                divider.Reset();
                await Until(() => Math.Abs(scroll2.ViewportWidth - before) <= 1, TimeSpan.FromSeconds(5));
            }
            else if (width is 1440.0) { dragged.Add("the launched session not drawn at 1440"); }
            // Pictures for review: the short message beside the short reply of a few blocks, wide and
            // narrow, both themes; high contrast's are taken in its own pass (PictureYoursInHighContrast).
            if (width is 1440.0 or 1200.0 or 400.0 && await Choose(window, main, launched.Id, "YourMessage") is not null)
            {
                await Task.Delay(300);
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                {
                    window.RootElement.RequestedTheme = theme;
                    await Task.Delay(300);
                    await Picture(window.RootElement, Path.ChangeExtension(outFile, $".yours.{width:0}.{theme.ToString().ToLowerInvariant()}.png"));
                }
                window.RootElement.RequestedTheme = ElementTheme.Default;
            }
        }
        check(wrong.Count == 0, $"puts your message at the right of the conversation's column, at most 85% of it, beside a short reply and a long one, the column and the reply box at the same measure, centred in the pane, at every width{(wrong.Count > 0 ? $": not {string.Join("; ", wrong)}" : "")}");
        check(dragged.Count == 0, $"keeps the conversation and the reply box centred in the pane while a divider is dragged{(dragged.Count > 0 ? $": not {string.Join("; ", dragged)}" : "")}");
    }

    // The launched session pictured above, for its pictures in high contrast, which is on only later,
    // in its own pass.
    static string? yoursId;

    // The light and dark pictures' third theme (#765): the launched session at the same three widths
    // in high contrast, while that pass has it on.
    static async Task PictureYoursInHighContrast(MainWindow window, Action<bool, string> check, string outFile)
    {
        var taken = new List<string>();
        if (yoursId is not null && window.Sessions.Layout is { } layout)
        {
            foreach (var (width, mode) in new[] { (1440.0, PaneMode.Three), (1200.0, PaneMode.Two), (400.0, PaneMode.One) })
            {
                Resize(window, width, 900);
                await Until(() => layout.Mode == mode, TimeSpan.FromSeconds(10));
                if (await Choose(window, window.ViewModel, yoursId, "YourMessage") is null) { continue; }
                layout.Tab = SessionTab.Conversation;
                await Task.Delay(300);
                await Picture(window.RootElement, Path.ChangeExtension(outFile, $".yours.{width:0}.highcontrast.png"));
                taken.Add($"{width:0}");
            }
            Resize(window, 1440, 1000);
        }
        check(taken.Count == 3, $"pictures your message and its reply in high contrast at 1440, 1200 and 400 ({(taken.Count > 0 ? string.Join(", ", taken) : "none")})");
    }

    // What is out of place in a conversation's column, its bubble and its reply box, or null: each
    // column min(viewport, measure) wide and centred in its scroller (at the leading edge once the
    // pane is narrower than the measure), the scroller the pane's width, and the bubble at the
    // column's right, at most 85% of it.
    static string? Misplaced(SessionView view, ScrollViewer scroll, StackPanel column, Border bubble, double readingMax)
    {
        var problems = new List<string>();
        var measure = Math.Min(scroll.ViewportWidth, readingMax);
        var at = (scroll.ViewportWidth - measure) / 2;
        var columnLeft = column.TransformToVisual(scroll).TransformPoint(default).X;
        // The bubble against the column as drawn; the column's own width is checked against the measure.
        var inner = column.ActualWidth - column.Padding.Left - column.Padding.Right;
        var right = bubble.TransformToVisual(column).TransformPoint(new Windows.Foundation.Point(bubble.ActualWidth, 0)).X;
        if (Math.Abs(columnLeft - at) > 1 || Math.Abs(column.ActualWidth - measure) > 1 || Math.Abs(right - (column.ActualWidth - column.Padding.Right)) > 1 || bubble.ActualWidth > inner * 0.85 + 1)
        {
            problems.Add($"column {columnLeft:0}+{column.ActualWidth:0} of {measure:0} at {at:0} in {scroll.ViewportWidth:0}, bubble {bubble.ActualWidth:0} wide ending at {right:0} in it");
        }
        // The scroll bar at the pane's edge: the scroller spans the session pane, the column inside it.
        var scrollLeft = scroll.TransformToVisual(view).TransformPoint(default).X;
        if (Math.Abs(scrollLeft) > 1 || Math.Abs(scroll.ActualWidth - view.ActualWidth) > 1)
        {
            problems.Add($"scroller {scrollLeft:0}+{scroll.ActualWidth:0} of the pane's {view.ActualWidth:0}");
        }
        // The reply box under it, at the same measure and centred the same way, never only as wide as its placeholder.
        var reply = ById<FrameworkElement>(view, "ReplyBox");
        var composer = reply is null ? null : FindAncestor<ScrollViewer>(reply);
        if (reply is null || composer is null) { problems.Add("no reply box"); }
        else
        {
            var replyMeasure = Math.Min(composer.ViewportWidth, readingMax);
            var replyAt = (composer.ViewportWidth - replyMeasure) / 2;
            var replyLeft = reply.TransformToVisual(composer).TransformPoint(default).X;
            if (Math.Abs(reply.ActualWidth - replyMeasure) > 1 || Math.Abs(replyLeft - replyAt) > 1)
            {
                problems.Add($"reply box {replyLeft:0}+{reply.ActualWidth:0} of {replyMeasure:0} at {replyAt:0}");
            }
        }
        return problems.Count > 0 ? string.Join(", ", problems) : null;
    }
}
