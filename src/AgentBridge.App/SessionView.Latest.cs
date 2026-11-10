using AgentBridge.App.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace AgentBridge.App;

public sealed partial class SessionView
{
    bool latestUsed, latestPinned, latestReturning;
    ScrollViewer? latestScroll;
    ScrollBar? latestBar;

    void InitializeLatest()
    {
        RegisterPropertyChangedCallback(CardProperty, (_, _) => ResetLatest());
        Unloaded += (_, _) => ResetLatest();
        ConversationList.LayoutUpdated += (_, _) =>
        {
            if (ConversationScroll is { } scroll && latestScroll != scroll)
            {
                latestScroll = scroll;
                scroll.ViewChanged += (_, e) =>
                {
                    if (latestUsed && latestReturning && !e.IsIntermediate)
                    {
                        latestReturning = false;
                        if (scroll.ScrollableHeight - scroll.VerticalOffset <= 0.5) { latestPinned = true; pinEnd = true; }
                    }
                    if (latestPinned) { anchor = null; }
                };
                scroll.AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, e) =>
                {
                    var delta = e.GetCurrentPoint(scroll).Properties;
                    if (delta.IsHorizontalMouseWheel) { return; }
                    LatestInput(up: delta.MouseWheelDelta > 0);
                }), true);
                scroll.AddHandler(PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
                {
                    if (KeyUserOf(e.OriginalSource as DependencyObject) != KeyUser.None) { return; }
                    if (e.Key is VirtualKey.Up or VirtualKey.PageUp or VirtualKey.Home) { LatestInput(up: true); }
                    else if (e.Key == VirtualKey.Space) { LatestInput(up: InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)); }
                    else if (e.Key is VirtualKey.Down or VirtualKey.PageDown or VirtualKey.End) { LatestInput(up: false); }
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
                            LatestInput(up: e.ScrollEventType is ScrollEventType.SmallDecrement or ScrollEventType.LargeDecrement or ScrollEventType.First
                                || e.NewValue < hooked.VerticalOffset - 0.5);
                            if (latestUsed && latestReturning && e.NewValue >= bar.Maximum - 0.5)
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
