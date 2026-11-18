// Compile the production pin hooks without loading WinUI or starting a live UI.
namespace Microsoft.UI.Xaml
{
    public class DependencyObject
    {
        public DependencyObject? Parent { get; init; }
    }

    public class UIElement : DependencyObject
    {
        public static readonly object PointerPressedEvent = new();
        public static readonly object PointerWheelChangedEvent = new();
        public bool IsLoaded { get; init; } = true;
        public event Action<UIElement, BringIntoViewRequestedEventArgs>? BringIntoViewRequested;
        public void Bring(UIElement target) => BringIntoViewRequested?.Invoke(this, new(target));
    }

    public sealed class BringIntoViewRequestedEventArgs(UIElement target) : EventArgs
    {
        public UIElement TargetElement => target;
    }
}

namespace Microsoft.UI.Xaml.Input
{
    public sealed class PointerRoutedEventArgs(Xaml.DependencyObject source)
    {
        public object OriginalSource => source;
    }

    public delegate void PointerEventHandler(object sender, PointerRoutedEventArgs args);
}

namespace Microsoft.UI.Xaml.Controls
{
    public sealed class ScrollViewer : UIElement
    {
        readonly List<(object Event, Input.PointerEventHandler Handler, bool HandledToo)> handlers = [];
        public double VerticalOffset { get; set; } = 100;
        public double ScrollableHeight { get; set; } = 200;
        public List<double> Targets { get; } = [];
        public bool AcceptChange { get; set; } = true;

        public void AddHandler(object routedEvent, object handler, bool handledEventsToo) =>
            handlers.Add((routedEvent, (Input.PointerEventHandler)handler, handledEventsToo));

        public void Press(DependencyObject source) => Raise(PointerPressedEvent, source);
        public void Wheel() => Raise(PointerWheelChangedEvent, this);

        void Raise(object routedEvent, DependencyObject source)
        {
            foreach (var hook in handlers.Where(h => h.Event == routedEvent && h.HandledToo))
            {
                hook.Handler(this, new(source));
            }
        }

        public bool ChangeView(double? horizontalOffset, double? verticalOffset, float? zoomFactor, bool disableAnimation)
        {
            if (verticalOffset is { } to) { Targets.Add(to); }
            return AcceptChange;
        }
    }
}

namespace Microsoft.UI.Xaml.Controls.Primitives
{
    public sealed class ScrollBar : UIElement;
}

namespace Microsoft.UI.Xaml.Media
{
    public static class VisualTreeHelper
    {
        public static DependencyObject? GetParent(DependencyObject node) => node.Parent;
    }
}

namespace AgentBridge.App
{
    public sealed partial class SessionView
    {
        readonly Microsoft.UI.Xaml.UIElement ConversationList = new();
        Microsoft.UI.Xaml.Controls.ScrollViewer? ConversationScroll { get; } = new();
        bool pinEnd = true;
        int pinReleases;
        bool panning = false;
        bool scrolling;
        double? anchoring;
        double? keyTarget = null;
        double anchorAt;

        public SessionView() => HookConversationInput(Scroller);
        public Microsoft.UI.Xaml.Controls.ScrollViewer Scroller => ConversationScroll!;
        public bool Pinned => pinEnd;
        public bool IsScrolling => scrolling;
        public int Releases => pinReleases;
        public double AnchorAt => anchorAt;
        public void Follow() => FollowPinnedEnd();
        public void Scrolling(double? correction = null) { scrolling = true; anchoring = correction; }
        public void StopScrolling() => scrolling = false;
    }
}
