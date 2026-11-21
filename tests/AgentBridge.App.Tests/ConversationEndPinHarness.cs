// Compile the production pin hooks without loading WinUI or starting a live UI.
namespace Windows.Foundation
{
    public readonly record struct Point(double X, double Y);
}

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
        public static readonly object PointerReleasedEvent = new();
        public static readonly object PointerCanceledEvent = new();
        public static readonly object PointerCaptureLostEvent = new();
        public bool IsLoaded { get; init; } = true;
        public double Top { get; init; } = -100;
        public double Height { get; init; } = 50;
        double? broughtAt;
        public Media.Transform TransformToVisual(UIElement viewport) =>
            new(Top + (broughtAt is { } at && viewport is Controls.ScrollViewer scroll ? at - scroll.VerticalOffset : 0));
        public event Action<UIElement, BringIntoViewRequestedEventArgs>? BringIntoViewRequested;
        public void Bring(UIElement target, double alignment = double.NaN, double offset = 0)
        {
            target.broughtAt = (this as Controls.ScrollViewer)?.VerticalOffset;
            BringIntoViewRequested?.Invoke(this, new(target) { VerticalAlignmentRatio = alignment, VerticalOffset = offset });
        }
    }

    public sealed class BringIntoViewRequestedEventArgs(UIElement target) : EventArgs
    {
        public UIElement TargetElement => target;
        public (double X, double Y, double Height) TargetRect => (0, 0, target.Height);
        public double VerticalAlignmentRatio { get; init; } = double.NaN;
        public double VerticalOffset { get; init; }
    }

    public enum Visibility { Visible, Collapsed }
    public sealed class RoutedEventArgs : EventArgs;

    public class FrameworkElement : UIElement
    {
        public double ActualHeight => Height;
        public string Text { get; set; } = "";
        public object? Content { get; set; }
        public Visibility Visibility { get; set; }
        public Action? Layout { get; init; }
        public void UpdateLayout() => Layout?.Invoke();
    }
}

namespace Microsoft.UI.Dispatching
{
    public enum DispatcherQueuePriority { Low }

    public sealed class DispatcherQueue
    {
        readonly Queue<Action> pending = [];
        public bool TryEnqueue(Action action) { pending.Enqueue(action); return true; }
        public bool TryEnqueue(DispatcherQueuePriority priority, Action action) => TryEnqueue(action);
        public DispatcherQueueTimer CreateTimer() => new();
        public void Drain() { while (pending.TryDequeue(out var action)) { action(); } }
    }

    public sealed class DispatcherQueueTimer
    {
        public TimeSpan Interval { get; set; }
        public bool IsRunning { get; private set; }
        public event EventHandler<object> Tick { add { } remove { } }
        public void Start() => IsRunning = true;
        public void Stop() => IsRunning = false;
    }
}

namespace Microsoft.UI.Xaml.Input
{
    public sealed record Pointer(uint PointerId, bool IsInContact);

    public sealed class PointerRoutedEventArgs(Xaml.DependencyObject source, uint pointer = 1, bool contact = false)
    {
        public object OriginalSource => source;
        public Pointer Pointer { get; } = new(pointer, contact);
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
        public double ViewportHeight { get; set; } = 100;
        public List<double> Targets { get; } = [];
        public bool AcceptChange { get; set; } = true;

        public void AddHandler(object routedEvent, object handler, bool handledEventsToo) =>
            handlers.Add((routedEvent, (Input.PointerEventHandler)handler, handledEventsToo));

        public void Press(DependencyObject source, uint pointer = 1) => Raise(PointerPressedEvent, source, pointer, true);
        public void Wheel() => Raise(PointerWheelChangedEvent, this);
        public void EndGesture(string end, uint pointer = 1, bool contact = false) =>
            Raise(end switch { "release" => PointerReleasedEvent, "cancel" => PointerCanceledEvent, _ => PointerCaptureLostEvent }, this, pointer, contact);

