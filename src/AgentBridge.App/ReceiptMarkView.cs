using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace AgentBridge.App;

// What stands beside a reply's or an answer's receipt (ReceiptMark; ui.md §6.5, decided on #617):
// the progress ring while it is on its way, still and in text3 while it waits for a machine that
// isn't connected, or the check badge once delivered: a Bridge disc with the check of the done
// glyph knocked out to the surface behind it. A Path of the design's own geometry
// (design/glyphs/check.svg), not Segoe's CheckMark (the owner's decision on #617), so it reads as
// the PWA's does, and holds in high contrast as Highlight on Window. Drawn only: the words beside
// it say what it means.
public sealed partial class ReceiptMarkView : UserControl
{
    public static readonly DependencyProperty MarkProperty = DependencyProperty.Register(
        nameof(Mark), typeof(ReceiptMark), typeof(ReceiptMarkView), new PropertyMetadata(ReceiptMark.None, (d, _) => ((ReceiptMarkView)d).Apply()));

    // What a screen reader is to hear of a receipt's line (the view model's Announcement): set on
    // the line itself, which is a polite live region, and raised as LiveRegionChanged. The line's
    // name becomes what was said ("Reply delivered"), so the subject goes with it, until its words
    // change: the view model clears it then, and the line is named by its words again, so a reader
    // moving to it hears what it says now, not what an earlier state said.
    public static readonly DependencyProperty AnnouncementProperty = DependencyProperty.RegisterAttached(
        "Announcement", typeof(string), typeof(ReceiptMarkView), new PropertyMetadata("", OnAnnouncement));

    // A view showing another card takes that card's last announcement with its bindings: said
    // already, when it happened, so the line is named by it but it is not said again.
    [ThreadStatic] static int hushed;

    public static void Hushed(Action update)
    {
        hushed++;
        try { update(); }
        finally { hushed--; }
    }

    public static string GetAnnouncement(DependencyObject o) => (string)o.GetValue(AnnouncementProperty);
    public static void SetAnnouncement(DependencyObject o, string value) => o.SetValue(AnnouncementProperty, value);

    readonly InFlightRing ring = new();
    readonly Grid check = new() { Width = 16, Height = 16 };

    public ReceiptMarkView()
    {
        IsTabStop = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        // The disc, r 6.6 on the 16 grid, and the check across it in the surface's colour.
        check.Children.Add(new Ellipse { Width = 13.2, Height = 13.2, Fill = Ui.Brush("AbBridge") });
        // M5.3 8.2 l1.9 1.9 3.6-3.8, as the glyph draws it.
        var tick = new PathFigure { StartPoint = new Windows.Foundation.Point(5.3, 8.2), IsClosed = false, IsFilled = false };
        tick.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(7.2, 10.1) });
        tick.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(10.8, 6.3) });
        var geometry = new PathGeometry();
        geometry.Figures.Add(tick);
        check.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry,
            Stroke = Ui.Brush("AbLayer"),
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        });
        var root = new Grid { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        root.Children.Add(ring);
        root.Children.Add(check);
        Content = root;
        Apply();
    }

    public ReceiptMark Mark
    {
        get => (ReceiptMark)GetValue(MarkProperty);
        set => SetValue(MarkProperty, value);
    }

    void Apply()
    {
        Visibility = Ui.Show(Mark != ReceiptMark.None);
        ring.Active = Mark is ReceiptMark.Ring or ReceiptMark.Still;
        ring.Still = Mark == ReceiptMark.Still;
        check.Visibility = Ui.Show(Mark == ReceiptMark.Check);
    }

    static void OnAnnouncement(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement line) { return; }
        if (e.NewValue is not string { Length: > 0 } said)
        {
            line.ClearValue(AutomationProperties.NameProperty);
            return;
        }
        AutomationProperties.SetLiveSetting(line, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(line, said);
        // Bound as the line is first drawn, it is what was said before: named, not said again.
        if (!line.IsLoaded || hushed > 0) { return; }
        // The peer is made if it is not there yet: a line a screen reader never visited has none,
        // and FromElement would find none, as LaunchView found.
        line.DispatcherQueue.TryEnqueue(() => FrameworkElementAutomationPeer.CreatePeerForElement(line)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged));
    }
}
