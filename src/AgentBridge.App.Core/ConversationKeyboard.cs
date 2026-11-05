namespace AgentBridge.App.Core;

// The conversation read with the keyboard as a page, not a list (#765, #707): the keys that scroll
// a page scroll the text, never move between blocks, and Tab reaches every link and Copy button in
// order, a block the list has not realised yet included.
public static class ConversationKeyboard
{
    // A line, as the ScrollViewer the conversation once was scrolled by one.
    public const double Line = 16;

    // The key as the conversation reads it, or null for one it leaves alone. The list under the
    // conversation moves between its items for plain, Ctrl and Shift arrows and pages alike, so
    // every one of them is taken (on the way down or the way back up: TakenEarly): Ctrl+Home and
    // Ctrl+End go to the top and the end as in any document, and the rest with Ctrl or Shift, and
    // Left and Right (the text never scrolls sideways), don't move. Alt is left to the window's own
    // keys. A key pressed in a text input, or in a control that uses it (a combo box, a slider, a
    // list of its own), is that control's: the caret, a value or a selection moves, never the page
    // under it, Ctrl+Home and Ctrl+End included.
    public static ReadingKey? Key(int virtualKey, bool ctrl, bool shift, bool alt, KeyUser focus = KeyUser.None)
    {
        if (alt || focus != KeyUser.None) { return null; }
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
    // A Tab that wrapped round the window passed the blocks from `from` to the end of the response
    // (its start, going back), then those from its other end up to `to`: Shift+Tab from the window's
    // first control onto the last block drawn passes every block after it.
    public static int? Passed(IReadOnlyList<ConversationBlock> blocks, int from, int to, bool forward, Func<int, bool> realised, bool wrapped = false)
    {
        if (!Wrapped(from, to, forward, toBeforeFrom: null) && !wrapped) { return Between(blocks, from, to, forward, realised); }
        var (end, start) = forward ? (blocks.Count, -1) : (-1, blocks.Count);
        return Between(blocks, from, end, forward, realised) ?? Between(blocks, start, to, forward, realised);
    }

    static int? Between(IReadOnlyList<ConversationBlock> blocks, int from, int to, bool forward, Func<int, bool> realised)
    {
        var step = forward ? 1 : -1;
        for (var i = from + step; forward ? i < to : i > to; i += step)
        {
            if (i >= 0 && i < blocks.Count && blocks[i].Focusable && !realised(i)) { return i; }
        }
        return null;
    }

    // Whether a Tab wrapped round the window: it went the wrong way, or stayed at the same place
    // (before the list, after it, or in one block) while going to what comes before what had focus
    // (toBeforeFrom, from the visual tree; null when that can't be told).
    public static bool Wrapped(int from, int to, bool forward, bool? toBeforeFrom) =>
        (forward ? to < from : to > from) || (to == from && toBeforeFrom == forward);

    // Where a block a Tab is on its way to is now: the response may have been cut at its start or
    // grown since the move was queued, so the move holds the block itself, not its index. -1 once it
    // is gone.
    public static int Where(IReadOnlyList<ConversationBlock> blocks, ConversationBlock block)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            if (ReferenceEquals(blocks[i], block)) { return i; }
        }
        return -1;
    }

    // Where a held Tab goes once it runs: to its block wherever that now is; or, the block gone
    // (a reply's last block is replaced as it streams in), to the block a Tab from `from`, where
    // focus still is, now stops in first, should that be one the list has not realised. Null when
    // Tab should simply go on from where focus is: to a block already realised, or past the list. A
    // held Tab that wrapped round the window goes on from the other end of the response, up to
    // where focus is.
    public static int? Held(IReadOnlyList<ConversationBlock> blocks, ConversationBlock target, int? from, bool forward, Func<int, bool> realised, bool wrapped = false)
    {
        if (Where(blocks, target) is >= 0 and var at) { return at; }
        if (from is not { } f) { return null; }
        var step = forward ? 1 : -1;
        for (var i = f + step; i >= 0 && i < blocks.Count; i += step)
        {
            if (blocks[i].Focusable) { return realised(i) ? null : i; }
        }
        if (!wrapped) { return null; }
        for (var i = forward ? 0 : blocks.Count - 1; forward ? i < f : i > f; i += step)
        {
            if (blocks[i].Focusable) { return realised(i) ? null : i; }
        }
        return null;
    }

    // Whether a reading key is taken on its way down to what has focus (PreviewKeyDown), so neither
    // the list nor anything in a block moves first, or only on its way back up (KeyDown), should
    // nothing inside have used it. Left and Right, and the keys made still by Shift or Ctrl, wait
    // for the way up, so a wide table's own scroller and selected text keep them; so does every key
    // pressed in selectable text, whose caret (F7, caret browsing) moves with the arrows.
    public static bool TakenEarly(ReadingKey key, bool inSelectableText) => key != ReadingKey.Still && !inSelectableText;

    // The ends of a Tab as Passed reads them, from the places of what had focus and what gets it
    // (null outside the conversation's tree: entering from a popup, or leaving for one).
    public static (int From, int To) Ends(int? from, int? to, int count, bool forward) =>
        (from ?? (forward ? -1 : count), to ?? (forward ? count : -1));
}

public enum ReadingKey { LineUp, LineDown, PageUp, PageDown, Top, End, Still }

// What had focus when a reading key reached the conversation: a text input (its caret moves) or a
// control that uses the key itself (a combo box, a slider, a list), or neither (a link, a button).
public enum KeyUser { None, Text, Selector }
