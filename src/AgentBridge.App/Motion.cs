using System.Globalization;
using System.Numerics;
using AgentBridge.App.Core;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

// ui.md §5.3's motion on what the app draws itself, timed by the generated AbMotion* resources
// (App.Core's MotionCatalogue) and run on each element's composition visual. A composition
// animation runs off the UI thread and changes neither layout nor hit testing, so a change shows
// at once, nothing waits for its motion, and a control works while it moves; starting another on
// the same element replaces the first rather than queueing behind it. Windows' "Animation effects"
// (UISettings.AnimationsEnabled) is read as each one starts: off, nothing travels, and an entry
// either cross-fades or is skipped. WinUI's own transitions (Expander, MenuFlyout, NavigationView,
// SelectorBar, pressed states) keep their own timing and follow the setting themselves.
public static class Motion
{
    static readonly Windows.UI.ViewManagement.UISettings Settings = new();
    static MotionCatalogue? catalogue;

    public static MotionCatalogue Catalogue => catalogue ??= MotionCatalogue.From(key =>
        Application.Current.Resources.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) : null);

    public static bool Enabled => Settings.AnimationsEnabled;

    // StartBringIntoView's own smooth scroll, at the platform's pace, unless it should jump.
    public static bool Scrolls => Catalogue.Enter("Scroll", Enabled).Runs;

    // The element comes in: from `from` (a direction, scaled by the entry's distance, which is 0
    // with animations off) to where it is, fading in when `fade`.
    public static void Enter(UIElement element, string entry, Vector2 from = default, bool fade = true) =>
        Run(element, Catalogue.Enter(entry, Enabled), new Vector3(from, 0), fade, scaled: true);

    // The element slides from `offset` (in pixels) to where it now is, without fading.
    public static void Slide(UIElement element, string entry, Vector2 offset) =>
        Run(element, Catalogue.Enter(entry, Enabled) is { Runs: true } step ? step : MotionStep.None, new Vector3(offset, 0), fade: false, scaled: false);

    // A copy of what left (a glyph, a dot), fading out where it was, and collapsed once done. The
    // copy is drawn only: the caller keeps it out of hit testing and the automation tree.
    public static void Leave(UIElement ghost, string entry)
    {
        var generation = Generation(ghost);
        var step = Catalogue.Exit(entry, Enabled);
        Settle(ghost);
        if (!step.Runs) { ghost.Visibility = Visibility.Collapsed; return; }
        ghost.Visibility = Visibility.Visible;
        Animated.AddOrUpdate(ghost, Marker);
        var visual = ElementCompositionPreview.GetElementVisual(ghost);
        var c = visual.Compositor;
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 1);
        fade.InsertKeyFrame(1, 0, Ease(c, step.Ease));
        fade.Duration = step.Duration;
        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation("Opacity", fade);
        batch.End();
        // Only the latest: a copy faded out again before this one finished stays for that one.
        batch.Completed += (_, _) =>
        {
            if (generation != Generations.GetValue(ghost, _ => new()).Value) { return; }
            ghost.Visibility = Visibility.Collapsed;
            visual.Opacity = 1;
        };
    }

    // A copy put away at once: the next state is drawn without it.
    public static void Hide(UIElement ghost)
    {
        Generation(ghost);
        Settle(ghost);
        ghost.Visibility = Visibility.Collapsed;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UIElement, System.Runtime.CompilerServices.StrongBox<int>> Generations = new();
    static int Generation(UIElement e) => ++Generations.GetValue(e, _ => new()).Value;
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UIElement, object> Animated = new();
    static readonly object Marker = new();

    // Whatever is moving on the element stops, and it shows where it is: a first draw, a row
    // given to another session, a question arriving under a push.
    public static void Settle(UIElement element)
    {
        // Only what this has animated: asking for an element's visual has a cost, and a row being
        // drawn for the first time has nothing to stop.
        if (!Animated.TryGetValue(element, out _)) { return; }
        var visual = ElementCompositionPreview.GetElementVisual(element);
        // Enabled first: until it is, the visual has no Translation to stop or set, and asking
        // for one throws.
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
        visual.Opacity = 1;
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
    }

    static void Run(UIElement element, MotionStep step, Vector3 from, bool fade, bool scaled)
    {
        Settle(element);
        if (!step.Runs) { return; }
        Animated.AddOrUpdate(element, Marker);
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var c = visual.Compositor;
        var ease = Ease(c, step.Ease);
        var start = scaled ? from * (float)step.Distance : from;
        if (start != Vector3.Zero)
        {
            var move = c.CreateVector3KeyFrameAnimation();
            move.InsertKeyFrame(0, start);
            move.InsertKeyFrame(1, Vector3.Zero, ease);
            move.Duration = step.Duration;
            visual.Properties.InsertVector3("Translation", start);
            visual.StartAnimation("Translation", move);
        }
        if (fade)
        {
            var show = c.CreateScalarKeyFrameAnimation();
            show.InsertKeyFrame(0, 0);
            show.InsertKeyFrame(1, 1, ease);
            show.Duration = step.Duration;
            visual.Opacity = 0;
            visual.StartAnimation("Opacity", show);
        }
    }

    static CubicBezierEasingFunction Ease(Compositor c, MotionEase e) =>
        c.CreateCubicBezierEasingFunction(new Vector2((float)e.X1, (float)e.Y1), new Vector2((float)e.X2, (float)e.Y2));

    // The trailing side, as a direction: right, or left in a right-to-left layout.
    public static Vector2 Trailing(FrameworkElement element) => new(element.FlowDirection == FlowDirection.RightToLeft ? -1 : 1, 0);

    // After the list has laid out the change (a row inserted or moved is given its container in
    // the next layout pass): the container showing `item`, if it is on screen at all. A few
    // passes, then nothing: an item off screen or in a hidden pane does not move later.
    public static void AfterLayout(ItemsControl list, object item, Action<FrameworkElement> run)
    {
        var tries = 0;
        void Once(object? sender, object e)
        {
            if (list.ContainerFromItem(item) is FrameworkElement container)
            {
                list.LayoutUpdated -= Once;
                run(container);
            }
            else if (++tries >= 3) { list.LayoutUpdated -= Once; }
        }
        list.LayoutUpdated += Once;
    }

    // An element's top within the list's panel, which scrolling does not move.
    public static double TopIn(FrameworkElement element, UIElement panel) => element.TransformToVisual(panel).TransformPoint(default).Y;

    public static bool Within(DependencyObject? node, DependencyObject ancestor)
    {
        for (var up = node; up is not null; up = VisualTreeHelper.GetParent(up))
        {
            if (up == ancestor) { return true; }
        }
        return false;
    }

    // A banner (an InfoBar over a page or the list) slides 12 down from its top edge and fades in
    // as it opens (ui.md §5.3, Toast and banner); never one open as the window first draws.
    public static void Banner(InfoBar bar) =>
        bar.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, (_, _) =>
        {
            if (bar.IsOpen && bar.IsLoaded) { Enter(bar, "Toast", new Vector2(0, -1)); }
        });
}
