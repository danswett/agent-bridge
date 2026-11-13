using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

public sealed partial class SessionView
{
    bool latestUsed, latestPinned, latestReturning;
    ScrollViewer? latestScroll;
    ScrollBar? latestBar;
    internal string LatestState => $"used={latestUsed}, pinned={latestPinned}, returning={latestReturning}, ordinaryPin={pinEnd}";

    void InitializeLatest()
    {
        Unloaded += (_, _) => ResetLatest();
        ConversationList.LayoutUpdated += (_, _) =>
        {
            if (ConversationScroll is { } scroll && latestScroll != scroll)
            {
                latestScroll = scroll;
                scroll.ViewChanged += (_, e) =>
                {
                    if (latestUsed && latestReturning)
                    {
                        var next = new LatestReturn(latestPinned, latestReturning).Scrolled(e.IsIntermediate, scroll.VerticalOffset, scroll.ScrollableHeight);
                        latestReturning = next.Returning;
                        if (next.Pinned) { latestPinned = true; pinEnd = true; }
                    }
                    if (latestPinned) { anchor = null; }
                };
                scroll.AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, e) =>
                {
                    var delta = e.GetCurrentPoint(scroll).Properties;
                    if (delta.IsHorizontalMouseWheel) { return; }
                    LatestInput(up: delta.MouseWheelDelta > 0);
                }), true);
                scroll.DirectManipulationStarted += (_, _) => LatestInput(up: true);
            }
            if (latestScroll is { } hooked && latestBar is null)
            {
                var nodes = new Queue<DependencyObject>([hooked]);
                while (nodes.TryDequeue(out var node))
                {
                    if (node is ScrollBar { Orientation: Orientation.Vertical } bar)
                    {
                        latestBar = bar;
                        bar.Scroll += (_, e) =>
                        {
                            if (e.ScrollEventType == ScrollEventType.EndScroll) { latestReturning = false; return; }
                            var thumb = e.ScrollEventType is ScrollEventType.ThumbTrack or ScrollEventType.ThumbPosition;
                            LatestInput(up: thumb || e.ScrollEventType is ScrollEventType.SmallDecrement or ScrollEventType.LargeDecrement or ScrollEventType.First
                                || e.NewValue < hooked.VerticalOffset - 0.5);
                            if (latestUsed && (latestReturning || thumb) && e.NewValue >= bar.Maximum - 0.5)
                            { latestPinned = true; pinEnd = true; latestReturning = false; }
                        };
                        break;
                    }
                    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) { nodes.Enqueue(VisualTreeHelper.GetChild(node, i)); }
                }
            }
            PinLatest();
        };
    }

    void ResetLatest() => latestUsed = latestPinned = latestReturning = false;

    void ReleaseFocusPin()
    {
        latestPinned = latestReturning = false;
        ReleasePin();
    }

    void LatestInput(bool up)
    {
        if (!latestUsed) { return; }
        latestReturning = !up;
        if (!up) { return; }
        latestPinned = false;
        anchor = null;
        ReleasePin();
    }

    void OnJumpToLatest(object sender, RoutedEventArgs e)
    {
        latestUsed = latestPinned = true;
        latestReturning = false;
        pinEnd = true;
        anchor = null;
        keyTarget = null;
        PinLatest();
    }

    void PinLatest()
    {
        if (!latestPinned || !IsLoaded || ConversationScroll is not { } scroll) { return; }
        // Explicit pinning must not fight the ordinary reading anchor. Layout and extent growth
        // can ask again, but an offset already at the end never schedules another scroll.
        anchor = null;
        if (Math.Abs(scroll.ScrollableHeight - scroll.VerticalOffset) > 0.5)
        {
            scroll.ChangeView(null, scroll.ScrollableHeight, null, disableAnimation: true);
        }
        anchor = null;
    }
}
