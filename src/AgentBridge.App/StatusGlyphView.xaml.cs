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
// moving part (the template part Spin or Orbit) turns once per 1.2 s or 8 s, and holds still
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
    string? role;

    public StatusGlyphView()
    {
        InitializeComponent();
        Loaded += (_, _) => { role = null; Apply(); };
        Unloaded += (_, _) => Stop();
        // The setters' ThemeResources are read as a state is entered: entered again, they are the
        // new theme's.
        ActualThemeChanged += (_, _) => { role = null; Apply(); };
        Apply();
    }

    public StatusGlyph Glyph { get => glyph; set { if (glyph != value) { glyph = value; Apply(); } } }
    public bool Driven { get => driven; set { if (driven != value) { driven = value; Apply(); } } }
    public double Size { get => size; set { var s = Sizes.Contains(value) ? value : 16; if (size != s) { size = s; Apply(); } } }
    public string Label { get => label; set { if (label != (value ?? "")) { label = value ?? ""; Apply(); } } }

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
    }

    // The moving part, found in the applied template; a still glyph has none.
    void Turn()
    {
        Stop();
        if (!IsLoaded || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) { return; }
        var (part, seconds) = glyph switch
        {
            StatusGlyph.Working => ("Spin", 1.2),
            StatusGlyph.Delegating => ("Orbit", 8.0),
            _ => (null, 0),
        };
        if (part is null) { return; }
        Shape.ApplyTemplate();
        if (Find(Shape, part) is not { RenderTransform: RotateTransform rotate }) { return; }
        var spin = new DoubleAnimation { From = 0, To = 360, Duration = TimeSpan.FromSeconds(seconds), RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(spin, rotate);
        Storyboard.SetTargetProperty(spin, nameof(RotateTransform.Angle));
        turning = new Storyboard();
        turning.Children.Add(spin);
        turning.Begin();
    }

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
