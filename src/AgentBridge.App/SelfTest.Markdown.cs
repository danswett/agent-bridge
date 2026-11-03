using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace AgentBridge.App;

// Agent text Markdig cannot take, or takes only slowly (#440): drawn as written, in the reading
// role, quickly. A response of 50,000 `[` ended the app with a stowed ArgumentException
// (0xC000027B), and 5,000 `[` with a stack overflow (0xC00000FD), once each was on screen.
static partial class SelfTest
{
    static async Task CheckUnparsableMarkdown(MainWindow window, Action<bool, string> check)
    {
        static string Repeat(string s, int n) => string.Concat(Enumerable.Repeat(s, n));
        var root = (Panel)window.RootElement;
        var wrong = new List<string>();
        var times = new List<string>();
        foreach (var (name, text) in new[] { ("50,000 [", new string('[', 50000)), ("5,000 [", new string('[', 5000)), ("20,000 \" *a\"", Repeat(" *a", 20000)), ("20,000 [](", Repeat("[](", 20000)) })
        {
            var view = new MarkdownView { Reading = true, Width = 600, IsHitTestVisible = false, Opacity = 0 };
            var clock = Stopwatch.StartNew();
            try
            {
                view.Markdown = text;
                root.Children.Add(view);
                view.UpdateLayout();
                clock.Stop();
                times.Add($"{name} {clock.ElapsedMilliseconds} ms");
                var drawn = string.Concat(view.Children.OfType<RichTextBlock>().SelectMany(r => r.Blocks.OfType<Paragraph>()).SelectMany(p => p.Inlines.OfType<Run>()).Select(r => r.Text));
                if (drawn != text) { wrong.Add($"{name}: {drawn.Length} of {text.Length} characters drawn as written"); }
                // Generous for a slow runner: Markdig alone took 2 to 9 s on these before.
                if (clock.ElapsedMilliseconds > 1500) { wrong.Add($"{name}: {clock.ElapsedMilliseconds} ms"); }
            }
            catch (Exception e) { wrong.Add($"{name}: {e.GetType().Name}"); }
            finally { root.Children.Remove(view); }
            await Task.Delay(50);
        }
        check(wrong.Count == 0, $"draws agent text the Markdown parser cannot take as written, quickly ({string.Join(", ", times)}){(wrong.Count > 0 ? $": not {string.Join("; ", wrong)}" : "")}");
    }
}
