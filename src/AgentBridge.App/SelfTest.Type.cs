using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AgentBridge.App;

// Inline mono (ui.md §4.2, #215): mono on a line of text has the text face's x-height. Measured in
// ink, not taken from the tokens: "x" drawn in Slipgates Sans at each ramp role and in Slipgates
// Mono at that role's generated inline size, the rows each fills counted.
static partial class SelfTest
{
    static async Task CheckInlineMono(MainWindow window, Action<bool, string> check)
    {
        var resources = Application.Current.Resources;
        var mono = (FontFamily)resources["MonoFontFamily"];
        var roles = new[] { "Row", "Reading", "Meta" };
        var pairs = roles.Select(role =>
        {
            var type = (Style)resources[$"AbType{role}"];
            TextBlock Sample() => new() { Style = type, Text = "xxxx", Foreground = Ui.Brush("AbText"), HorizontalAlignment = HorizontalAlignment.Left };
            var text = Sample();
            var inline = Sample();
            inline.FontFamily = mono;
            inline.FontSize = (double)resources[$"AbInlineMono{role}"];
            return (Role: role, Text: text, Mono: inline);
        }).ToList();

        var host = new StackPanel { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        foreach (var (_, text, inline) in pairs) { host.Children.Add(text); host.Children.Add(inline); }
        var root = (Panel)window.RootElement;
        root.Children.Add(host);
        try
        {
            host.UpdateLayout();
            await Task.Delay(100);
            var measured = new List<string>();
            var off = new List<string>();
            foreach (var (role, text, inline) in pairs)
            {
                var (t, m) = (await XHeight(text), await XHeight(inline));
                measured.Add($"{role} {t:0.#}/{m:0.#}");
                if (t <= 0 || m <= 0 || Math.Abs(t - m) > 1) { off.Add(role); }
            }
            check(off.Count == 0, $"gives inline mono the text face's x-height, within 1 px, in every role (text/mono: {string.Join(", ", measured)})");
        }
        finally { root.Children.Remove(host); }

        // The app's inline mono styles are the generated sizes, in the mono, at the role's line height.
        var styles = new[] { ("InlineMonoRow", "Row"), ("InlineMonoMeta", "Meta") };
        var wrong = styles.Where(s => !(resources.TryGetValue(s.Item1, out var o) && o is Style style
            && Setter(style, TextBlock.FontSizeProperty) is double size && size == (double)resources[$"AbInlineMono{s.Item2}"]
            && Setter(style, TextBlock.FontFamilyProperty) is FontFamily f && f.Source == mono.Source
            && Setter(style, TextBlock.LineHeightProperty) is double line && line == (double)Setter((Style)resources[$"AbType{s.Item2}"], TextBlock.LineHeightProperty)!)).Select(s => s.Item1).ToList();
        check(wrong.Count == 0, $"sets the app's inline mono styles from the generated sizes and the role's line height{(wrong.Count > 0 ? $": not {string.Join(", ", wrong)}" : "")}");

        // A code span in a response: the reading role's inline size, not the mono's own.
        var md = new MarkdownView { Reading = true, Markdown = "Run `git status` first." };
        var span = md.Children.OfType<RichTextBlock>().SelectMany(r => r.Blocks.OfType<Paragraph>()).SelectMany(p => p.Inlines.OfType<Run>()).FirstOrDefault(r => r.FontFamily?.Source == mono.Source);
        var want = (double)resources["AbInlineMonoReading"];
        check(span is not null && Math.Abs(span.FontSize - want) < 0.01, $"sets a code span in a response at the reading role's inline mono size ({span?.FontSize:0.##} px, {want:0.##} wanted)");
    }

    // A style's own setter for a property, or its base style's.
    static object? Setter(Style style, DependencyProperty property)
    {
        for (var s = style; s is not null; s = s.BasedOn)
        {
            if (s.Setters.OfType<Setter>().FirstOrDefault(x => x.Property == property) is { } setter) { return setter.Value; }
        }
        return null;
    }

    // The ink an element's text fills, top to bottom, in effective pixels.
    static async Task<double> XHeight(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0 || element.ActualHeight <= 0) { return 0; }
        var rows = 0;
        for (var y = 0; y < bitmap.PixelHeight; y++)
        {
            for (var x = 0; x < bitmap.PixelWidth; x++)
            {
                if (pixels[((y * bitmap.PixelWidth) + x) * 4 + 3] >= 128) { rows++; break; }
            }
        }
        return rows * element.ActualHeight / bitmap.PixelHeight;
    }
}
