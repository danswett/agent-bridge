using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AgentBridge.App;

public sealed partial class SessionView
{
    readonly HashSet<uint> scrollbarPointers = [];
    Func<bool>? navigationPending;
    bool programmaticScroll;
    int conversationLayoutDepth;
    internal bool FollowsConversationEnd => pinEnd;

    internal T DuringConversationLayout<T>(Func<T> layout)
    {
        conversationLayoutDepth++;
        try { return layout(); }
        finally { conversationLayoutDepth--; }
    }

    void UpdateConversationLayout(FrameworkElement content) =>
        DuringConversationLayout(() => { content.UpdateLayout(); return 0; });

    internal ItemsUpdatingScrollMode? ConversationAnchoringMode =>
        (ConversationList.ItemsPanelRoot as ItemsStackPanel)?.ItemsUpdatingScrollMode;

    void SetEndPin(bool value) { pinEnd = value; SyncConversationAnchoring(); }

    void SyncConversationAnchoring()
    {
        if (ConversationList.ItemsPanelRoot is not ItemsStackPanel panel) { return; }
        // IsIntermediate also belongs to SDK layout, not just reader input.
        var mode = pinEnd && !follow.Held && !panning && keyTarget is null
            && scrollbarPointers.Count == 0 && navigationPending is null && ConversationList.IsLoaded
            ? ItemsUpdatingScrollMode.KeepLastItemInView : ItemsUpdatingScrollMode.KeepItemsInView;
        if (panel.ItemsUpdatingScrollMode != mode)
        {
            DuringConversationLayout(() => { panel.ItemsUpdatingScrollMode = mode; return 0; });
        }
    }

    internal void HoldConversationPointer(uint pointer) { follow.PointerOver(pointer); SyncConversationAnchoring(); }
    internal void ReleaseConversationPointer(uint pointer) => follow.PointerLeft(pointer);

    // Our own ScrollIntoView (a restore) raises the same bring request as question navigation;
    // it must not take the person's navigation custody, or nothing clears it after our anchor.
    void ScrollProgrammatically(Action scroll)
    {
        programmaticScroll = true;
        try { scroll(); }
        finally { programmaticScroll = false; }
    }

