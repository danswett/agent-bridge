using System.Collections.ObjectModel;
using AgentBridge.App.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace AgentBridge.App;

// The conversation's response as a list (#765): its top-level blocks are the list's items, so only
// those near the viewport are realised, and a live resize re-wraps those alone; it once re-wrapped
// every block of a long response on every step of a drag. The block at the top of the viewport
// stays there through a change of width: the header above the blocks re-wraps too, and the list
// alone would keep the offset, not the block.
public sealed partial class SessionView
{
    ConversationItems blocks = [];
    // What the list was last made from, so a card's every change doesn't parse its response again.
    SessionCardViewModel? blocksOf;
    string? blocksFrom;

    ScrollViewer? ConversationScroll { get; set; }
    ScrollAnchor? anchor;
    // The offset when the anchor was taken, or last put back: a layout that moves the anchor while
    // the offset is still this moved it under a still view.
    double anchorAt;
    // While a finger or the touchpad pans the conversation: what moves then is the person's.
    bool panning;
    // While a scroll is under way (a key's animated page, a wheel): the view isn't still until it stops.
    bool scrolling;
    // The offset a correction of ours asked for: its ViewChanged is not the person scrolling.
    double? anchoring;
    // Where the last scrolling key is taking the conversation, until that scroll has stopped.
    double? keyTarget;
    // While Tab is sent on past a block that could not be realised, so it isn't held again for it.
    bool lettingTabPass;
    // The blocks the list had laid out when Tab was last pressed in the session, until that press
    // has been handled: the focus search realises the block it lands in on its way (Reached).
    HashSet<int>? laidOutAtTab;
    // Pointer presses in the session, so a held Tab gives way once the person has clicked since,
    // focus moved by it or not (Resume). Keys aren't counted: the held Tab's own key would drop it.
    int presses;
    // How far a Tab that lands in a block by itself brings it in (ConversationKeyboard.Reached): any
    // block not wholly in view, laid out or not, as B and the creative director ruled on #829 (ui.md §6.4).
    static readonly TabReach Reach = TabReach.NotWhollyInView;
    // Where focus goes on a stop not wholly in view in a block brought to the leading edge (one taller
    // than the view): brought in, the block's start leaving the edge, until B rules on #829.
    static readonly TabLanding Landing = TabLanding.FocusVisible;

