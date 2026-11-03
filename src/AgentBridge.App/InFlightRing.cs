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

    readonly ProgressRing ring = new() { Width = 16, Height = 16, IsActive = false };
    readonly Windows.UI.ViewManagement.UISettings settings = new();
    bool listening;

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
        Apply();
    }

    public bool Active
    {
        get => (bool)GetValue(ActiveProperty);
        set => SetValue(ActiveProperty, value);
    }

    void Apply()
    {
        Visibility = Ui.Show(Active);
        ring.IsIndeterminate = Active && MotionCatalogue.Loops(settings.AnimationsEnabled);
        ring.IsActive = Active;
    }

    void Listen()
    {
        if (listening) { return; }
        listening = true;
        settings.AnimationsEnabledChanged += OnAnimationsChanged;
    }

    void Unlisten()
    {
        if (!listening) { return; }
        listening = false;
        settings.AnimationsEnabledChanged -= OnAnimationsChanged;
    }

    // Raised off the UI thread.
    void OnAnimationsChanged(Windows.UI.ViewManagement.UISettings sender, Windows.UI.ViewManagement.UISettingsAnimationsEnabledChangedEventArgs args) =>
        DispatcherQueue.TryEnqueue(() => { if (IsLoaded) { Apply(); } });
}
