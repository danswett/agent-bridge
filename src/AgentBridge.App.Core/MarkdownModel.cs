using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AgentBridge.App.Core;

// Agent responses and questions are Markdown, rendered without raw HTML (docs/protocol/
// v1-sessions.md). Markdig parses; this reduces what it finds to the few shapes a card draws,
// so the drawing side (XAML) is a plain mapping and the rules are tested here.
public abstract record MdBlock;
public sealed record MdParagraph(IReadOnlyList<MdInline> Inlines) : MdBlock;
public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Inlines) : MdBlock;
public sealed record MdCode(string Text, string? Language) : MdBlock;
public sealed record MdQuote(IReadOnlyList<MdBlock> Blocks) : MdBlock;
public sealed record MdList(bool Ordered, int Start, IReadOnlyList<IReadOnlyList<MdBlock>> Items) : MdBlock;
public sealed record MdRule : MdBlock;
public sealed record MdTable(IReadOnlyList<IReadOnlyList<IReadOnlyList<MdInline>>> Rows, bool HasHeader) : MdBlock;

[Flags]
public enum MdStyle { None = 0, Bold = 1, Italic = 2, Code = 4, Strike = 8 }

public abstract record MdInline;
public sealed record MdText(string Text, MdStyle Style = MdStyle.None) : MdInline;
public sealed record MdLink(string Text, string Url, MdStyle Style = MdStyle.None) : MdInline;
public sealed record MdBreak : MdInline;

public static class MarkdownModel
{
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .DisableHtml()
        .Build();

    public static IReadOnlyList<MdBlock> Parse(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown)) { return []; }
        var doc = Markdown.Parse(markdown, Pipeline);
        return Blocks(doc);
    }

    // Where a message's prose starts, as this parser reads it: the source line (0-based) of its first
    // paragraph or heading at any depth, inside quotes and lists but never code or a table; and,
    // for a message with none, its first line of code. For a one-line summary (Needs you).
    public static (int? ProseLine, string? FirstCode) Outline(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown)) { return (null, null); }
        var doc = Markdown.Parse(markdown, Pipeline);
        var blocks = doc.Descendants<Block>().ToList();
        var prose = blocks.FirstOrDefault(b => b is ParagraphBlock or HeadingBlock && !InTable(b));
        var code = blocks.OfType<CodeBlock>().FirstOrDefault();
        var first = code is null ? null : Lines(code).Split('\n').FirstOrDefault(l => l.Trim().Length > 0);
        return (prose?.Line, first);
    }

    static bool InTable(Block b)
    {
        for (var p = b.Parent; p is not null; p = p.Parent) { if (p is Table) { return true; } }
        return false;
    }

    static List<MdBlock> Blocks(ContainerBlock container)
    {
        var list = new List<MdBlock>();
        foreach (var block in container)
        {
            switch (block)
            {
                case HeadingBlock h:
                    list.Add(new MdHeading(h.Level, Inlines(h.Inline)));
                    break;
                case ParagraphBlock p:
                    list.Add(new MdParagraph(Inlines(p.Inline)));
                    break;
                case FencedCodeBlock f:
                    list.Add(new MdCode(Lines(f), string.IsNullOrEmpty(f.Info) ? null : f.Info));
                    break;
                case CodeBlock c:
                    list.Add(new MdCode(Lines(c), null));
                    break;
                case QuoteBlock q:
                    list.Add(new MdQuote(Blocks(q)));
                    break;
                case ListBlock l:
                    var start = l.IsOrdered && int.TryParse(l.OrderedStart, out var s) ? s : 1;
                    list.Add(new MdList(l.IsOrdered, start, [.. l.OfType<ListItemBlock>().Select(i => (IReadOnlyList<MdBlock>)Blocks(i))]));
                    break;
                case ThematicBreakBlock:
                    list.Add(new MdRule());
                    break;
                case Table t:
                    var rows = t.OfType<TableRow>().Select(r => (IReadOnlyList<IReadOnlyList<MdInline>>)[.. r.OfType<TableCell>().Select(c => (IReadOnlyList<MdInline>)[.. c.OfType<ParagraphBlock>().SelectMany(p => Inlines(p.Inline))])]).ToList();
                    list.Add(new MdTable(rows, t.OfType<TableRow>().FirstOrDefault()?.IsHeader == true));
                    break;
                // Raw HTML is shown as the text it is, never interpreted.
                case HtmlBlock html:
                    list.Add(new MdParagraph([new MdText(Lines(html))]));
                    break;
                case ContainerBlock other:
                    list.AddRange(Blocks(other));
                    break;
                case LeafBlock leaf when leaf.Inline is not null:
                    list.Add(new MdParagraph(Inlines(leaf.Inline)));
                    break;
            }
        }
        return list;
    }

    static string Lines(LeafBlock block)
    {
        var b = new StringBuilder();
        var lines = block.Lines;
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0) { b.Append('\n'); }
            b.Append(lines.Lines[i].Slice.ToString());
        }
        return b.ToString();
    }

    static List<MdInline> Inlines(ContainerInline? container)
    {
        var list = new List<MdInline>();
        if (container is not null) { Walk(container, MdStyle.None, list); }
        // Adjacent runs of one style are one run, which is what a text block wants.
        var merged = new List<MdInline>();
        foreach (var i in list)
        {
            if (i is MdText t && merged.Count > 0 && merged[^1] is MdText prev && prev.Style == t.Style) { merged[^1] = prev with { Text = prev.Text + t.Text }; }
            else { merged.Add(i); }
        }
        return merged;
    }

    static void Walk(ContainerInline container, MdStyle style, List<MdInline> list)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    list.Add(new MdText(lit.Content.ToString(), style));
                    break;
                case CodeInline code:
                    list.Add(new MdText(code.Content, style | MdStyle.Code));
                    break;
                case LineBreakInline br:
                    if (br.IsHard) { list.Add(new MdBreak()); } else { list.Add(new MdText(" ", style)); }
                    break;
                case EmphasisInline em:
                    var add = em.DelimiterChar == '~' ? MdStyle.Strike : em.DelimiterCount >= 2 ? MdStyle.Bold : MdStyle.Italic;
                    Walk(em, style | add, list);
                    break;
                case LinkInline link when !link.IsImage && Safe(link.Url) is { } url:
                    var text = new List<MdInline>();
                    Walk(link, style, text);
                    var label = string.Concat(text.OfType<MdText>().Select(t => t.Text));
                    list.Add(new MdLink(label.Length > 0 ? label : url, url, style));
                    break;
                case LinkInline link:
                    // An image, or a link to anything but the web: its words, never a fetch or a launch.
                    var words = new List<MdInline>();
                    Walk(link, style, words);
                    list.AddRange(words.Count > 0 ? words : [new MdText(link.Url ?? "", style)]);
                    break;
                case AutolinkInline auto when Safe(auto.Url) is { } u:
                    list.Add(new MdLink(auto.Url, u, style));
                    break;
                case AutolinkInline auto:
                    list.Add(new MdText(auto.Url, style));
                    break;
                case HtmlInline html:
                    list.Add(new MdText(html.Tag, style));
                    break;
                case HtmlEntityInline entity:
                    list.Add(new MdText(entity.Transcoded.ToString(), style));
                    break;
                case ContainerInline inner:
                    Walk(inner, style, list);
                    break;
            }
        }
    }

    // Only http and https links are followed: a card must never start a program or open a file.
    public static string? Safe(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u.AbsoluteUri : null;
}
