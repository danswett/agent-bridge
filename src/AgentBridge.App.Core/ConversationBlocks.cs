using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace AgentBridge.App.Core;

// One of a response's top-level blocks, as an item of the conversation's list (#765). The list
// realises only the blocks near the viewport: a live resize re-wraps those, not all of a long
// response, which once re-wrapped every block of it on every step of the drag. A class, not the
// record itself: two equal blocks (two rules) are still two items, so the list and a screen reader
// never take one for the other.
public sealed class ConversationBlock(MdBlock block)
{
    public MdBlock Block { get; } = block;

    // What the block says, as plain text.
    public string Text { get; } = ConversationBlocks.TextOf(block);

    // The item's name to a screen reader, whether or not the list has realised it, so Narrator's
    // scan and search reach a block that is off screen (#707): its text with hidden characters as
    // their escapes, cut to 500 characters at a whole grapheme (NameOf). The whole text is still
    // read from the item's content.
    public string Name => name ??= ConversationBlocks.NameOf(Text);
    string? name;

    // Whether Tab stops in it (a link, a Copy button), so Tab can bring it in unrealised.
    public bool Focusable { get; } = ConversationKeyboard.Focusable(block);

    public override string ToString() => Name;
}

public static class ConversationBlocks
{
    public const int NameLongest = 500;

    // What TextOf trims from a block's edges: plain spaces and line breaks, which HiddenText.Reveal
    // leaves as they are, never a character it would show as an escape.
    static readonly char[] AsciiEdges = [' ', '\n'];

    // A block's text as its item's name: hidden characters as their escapes (HiddenText.Reveal), as a
    // code block draws them, so a bidi override or a zero-width character in prose can't make the
    // name read other than as it runs (#707, B's call); then, when longer than NameLongest, cut at the
    // last whole grapheme that fits with "…" after it, so an emoji or a letter and its accents is
    // never split. Nothing is trimmed or cut before the reveal, so a hidden character at either edge
    // is named too (#829).
    public static string NameOf(string text)
    {
        text = HiddenText.Reveal(text);
        if (text.Length <= NameLongest) { return text; }
        var end = 0;
        while (end < text.Length)
        {
            var next = end + StringInfo.GetNextTextElementLength(text, end);
            if (next > NameLongest - 1) { break; }
            end = next;
        }
        return string.Concat(text.AsSpan(0, end), "…");
    }

    // The list's items made to match a response's blocks, keeping each block already shown that
    // is still there: one realised stays as it is, where it is, and the list keeps the block at the
    // top of the viewport in place (KeepItemsInView). A response that grew keeps its head; one that
    // lost its earliest blocks (cut at the start to fit, SessionBudget) keeps the rest, the cut
    // falling between two blocks alike or partway through what is now its first block included.
    // The longest run of blocks kept wins, the first found of two as long. False, with nothing
    // changed, when nothing can be kept: the caller replaces the items whole, in one reset rather
    // than an add for each block.
    public static bool Update(ObservableCollection<ConversationBlock> shown, IReadOnlyList<MdBlock> next) => Update(shown, next, Same);

    // Linear in the blocks, never quadratic on repeated text (4,096 paragraphs alike once made 16.8
    // million comparisons on the UI thread): how far each shown block runs alike with next, from
    // next[0] and from next[1], is the Z-function of next's tail, a stop, then the shown blocks,
    // which compares each block a bounded number of times.
    internal static bool Update(ObservableCollection<ConversationBlock> shown, IReadOnlyList<MdBlock> next, Func<MdBlock, MdBlock, bool> same)
    {
        // The run kept: from shown[start] and next[from] on. next[0] is new when a cut fell inside it.
        var (start, from, kept) = (0, 0, 0);
        for (var lead = 0; lead < Math.Min(2, next.Count); lead++)
        {
            var runs = Runs(shown, next, lead, same);
            for (var s = 0; s < shown.Count; s++)
            {
                if (runs[s] > kept) { (start, from, kept) = (s, lead, runs[s]); }
            }
        }
        if (kept == 0) { return false; }
        for (var i = shown.Count - 1; i >= start + kept; i--) { shown.RemoveAt(i); }
        for (var i = 0; i < start; i++) { shown.RemoveAt(0); }
        for (var i = from - 1; i >= 0; i--) { shown.Insert(0, new ConversationBlock(next[i])); }
        for (var i = from + kept; i < next.Count; i++) { shown.Add(new ConversationBlock(next[i])); }
        return true;
    }

    // For each shown[s], how many blocks from it are the same as next[lead] on: the Z-function of
    // next[lead..], a stop alike with nothing, then shown.
    static int[] Runs(IReadOnlyList<ConversationBlock> shown, IReadOnlyList<MdBlock> next, int lead, Func<MdBlock, MdBlock, bool> same)
    {
        var p = next.Count - lead;
        var n = p + 1 + shown.Count;
        MdBlock? At(int i) => i < p ? next[lead + i] : i == p ? null : shown[i - p - 1].Block;
        bool Alike(int i, int j) => At(i) is { } a && At(j) is { } b && same(a, b);
        var z = new int[n];
        for (int i = 1, l = 0, r = 0; i < n; i++)
        {
            if (i < r) { z[i] = Math.Min(r - i, z[i - l]); }
            while (i + z[i] < n && Alike(z[i], i + z[i])) { z[i]++; }
            if (i + z[i] > r) { (l, r) = (i, i + z[i]); }
        }
        var runs = new int[shown.Count];
        for (var s = 0; s < shown.Count; s++) { runs[s] = z[p + 1 + s]; }
        return runs;
    }