        void Raise(object routedEvent, DependencyObject source, uint pointer = 1, bool contact = false)
        {
            foreach (var hook in handlers.Where(h => h.Event == routedEvent && h.HandledToo))
            {
                hook.Handler(this, new(source, pointer, contact));
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
    public static class CompositionTarget
    {
        public static event EventHandler<object> Rendering { add { } remove { } }
    }

    public sealed class Transform(double top)
    {
        public Windows.Foundation.Point TransformPoint(Windows.Foundation.Point point) => new(point.X, point.Y + top);
    }

    public static class VisualTreeHelper
    {
        public static DependencyObject? GetParent(DependencyObject node) => node.Parent;
    }
}

namespace AgentBridge.App
{
    public sealed partial class SessionView
    {
        readonly Core.ConversationFollow follow = new();
        readonly ProbeList ConversationList = new();
        readonly bool attached = true;
        readonly List<object> blocks = [];
        Microsoft.UI.Xaml.Controls.ScrollViewer? ConversationScroll { get; } = new();
        bool panning = false;
        bool scrolling;
        double? anchoring;
        double? keyTarget = null;
        double anchorAt;
        Core.ScrollAnchor? anchor;
        readonly Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue = new();
        readonly Microsoft.UI.Xaml.FrameworkElement HistoryLine = new(), HistoryRetry = new(), HistoryEarlier = new();
        readonly ProbeRows HistoryRows = new();
        readonly Microsoft.UI.Xaml.FrameworkElement HistoryColumn;
        ProbeCard? Card;
        bool IsLoaded => true;

        public SessionView()
        {
            pinEnd = true;
            HistoryColumn = new() { Layout = () => Scroller.ScrollableHeight = 100 + historyShown.Count * 100 };
            HookConversationInput(Scroller);
        }
        public Microsoft.UI.Xaml.Controls.ScrollViewer Scroller => ConversationScroll!;
        public bool Pinned => pinEnd;
        public bool IsScrolling => scrolling;
        public int Releases => pinReleases;
        public double AnchorAt => anchorAt;
        public bool HistoryEnd => HistoryAtEnd();
        public Core.ConversationFollow FollowState => follow;
        public void ResetSessionForProbe() => ResetScrollbarGestures();
        public void Follow() => FollowPinnedEnd();
        public void Moved(double offset) { Scroller.VerticalOffset = offset; PinViewChanged(Scroller); anchoring = null; }
        public void Scrolling(double? correction = null) { scrolling = true; anchoring = correction; }
        public void StopScrolling() { scrolling = false; CompleteNavigation(); }
        public bool HasAnchor => anchor is not null;
        public int ShownCount => historyShown.Count;
        public bool HistoryDrawing => historyDraw?.IsRunning == true;
        public void FirstFrameForProbe() => historyFirstFrame?.Invoke(this, new());
        public void DrawEarlierForProbe() => DrawEarlier();
        public void ProgrammaticBringForProbe(Microsoft.UI.Xaml.UIElement target, double alignment) => ScrollProgrammatically(() => Scroller.Bring(target, alignment));
        public void AnchorForProbe(double to) => Anchoring(Scroller, to);
        public void DrainForProbe() => DispatcherQueue.Drain();
        public void KeepPlaceForProbe() => KeepPlace(false, Scroller.VerticalOffset);
        public void GestureForProbe(string gesture)
        {
            panning = gesture == "pan";
            scrolling = gesture == "scroll";
            keyTarget = gesture == "key" ? 50 : null;
        }
        public void DetachForProbe() => UnwatchHistory();
        public void SwitchForProbe()
        {
            Card = new("next", Card!.Stored);
            follow.Reset();
            WatchHistory();
            Scroller.VerticalOffset = 0;
        }
        public void PrepareHistoryForProbe()
        {
            var connection = new Core.HostConnection(
                _ => Task.FromException<AgentBridge.Local.LocalChannel>(new InvalidOperationException("Offline probe cannot connect.")),
                new LockedDispatcher());
            var stored = new Core.SessionHistory(connection, "probe");
            stored.AddLive(new() { Id = "older", At = default, Prompt = new() { Text = "Older prompt" } });
            stored.AddLive(new() { Id = "newer", At = default, Prompt = new() { Text = "Newer prompt" } });
            Card = new("probe", stored);
            Scroller.VerticalOffset = Scroller.ScrollableHeight;
            WatchHistory();
            DrainForProbe();
            Scroller.Targets.Clear();
        }
        static double Top(Microsoft.UI.Xaml.UIElement element, Microsoft.UI.Xaml.UIElement viewport) => element.Top;
        sealed record ProbeCard(string Id, Core.SessionHistory? Stored, string? UserMessage = null)
        {
            public ProbeRow? Row { get; init; }
            public Protocol.Session Session { get; set; } = Make.Session(Id);
            public string? LiveTurn => Session.Activity.Turn;
            public bool ResponseTruncated => Session.Activity.ResponseTruncated == true;
        }
        sealed record ProbeRow(string Key);
        sealed class ProbeList : Microsoft.UI.Xaml.FrameworkElement
        {
            public bool Available = true;
            public object? ContainerFromIndex(int index) => Available ? new Microsoft.UI.Xaml.FrameworkElement { Top = 40 } : null;
            public void ScrollIntoView(object item) => Scrolled?.Invoke();
            public Action? Scrolled { get; set; }
        }
        sealed class ProbeRows
        {
            public Microsoft.UI.Xaml.UIElement? TryGetElement(int index) => null;
            public Microsoft.UI.Xaml.UIElement? GetOrCreateElement(int index) => null;
        }
    }

    static class Ui
    {
        public static Microsoft.UI.Xaml.Visibility Show(bool visible) =>
            visible ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    }
}
