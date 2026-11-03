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
    static readonly MarkdownPipeline Pipeline = Builder().Build();

    // The same block parsers with no inline ones: the paragraphs and headings Markdig would read
    // inlines in, found in linear time, to weigh before reading them (Bounded). Built from the same
    // extensions, since some add block parsers too: the pipe tables' holds a `- |` line in the
    // paragraph above it, and without it a paragraph of thousands of lines was weighed as thousands
    // of one-line list items.
    static readonly MarkdownPipeline BlocksOnly = Builder().Use<NoInlines>().Build();

    static MarkdownPipelineBuilder Builder() => new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .DisableHtml();

    // Set up last, after every other extension has added its parsers.
    sealed class NoInlines : IMarkdownExtension
    {
        public void Setup(MarkdownPipelineBuilder pipeline) => pipeline.InlineParsers.Clear();
        public void Setup(MarkdownPipeline pipeline, Markdig.Renderers.IMarkdownRenderer renderer) { }
    }

    // Markdig is neither total nor linear on what an agent can write (#440). It throws "too deeply
    // nested" on 50,000 `[` or 20,000 ` *a`, uncaught on the way to the screen (the app ended with a
    // stowed exception), and it is quadratic on runs of inline markup in one paragraph: 20,000 `[](`
    // took 2 to 9 s on the UI thread, as did `(www.a` repeated, nested `1. 1. 1.` and a table
    // thousands of rows long. Text it cannot take, or could only take slowly, is drawn as written
    // instead (Literal): never dropped, never a crash. The limits are far beyond what an agent
    // writes: the protocol sends at most 16 KiB of response (docs/protocol/v1-sessions.md), a 16 KiB
    // table of 250 rows, each with code, a link and bold, weighs about half of InlineBudget, no 16 KiB
    // of this repository's own docs reaches it, and real nesting is a few levels deep. At the budget,
    // the slowest shape measured (a table of 1,400 rows) parsed in about 0.1 s.
    internal const int MaxChars = 64 * 1024;
    internal const int MaxNesting = 32;
    internal const long InlineBudget = 32_000_000;
    // Every character that can start an inline of its own: delimiters, brackets, code spans, and the
    // entities (`&gt;`), backslash escapes and angle-bracket autolinks or tags that each become a node.
    const string InlineMarks = "[]()<!*_~^=+`|&\\";

    public static IReadOnlyList<MdBlock> Parse(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown)) { return []; }
        return TryRead(markdown, Blocks, out var blocks) ? blocks : [Literal(markdown)];
    }

    // Where a message's prose starts, as this parser reads it: the source line (0-based) of its first
    // paragraph or heading at any depth, inside quotes and lists but never code or a table; and,
    // for a message with none, its first line of code. For a one-line summary (Needs you). Text drawn
    // as written is all prose: its first line with words.
    public static (int? ProseLine, string? FirstCode) Outline(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown)) { return (null, null); }
        var read = TryRead(markdown, doc =>
        {
            var blocks = doc.Descendants<Block>().ToList();
            var prose = blocks.FirstOrDefault(b => b is ParagraphBlock or HeadingBlock && !InTable(b));
            var code = blocks.OfType<CodeBlock>().FirstOrDefault();
            var first = code is null ? null : Lines(code).Split('\n').FirstOrDefault(l => l.Trim().Length > 0);
            return (prose?.Line, first);
        }, out var outline);
        return read ? outline : (FirstWords(markdown), null);
    }

    // What the parser makes of the text, or false when it is not to be parsed (Bounded) or the parser
    // fails on it, whatever it throws: agent text must never take the view or the app down with it.
    static bool TryRead<T>(string markdown, Func<MarkdownDocument, T> read, out T result)
    {
        result = default!;
        try
        {
            if (!Bounded(markdown)) { return false; }
            var doc = Markdown.Parse(markdown, Pipeline);
            if (doc.Descendants<LeafBlock>().Any(leaf => leaf.Inline is { } inline && InlineDepth(inline) > MaxNesting)) { return false; }
            result = read(doc);
            return true;
        }
        catch (Exception) { return false; }
    }

    // Counted without recursion: 2,000 unclosed `[` nest 2,000 deep, and walking that recursively
    // (Walk) overflowed the stack, which no catch survives.
    static int InlineDepth(ContainerInline root)
    {
        var deepest = 0;
        var pending = new Stack<(Inline Inline, int Depth)>();
        pending.Push((root, 1));
        while (pending.Count > 0)
        {
            var (inline, depth) = pending.Pop();
            deepest = Math.Max(deepest, depth);
            if (deepest > MaxNesting) { break; }
            if (inline is not ContainerInline container) { continue; }
            for (var child = container.FirstChild; child is not null; child = child.NextSibling) { pending.Push((child, depth + 1)); }
        }
        return deepest;
    }

    // Whether Markdig can read the text quickly, decided in linear time: not too long, not nested
    // too deep (a run of list or quote markers on one line, the quadratic `1. 1. 1.`, is caught before
    // the block parser sees it), and within InlineBudget, weighed on the block parser's own
    // paragraphs so the weight follows exactly where Markdig reads inlines (code is not weighed).
    internal static bool Bounded(string markdown)
    {
        if (markdown.Length > MaxChars || MarkersOnALine(markdown) > MaxNesting) { return false; }
        var doc = Markdown.Parse(markdown, BlocksOnly);
        if (Depth(doc) > MaxNesting) { return false; }
        long work = 0;
        foreach (var leaf in doc.Descendants<LeafBlock>())
        {
            if (!leaf.ProcessInlines || leaf.Span.IsEmpty) { continue; }
            var start = Math.Clamp(leaf.Span.Start, 0, markdown.Length);
            var end = Math.Clamp(leaf.Span.End + 1, start, markdown.Length);
            work += InlineWork(markdown.AsSpan(start, end - start));
            if (work > InlineBudget) { return false; }
        }
        return true;
    }

    // A paragraph's weight: its marks squared (the delimiter and bracket matching that spans its
    // lines), and each line's marks times its length (the link and autolink scans that run on to the
    // end of the line from each). A mark is one of InlineMarks, where a bare autolink can start, or a
    // line break.
    static long InlineWork(ReadOnlySpan<char> text)
    {
        long marks = 0, lineMarks = 0, lineLength = 0, lines = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\n' or '\r')
            {
                lines += lineMarks * lineLength;
                lineMarks = lineLength = 0;
                // A line break within a paragraph is an inline too, and the delimiters before it are
                // matched across it: `*a` on 5,000 lines was the slowest shape measured without this.
                if (c == '\n' || i + 1 >= text.Length || text[i + 1] != '\n') { marks++; }
                continue;
            }
            lineLength++;
            if (InlineMarks.Contains(c) || (c == ':' && text[i..].StartsWith("://")) || (c == 'w' && text[i..].StartsWith("www."))) { marks++; lineMarks++; }
        }
        return marks * marks + lines + lineMarks * lineLength;
    }

    // The most list and quote markers that open any one line: `> - 1. text` has three.
    static int MarkersOnALine(string text)
    {
        var most = 0;
        var i = 0;
        while (i < text.Length)
        {
            var count = 0;
            while (true)
            {
                while (i < text.Length && text[i] is ' ' or '\t') { i++; }
                if (i >= text.Length) { break; }
                var at = i;
                if (text[i] == '>') { i++; }
                else if (text[i] is '-' or '+' or '*') { i++; }
                else
                {
                    while (i < text.Length && i - at < 10 && char.IsAsciiDigit(text[i])) { i++; }
                    if (i == at || i >= text.Length || text[i] is not ('.' or ')')) { i = at; break; }
                    i++;
                }
                // A list marker is followed by a space or the line's end; a quote marker needs neither.
                if (text[at] != '>' && i < text.Length && text[i] is not (' ' or '\t' or '\n' or '\r')) { i = at; break; }
                count++;
            }
            most = Math.Max(most, count);
            while (i < text.Length && text[i] is not ('\n' or '\r')) { i++; }
            while (i < text.Length && text[i] is '\n' or '\r') { i++; }
        }
        return most;
    }

    static int Depth(ContainerBlock container)
    {
        var deepest = 0;
        foreach (var block in container)
        {
            if (block is ContainerBlock inner) { deepest = Math.Max(deepest, Depth(inner)); }
        }
        return deepest + 1;
    }

    // Text the parser is not given, as written: one paragraph of plain words, each line kept.
    static MdParagraph Literal(string markdown)
    {
        var inlines = new List<MdInline>();
        var first = true;
        foreach (var line in SourceLines(markdown))
        {
            if (!first) { inlines.Add(new MdBreak()); }
            first = false;
            if (line.Length > 0) { inlines.Add(new MdText(line)); }
        }
        return new MdParagraph(inlines);
    }

    static int? FirstWords(string markdown)
    {
        var n = 0;
        foreach (var line in SourceLines(markdown))
        {
            if (line.Trim().Length > 0) { return n; }
            n++;
        }
        return null;
    }

    // Lines as Markdig counts them: a CRLF, a CR or an LF ends one.
    static IEnumerable<string> SourceLines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n')) { continue; }
            yield return text[start..i];
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { i++; }
            start = i + 1;
        }
        yield return text[start..];
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
            // Markdig ends a line at a bare CR as well, which a shell does not: `echo safe \r#; echo SECOND`
            // drew its second command on a line of its own, looking commented out, while bash runs it.
            // A bare CR is kept as the character it is, for HiddenText.Reveal to draw as `\u000D` (#220).
            // NEL, LS and PS never end a line here, and Reveal escapes them where they are.
            if (i > 0) { b.Append(lines.Lines[i - 1].NewLine == Markdig.Helpers.NewLine.CarriageReturn ? '\r' : '\n'); }
            b.Append(lines.Lines[i].Slice.ToString());
        }
        // A code block's last line ends at a bare CR too when the closing fence or the message's end
        // follows it: `printf '%s' safe\r` drew as `safe` with no escape, and pasted it ends the
        // command. Its ordinary LF or CRLF ending is the fence's, not the block's.
        if (block is CodeBlock && lines.Count > 0 && lines.Lines[lines.Count - 1].NewLine == Markdig.Helpers.NewLine.CarriageReturn) { b.Append('\r'); }
        return b.ToString();
    }

    static List<MdInline> Inlines(ContainerInline? container)
    {
        var list = new List<MdInline>();
        if (container is not null) { Walk(container, MdStyle.None, list); }
        // Adjacent runs of one style are one run, which is what a text block wants. Joined once per
        // run, not by appending each piece to the run so far: 16,384 `&gt;` are 16,384 pieces, and
        // re-copying the growing run allocated about 256 MiB on the UI thread (#440).
        var merged = new List<MdInline>();
        var run = new StringBuilder();
        MdStyle? style = null;
        void Flush()
        {
            if (style is { } s) { merged.Add(new MdText(run.ToString(), s)); }
            run.Clear();
            style = null;
        }
        foreach (var i in list)
        {
            if (i is MdText t)
            {
                if (style != t.Style) { Flush(); style = t.Style; }
                run.Append(t.Text);
            }
            else
            {
                Flush();
                merged.Add(i);
            }
        }
        Flush();
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
                // A pipe outside a table: left as a delimiter, it drew as nothing, so "iwr x | iex"
                // read as "iwr x  iex" (#220). It is the character it is, then what follows it.
                case DelimiterInline delimiter:
                    list.Add(new MdText(delimiter.ToLiteral(), style));
                    Walk(delimiter, style, list);
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

    // An approval's message as its card draws it (#220): exactly as written, fence markers
    // included, as one block in the code face; nothing in it is read as Markdown. Whatever a parser
    // decides can hide what runs. A Codex approval carries its command in prose, where a link drew
    // `echo "[safe](https://a/";curl…|sh;")"` as `echo "safe"`; and a line of three backticks, which
    // Markdown takes as a fence, is a command substitution to bash: `bash -c '` then a fenced
    // `echo rm -rf ~` read as a harmless echo, and bash runs `rm -rf ~`. The block reveals what is
    // not on screen as it runs, right-to-left letters and a bare CR included (CodeCopy), and reads
    // left to right. Only CRLF line endings are taken as line breaks.
    public static IReadOnlyList<MdBlock> Approval(string? markdown) =>
        string.IsNullOrEmpty(markdown) ? [] : [new MdCode(markdown.Replace("\r\n", "\n"), null)];
}
