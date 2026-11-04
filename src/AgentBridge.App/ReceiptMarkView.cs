using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

// What stands beside a reply's or an answer's receipt (ReceiptMark; ui.md §6.5, decided on #617):
// the progress ring while it is on its way, still and in text3 while it waits for a machine that
// isn't connected, or the check badge once delivered: a Bridge disc with the check cut out of it,
// a hole that shows whatever surface is behind (a card, the reply box, a contrast theme's Window).
// A Path of the design's own geometry (design/icons/check.svg, #617), not Segoe's CheckMark (the
// owner's decision), so it reads as the PWA's does, and holds in high contrast as Highlight with
// the check knocked out. Drawn only: the words beside it say what it means.
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

    // design/icons/check.svg on its 20 px box: the disc, then the check as a second figure, which
    // the even-odd fill ("F0") leaves as a hole rather than painting it in a colour of its own,
    // which would show as a patch on any surface but the one it was picked for.
    const string CheckBadge = "F0 M18 10A8 8 0 0 1 2 10A8 8 0 0 1 18 10Z" +
        "M5.463 11.08L8.091 13.709A1.143 1.143 0 0 0 9.73 13.686L14.687 8.443A1.143 1.143 0 0 0 13.027 6.871L8.877 11.261L7.08 9.463A1.143 1.143 0 0 0 5.463 11.08Z";

    readonly InFlightRing ring = new();
    readonly Viewbox check = new() { Width = 16, Height = 16 };

    public ReceiptMarkView()
    {
        IsTabStop = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        // The icon's 20 px box scaled to 16, its margin and all, as the PWA's sprite is drawn.
        var box = new Grid { Width = 20, Height = 20 };
        box.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), CheckBadge),
            Fill = Ui.Brush("AbBridge"),
        });
        check.Child = box;
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
