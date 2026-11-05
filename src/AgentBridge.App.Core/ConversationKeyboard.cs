namespace AgentBridge.App.Core;

// The conversation read with the keyboard as a page, not a list (#765, #707): the keys that scroll
// a page scroll the text, never move between blocks, and Tab reaches every link and Copy button in
// order, a block the list has not realised yet included.
public static class ConversationKeyboard
{
    // A line, as the ScrollViewer the conversation once was scrolled by one.
    public const double Line = 16;

    // The key as the conversation reads it, or null for one it leaves alone. A key reaches the
    // conversation only once what had focus has passed it on, and the list under it moves between
    // its items for plain, Ctrl and Shift arrows and pages alike, so every one of them is taken:
    // Ctrl+Home and Ctrl+End go to the top and the end as in any document, and the rest with Ctrl
    // or Shift, and Left and Right (the text never scrolls sideways), don't move. Alt is left to
    // the window's own keys.
    public static ReadingKey? Key(int virtualKey, bool ctrl, bool shift, bool alt)
    {
        if (alt) { return null; }
        ReadingKey? key = virtualKey switch
        {
            0x26 => ReadingKey.LineUp,
            0x28 => ReadingKey.LineDown,
            0x21 => ReadingKey.PageUp,
            0x22 => ReadingKey.PageDown,
            0x24 => ReadingKey.Top,
            0x23 => ReadingKey.End,
            0x25 or 0x27 => ReadingKey.Still,
            _ => null,
        };
        if (key is null) { return null; }
        return shift || (ctrl && key is not (ReadingKey.Top or ReadingKey.End)) ? ReadingKey.Still : key;
    }

    // Where a key leaves the conversation's scroller: a page is the viewport's height, and Home and
    // End are the top and the end.
    public static double Offset(ReadingKey key, double offset, double viewport, double scrollable)
    {
        var to = key switch
        {
            ReadingKey.LineUp => offset - Line,
            ReadingKey.LineDown => offset + Line,
            ReadingKey.PageUp => offset - viewport,
            ReadingKey.PageDown => offset + viewport,
            ReadingKey.Top => 0,
            ReadingKey.End => scrollable,
            _ => offset,
        };
        return Math.Clamp(to, 0, Math.Max(0, scrollable));
    }

    // Whether Tab stops in a block: at a link, or at a code block's Copy button. Text is selectable
    // but not a tab stop.
    public static bool Focusable(MdBlock block) => block switch
    {
        MdParagraph p => p.Inlines.Any(i => i is MdLink),
        MdHeading h => h.Inlines.Any(i => i is MdLink),
        MdCode => true,
        MdQuote q => q.Blocks.Any(Focusable),
        MdList l => l.Items.Any(item => item.Any(Focusable)),
        MdTable t => t.Rows.Any(r => r.Any(c => c.Any(i => i is MdLink))),
        _ => false,
    };

    // The block Tab should have stopped in, moving from `from` to `to` (forward or back), that it
    // passed because the list had not realised it: the first such block on the way, or null when
    // none was passed. Places are the list's: -1 its header, a block's index, blocks.Count its footer.
    public static int? Passed(IReadOnlyList<ConversationBlock> blocks, int from, int to, bool forward, Func<int, bool> realised)
    {
        var step = forward ? 1 : -1;
        for (var i = from + step; forward ? i < to : i > to; i += step)
        {
            if (i >= 0 && i < blocks.Count && blocks[i].Focusable && !realised(i)) { return i; }
        }
        return null;
    }
}

public enum ReadingKey { LineUp, LineDown, PageUp, PageDown, Top, End, Still }
