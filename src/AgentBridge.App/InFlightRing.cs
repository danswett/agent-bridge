using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

// The platform's progress ring on a button while the request it sent is on its way (ui.md §5.3,
// Send and Answer): shown and turning while Active, and gone once the request is. It is the only
// loop besides the status glyphs, so it turns only with Windows' animation effects on, and follows
// that setting while on screen, as StatusGlyphView does: off, it is a still arc. Drawn only: the
// button's own name and status line say what is happening.
public sealed partial class InFlightRing : UserControl
{
    public static readonly DependencyProperty ActiveProperty = DependencyProperty.Register(
        nameof(Active), typeof(bool), typeof(InFlightRing), new PropertyMetadata(false, (d, _) => ((InFlightRing)d).Apply()));

    // Still: a request held on this device for a machine that isn't connected (Send's ring while its
    // receipt says "Waiting for the machine", ui.md §6.5): nothing is moving, so the arc stays still, in text3.
    public static readonly DependencyProperty StillProperty = DependencyProperty.Register(
        nameof(Still), typeof(bool), typeof(InFlightRing), new PropertyMetadata(false, (d, _) => ((InFlightRing)d).Apply()));

    readonly ProgressRing ring = new() { Width = 16, Height = 16, IsActive = false };
    readonly Windows.UI.ViewManagement.UISettings settings = new();
    bool listening;
    Microsoft.UI.Xaml.Controls.Primitives.ButtonBase? owner;

    public InFlightRing()
    {
        // A three-quarter arc when still: a determinate ring at 75 of its 100.
        ring.Value = 75;
        Content = ring;
        IsTabStop = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        // The ring's own peer too: Raw on this control doesn't hide its children, and the still
        // arc's 75 would be read as real progress.
        AutomationProperties.SetAccessibilityView(ring, AccessibilityView.Raw);
        Loaded += (_, _) => { Listen(); Apply(); };
        Unloaded += (_, _) => { if (!IsLoaded) { Unlisten(); } };
        // A held ring's colour follows a theme or contrast switch made while it is shown: read once,
        // it kept the last palette's text3 or GrayText (#625, Copilot 4485379205).
        ActualThemeChanged += (_, _) => Apply();
        Apply();
    }

    public bool Active
    {
        get => (bool)GetValue(ActiveProperty);
        set => SetValue(ActiveProperty, value);
    }

    public bool Still
    {
        get => (bool)GetValue(StillProperty);
        set => SetValue(StillProperty, value);
    }

    void Apply()
    {
        Visibility = Ui.Show(Active);
        ring.IsIndeterminate = Active && !Still && MotionCatalogue.Loops(settings.AnimationsEnabled);
        ring.IsActive = Active;
        // In a contrast theme, GrayText (ui.md §9): a held request never looks active.
        if (Still) { ring.Foreground = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast ? new Microsoft.UI.Xaml.Media.SolidColorBrush(SystemColours.Of(Windows.UI.ViewManagement.UIElementType.GrayText)) : Ui.Brush("AbText3"); }
        // Otherwise the owning button's ink: the platform's own accent ring was the accent button's
        // fill, so on Send it drew teal on teal and wasn't seen. An enabled button's own Foreground,
        // not the presenter's inherited one (#87FFFFFF on dark's #2DD4BF fill); a disabled one's
        // (Approve and Deny while their answer goes) the dimmed ink its label takes, on its dimmed fill.
        else { ring.SetBinding(Control.ForegroundProperty, new Microsoft.UI.Xaml.Data.Binding { Source = owner is { IsEnabled: true } ? owner : this, Path = new PropertyPath(nameof(Foreground)) }); }
    }

    Microsoft.UI.Xaml.Controls.Primitives.ButtonBase? OwningButton()
    {
        for (var e = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(this); e is not null; e = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(e))
        {
            if (e is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase b) { return b; }
        }
        return null;
    }

    void Listen()
    {
        if (listening) { return; }
        listening = true;
        owner = OwningButton();
        if (owner is not null) { owner.IsEnabledChanged += OnOwnerEnabledChanged; }
        settings.AnimationsEnabledChanged += OnAnimationsChanged;
        SystemColours.Changed += OnColoursChanged;
    }

    void Unlisten()
    {
        if (!listening) { return; }
        listening = false;
        if (owner is not null) { owner.IsEnabledChanged -= OnOwnerEnabledChanged; owner = null; }
        settings.AnimationsEnabledChanged -= OnAnimationsChanged;
        SystemColours.Changed -= OnColoursChanged;
    }

    void OnOwnerEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) => Apply();

    // Raised off the UI thread.
    void OnAnimationsChanged(Windows.UI.ViewManagement.UISettings sender, Windows.UI.ViewManagement.UISettingsAnimationsEnabledChangedEventArgs args) =>
        DispatcherQueue.TryEnqueue(() => { if (IsLoaded) { Apply(); } });

    // Raised off the UI thread too; applied after XAML's own handling of the change.
    void OnColoursChanged() =>
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (IsLoaded) { Apply(); } });
}
