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

    public MarkdownView() => Spacing = 8;

    public string? Markdown
    {
        get => markdown;
        set
        {
            if (value == markdown) { return; }
            markdown = value;
            Children.Clear();
            foreach (var b in MarkdownModel.Parse(value)) { Children.Add(Block(b)); }
        }
    }

    static UIElement Block(MdBlock block) => block switch
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

    static UIElement Heading(MdHeading h) => Text(h.Inlines, h.Level switch { 1 => 20, 2 => 18, 3 => 16, _ => 14 }, bold: true);

    static UIElement Code(MdCode c) => new Border
    {
        Background = Ui.Brush("AbSubtle"),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(10, 8, 10, 8),
        Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = new TextBlock { Text = c.Text.TrimEnd('\n'), FontFamily = Mono, FontSize = 13, IsTextSelectionEnabled = true },
        },
    };

    static UIElement Quote(MdQuote q)
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

    static UIElement List(MdList l)
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
