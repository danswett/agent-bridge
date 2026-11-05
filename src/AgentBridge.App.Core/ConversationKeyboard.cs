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

    // What a held Tab does when it runs (#829): nothing at all once another session's conversation is
    // shown, or once focus has moved from where the Tab left it (a click, the reply box, a session's
    // own focus); otherwise as Held says, however the list was replaced since. Focus taken from a
    // block the reply has since replaced (leftFrom, null for anywhere outside the list) went with the
    // block, so that alone isn't a move: but only while focus is parked where the list leaves it on
    // taking the focused element away (focusParked: nowhere, or in the list). A pointer pressed in the
    // session since the Tab (pressed) drops it too, focus moved or not: the person has acted. Keys
    // aren't counted, so the Tab's own key can't drop it; a key that moves focus moves it from where
    // the Tab left it. Block is where to bring focus; null with Dropped false, Tab goes on from where focus is.
    public readonly record struct HeldMove(bool Dropped, int? Block);

    public static HeldMove Resume(string? heldIn, string? shownIn, object? focusedThen, ConversationBlock? leftFrom, object? focusedNow, bool focusParked, bool pressed, IReadOnlyList<ConversationBlock> blocks, ConversationBlock target, int? from, bool forward, Func<int, bool> realised, bool wrapped = false) =>
        heldIn is null || heldIn != shownIn || pressed
            || (focusedThen is not null && !ReferenceEquals(focusedThen, focusedNow) && (leftFrom is null || Where(blocks, leftFrom) >= 0 || !focusParked))
            ? new(true, null)
            : new(false, Held(blocks, target, from, forward, realised, wrapped));

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

    // The block a Tab lands in by itself that is still brought in at the leading edge, as a held Tab
    // is (#829): one the list had not laid out when Tab was pressed (laidOutAtKey), which the focus
    // search realised on its way and would only scroll as far as it must; with
    // TabReach.NotWhollyInView, also one laid out but not wholly in view. Null for a move within one
    // block, or one that ends outside the blocks (to, from: places as Passed reads them, null outside
    // the conversation's tree).
    public static int? Reached(int? from, int? to, int count, TabReach reach, Func<int, bool> laidOutAtKey, Func<int, bool> whollyInView)
    {
        if (to is not (>= 0 and var t) || t >= count || from == t) { return null; }
        if (!laidOutAtKey(t)) { return t; }
        return reach == TabReach.NotWhollyInView && !whollyInView(t) ? t : null;
    }

    // Whether a block, its top `top` from the viewport's and `height` tall, is wholly in a viewport
    // `viewport` tall: half a pixel either way is rounding.
    public static bool WhollyInView(double top, double height, double viewport) => top >= -0.5 && top + height <= viewport + 0.5;

    // Whether the scroll the focus manager makes to a stop focused in a block just brought to the
    // leading edge is held back (#829): it pads a control 20 px each side, so a Copy button 12 px
    // down its block would take the block's start 8 px off the edge. Held when the stop itself, its
    // top `top` from the viewport's and `height` tall, is wholly in view, as nothing need move; one
    // not wholly in view (in a block taller than the view) is brought in with TabLanding.FocusVisible,
    // and stays where it is, the block's start at the edge, with TabLanding.BlockStart.
    public static bool HoldsFocusScroll(TabLanding landing, double top, double height, double viewport) =>
        landing == TabLanding.BlockStart || WhollyInView(top, height, viewport);

    // Whether a focus move is the focus manager's to report, not a routed LosingFocus or
    // GettingFocus's (#829): those are raised only on a control, never on a text element such as a
    // link, so a move between two links, or between a link and anything outside the session, raises
    // neither in the session. Ours when an end is in the session (here) and neither is a control
    // in it, whose routed event has been handled already.
    public static bool Unrouted(bool fromControlHere, bool toControlHere, bool fromHere, bool toHere) =>
        !fromControlHere && !toControlHere && (fromHere || toHere);
}

// How far Tab brings in a block it lands in by itself (Reached): one not laid out when Tab was
// pressed, or also one not wholly in view.
public enum TabReach { NotLaidOut, NotWhollyInView }

// Which gives way when the stop Tab focuses in a block brought to the leading edge is not wholly in
// view (HoldsFocusScroll): the block's start at the edge, or the stop in view.
public enum TabLanding { FocusVisible, BlockStart }

public enum ReadingKey { LineUp, LineDown, PageUp, PageDown, Top, End, Still }

// What had focus when a reading key reached the conversation: a text input (its caret moves) or a
// control that uses the key itself (a combo box, a slider, a list), or neither (a link, a button).
public enum KeyUser { None, Text, Selector }
