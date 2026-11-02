using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

// Your messages (ui.md §6.4, #255): a bubble at the right of the conversation's column, at most 85%
// of it, as the PWA has it, whatever else the conversation holds. The column once took the width of
// its widest block, so beside a short reply your short message sat at the left of a wide pane, and
// the reply box, under it, was as narrow as its placeholder.
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

        var wrong = new List<string>();
        foreach (var (width, mode) in new[] { (1440.0, PaneMode.Three), (1200.0, PaneMode.Two), (400.0, PaneMode.One) })
        {
            Resize(window, width, 900);
            await Until(() => layout.Mode == mode, TimeSpan.FromSeconds(10));
            foreach (var card in cards)
            {
                var view = await Choose(window, main, card.Id, "YourMessage");
                layout.Tab = SessionTab.Conversation;
                var scroll = view is null ? null : ById<ScrollViewer>(view, "Conversation");
                var column = scroll is null ? null : Find<StackPanel>(scroll).FirstOrDefault(s => s.Name == "Conversation");
                var bubble = view is null ? null : ById<Border>(view, "YourMessage");
                if (scroll is null || column is null || bubble is null || !await Until(() => bubble.ActualWidth > 0 && scroll.ViewportWidth > 0, TimeSpan.FromSeconds(5)))
                {
                    wrong.Add($"{card.Id} at {width}: not drawn");
                    continue;
                }
                await Task.Delay(300);
                var measure = Math.Min(scroll.ViewportWidth, readingMax);
                var inner = measure - column.Padding.Left - column.Padding.Right;
                var columnLeft = column.TransformToVisual(scroll).TransformPoint(default).X;
                var right = bubble.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point(bubble.ActualWidth, 0)).X;
                if (Math.Abs(columnLeft) > 1 || Math.Abs(column.ActualWidth - measure) > 1 || Math.Abs(right - (measure - column.Padding.Right)) > 1 || bubble.ActualWidth > inner * 0.85 + 1)
                {
                    wrong.Add($"{card.Id} at {width}: column {columnLeft:0}+{column.ActualWidth:0} of {measure:0}, bubble {bubble.ActualWidth:0} wide ending at {right:0}");
                }
                // The reply box under it, at the same measure, never only as wide as its placeholder.
                var reply = ById<FrameworkElement>(view!, "ReplyBox");
                var composer = reply is null ? null : FindAncestor<ScrollViewer>(reply);
                if (reply is null || composer is null || Math.Abs(reply.ActualWidth - Math.Min(composer.ViewportWidth, readingMax)) > 1)
                {
                    wrong.Add($"{card.Id} at {width}: reply box {reply?.ActualWidth ?? 0:0} of {(composer is null ? 0 : Math.Min(composer.ViewportWidth, readingMax)):0}");
                }
            }
            // Pictures for review: the short message beside the short reply, wide and narrow, both themes.
            if (width is 1200.0 or 400.0 && await Choose(window, main, launched.Id, "YourMessage") is not null)
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
        check(wrong.Count == 0, $"puts your message at the right of the conversation's column, at most 85% of it, beside a short reply and a long one, the reply box at the same measure, at every width{(wrong.Count > 0 ? $": not {string.Join("; ", wrong)}" : "")}");
    }
}
