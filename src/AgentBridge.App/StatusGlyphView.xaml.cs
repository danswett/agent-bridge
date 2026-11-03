using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace AgentBridge.App;

// A status glyph (pwa/js/ui/glyph.js): one of the generated AbGlyph* styles at 12, 14, 16, 20
// or 24 px, in its role's colour, with the driven diamond over it while an agent drives. The
// moving part (the template part Spin or Orbit) turns once per AbMotionGlyphWorkingPeriod or
// AbMotionGlyphDelegatingPeriod, and holds still
// when Windows has animations turned off. Named for its state when Label is set (ui.md §9),
// otherwise hidden from assistive technology as the words beside it say it.
public sealed partial class StatusGlyphView : UserControl
{
    static readonly double[] Sizes = [12, 14, 16, 20, 24];

    StatusGlyph glyph = StatusGlyph.Idle;
    bool driven;
    double size = 16;
    string label = "";
    Storyboard? turning;
    TimeSpan turningPeriod;
    // A cross-fade begun in this pass of the dispatcher.
    bool fading;
    string? role;
    // Windows' animation effects, followed while on screen: turned off with a glyph turning, it
    // stops; turned back on, it turns again (ui.md §9).
    readonly Windows.UI.ViewManagement.UISettings settings = new();
    bool listening;

    public StatusGlyphView()
    {
        InitializeComponent();
        Loaded += (_, _) => { Listen(); role = null; Apply(); };
        // Loaded and Unloaded can come in either order when a control is moved (SessionRow).
        Unloaded += (_, _) => { if (!IsLoaded) { Unlisten(); Stop(); } };
        // The setters' ThemeResources are read as a state is entered: entered again, they are the
        // new theme's.
        ActualThemeChanged += (_, _) => { role = null; Apply(); };
        Apply();
    }

    public StatusGlyph Glyph { get => glyph; set { if (glyph != value) { glyph = value; Apply(); } } }
    public bool Driven { get => driven; set { if (driven != value) { driven = value; Apply(); } } }
    public double Size { get => size; set { var s = Sizes.Contains(value) ? value : 16; if (size != s) { size = s; Apply(); } } }
    public string Label { get => label; set { if (label != (value ?? "")) { label = value ?? ""; Apply(); } } }
    // A status glyph whose state changes cross-fades into the new one (ui.md §5.3, Status glyph):
    // a row's and the session header's. A tool's glyph changes at once.
    public bool Fades { get; set; }

    // The host gave it another session to show (a recycled row, another session opened): the
    // change is not a state change, so whatever was fading is done.
    public void Settle()
    {
        Motion.Hide(Old);
        Motion.Settle(Shape);
    }

    // The generated style for a glyph: AbGlyph<Name>.
    public static string StyleKey(StatusGlyph g) => $"AbGlyph{g}";

    // The colour role a glyph draws in (ui.md §2.1): rule 7 turns active to agent and leaves the
    // rest.
    public static string RoleOf(StatusGlyph g, bool driven) => g switch
    {
        StatusGlyph.Needs => "Needs",
        StatusGlyph.Working or StatusGlyph.Delegating => driven ? "Agent" : "Active",
        StatusGlyph.Failed => "Danger",
        _ => "Neutral",
    };

    void Apply()
    {
        var (wasStyle, wasBrush, wasRole, wasAngle) = (Shape.Style, Shape.Foreground, role, Angle());
        Width = Height = size;
        Shape.Width = Shape.Height = size;
        Badge.Width = Badge.Height = size;
        var style = (Style)Application.Current.Resources[StyleKey(glyph)];
        var restyled = !ReferenceEquals(Shape.Style, style);
        if (restyled) { Shape.Style = style; }
        Badge.Visibility = Ui.Show(driven);
        var next = RoleOf(glyph, driven);
        if (role != next)
        {
            if (role is null) { VisualStateManager.GoToState(this, "Unset", false); }
            VisualStateManager.GoToState(this, next, false);
            role = next;
        }
        AutomationProperties.SetName(this, label);
        AutomationProperties.SetAccessibilityView(this, label.Length > 0 ? AccessibilityView.Content : AccessibilityView.Raw);
        if (restyled || turning is null) { Turn(); }
        // Never on a first draw or a theme change, which leave the role unset first.
        if (Fades && IsLoaded && wasRole is not null && (restyled || wasRole != next)) { CrossFade(wasStyle, wasBrush, wasAngle); }
    }

    // The old glyph, in its old colour and at the angle it had turned to, fades out as the new
    // one fades in. The words beside it change at once.
    void CrossFade(Style was, Brush wasBrush, double? angle)
    {
        // The glyph and its driver arrive as separate bindings, each applying: the copy keeps what
        // was drawn before the first of them, and the fade already under way carries the rest.
        if (fading) { return; }
        fading = true;
        if (!DispatcherQueue.TryEnqueue(() => fading = false)) { fading = false; }
        Old.Width = Old.Height = size;
        Old.Style = was;
        Old.Foreground = wasBrush;
        if (angle is { } a)
        {
            Old.ApplyTemplate();
            if ((Find(Old, "Spin") ?? Find(Old, "Orbit")) is { RenderTransform: RotateTransform turned }) { turned.Angle = a; }
        }
        Motion.Leave(Old, "Glyph");
        Motion.Enter(Shape, "Glyph");
    }

    // The moving part, found in the applied template; a still glyph has none.
    void Turn()
    {
        Stop();
        if (!IsLoaded || !MotionCatalogue.Loops(settings.AnimationsEnabled)) { return; }
        var (part, period) = glyph switch
        {
            StatusGlyph.Working => ("Spin", Motion.Catalogue.WorkingPeriod),
            StatusGlyph.Delegating => ("Orbit", Motion.Catalogue.DelegatingPeriod),
            _ => (null, TimeSpan.Zero),
        };
        if (part is null) { return; }
        Shape.ApplyTemplate();
        if (Find(Shape, part) is not { RenderTransform: RotateTransform rotate }) { return; }
        var spin = new DoubleAnimation { From = 0, To = 360, Duration = period, RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(spin, rotate);
        Storyboard.SetTargetProperty(spin, nameof(RotateTransform.Angle));
        turning = new Storyboard();
        turning.Children.Add(spin);
        turningPeriod = period;
        turning.Begin();
    }

    // How far the moving part has turned, while it turns.
    double? Angle() => turning is null || turningPeriod <= TimeSpan.Zero ? null
        : turning.GetCurrentTime().TotalMilliseconds % turningPeriod.TotalMilliseconds / turningPeriod.TotalMilliseconds * 360;

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
        DispatcherQueue.TryEnqueue(() => { if (IsLoaded) { Turn(); } });

    void Stop()
    {
        turning?.Stop();
        turning = null;
    }

    static FrameworkElement? Find(DependencyObject root, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: var n } e && n == name) { return e; }
            if (Find(child, name) is { } found) { return found; }
        }
        return null;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new GlyphPeer(this);

    // An image named for its state, with nothing inside it to read.
    sealed partial class GlyphPeer(StatusGlyphView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetClassNameCore() => nameof(StatusGlyphView);
        protected override IList<AutomationPeer>? GetChildrenCore() => null;
    }
}
