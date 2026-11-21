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
    }

    (double Offset, double Extent)? seen;

    // UIA or a screen reader can move the view back from the end with no wheel or press: that is the
    // person's move too, or the final view change pulls it back. Growth or an anchored shrink above
    // can change both the offset and gap while the extent is remeasured or our end correction is pending.
    void PinViewChanged(ScrollViewer scroll)
    {
        var (offset, extent) = (scroll.VerticalOffset, scroll.ScrollableHeight);
        if (pinEnd && seen is { } was && offset < was.Offset - 0.5 && extent - offset > was.Extent - was.Offset + 0.5)
        {
            var remeasure = Math.Abs(extent - was.Extent);
            // A remeasure can shift the virtualised anchor by the extent delta plus one viewport.
            // A queued end correction keeps that residual exempt even if its target remeasures again.
            var layoutOnly = Math.Abs(extent - offset) <= 1.5
                || remeasure >= 0.5 && Math.Abs(offset - was.Offset) <= remeasure + scroll.ViewportHeight + 1.5
                || anchoring is not null && extent - offset <= scroll.ViewportHeight + 1.5;
            if (!layoutOnly && (anchoring is not { } asked || Math.Abs(offset - asked) >= 0.5)) { ReleasePin(); }
        }
        seen = (offset, extent);
    }
    // A final event can belong to a correction queued before this navigation request.
    void CompleteNavigation() { if (navigationPending?.Invoke() == false) { navigationPending = null; } }

    // The person is scrolling: the view is theirs from here, and stays where they leave it.
    internal void ReleasePin()
    {
        restoreReading = null; pinEnd = false; navigationPending = null; anchoring = null; pinReleases++;
        if (historyFirstFrame is null && history is { } stored && historyVisible < stored.Turns.Count) { historyDraw?.Start(); }
    }

    bool HistoryAtEnd() => navigationPending is null && scrollbarPointers.Count == 0 && (pinEnd || !panning && !scrolling && keyTarget is null
        && ConversationScroll is { ScrollableHeight: > 0 } scroll && scroll.VerticalOffset >= scroll.ScrollableHeight - ConversationFollow.Slack);

    // Older turns can finish measuring after KeepPlace restored an earlier end. Follow the
    // measured end on layout and final view changes, including while an older target is pending.
    void FollowPinnedEnd()
    {
        // Growth under a still view, measured so a later anchored shrink above is not read as a move.
        if (ConversationScroll is { } still && seen is { } was && Math.Abs(still.VerticalOffset - was.Offset) < 0.5) { seen = (was.Offset, still.ScrollableHeight); }
        if (follow.Held || scrollbarPointers.Count > 0 || panning || scrolling && anchoring is null || keyTarget is not null || !ConversationList.IsLoaded || ConversationScroll is not { } scroll) { return; }
        if (ConversationEndPin.Correction(pinEnd, scroll.VerticalOffset, scroll.ScrollableHeight, anchoring) is { } to) { Anchoring(scroll, to); }
    }

    void Anchoring(ScrollViewer scroll, double to)
    {
        anchoring = to;
        anchorAt = to;
        if (!scroll.ChangeView(null, to, null, disableAnimation: true)) { anchoring = null; }
    }
}

static class ConversationEndPin
{
    public static double? Correction(bool pinned, double offset, double extent, double? pending) =>
        pinned && Math.Abs(extent - offset) > 0.5
        && (pending is not { } asked || Math.Abs(extent - asked) > 0.5) ? extent : null;
}
