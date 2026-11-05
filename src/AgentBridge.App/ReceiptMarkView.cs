using System.Runtime.CompilerServices;
using AgentBridge.App.Core;
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
        var said = e.NewValue as string ?? "";
        var state = Line(line);
        // A new generation for any change, a cleared or hushed one too: whatever was queued for the
        // line before it is not raised (ReceiptAnnouncer).
        var ticket = state.Announcer.Changed(said, line.IsLoaded, hushed > 0);
        if (said.Length == 0)
        {
            line.ClearValue(AutomationProperties.NameProperty);
            return;
        }
        AutomationProperties.SetLiveSetting(line, GetUrgent(line) ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        AutomationProperties.SetName(line, said);
        // Bound as the line is first drawn, it is what was said before: named, not said again.
        if (ticket is not { } queued) { return; }
        // The peer is made if it is not there yet: a line a screen reader never visited has none,
        // and FromElement would find none, as LaunchView found.
        line.DispatcherQueue.TryEnqueue(() =>
        {
            if (state.Announcer.Raise(queued, line.IsLoaded)) { FrameworkElementAutomationPeer.CreatePeerForElement(line)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged); }
        });
    }

    // The line's colour for its tone (Ui.ReceiptBrush), painted again when the theme or the
    // system's colours change while it is shown. Resolved only as its binding changed, an outcome
    // left on screen across a switch to high contrast kept the normal theme's colour, not the
    // window's text (#625, Copilot 4485379205). An int: a .NET enum does not box into WinRT.
    public static readonly DependencyProperty ToneProperty = DependencyProperty.RegisterAttached(
        "Tone", typeof(int), typeof(ReceiptMarkView), new PropertyMetadata(-1, (d, _) => { if (d is TextBlock line) { Line(line).Ink(); } }));

    public static Tone GetTone(DependencyObject o) => o.GetValue(ToneProperty) is int t && t >= 0 ? (Tone)t : Tone.None;
    public static void SetTone(DependencyObject o, Tone value) => o.SetValue(ToneProperty, (int)value);

    static readonly ConditionalWeakTable<FrameworkElement, LineState> lines = new();

    static LineState Line(FrameworkElement line) => lines.GetValue(line, l => new LineState(l));

    // What a receipt line keeps between its changes: its announcements' generation, and whether
    // it follows the system's colours, only while it is loaded.
    sealed class LineState
    {
        readonly FrameworkElement line;
        public readonly ReceiptAnnouncer Announcer = new();
        bool inked;
        bool listening;

        public LineState(FrameworkElement line)
        {
            this.line = line;
            // Loaded and Unloaded can come in either order when an element is moved.
            line.Loaded += (_, _) => { if (inked) { Listen(); Ink(); } };
            line.Unloaded += (_, _) =>
            {
                if (line.IsLoaded) { return; }
                Announcer.Unloaded();
                Unlisten();
            };
            line.ActualThemeChanged += (_, _) => { if (inked) { Ink(); } };
        }

        public void Ink()
        {
            if (line is not TextBlock text) { return; }
            text.Foreground = Ui.ReceiptBrush(GetTone(text));
            inked = true;
            if (line.IsLoaded) { Listen(); }
        }

        void Listen()
        {
            if (listening) { return; }
            listening = true;
            SystemColours.Changed += OnColours;
        }

        void Unlisten()
        {
            if (!listening) { return; }
            listening = false;
            SystemColours.Changed -= OnColours;
        }

        // Raised off the UI thread; painted after XAML's own handling of the change, at low priority.
        void OnColours() =>
            line.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (line.IsLoaded) { Ink(); } });
    }
}

// Windows' colours changed: high contrast turned on or off, or another contrast theme chosen.
// One UISettings for the app (a UISettings no longer referenced stops raising its events); each
// listener subscribes only while it is on screen.
static class SystemColours
{
    static readonly Windows.UI.ViewManagement.UISettings settings = new();
    static event Action? changed;
    static bool hooked;
    static readonly Lock gate = new();

    public static event Action Changed
    {
        add
        {
            lock (gate)
            {
                changed += value;
                if (!hooked) { hooked = true; settings.ColorValuesChanged += (_, _) => Volatile.Read(ref changed)?.Invoke(); }
            }
        }
        remove { lock (gate) { changed -= value; } }
    }

    public static Windows.UI.Color Of(Windows.UI.ViewManagement.UIElementType element) => settings.UIElementColor(element);
}