    void ShowResponse(bool reset)
    {
        var card = Card;
        var from = card is { HasVoice: true } ? card.Response : null;
        if (!reset && card == blocksOf && from == blocksFrom) { return; }
        blocksOf = card;
        blocksFrom = from;
        IReadOnlyList<MdBlock> next = from is null ? [] : MarkdownModel.Parse(from);
        if (reset || !ConversationBlocks.Update(blocks, next))
        {
            blocks = new ConversationItems(next.Select(b => new ConversationBlock(b)));
            ConversationList.ItemsSource = blocks;
        }
        // Indexes have moved: the anchor taken again once the list has laid them out.
        anchor = null;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, TakeAnchor);
        // A reply arriving or growing under a view still at the end (#1646); a view a key or focus moved away is left.
        if (pinEnd && follow.AtEnd) { KeepPlace(true, 0); }
        // The response's empty view held 8 under its label, as its first block does.
        FooterLead.Height = card is { HasVoice: true } && blocks.Count == 0 ? 8 : 0;
    }

    void HookConversation()
    {
        if (ConversationScroll is not null || Shallowest<ScrollViewer>(ConversationList) is not { } scroll) { return; }
        ConversationScroll = scroll;
        scroll.ViewChanged += (_, e) =>
        {
            follow.Scrolled(scroll.VerticalOffset, scroll.ScrollableHeight);
            if (newMessages > 0 && follow.AtEnd) { ShowNewMessages(); }
            SyncQuestionBar();
            // A question still moving into view is held from the pointer until it has stopped.
            guard.Scrolled(DateTimeOffset.UtcNow);
            if (anchoring is not { } asked || Math.Abs(scroll.VerticalOffset - asked) >= 0.5) { TakeAnchor(); }
            scrolling = e.IsIntermediate;
            if (!e.IsIntermediate) { anchoring = null; keyTarget = null; }
        };
        scroll.DirectManipulationStarted += (_, _) => { keyTarget = null; panning = true; follow.PanStarted(); ReleasePin(); };
        scroll.DirectManipulationCompleted += (_, _) => { panning = false; follow.PanEnded(); };
        scroll.PointerWheelChanged += (_, _) => ReleasePin();
        ConversationList.LayoutUpdated += OnConversationLaidOut;
        // Tunnelling, so the keys reach this before anything inside the list can take them: the list
        // would move between its items, a child might handle a key first (ui.md §6.4). What has focus
        // keeps a key it uses (KeyUserOf); the keys TakenEarly leaves are taken on the way back up,
        // once what has focus has had them.
        scroll.PreviewKeyDown += (_, e) => OnReadingKey(e, early: true);
        scroll.KeyDown += (_, e) => OnReadingKey(e, early: false);
    }

    // The keys that scroll a page scroll the conversation, as its ScrollViewer did (ui.md §6.4),
    // unless what has focus is a text input or a control that uses the key.
    void OnReadingKey(KeyRoutedEventArgs e, bool early)
    {
        if (ConversationScroll is not { } scroll) { return; }
        static bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ConversationKeyboard.Key((int)e.Key, Down(VirtualKey.Control), Down(VirtualKey.Shift), e.KeyStatus.IsMenuKeyDown || Down(VirtualKey.Menu), KeyUserOf(e.OriginalSource as DependencyObject)) is not { } key) { return; }
        if (early && !ConversationKeyboard.TakenEarly(key, e.OriginalSource is TextBlock or RichTextBlock or RichTextBlockOverflow)) { return; }
        e.Handled = true;
        // From where the last key is taking it, so a key pressed while that scroll still moves adds to it.
        var from = keyTarget ?? scroll.VerticalOffset;
        var to = ConversationKeyboard.Offset(key, from, scroll.ViewportHeight, scroll.ScrollableHeight);
        if (Math.Abs(to - from) >= 0.5 && scroll.ChangeView(null, to, null, disableAnimation: !Motion.Scrolls)) { keyTarget = to; ReleasePin(); }
    }

    // What had focus when a key reached the conversation, from it up to the list: a text input, a
    // control that uses the keys itself, or neither.
    KeyUser KeyUserOf(DependencyObject? source)
    {
        for (var node = source is TextElement t ? t.ContentStart?.VisualParent : source; node is not null && node != ConversationList; node = VisualTreeHelper.GetParent(node))
        {
            switch (node)
            {
                case TextBox or RichEditBox or PasswordBox or AutoSuggestBox or NumberBox: return KeyUser.Text;
                case Selector or Slider or RadioButton or RadioButtons or RatingControl or CalendarView or DatePicker or TimePicker or TreeView: return KeyUser.Selector;
            }
        }
        return KeyUser.None;
    }

    // Tab and Shift+Tab stop in every block with a link or a Copy button, in order: one the list
    // has not realised yet is passed by the focus search, so the move is held, the block realised
    // and focus put on its first stop (its last, going back). The session's handlers see every move
    // out of (LosingFocus) or into (GettingFocus) anything in it, handled or not, so a Tab that passes
    // the whole list, nothing in it a stop until a block is realised, is held too; whichever comes
    // first holds it. A link is a text element, whose moves raise neither, so those come from the
    // focus manager (OnFocusMoving). A block Tab lands in by itself that isn't wholly in view is
    // brought in at the leading edge too (Reached); one wholly in view doesn't move, the focus
    // manager's own scroll to its stop held (InViewAtTab).
    void OnLeavingBlock(UIElement sender, LosingFocusEventArgs e)
    {
        if (TabPassed(e.OldFocusedElement, e.NewFocusedElement, e.Direction) is { } passed) { e.Cancel = true; FocusBlock(passed.Index, e.Direction == FocusNavigationDirection.Next, passed.Wrapped, e.OldFocusedElement); }
    }

    void OnReachingBlock(UIElement sender, GettingFocusEventArgs e)
    {
        if (TabPassed(e.OldFocusedElement, e.NewFocusedElement, e.Direction) is { } passed) { e.Cancel = true; FocusBlock(passed.Index, e.Direction == FocusNavigationDirection.Next, passed.Wrapped, e.OldFocusedElement); }
        else if (!e.Cancel && InViewAtTab(e.NewFocusedElement, e.Direction) is { } kept) { HoldFocusScroll(kept.Container, kept.Source).Leaving(); }
    }

    // The block a Tab lands in that is wholly in view, so stays where it is (ui.md §6.4), and what
    // the focus manager's scroll to the stop will come from: it pads a control 20 px each side
    // (BringIntoViewHandler), so a Copy button within 20 px of the view's edge in a block wholly in
    // view moved the view (B on #829, Copilot 5783106122). Null for any other move.
    (UIElement Container, UIElement Source)? InViewAtTab(DependencyObject? to, FocusNavigationDirection direction)
    {
        if (direction is not (FocusNavigationDirection.Next or FocusNavigationDirection.Previous) || lettingTabPass || ConversationScroll is not { } scroll) { return null; }
        if (PlaceOf(to) is not (>= 0 and var at) || at >= blocks.Count || ConversationList.ContainerFromIndex(at) is not FrameworkElement container) { return null; }
        if (!ConversationKeyboard.WhollyInView(Top(container, scroll), container.ActualHeight, scroll.ViewportHeight)) { return null; }
        return (to as UIElement ?? (to as TextElement)?.ContentStart?.VisualParent) is { } source ? (container, source) : null;
    }

    (int Index, bool Wrapped)? TabPassed(DependencyObject? from, DependencyObject? to, FocusNavigationDirection direction)
    {
        if (direction is not (FocusNavigationDirection.Next or FocusNavigationDirection.Previous)) { return null; }
        // Not while the list is hidden (the Delegates or Tools tab), nor for a move made on from a
        // block that could not be realised.
        if (lettingTabPass || ConversationList.Visibility != Visibility.Visible || !ConversationList.IsLoaded) { return null; }
        var forward = direction == FocusNavigationDirection.Next;
        var (was, now) = (PlaceOf(from), PlaceOf(to));
        // A move with neither end in the conversation's tree (within a flyout, say) isn't ours.
        if (was is null && now is null) { return null; }
        var (f, t) = ConversationKeyboard.Ends(was, now, blocks.Count, forward);
        // Two ends at one place (both before the list, say) wrapped when the second comes first.
        var wrapped = ConversationKeyboard.Wrapped(f, t, forward, f == t ? Precedes(to, from) : null);
        if (ConversationKeyboard.Passed(blocks, f, t, forward, i => ConversationList.ContainerFromIndex(i) is not null, wrapped) is { } index) { return (index, wrapped); }
        // Landed in a block by itself: still brought in at the leading edge when it wasn't laid out
        // as Tab was pressed, or isn't wholly in view (Reach).
        var laidOut = laidOutAtTab;
        return ConversationKeyboard.Reached(was, now, blocks.Count, Reach,
            i => laidOut?.Contains(i) ?? ConversationList.ContainerFromIndex(i) is not null,
            i => ConversationList.ContainerFromIndex(i) is FrameworkElement c && ConversationScroll is { } s && ConversationKeyboard.WhollyInView(Top(c, s), c.ActualHeight, s.ViewportHeight)) is { } reached
            ? (reached, wrapped) : null;
    }

    // Tab pressed anywhere in the session: what the list has laid out then, until the press has been
    // handled, focus moved or not.
    void OnTabKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Tab || ConversationList.ItemsPanelRoot is not ItemsStackPanel panel || panel.FirstCacheIndex < 0) { return; }
        var laidOut = new HashSet<int>();
        for (var i = panel.FirstCacheIndex; i <= panel.LastCacheIndex; i++)
        {
            if (ConversationList.ContainerFromIndex(i) is not null) { laidOut.Add(i); }
        }
        laidOutAtTab = laidOut;
        DispatcherQueue.TryEnqueue(() => { if (laidOutAtTab == laidOut) { laidOutAtTab = null; } });
    }

    // Every focus move in the app, as the focus manager reports it: a link is a text element, not a
    // control, so a move between two links (or between a link and anything outside the session)
    // raises no routed LosingFocus or GettingFocus here (#829); held as those are.
    void OnFocusMoving(object? sender, GettingFocusEventArgs e)
    {
        if (e.Cancel || !DispatcherQueue.HasThreadAccess || !IsLoaded) { return; }
        if (RedirectFoldFocus(e)) { return; }
        var (from, to) = (e.OldFocusedElement, e.NewFocusedElement);
        var (fromHere, toHere) = (Within(from), Within(to));
        if (ConversationKeyboard.Unrouted(fromHere && from is UIElement, toHere && to is UIElement, fromHere, toHere)) { OnReachingBlock(this, e); }
    }

    // Whether an element, a link in a text block included, is in this session's tree.
    bool Within(DependencyObject? element) => Under(element, this);

    // Whether an element is in the conversation list: on it, its scroller, or in a block.
    bool InList(DependencyObject? element) => Under(element, ConversationList);

    static bool Under(DependencyObject? element, DependencyObject ancestor)
    {
        for (var node = element is TextElement t ? t.ContentStart?.VisualParent : element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node == ancestor) { return true; }
        }
        return false;
    }

    // Whether a comes before b in the visual tree; null for one element, or two in different trees.
    static bool? Precedes(DependencyObject? a, DependencyObject? b)
    {
        static DependencyObject? Drawn(DependencyObject? e) => e is TextElement t ? t.ContentStart?.VisualParent : e;
        return (Drawn(a), Drawn(b)) is ({ } x, { } y) && x != y ? Before(x, y) : null;
    }

    // Where in the conversation an element is: -1 its header or anything before the list, a block's
    // index, the block count its footer or anything after the list; null on the list itself, or in
    // another tree (a popup). A link is a text element, in its text block.
    int? PlaceOf(DependencyObject? element)
    {
        var start = element is TextElement t ? t.ContentStart?.VisualParent : element;
        for (var node = start; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node == ConversationHeader) { return -1; }
            if (node == ConversationFooter) { return blocks.Count; }
            if (node is ListViewItem item && ItemsControl.ItemsControlFromItemContainer(item) == ConversationList) { return ConversationList.IndexFromContainer(item); }
            if (node == ConversationList) { return null; }
        }
        return start is null ? null : Before(start, ConversationList) is { } before ? (before ? -1 : blocks.Count) : null;
    }

    // Whether a comes before b in the visual tree, as the focus search orders them: by where their
    // branches part. Null when they are in different trees.
    static bool? Before(DependencyObject a, DependencyObject b)
    {
        static List<DependencyObject> Chain(DependencyObject node)
        {
            var chain = new List<DependencyObject>();
            for (DependencyObject? n = node; n is not null; n = VisualTreeHelper.GetParent(n)) { chain.Add(n); }
            chain.Reverse();
            return chain;
        }
        var (ca, cb) = (Chain(a), Chain(b));
        if (ca[0] != cb[0]) { return null; }
        var i = 0;
        while (i < ca.Count && i < cb.Count && ca[i] == cb[i]) { i++; }
        if (i == ca.Count) { return true; }
        if (i == cb.Count) { return false; }
        var parent = ca[i - 1];
        for (var k = 0; k < VisualTreeHelper.GetChildrenCount(parent); k++)
        {
            var child = VisualTreeHelper.GetChild(parent, k);
            if (child == ca[i]) { return true; }
            if (child == cb[i]) { return false; }
        }
        return null;
    }

    // The block brought into view at the leading edge and laid out, then focus on its first stop (or
    // last, going back), as the keyboard had put it there. Should it hold none after all, Tab goes on
    // from where it was; should it not be realised even after a few frames of layout, Tab goes on
    // past it rather than staying where it was. Held for this session, with focus where the Tab left
    // it (cancelled, it stays there): should either change before it runs, it does nothing (Resume).
    void FocusBlock(int index, bool forward, bool wrapped, DependencyObject? focusedThen)
    {
        var target = blocks[index];
        var heldIn = Card?.Id;
        var leftFrom = PlaceOf(focusedThen) is { } p && p >= 0 && p < blocks.Count ? blocks[p] : null;
        var pressedThen = presses;
        var direction = forward ? FocusNavigationDirection.Next : FocusNavigationDirection.Previous;
        var waited = 0;
        ConversationBlock? brought = null;
        DispatcherQueue.TryEnqueue(Bring);

        // The response may change while this waits, even to a new list of blocks, so the block is
        // found again each time; one replaced as the reply streams in gives way to the block Tab now
        // stops in (Held). Another session shown since, a pointer pressed since, or focus moved other
        // than with a replaced block (anywhere outside the list), and it does nothing.
        void Bring()
        {
            var focused = XamlRoot is { } r ? FocusManager.GetFocusedElement(r) : null;
            var parked = focused is null || InList(focused as DependencyObject);
            var move = ConversationKeyboard.Resume(heldIn, Card?.Id, focusedThen, leftFrom, focused, parked, presses != pressedThen, blocks, target, PlaceOf(focused as DependencyObject), forward, i => ConversationList.ContainerFromIndex(i) is not null, wrapped);
            if (move.Dropped) { return; }
            if (move.Block is not { } at)
            {
                PassOn();
                return;
            }
            target = blocks[at];
            if (!ReferenceEquals(brought, target)) { ConversationList.ScrollIntoView(target, ScrollIntoViewAlignment.Leading); brought = target; }
            ConversationList.UpdateLayout();
            if (ConversationList.ContainerFromIndex(at) is not { } container)
            {
                // Not laid out yet: again at the next layout, a few times at most.
                if (++waited < 5 && DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Bring)) { return; }
                PassOn();
                return;
            }
            var stop = forward ? FocusManager.FindFirstFocusableElement(container) : FocusManager.FindLastFocusableElement(container);
            if (stop is not null) { Land((UIElement)container, stop); }
            // Realised now, so the search passes it this time rather than coming back to it.
            else if (XamlRoot?.Content is { } root) { FocusManager.TryMoveFocus(direction, new FindNextElementOptions { SearchRoot = root }); }
        }

        // Tab goes on from where focus is, past what could not be brought, never staying put.
        void PassOn()
        {
            if (XamlRoot?.Content is not { } within) { return; }
            lettingTabPass = true;
            try { FocusManager.TryMoveFocus(direction, new FindNextElementOptions { SearchRoot = within }); }
            finally { lettingTabPass = false; }
        }
    }

    // Focus on a stop in a block just brought to the leading edge, the focus manager's own scroll to
    // it held back when nothing need move (HoldsFocusScroll): it pads a control 20 px each side
    // (BringIntoViewHandler), so a Copy button 12 px down its block took the block's start 8 px off
    // the edge (run 41997001156). A link it brings in unpadded, so only as far as it must. Held
    // once (HoldFocusScroll).
    void Land(UIElement container, DependencyObject stop)
    {
        var hold = stop is FrameworkElement e && ConversationScroll is { } s
            ? ConversationKeyboard.HoldsFocusScroll(Landing, Top(e, s), e.ActualHeight, s.ViewportHeight)
            : Landing == TabLanding.BlockStart;
        var source = stop as UIElement ?? (stop as TextElement)?.ContentStart?.VisualParent;
        // Focus already there moves nothing, so asks for no scroll to hold.
        if (!hold || source is null || XamlRoot is not { } root || ReferenceEquals(FocusManager.GetFocusedElement(root), stop))
        {
            _ = FocusManager.TryFocusAsync(stop, FocusState.Keyboard);
            return;
        }
        var (release, leaving) = HoldFocusScroll(container, source);
        FocusManager.TryFocusAsync(stop, FocusState.Keyboard).Completed = (op, status) =>
        {
            if (status != AsyncStatus.Completed || !op.GetResults().Succeeded) { DispatcherQueue.TryEnqueue(() => release()); }
        };
        // Only once focus has moved: the move itself may raise LosingFocus here.
        leaving();
    }

    // The focus manager's scroll to a stop just focused in a block, held back once: its request is
    // raised as focus moves but routed later, after the focus has completed, so the hold waits for
    // that request itself (from the stop, or a link's text) and takes only it. Release lets it go
    // unused (focus refused); Leaving, called once focus is in the block, lets it go should focus
    // move on from the block first.
    (Action Release, Action Leaving) HoldFocusScroll(UIElement container, UIElement source)
    {
        TypedEventHandler<UIElement, BringIntoViewRequestedEventArgs>? held = null;
        TypedEventHandler<UIElement, LosingFocusEventArgs>? left = null;
        void Release()
        {
            container.BringIntoViewRequested -= held;
            container.LosingFocus -= left;
        }
        held = (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, source)) { return; }
            e.Handled = true;
            Release();
        };
        left = (_, _) => Release();
        container.BringIntoViewRequested += held;
        return (Release, () => container.LosingFocus += left);
    }

    // The block across the viewport's top edge, where it starts against that edge, and the offset then.
    void TakeAnchor()
    {
        anchor = null;
        if (ConversationScroll is not { } scroll || ConversationList.ItemsPanelRoot is not ItemsStackPanel panel || panel.FirstVisibleIndex < 0) { return; }
        var realised = new List<(int, double, double)>();
        for (var i = panel.FirstVisibleIndex; i <= Math.Min(panel.FirstVisibleIndex + 1, blocks.Count - 1); i++)
        {
            if (ConversationList.ContainerFromIndex(i) is FrameworkElement c) { realised.Add((i, Top(c, scroll), c.ActualHeight)); }
        }
        anchor = ScrollAnchor.At(realised);
        anchorAt = scroll.VerticalOffset;
    }

    // Laid out at a new width: the anchor block back where it was, in the same pass, before it is drawn.
    void KeepAnchor()
    {
        if (anchor is not { } a || ConversationScroll is not { } scroll || ConversationList.ContainerFromIndex(a.Index) is not FrameworkElement c) { return; }
        var to = a.OffsetFor(scroll.VerticalOffset, Top(c, scroll), scroll.ScrollableHeight);
        if (Math.Abs(to - scroll.VerticalOffset) < 0.5) { return; }
        Anchoring(scroll, to);
    }

    // After any layout under a still view: a block above the anchor realised, or an estimated height
    // corrected, put the anchor back where it was, so what is read never moves (ui.md §6.4). Not while
    // the person pans or a scroll is under way, nor while a correction of ours is on its way.
    void OnConversationLaidOut(object? sender, object e)
    {
        if (panning || scrolling || keyTarget is not null || anchoring is not null || anchor is not { } a || ConversationScroll is not { } scroll || !ConversationList.IsLoaded
            || ConversationList.ContainerFromIndex(a.Index) is not FrameworkElement c) { return; }
        if (a.Correction(anchorAt, scroll.VerticalOffset, Top(c, scroll), scroll.ScrollableHeight) is { } to) { Anchoring(scroll, to); }
    }

    void Anchoring(ScrollViewer scroll, double to)
    {
        anchoring = to;
        anchorAt = to;
        scroll.ChangeView(null, to, null, disableAnimation: true);
    }

    static double Top(UIElement element, UIElement viewport) => element.TransformToVisual(viewport).TransformPoint(default).Y;

    // Each block named by its text, as a screen reader finds it unrealised (ConversationBlock).
    void OnBlockShown(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is ConversationBlock block) { AutomationProperties.SetName(args.ItemContainer, block.Name); }
    }

    // The list's own scroller: the first found going down, above anything its items hold.
    internal static T? Shallowest<T>(DependencyObject root) where T : DependencyObject
    {
        var queue = new Queue<DependencyObject>([root]);
        while (queue.TryDequeue(out var node))
        {
            if (node is T t && node != root) { return t; }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) { queue.Enqueue(VisualTreeHelper.GetChild(node, i)); }
        }
        return null;
    }
}
