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
        scroll.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) => ReleasePin()), true);
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
    void PinViewChanged(ScrollViewer scroll)
    {
        RememberStillExtent(scroll);
        var (offset, extent) = (scroll.VerticalOffset, scroll.ScrollableHeight);
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
        restoreReading = null; pinEnd = false; navigationPending = null; anchoring = null; stationaryRemeasure = stationaryChange = 0; stationaryFromEnd = false; pinReleases++;
        RecordPin(pinReleased, $"{by}:{line} at ({ConversationScroll?.VerticalOffset}, {ConversationScroll?.ScrollableHeight}) seen={seen} layoutDepth={conversationLayoutDepth}");
        SyncConversationAnchoring();
        if (historyFirstFrame is null && history is { } stored && historyVisible < stored.Turns.Count) { historyDraw?.Start(); }
    }

    bool HistoryAtEnd() => navigationPending is null && scrollbarPointers.Count == 0 && (pinEnd || !panning && !scrolling && keyTarget is null
        && ConversationScroll is { ScrollableHeight: > 0 } scroll && scroll.VerticalOffset >= scroll.ScrollableHeight - ConversationFollow.EndFollowSlack);

    // Older turns can finish measuring after KeepPlace restored an earlier end. Follow the
    // measured end on layout and final view changes, including while an older target is pending.
    void FollowPinnedEnd()
    {
        if (ConversationScroll is { } still) { RememberStillExtent(still); }
        SyncConversationAnchoring();
        if (follow.Held || scrollbarPointers.Count > 0 || panning || scrolling && anchoring is null || keyTarget is not null || !ConversationList.IsLoaded || ConversationScroll is not { } scroll) { return; }
        if (ConversationEndPin.Correction(pinEnd, scroll.VerticalOffset, scroll.ScrollableHeight, anchoring) is { } to) { Anchoring(scroll, to); }
    }

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

    void Anchoring(ScrollViewer scroll, double to)
    {
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
