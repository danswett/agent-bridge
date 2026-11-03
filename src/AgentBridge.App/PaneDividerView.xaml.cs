using AgentBridge.App.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AgentBridge.App;

// What a divider resizes: the Sessions page, which keeps the widths (PaneWidths) and lays the panes
// out by them.
interface IPaneSizer
{
    // The divider's pane's width now, and how far it can go.
    (double Value, double Min, double Max) Span(PaneDivider divider);
    void Set(PaneDivider divider, double width);
    void Drag(PaneDivider divider, double from, double dx);
    void Nudge(PaneDivider divider, int direction);
    void Reset(PaneDivider divider);
    // The drag over: the width kept for the next start.
    void Keep();
}

// A divider between two panes (ui.md §5.2, #297): dragged with a mouse, pen or finger, moved a step
// by the arrow keys, and double-clicked back to the pane's default. Assistive technology sees it as
// a splitter whose value is its pane's width.
public sealed partial class PaneDividerView : UserControl
{
    bool over;
    bool dragging;
    double startX;
    double from;
    uint? pointer;

    public PaneDividerView()
    {
        InitializeComponent();
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        GotFocus += (_, _) => Show();
        LostFocus += (_, _) => Show();
    }

    // The last press, to tell a double-click: counted here rather than from DoubleTapped, which a
    // press taken for a drag (handled, the pointer captured) need not raise.
    ulong lastPress;
    double lastX = double.NaN;

    public PaneDivider Divider { get; set; }
    internal IPaneSizer? Sizer { get; set; }

    // For the self-test: the resize cursor, and which line shows.
    internal bool HasResizeCursor => ProtectedCursor is InputSystemCursor { CursorShape: InputSystemCursorShape.SizeWestEast };
    internal bool ShowsHover => HoverLine.Visibility == Visibility.Visible;
    internal bool ShowsActive => ActiveLine.Visibility == Visibility.Visible;

    // For the self-test: the lines' brushes, from the design's.
    internal Microsoft.UI.Xaml.Media.Brush HoverBrush => HoverLine.Fill;
    internal Microsoft.UI.Xaml.Media.Brush ActiveBrush => ActiveLine.Fill;

    // Pointed at: AbStroke2. Dragged, or focused from the keyboard: Bridge, as focus is elsewhere.
    void Show()
    {
        var active = dragging || FocusState == FocusState.Keyboard;
        ActiveLine.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        HoverLine.Visibility = over && !active ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        over = true;
        Show();
    }

    protected override void OnPointerExited(PointerRoutedEventArgs e)
    {
        over = false;
        Show();
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(null);
        if (point.PointerDeviceType == PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed) { return; }
        e.Handled = true;
        var x = point.Position.X;
        // Timestamps are in microseconds; GetDoubleClickTime in milliseconds.
        var twice = point.Timestamp - lastPress <= GetDoubleClickTime() * 1000UL && Math.Abs(x - lastX) <= 4;
        lastPress = point.Timestamp;
        lastX = x;
        if (twice)
        {
            lastX = double.NaN;
            Reset();
            return;
        }
        if (!CapturePointer(e.Pointer)) { return; }
        pointer = e.Pointer.PointerId;
        Begin(x);
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial uint GetDoubleClickTime();

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (!dragging || e.Pointer.PointerId != pointer) { return; }
        e.Handled = true;
        Move(e.GetCurrentPoint(null).Position.X);
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != pointer) { return; }
        e.Handled = true;
        ReleasePointerCapture(e.Pointer);
        End();
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e) => End();

    // A drag, by the pointer's x in the window: measured from where it was pressed, so the divider
    // stays under the pointer however its panes are laid out meanwhile.
    internal void Begin(double x)
    {
        if (Sizer is null) { return; }
        dragging = true;
        startX = x;
        from = Sizer.Span(Divider).Value;
        Show();
    }

    internal void Move(double x)
    {
        if (!dragging || Sizer is null) { return; }
        var before = Sizer.Span(Divider).Value;
        Sizer.Drag(Divider, from, x - startX);
        Announce(before);
    }

    internal void End()
    {
        pointer = null;
        if (!dragging) { return; }
        dragging = false;
        Sizer?.Keep();
        Show();
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (Press(e.Key)) { e.Handled = true; return; }
        base.OnKeyDown(e);
    }

    // Left and Right move the divider a step, as a drag would.
    internal bool Press(VirtualKey key)
    {
        if (Sizer is null || key is not (VirtualKey.Left or VirtualKey.Right)) { return false; }
        var before = Sizer.Span(Divider).Value;
        Sizer.Nudge(Divider, key == VirtualKey.Right ? 1 : -1);
        Announce(before);
        return true;
    }

    internal void Reset()
    {
        if (Sizer is null) { return; }
        var before = Sizer.Span(Divider).Value;
        Sizer.Reset(Divider);
        Announce(before);
    }

    void Announce(double before)
    {
        if (Sizer is null || FrameworkElementAutomationPeer.FromElement(this) is not { } peer) { return; }
        var now = Sizer.Span(Divider).Value;
        if (now != before) { peer.RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, before, now); }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    // A splitter: its value the pane's width in pixels, set by a screen reader as by a drag.
    sealed partial class Peer(PaneDividerView owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Thumb;
        protected override string GetLocalizedControlTypeCore() => "splitter";
        protected override string GetClassNameCore() => nameof(PaneDividerView);
        protected override object? GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.RangeValue ? this : base.GetPatternCore(patternInterface);

        (double Value, double Min, double Max) Span => owner.Sizer?.Span(owner.Divider) ?? (0, 0, 0);
        public double Value => Span.Value;
        public double Minimum => Span.Min;
        public double Maximum => Span.Max;
        public double SmallChange => PaneWidths.Step;
        public double LargeChange => PaneWidths.Step * 4;
        public bool IsReadOnly => owner.Sizer is null;

        public void SetValue(double value)
        {
            if (owner.Sizer is not { } sizer) { return; }
            var before = sizer.Span(owner.Divider).Value;
            sizer.Set(owner.Divider, value);
            sizer.Keep();
            owner.Announce(before);
        }
    }
}
