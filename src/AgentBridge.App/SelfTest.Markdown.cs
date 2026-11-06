using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using AgentBridge.App.Core;

namespace AgentBridge.App;

// Agent text Markdig cannot take, or takes only slowly (#440): drawn as written, in the reading
// role, in time that grows linearly with it. A response of 50,000 `[` ended the app with a stowed
// ArgumentException (0xC000027B), and 5,000 `[` with a stack overflow (0xC00000FD), once each was
// on screen; 20,000 `[](` took Markdig 5 to 9 s. The time is judged by its growth between two
// sizes (Growth), not by a limit in milliseconds, which CI's Arm64 runner exceeded drawing 20,000
// `[](` linearly (#681).
static partial class SelfTest
{
    static async Task CheckUnparsableMarkdown(MainWindow window, Action<bool, string> check)
    {
        static string Repeat(string s, int n) => string.Concat(Enumerable.Repeat(s, n));
        var root = (Panel)window.RootElement;

        // What a view draws for the text, laid out as on screen.
        string Draw(string text)
        {
            var view = new MarkdownView { Reading = true, Width = 600, IsHitTestVisible = false, Opacity = 0 };
            try
            {
                view.Markdown = text;
                root.Children.Add(view);
                view.UpdateLayout();
                return string.Concat(view.Children.OfType<RichTextBlock>().SelectMany(r => r.Blocks.OfType<Paragraph>()).SelectMany(p => p.Inlines.OfType<Run>()).Select(r => r.Text));
            }
            finally { root.Children.Remove(view); }
        }

        var wrong = new List<string>();
        foreach (var (name, text) in new[] { ("50,000 [", new string('[', 50000)), ("5,000 [", new string('[', 5000)), ("20,000 \" *a\"", Repeat(" *a", 20000)), ("20,000 [](", Repeat("[](", 20000)) })
        {
            try
            {
                if (Draw(text) is var drawn && drawn != text) { wrong.Add($"{name}: {drawn.Length} of {text.Length} characters drawn as written"); }
            }
            catch (Exception e) { wrong.Add($"{name}: {e.GetType().Name}"); }
            await Task.Delay(50);
        }
        check(wrong.Count == 0, $"draws agent text the Markdown parser cannot take as written{(wrong.Count > 0 ? $": not {string.Join("; ", wrong)}" : "")}");

        var growth = new List<string>();
        var steeper = new List<string>();
        foreach (var (unit, size) in new[] { ("[", 12500), (" *a", 5000), ("[](", 5000) })
        {
            var name = $"\"{unit}\" ×{size:N0} to ×{size * Growth.Step:N0}";
            try
            {
                var result = Growth.Measure(n => Draw(Repeat(unit, n)), size);
                growth.Add($"{name}: {result}");
                if (!result.Linear) { steeper.Add(name); }
            }
            catch (Exception e) { steeper.Add($"{name}: {e.GetType().Name}"); }
            await Task.Delay(50);
        }
        check(steeper.Count == 0, $"draws such text in time that grows linearly with it ({string.Join("; ", growth)}){(steeper.Count > 0 ? $": not {string.Join("; ", steeper)}" : "")}");
    }
}
