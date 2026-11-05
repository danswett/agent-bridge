using System.Collections.ObjectModel;
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

    // What the block says, as plain text: the item's name to a screen reader, whether or not the
    // list has realised it, so Narrator's scan and search reach a block that is off screen (#707).
    public string Text { get; } = ConversationBlocks.TextOf(block);

    public override string ToString() => Text;
}

public static class ConversationBlocks
{
    // The list's items made to match a response's blocks, keeping each block already shown that
    // is still there: one realised stays as it is, where it is, and the list keeps the block at the
    // top of the viewport in place (KeepItemsInView). A response that grew keeps its head; one that
    // lost its earliest blocks (cut at the start to fit, SessionBudget) keeps the rest. False, with
    // nothing changed, when nothing can be kept: the caller replaces the items whole, in one reset
    // rather than an add for each block.
    public static bool Update(ObservableCollection<ConversationBlock> shown, IReadOnlyList<MdBlock> next)
    {
        var start = next.Count == 0 ? -1 : IndexOf(shown, next[0]);
        var kept = 0;
        if (start >= 0)
        {
            while (start + kept < shown.Count && kept < next.Count && Same(shown[start + kept].Block, next[kept])) { kept++; }
        }
        if (kept == 0) { return false; }
        for (var i = shown.Count - 1; i >= start + kept; i--) { shown.RemoveAt(i); }
        for (var i = 0; i < start; i++) { shown.RemoveAt(0); }
        for (var i = kept; i < next.Count; i++) { shown.Add(new ConversationBlock(next[i])); }
        return true;
    }

    static int IndexOf(ObservableCollection<ConversationBlock> shown, MdBlock block)
    {
        for (var i = 0; i < shown.Count; i++) { if (Same(shown[i].Block, block)) { return i; } }
        return -1;
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
        return text.ToString().Trim();
    }

    static void Append(StringBuilder text, MdBlock block)
    {
        switch (block)
        {
            case MdParagraph p: Inlines(text, p.Inlines); break;
            case MdHeading h: Inlines(text, h.Inlines); break;
            case MdCode c: text.Append(c.Text); break;
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
}
