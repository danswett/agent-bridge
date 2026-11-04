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

    // A word WinUI's text layout fails on (#493): "Done: " and 32,000 `[` in the conversation's
    // reading column threw E_FAIL from RichTextBlock's Measure, and the app ended with 0xC000027B.
    // Drawn now in pieces with breaks of no width between them: every character as written, and
    // selecting all of it copies exactly the text.
    static async Task CheckLongWords(MainWindow window, Action<bool, string> check)
    {
        var root = (Panel)window.RootElement;
        var wrong = new List<string>();
        var times = new List<string>();
        var a = new string('a', 20000);
        var b = new string('b', 20000);
        foreach (var (name, markdown, text) in new[]
        {
            ("\"Done: \" and 32,000 [", "Done: " + new string('[', 32000), "Done: " + new string('[', 32000)),
            ("\"Done: \" and 100,000 [", "Done: " + new string('[', 100000), "Done: " + new string('[', 100000)),
            // One word on screen across two runs, neither alone near the limit.
            ("a bold word run on", $"Done: **{a}**{b}", $"Done: {a}{b}"),
            // No line may end after `[`, even past a space: as unbreakable as the word above.
            ("\"Done: \" and 20,000 \"[ \"", "Done: " + string.Concat(Enumerable.Repeat("[ ", 20000)), "Done: " + string.Concat(Enumerable.Repeat("[ ", 20000))),
        })
        {
            var view = new MarkdownView { Reading = true };
            var column = new ReadingColumn { MaxWidth = (double)Application.Current.Resources["AbLayoutReadingMax"], Padding = new Thickness(24, 8, 24, 24) };
            column.Children.Add(view);
            var scroll = new ScrollViewer { Width = 700, Height = 600, Content = column, IsHitTestVisible = false, Opacity = 0 };
            var clock = Stopwatch.StartNew();
            try
            {
                view.Markdown = markdown;
                root.Children.Add(scroll);
                scroll.UpdateLayout();
                clock.Stop();
                times.Add($"{name} {clock.ElapsedMilliseconds} ms");
                var blocks = view.Children.OfType<RichTextBlock>().ToList();
                var drawn = string.Concat(blocks.SelectMany(r => r.Blocks.OfType<Paragraph>()).SelectMany(p => p.Inlines.OfType<Run>()).Select(r => r.Text));
                if (drawn != text) { wrong.Add($"{name}: {drawn.Length} of {text.Length} characters drawn as written"); }
                if (view.ActualHeight <= 0) { wrong.Add($"{name}: not drawn"); }
                var copied = string.Concat(blocks.Select(r =>
                {
                    r.SelectAll();
                    return r.SelectedText;
                }));
                if (copied != text) { wrong.Add($"{name}: copies {copied.Length} characters, not the {text.Length} written"); }
                if (clock.ElapsedMilliseconds > 3000) { wrong.Add($"{name}: {clock.ElapsedMilliseconds} ms"); }
            }
            catch (Exception e) { wrong.Add($"{name}: {e.GetType().Name} {e.Message}"); }
            finally { root.Children.Remove(scroll); }
            await Task.Delay(50);
        }
        check(wrong.Count == 0, $"draws a word too long for WinUI's text layout as written, and copies it exactly ({string.Join(", ", times)}){(wrong.Count > 0 ? $": not {string.Join("; ", wrong)}" : "")}");
    }
}