    // The same block, drawn the same: records compare lists by reference, so their contents here.
    public static bool Same(MdBlock a, MdBlock b) => (a, b) switch
    {
        (MdParagraph x, MdParagraph y) => x.Inlines.SequenceEqual(y.Inlines),
        (MdHeading x, MdHeading y) => x.Level == y.Level && x.Inlines.SequenceEqual(y.Inlines),
        (MdCode x, MdCode y) => x == y,
        (MdQuote x, MdQuote y) => SameBlocks(x.Blocks, y.Blocks),
        (MdList x, MdList y) => x.Ordered == y.Ordered && x.Start == y.Start && x.Items.Count == y.Items.Count && x.Items.Zip(y.Items).All(p => SameBlocks(p.First, p.Second)),
        (MdRule, MdRule) => true,
        (MdTable x, MdTable y) => x.HasHeader == y.HasHeader && x.Rows.Count == y.Rows.Count
            && x.Rows.Zip(y.Rows).All(r => r.First.Count == r.Second.Count && r.First.Zip(r.Second).All(c => c.First.SequenceEqual(c.Second))),
        _ => false,
    };

    static bool SameBlocks(IReadOnlyList<MdBlock> a, IReadOnlyList<MdBlock> b) => a.Count == b.Count && a.Zip(b).All(p => Same(p.First, p.Second));

    public static string TextOf(MdBlock block)
    {
        var text = new StringBuilder();
        Append(text, block);
        // Only the plain spaces and line breaks at its edges: a no-break space, a line separator or any
        // other hidden character there is part of what the block says, and NameOf reveals it (#829).
        return text.ToString().Trim(AsciiEdges);
    }

    static void Append(StringBuilder text, MdBlock block)
    {
        switch (block)
        {
            case MdParagraph p: Inlines(text, p.Inlines); break;
            case MdHeading h: Inlines(text, h.Inlines); break;
            case MdCode c: text.Append(CodeCopy.Drawn(c.Text)); break;
            case MdQuote q: Lines(text, q.Blocks); break;
            case MdList l:
                var n = l.Start;
                foreach (var item in l.Items)
                {
                    if (text.Length > 0) { text.Append('\n'); }
                    text.Append(l.Ordered ? $"{n++}. " : "• ");
                    Lines(text, item);
                }
                break;
            case MdRule: text.Append("Separator"); break;
            case MdTable t:
                foreach (var row in t.Rows)
                {
                    if (text.Length > 0) { text.Append('\n'); }
                    for (var c = 0; c < row.Count; c++)
                    {
                        if (c > 0) { text.Append(", "); }
                        Inlines(text, row[c]);
                    }
                }
                break;
        }
    }

    static void Lines(StringBuilder text, IReadOnlyList<MdBlock> blocks)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            if (i > 0) { text.Append('\n'); }
            Append(text, blocks[i]);
        }
    }

    static void Inlines(StringBuilder text, IReadOnlyList<MdInline> inlines)
    {
        foreach (var i in inlines)
        {
            text.Append(i switch { MdText t => t.Text, MdLink l => l.Text, MdBreak => "\n", _ => "" });
        }
    }
}

// The block at the top of the conversation's viewport, and how far above or below that edge it
// starts, so a re-wrap (a live resize) leaves it where it was (#765): the scroll offset is moved by
// as much as the block moved, never kept for its own sake. The header above the blocks at the top
// anchors nothing: what is above the blocks doesn't move as they re-wrap.
public readonly record struct ScrollAnchor(int Index, double Top)
{
    // Of the realised blocks (their index, top in the viewport and height), the one across the
    // viewport's top edge, or the first below it when it starts exactly there.
    public static ScrollAnchor? At(IEnumerable<(int Index, double Top, double Height)> realised)
    {
        foreach (var (index, top, height) in realised.OrderBy(r => r.Index))
        {
            if (top + height <= 0) { continue; }
            return top <= 0 ? new ScrollAnchor(index, top) : null;
        }
        return null;
    }

    // The offset that puts the block back where it was, now that it starts at topNow in the
    // viewport: within the scroller's range.
    public double OffsetFor(double offset, double topNow, double scrollable) => Math.Clamp(offset + topNow - Top, 0, Math.Max(0, scrollable));

    // After any layout pass: the offset that puts the block back when layout moved it under a still
    // view (a block above it realised, its estimated height corrected), or null when nothing moved
    // it or the view moved since the anchor was taken at takenAt (the person scrolled, or the list
    // kept the block in place itself), which takes the anchor again rather than undoing it.
    public double? Correction(double takenAt, double offset, double topNow, double scrollable)
    {
        if (Math.Abs(offset - takenAt) >= 0.5) { return null; }
        var to = OffsetFor(offset, topNow, scrollable);
        return Math.Abs(to - offset) >= 0.5 ? to : null;
    }
}
