using AgentBridge.App.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

// A response, reasoning or question drawn from Markdown, as the PWA draws it (pwa/js/
// markdown.js): paragraphs, headings, lists, quotes, code, tables and links that are safe to
// open, and nothing else (no HTML, no images). The model is MarkdownModel's, so both apps read
// a message the same way.
public sealed class MarkdownView : StackPanel
{
    // The design's monospace stack (design/tokens.json type.mono), from the app's resources.
    static FontFamily Mono => (FontFamily)Application.Current.Resources["MonoFontFamily"];
    string? markdown;
    bool wrapCode;

    public MarkdownView() => Spacing = 8;

    // Set in the reading role (docs/design/ui.md §4.3, the generated AbTypeReading): a response in
    // the conversation, a step larger than the UI around it. Headings and code keep their own sizes.
    public bool Reading { get; set; }

    public string? Markdown
    {
        get => markdown;
        set
        {
            if (value == markdown) { return; }
            markdown = value;
            Render();
        }
    }

    // The reading role's size and line height, taken from its generated style.
    static T ReadingType<T>(DependencyProperty property) =>
        (T)((Style)Application.Current.Resources["AbTypeReading"]).Setters.OfType<Setter>().First(s => s.Property == property).Value;

    static void Read(DependencyObject element)
    {
        switch (element)
        {
            case RichTextBlock rich when rich.ReadLocalValue(RichTextBlock.FontSizeProperty) == DependencyProperty.UnsetValue:
                rich.FontSize = ReadingType<double>(TextBlock.FontSizeProperty);
                rich.LineHeight = ReadingType<double>(TextBlock.LineHeightProperty);
                rich.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                FitInlineMono(rich);
                break;
            case Panel panel:
                foreach (var child in panel.Children) { Read(child); }
                break;
            case Border { Child: { } child }:
                Read(child);
                break;
            // A table, in its sideways scroller: the same size as the text around it.
            case ContentControl { Content: DependencyObject inner }:
                Read(inner);
                break;
        }
    }

    // Code wraps rather than scrolls: where every character must be seen without scrolling, as an
    // approval's prompt in Needs you, whose Approve once allows all of it (#165).
    public bool WrapCode
    {
        get => wrapCode;
        set
        {
            if (value == wrapCode) { return; }
            wrapCode = value;
            Render();
        }
    }

    void Render()
    {
        Children.Clear();
        if (markdown is null) { return; }
        foreach (var b in MarkdownModel.Parse(markdown))
        {
            var element = Block(b);
            if (Reading) { Read(element); }
            Children.Add(element);
        }
    }

    UIElement Block(MdBlock block) => block switch
    {
        MdParagraph p => Text(p.Inlines),
        MdHeading h => Heading(h),
        MdCode c => Code(c),
        MdQuote q => Quote(q),
        MdList l => List(l),
        MdRule => new Border { Height = 1, Background = Ui.Brush("AbStroke2"), Margin = new Thickness(0, 4, 0, 4) },
        MdTable t => Table(t),
        _ => new TextBlock(),
    };

    static RichTextBlock Text(IReadOnlyList<MdInline> inlines, double? size = null, bool bold = false)
    {
        var rich = new RichTextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        if (size is { } s) { rich.FontSize = s; }
        if (bold) { rich.FontWeight = FontWeights.SemiBold; }
        var para = new Paragraph();
        foreach (var i in inlines) { para.Inlines.Add(Inline(i)); }
        rich.Blocks.Add(para);
        FitInlineMono(rich);
        return rich;
    }

    static Inline Inline(MdInline inline)
    {
        switch (inline)
        {
            case MdBreak:
                return new LineBreak();
            case MdLink link:
                {
                    var h = new Hyperlink { NavigateUri = Uri.TryCreate(link.Url, UriKind.Absolute, out var u) ? u : null };
                    ToolTipService.SetToolTip(h, link.Url);
                    h.Inlines.Add(Styled(new Run { Text = link.Text }, link.Style));
                    return h;
                }
            case MdText t:
                return Styled(new Run { Text = t.Text }, t.Style);
            default:
                return new Run();
        }
    }

    static Run Styled(Run run, MdStyle style)
    {
        if (style.HasFlag(MdStyle.Bold)) { run.FontWeight = FontWeights.SemiBold; }
        if (style.HasFlag(MdStyle.Italic)) { run.FontStyle = Windows.UI.Text.FontStyle.Italic; }
        if (style.HasFlag(MdStyle.Strike)) { run.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough; }
        if (style.HasFlag(MdStyle.Code)) { run.FontFamily = Mono; }
        return run;
    }

    // A code span in prose is inline mono (ui.md §4.2, #215): at the size that gives it the text
    // face's x-height (the generated AbInlineMonoScale), on the paragraph's own line height and
    // weight. Set again once the paragraph takes the reading size.
    static void FitInlineMono(RichTextBlock rich)
    {
        var size = Math.Round(rich.FontSize * (double)Application.Current.Resources["AbInlineMonoScale"], 2);
        var mono = Mono.Source;
        foreach (var run in rich.Blocks.OfType<Paragraph>().SelectMany(p => Runs(p.Inlines)).Where(r => r.FontFamily?.Source == mono)) { run.FontSize = size; }
    }

    static IEnumerable<Run> Runs(InlineCollection inlines)
    {
        foreach (var i in inlines)
        {
            if (i is Run r) { yield return r; }
            else if (i is Span s) { foreach (var inner in Runs(s.Inlines)) { yield return inner; } }
        }
    }

    static UIElement Heading(MdHeading h) => Text(h.Inlines, h.Level switch { 1 => 20, 2 => 18, 3 => 16, _ => 14 }, bold: true);

    UIElement Code(MdCode c) => new Border
    {
        Background = Ui.Brush("AbSubtle"),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(10, 8, 10, 8),
        Child = WrapCode
            // The lane's code reads in the order it runs: left to right, never a reading order
            // detected from its letters (NeedsYouEntry refuses right-to-left text and anything drawn
            // elsewhere, and escapes it in the line).
            ? new TextBlock { Text = c.Text.TrimEnd('\n'), FontFamily = Mono, FontSize = 13, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FlowDirection = FlowDirection.LeftToRight, TextReadingOrder = TextReadingOrder.UseFlowDirection }
            : new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = new TextBlock { Text = c.Text.TrimEnd('\n'), FontFamily = Mono, FontSize = 13, IsTextSelectionEnabled = true },
        },
    };

    UIElement Quote(MdQuote q)
    {
        var inner = new StackPanel { Spacing = 6 };
        foreach (var b in q.Blocks) { inner.Children.Add(Block(b)); }
        return new Border
        {
            BorderBrush = Ui.Brush("AbStroke2"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 0, 0, 0),
            Child = inner,
        };
    }

    UIElement List(MdList l)
    {
        var panel = new StackPanel { Spacing = 4 };
        var n = l.Start;
        foreach (var item in l.Items)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = l.Ordered ? $"{n++}." : "•", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 6, 0) });
            var body = new StackPanel { Spacing = 4 };
            foreach (var b in item) { body.Children.Add(Block(b)); }
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            panel.Children.Add(row);
        }
        return panel;
    }

    static UIElement Table(MdTable t)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 4 };
        var columns = t.Rows.Count == 0 ? 0 : t.Rows.Max(r => r.Count);
        for (var c = 0; c < columns; c++) { grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); }
        for (var r = 0; r < t.Rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < t.Rows[r].Count; c++)
            {
                var cell = Text(t.Rows[r][c], bold: r == 0 && t.HasHeader);
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = grid,
        };
    }
}
