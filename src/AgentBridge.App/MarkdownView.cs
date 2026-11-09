using AgentBridge.App.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

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
    bool approval;

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

    // One block of a response, drawn alone: an item of the conversation's list, which realises only
    // the blocks near the viewport (#765). Set instead of Markdown.
    public MdBlock? Block
    {
        get => block;
        set
        {
            if (ReferenceEquals(value, block)) { return; }
            block = value;
            Render();
        }
    }
    MdBlock? block;

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

    // An approval's message on its card (#220): drawn exactly as written, fence markers included,
    // as one block in the code face, with nothing in it read as Markdown (MarkdownModel.Approval):
    // what is not on screen as it runs (right-to-left letters too) shown as escapes, read left to
    // right. A Codex approval's command is prose, and Approve once allows what was read.
    public bool Approval
    {
        get => approval;
        set
        {
            if (value == approval) { return; }
            approval = value;
            if (value) { FlowDirection = FlowDirection.LeftToRight; } else { ClearValue(FlowDirectionProperty); }
            Render();
        }
    }

    // Where Copy puts a block's text. The self-test swaps in its own, so a run never takes the
    // clipboard from the person at the machine, except the one check of the real thing
    // (SelfTest.Code.cs), which puts back what was there.
    internal static Func<string, bool> PutText = ClipboardText.Put;

    void Render()
    {
        Children.Clear();
        if (block is null && markdown is null) { return; }
        IReadOnlyList<MdBlock> blocks = block is not null ? [block] : Approval ? MarkdownModel.Approval(markdown) : MarkdownModel.Parse(markdown);
        foreach (var b in blocks)
        {
            var element = Draw(b);
            if (Reading) { Read(element); }
            Children.Add(element);
        }
    }

    UIElement Draw(MdBlock block) => block switch
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
        var pieces = LongWords.Pieces([.. inlines.Select(i => i switch { MdText t => t.Text, MdLink l => l.Text, MdBreak => "\n", _ => "" })]);
        for (var k = 0; k < inlines.Count; k++) { Inlines(inlines[k], pieces[k], para.Inlines); }
        rich.Blocks.Add(para);
        FitInlineMono(rich);
        return rich;
    }

    // Each inline in its pieces (LongWords), cut where the paragraph needs a break, so a long word
    // never fails WinUI's text layout (#493). Selecting and copying gives the text as written: the
    // break is an empty element, never a character.
    static void Inlines(MdInline inline, IReadOnlyList<string> pieces, InlineCollection into)
    {
        switch (inline)
        {
            case MdBreak:
                into.Add(new LineBreak());
                break;
            case MdLink link:
                for (var k = 0; k < pieces.Count; k++)
                {
                    if (k > 0) { into.Add(Break()); }
                    if (pieces[k].Length == 0) { continue; }
                    // A hyperlink holds only text, so a long one is drawn as several to the same place.
                    var h = new Hyperlink { NavigateUri = Uri.TryCreate(link.Url, UriKind.Absolute, out var u) ? u : null };
                    ToolTipService.SetToolTip(h, link.Url);
                    h.Inlines.Add(Styled(new Run { Text = pieces[k] }, link.Style));
                    into.Add(h);
                }
                break;
            case MdText t:
                for (var k = 0; k < pieces.Count; k++)
                {
                    if (k > 0) { into.Add(Break()); }
                    into.Add(Styled(new Run { Text = pieces[k] }, t.Style));
                }
                break;
            default:
                into.Add(new Run());
                break;
        }
    }

    // A break opportunity of no width: an empty element a line may end at, which a selection
    // copies as nothing. A zero-width space would be copied with the text.
    static InlineUIContainer Break()
    {
        var empty = new Border { Width = 0, Height = 0 };
        AutomationProperties.SetAccessibilityView(empty, AccessibilityView.Raw);
        return new InlineUIContainer { Child = empty };
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

    // Code wraps rather than scrolls (#296): every character of a long command is seen without
    // scrolling sideways, as in the PWA. The wrap is the text block's own and adds no line breaks,
    // so selecting the text, or Copy, gives the command as drawn.
    // Every block reads strictly left to right, in the order it runs, never a reading order detected
    // from its letters, with what is not on screen as it runs drawn as escapes (CodeCopy, #220):
    // the conversation's, the delegate inspector's, and an approval's on its card, where Approve once
    // allows all of it.
    UIElement Code(MdCode c)
    {
        var copy = new CodeCopy(c, text => PutText(text), new QueueDispatcher(DispatcherQueue));
        var text = new TextBlock
        {
            Style = (Style)Application.Current.Resources["AbTypeCode"],
            Text = copy.Text,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            FlowDirection = FlowDirection.LeftToRight,
            TextReadingOrder = TextReadingOrder.UseFlowDirection,
            Margin = new Thickness(0, 0, 6, 0),
        };
        AutomationProperties.SetAutomationId(text, "CodeText");
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // What the block held that its escapes stand for, said in words beside Copy: both if both.
        var notes = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        if (copy.Hidden) { notes.Children.Add(Note(CodeCopy.HiddenNote, "CodeHidden")); }
        if (copy.RightToLeft) { notes.Children.Add(Note(CodeCopy.RightToLeftNote, "CodeRightToLeft")); }
        head.Children.Add(notes);
        // An approval's card has no Copy: its block is the whole message, prose and fence lines too,
        // and pasted into bash a line of backticks runs as a command substitution.
        if (!Approval)
        {
            var button = CopyButton(copy);
            Grid.SetColumn(button, 1);
            head.Children.Add(button);
        }
        Grid.SetRow(text, 1);
        var grid = new Grid { RowSpacing = 2 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(head);
        grid.Children.Add(text);
        return new Border
        {
            Background = Ui.Brush("AbSubtle"),
            CornerRadius = new CornerRadius(6),
            // Copy sits in the top-right corner above the code, so it never covers a line of it.
            Padding = new Thickness(10, 4, 4, 8),
            Child = grid,
        };
    }

    static TextBlock Note(string text, string id)
    {
        var note = new TextBlock
        {
            Style = (Style)Application.Current.Resources["AbTypeMeta"],
            Foreground = Ui.Brush("AbText2"),
            Text = text,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(note, id);
        return note;
    }

    // A block's Copy button, as the PWA's: its icon and "Copy", then "Copied" (or "Copy failed")
    // for a moment. Its name stays "Copy code" whatever it shows, and what happened is said to a
    // screen reader, as the PWA's live region says it. 32 high, the desktop's smallest target (§9).
    static Button CopyButton(CodeCopy copy)
    {
        var resources = Application.Current.Resources;
        var label = new TextBlock { Style = (Style)resources["AbTypeMeta"], Text = copy.Label, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new ContentControl { Style = (Style)resources["AbIconCopy"], Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(label);
        var button = new Button
        {
            Style = (Style)resources["AbIconButton"],
            Width = double.NaN,
            MinWidth = 32,
            Padding = new Thickness(6, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = Ui.Brush("AbText2"),
            Content = content,
        };
        AutomationProperties.SetName(button, CodeCopy.Name);
        AutomationProperties.SetAutomationId(button, "CopyCode");
        ToolTipService.SetToolTip(button, CodeCopy.Name);
        copy.PropertyChanged += (_, _) =>
        {
            label.Text = copy.Label;
            button.Foreground = Ui.Brush(copy.Label switch { CodeCopy.Done => "AbBridge", CodeCopy.Failed => "AbDangerText", _ => "AbText2" });
        };
        button.Click += (_, _) =>
        {
            var copied = copy.Copy();
            // Created if need be: FromElement finds only a peer something already asked for.
            FrameworkElementAutomationPeer.CreatePeerForElement(button)?.RaiseNotificationEvent(
                copied ? AutomationNotificationKind.ActionCompleted : AutomationNotificationKind.ActionAborted,
                AutomationNotificationProcessing.MostRecent, copy.Label, "AgentBridgeCodeCopied");
        };
        return button;
    }

    UIElement Quote(MdQuote q)
    {
        var inner = new StackPanel { Spacing = 6 };
        foreach (var b in q.Blocks) { inner.Children.Add(Draw(b)); }
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
            foreach (var b in item) { body.Children.Add(Draw(b)); }
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
