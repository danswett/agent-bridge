using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

// A reply's or an answer's receipt line as a screen reader hears it (ui.md §6.5, decided on #617).
// Nothing is drawn beside the line any more ("quiet unless something's wrong", c362e0c8): its ring
// is Send's own (InFlightRing) and its check Send's (CheckGlyph). What is left is the line's live
// region, which hears every stage, the quiet ones included, though the line shows only exceptions.
public sealed partial class ReceiptMarkView : UserControl
{    // What a screen reader is to hear of a receipt's line (the view model's Announcement): set on
    // the line itself, a live region (polite, or assertive for a failure: Urgent), and raised as
    // LiveRegionChanged. The line's name becomes what was said ("Reply delivered"), so the subject
    // goes with it, until its words change: the view model clears it then, and the line is named by its words again, so a reader
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

    // Whether the announcement is a failure or a refusal (the view model's AnnouncementUrgent), set
    // before it: those need the person to act and are said assertively (ui.md §9.1 A6). Every
    // announcement was made polite here, failures too (#625, Copilot 4478422096).
    public static readonly DependencyProperty UrgentProperty = DependencyProperty.RegisterAttached(
        "Urgent", typeof(bool), typeof(ReceiptMarkView), new PropertyMetadata(false));

    public static bool GetUrgent(DependencyObject o) => (bool)o.GetValue(UrgentProperty);
    public static void SetUrgent(DependencyObject o, bool value) => o.SetValue(UrgentProperty, value);

    static void OnAnnouncement(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement line) { return; }
        if (e.NewValue is not string { Length: > 0 } said)
        {
            line.ClearValue(AutomationProperties.NameProperty);
            return;
        }
        AutomationProperties.SetLiveSetting(line, GetUrgent(line) ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        AutomationProperties.SetName(line, said);
        // Bound as the line is first drawn, it is what was said before: named, not said again.
        if (!line.IsLoaded || hushed > 0) { return; }
        // The peer is made if it is not there yet: a line a screen reader never visited has none,
        // and FromElement would find none, as LaunchView found.
        line.DispatcherQueue.TryEnqueue(() => FrameworkElementAutomationPeer.CreatePeerForElement(line)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged));
    }
}
