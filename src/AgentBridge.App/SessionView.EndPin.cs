using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

public sealed partial class SessionView
{
    readonly HashSet<uint> scrollbarPointers = [];

    void HookConversationInput(ScrollViewer scroll)
    {
        // Question navigation and UIA ScrollIntoView own their destination, even without focus.
        scroll.BringIntoViewRequested += (_, _) => ReleasePin();
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
        scroll.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, e) =>
        {
            if (!e.Pointer.IsInContact) { EndScrollbarGesture(e.Pointer.PointerId); }
        }), true);
    }

    void EndScrollbarGesture(uint pointer) => scrollbarPointers.Remove(pointer);
    void ResetScrollbarGestures() => scrollbarPointers.Clear();

    // The person is scrolling: the view is theirs from here, and stays where they leave it.
    internal void ReleasePin() { pinEnd = false; anchoring = null; pinReleases++; }

    bool HistoryAtEnd() => scrollbarPointers.Count == 0 && (pinEnd || !panning && !scrolling && keyTarget is null
        && ConversationScroll is { ScrollableHeight: > 0 } scroll && scroll.VerticalOffset >= scroll.ScrollableHeight - ConversationFollow.Slack);

    // Older turns can finish measuring after KeepPlace restored an earlier end. Follow the
    // measured end on layout and final view changes, including while an older target is pending.
    void FollowPinnedEnd()
    {
        if (scrollbarPointers.Count > 0 || panning || scrolling && anchoring is null || keyTarget is not null || !ConversationList.IsLoaded || ConversationScroll is not { } scroll) { return; }
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