    void HookConversationInput(ScrollViewer scroll)
    {
        follow.HoldEnded += () =>
        {
            FollowPinnedEnd();
            if (historyFirstFrame is null && history is { } stored && historyVisible < stored.Turns.Count) { historyDraw?.Start(); }
        };
        // Question navigation and UIA ScrollIntoView own their destination, even without focus.
        scroll.BringIntoViewRequested += (_, e) =>
        {
            if (programmaticScroll) { return; }
            var (target, rect) = (e.TargetElement, e.TargetRect);
            double Top() => target.TransformToVisual(scroll).TransformPoint(new Point(rect.X, rect.Y)).Y;
            var gap = scroll.ViewportHeight - rect.Height;
            var goal = (double.IsNaN(e.VerticalAlignmentRatio)
                ? Math.Clamp(Top(), Math.Min(0, gap), Math.Max(0, gap)) : gap * e.VerticalAlignmentRatio) + e.VerticalOffset;
            bool Pending() => target is not FrameworkElement { IsLoaded: false } && Math.Abs(scroll.VerticalOffset
                - Math.Clamp(scroll.VerticalOffset + Top() - goal, 0, scroll.ScrollableHeight)) > 0.5;
            if (!Pending()) { return; }
            ReleasePin();
            navigationPending = Pending;
        };
        scroll.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, e) =>
            WheelTurned(scroll, e.GetCurrentPoint(scroll).Properties.MouseWheelDelta)), true);
        // Scrollbar parts handle their presses; release before their first ViewChanged or layout.
        scroll.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            var fromBar = false;
            for (var node = e.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is ScrollViewer owner)
                {
                    if (owner == scroll && fromBar) { scrollbarPointers.Add(e.Pointer.PointerId); ReleasePin(); }
                    return;
                }
                if (node is ScrollBar) { fromBar = true; }
            }
        }), true);
        scroll.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) => EndScrollbarGesture(e.Pointer.PointerId)), true);
        scroll.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, e) => EndScrollbarGesture(e.Pointer.PointerId)), true);
        // A release outside this window may never arrive after capture is lost.
        scroll.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, e) => EndScrollbarGesture(e.Pointer.PointerId)), true);
    }

    // A wheel notch animates from its input, a frame or more before its first view change reaches us. Until one
    // does, the view is the person's: a correction's ChangeView without animation would cancel the notch in
    // flight, so identical notches moved different distances (#4243). A wheel that cannot move the view (at an
    // edge) raises no view change, so the hold lapses on its own after WheelStart.
    bool wheelStarting;
    // Pinned, and layout moved the view back from the end: follow the end even under the pointer (#4243).
    bool pinCoerced;
    int wheelInputs;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? wheelLapse;
    internal static readonly TimeSpan WheelStart = TimeSpan.FromMilliseconds(250);
    bool ReaderScrolling => scrolling || wheelStarting;

    // The self-test's control-level notch takes the same path as the wheel handler (SelfTest.WheelNotch).
    internal void WheelForSelfTest(int delta) { if (ConversationScroll is { } scroll) { WheelTurned(scroll, delta); } }

    // Turned toward the edge the view is already at, the wheel moves nothing: the pin and the view stay as they are,
    // so a reply streaming in at the end is still followed (#4313). Positive is away from the person (up).
    void WheelTurned(ScrollViewer scroll, int delta)
    {
        if (delta < 0 && scroll.VerticalOffset >= scroll.ScrollableHeight - 0.5 || delta > 0 && scroll.VerticalOffset <= 0.5) { return; }
        WheelInput(towardEnd: delta < 0);
    }

    // The layout, not the reader, left the view one viewport short of an end it had reached, while the wheel or a pan
    // (which released the pin) was bringing it there: the end is still the destination until the reader turns away (#4243).
    bool endLost;

    void WheelInput(bool towardEnd)
    {
        wheelStarting = true;
        var input = ++wheelInputs;
        var lost = endLost && towardEnd;
        ReleasePin();
        endLost = lost;
        wheelLapse?.Stop();
        wheelLapse = DispatcherQueue.CreateTimer();
        wheelLapse.Interval = WheelStart;
        wheelLapse.IsRepeating = false;
        wheelLapse.Tick += (_, _) =>
        {
            if (input != wheelInputs) { return; }
            wheelStarting = false;
            if (endLost) { FollowPinnedEnd(); }
        };
        wheelLapse.Start();
    }

    void EndScrollbarGesture(uint pointer)
    {
        if (scrollbarPointers.Remove(pointer)) { follow.Released(pointer); }
    }
    // A rewatch forgets these presses, so their follow hold goes too: nothing could release it later.
    // The measured view stays: the same session keeps its pin, so its first move back must still release.
    void ResetScrollbarGestures()
    {
        foreach (var pointer in scrollbarPointers) { follow.Released(pointer); }
        scrollbarPointers.Clear();
        (stationaryRemeasure, stationaryChange) = (0, 0);
        stationaryFromEnd = false;
        endLost = false;
    }

    (double Offset, double Extent)? seen;
    double stationaryRemeasure;
    double stationaryChange;
    bool stationaryFromEnd;

    // Layout and stationary view events must not consume the evidence for the next anchored move.
    void RememberStillExtent(ScrollViewer scroll)
    {
        if (seen is { } was && Math.Abs(scroll.VerticalOffset - was.Offset) < 0.5)
        {
            var change = was.Extent - scroll.ScrollableHeight;
            if (Math.Abs(change) >= 0.5)
            {
                stationaryFromEnd |= Math.Abs(was.Offset - was.Extent) <= 1.5;
                stationaryChange = stationaryFromEnd ? change : 0;
            }
            stationaryRemeasure = Math.Max(stationaryRemeasure, Math.Abs(change));
            seen = (was.Offset, scroll.ScrollableHeight);
        }
    }

    // UIA or a screen reader can move the view back from the end with no wheel or press: that is the
    // person's move too, or the final view change pulls it back. Growth or an anchored shrink above
    // can change both the offset and gap while the extent is remeasured or our end correction is pending.
    void PinViewChanged(ScrollViewer scroll, bool final)
    {
        // The notch's own view changes have begun: `scrolling` holds the view from here until its final one.
        wheelStarting = false;
        RememberStillExtent(scroll);
        var (offset, extent) = (scroll.VerticalOffset, scroll.ScrollableHeight);
        // The view was at the end and the extent was remeasured, and the view lands one viewport short of the new end: the
        // list's own landing, whatever the pin (a wheel or pan under way has released it). A reader's move back from the end
        // that is not a remeasure's, or that is a key's or a scrollbar's, is not this. The first view change at the end clears it.
        if (seen is { } at && (at.Offset >= at.Extent - ConversationFollow.EndFollowSlack || stationaryFromEnd) && offset < at.Offset - 8
            && Math.Max(Math.Abs(extent - at.Extent), stationaryRemeasure) > 8 && Math.Abs(extent - offset - scroll.ViewportHeight) <= 8
            && keyTarget is null && scrollbarPointers.Count == 0 && navigationPending is null) { endLost = true; }
        if (offset >= extent - 0.5) { endLost = false; }
        // Native measure/arrange can coerce the offset before returning to the UI dispatcher.
        // Only that synchronous layout owns these moves; reader input still releases directly.
        if (conversationLayoutDepth == 0 && (pinEnd || anchoring is { } pending && Math.Abs(pending - extent) <= ConversationFollow.EndFollowSlack) && seen is { } was && offset < was.Offset - 0.5 && extent - offset > was.Extent - was.Offset + 0.5)
        {
            var (change, drop, gap) = (was.Extent - extent, was.Offset - offset, extent - offset);
            var remeasure = Math.Max(Math.Abs(change), stationaryRemeasure);
            var shrink = Math.Abs(change) >= 0.5 ? change : stationaryChange;
            // A shrink above moves the view by at most the extent delta, and a real remeasure (seen before or
            // with this move) can shift the virtualised anchor to one viewport short of the end. A drop under a
            // text line is layout rounding. Anything else, such as a reader stepping or paging back, is the person's move.
            var layoutOnly = gap <= 1.5 || drop <= 8 || shrink >= 0.5 && drop <= shrink + 1.5
                || Math.Max(remeasure, was.Offset - was.Extent) > 8 && Math.Abs(gap - scroll.ViewportHeight) <= 8;
            if (!layoutOnly && (anchoring is not { } asked || Math.Abs(offset - asked) >= 0.5)) { ReleasePin(); }
        }
        if (anchoring is { } target && Math.Abs(target - extent) <= ConversationFollow.EndFollowSlack && Math.Abs(offset - extent) <= ConversationFollow.EndFollowSlack) { SetEndPin(true); }
        // The person's own scroll (wheel, arrow, key) landing on the very end re-engages the pin (#4243; #1756 case 5),
        // so the last rows realising as the end comes into view are followed, not left above it.
        if (final && !pinEnd && conversationLayoutDepth == 0 && extent > 0 && offset >= extent - 0.5
            && scrollbarPointers.Count == 0 && !panning && navigationPending is null) { SetEndPin(true); }
        // Still pinned after a move back: layout's, not the person's (they would have released it). Undone even
        // under the pointer, as the hover hold is for moving what is read, and this already moved it (#4243).
        if (pinEnd && seen is { } before && offset < before.Offset - 0.5) { pinCoerced = true; }
        if (Math.Abs(offset - extent) <= 0.5) { pinCoerced = false; }
        if (seen is { } moved && Math.Abs(offset - moved.Offset) >= 0.5)
        {
            (stationaryRemeasure, stationaryChange) = (0, 0); stationaryFromEnd = false;
        }
        seen = (offset, extent);
        SyncConversationAnchoring();
    }
    // A final event can belong to a correction queued before this navigation request.
    void CompleteNavigation() { if (navigationPending?.Invoke() == false) { navigationPending = null; } }

    // The person is scrolling: the view is theirs from here, and stays where they leave it.
    internal void ReleasePin([System.Runtime.CompilerServices.CallerMemberName] string by = "", [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        restoreReading = null; pinEnd = false; pinCoerced = false; endLost = false; navigationPending = null; anchoring = null; stationaryRemeasure = stationaryChange = 0; stationaryFromEnd = false; pinReleases++;
        RecordPin(pinReleased, $"{by}:{line} at ({ConversationScroll?.VerticalOffset}, {ConversationScroll?.ScrollableHeight}) seen={seen} layoutDepth={conversationLayoutDepth}");
        SyncConversationAnchoring();
        if (historyFirstFrame is null && history is { } stored && historyVisible < stored.Turns.Count) { historyDraw?.Start(); }
    }

    bool HistoryAtEnd() => navigationPending is null && scrollbarPointers.Count == 0 && (pinEnd || !panning && !ReaderScrolling && keyTarget is null
        && ConversationScroll is { ScrollableHeight: > 0 } scroll && scroll.VerticalOffset >= scroll.ScrollableHeight - ConversationFollow.EndFollowSlack);

    // Older turns can finish measuring after KeepPlace restored an earlier end. Follow the
    // measured end on layout and final view changes, including while an older target is pending.
    void FollowPinnedEnd()
    {
        if (ConversationScroll is { } still) { RememberStillExtent(still); }
        SyncConversationAnchoring();
        if (follow.Held && !pinCoerced && !endLost || scrollbarPointers.Count > 0 || panning || ReaderScrolling && anchoring is null || keyTarget is not null || !ConversationList.IsLoaded || ConversationScroll is not { } scroll) { return; }
        if (ConversationEndPin.Correction(pinEnd || endLost, scroll.VerticalOffset, scroll.ScrollableHeight, anchoring) is { } to) { Anchoring(scroll, to); }
    }

    // After a layout under a still view, the block across the top edge back where it was (ui.md §6.4).
    // Not while the person pans, holds a scrollbar part or a scroll is under way, nor while a correction of ours
    // is on its way: a thumb drag's steps are final view changes, and a ChangeView under it bounced the bar (#1756).
    void KeepAnchoredBlock()
    {
        if (anchoring is not null || anchor is not { } a || ConversationScroll is not { } scroll || !ConversationList.IsLoaded
            || ConversationList.ContainerFromIndex(a.Index) is not FrameworkElement c) { return; }
        // A layout skipped while held was drawn: the person saw the block move. After the release (which may raise no
        // view change), start again from where it is, rather than pull the view back by it (#4243, trackpad).
        // Only a pan or a pressed scrollbar part can end with no view change; a wheel notch or key scroll ends in its own,
        // and a wheel event without one lapses, after which its realisation is still put back (CI 45325080892).
        if (panning || scrollbarPointers.Count > 0) { anchorHeld = true; return; }
        if (ReaderScrolling || keyTarget is not null) { return; }
        if (anchorHeld) { anchorHeld = false; anchor = new(a.Index, Top(c, scroll)); anchorAt = scroll.VerticalOffset; return; }
        if (a.Correction(anchorAt, scroll.VerticalOffset, Top(c, scroll), scroll.ScrollableHeight) is { } to) { Anchoring(scroll, to); }
    }
    // Set when the person held the view through a layout; a fresh anchor (TakeAnchor) clears it.
    bool anchorHeld;

    // Every gate FollowPinnedEnd and KeepPlace consult, for a hosted failure to name the one that held.
    internal string PinState() => $"pinEnd={pinEnd} anchoring={anchoring} scrolling={scrolling} panning={panning} held={follow.Held} keyTarget={keyTarget} bars={scrollbarPointers.Count} loaded={ConversationList.IsLoaded} navigating={navigationPending is not null} restoring={restoreReading is not null} releases={pinReleases} seen={seen} released=[{string.Join(" | ", pinReleased)}] requests=[{string.Join(" | ", pinRequests)}]";

    static void RecordPin(Queue<string> records, string record)
    {
        if (records.Count == 3) { records.Dequeue(); }
        records.Enqueue(record);
    }

    internal bool ChangeConversationView(ScrollViewer scroll, double to, bool disableAnimation = true,
        [System.Runtime.CompilerServices.CallerMemberName] string by = "", [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        RecordPin(pinRequests, $"{by}:{line} at ({scroll.VerticalOffset}, {scroll.ScrollableHeight}) to={to}");
        return scroll.ChangeView(null, to, null, disableAnimation: disableAnimation);
    }

    // Counts the corrections that moved the view (#3782, #3842); OnConversationLaidOut skips them in a storm.
    readonly LayoutStormGuard layoutStorm = new(TimeProvider.System);

    void Anchoring(ScrollViewer scroll, double to)
    {
        layoutStorm.Run();
        anchoring = to;
        anchorAt = to;
        if (!ChangeConversationView(scroll, to)) { anchoring = null; }
    }
}

static class ConversationEndPin
{
    public static double? Correction(bool pinned, double offset, double extent, double? pending) =>
        pinned && Math.Abs(extent - offset) > 0.5
        && (pending is not { } asked || Math.Abs(extent - asked) > 0.5) ? extent : null;
}
