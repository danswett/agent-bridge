using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace AgentBridge.App;

// The panes' dividers (ui.md §5.2, #297): found, named and focusable, with the resize cursor and the
// design's brushes; dragged, moved by the arrow keys and set through their automation peers, the
// panes taking each width; the session never under its narrowest nor a pane pushed off a narrower
// window, and the widths back once it widens again; and double-clicked back to the defaults.
static partial class SelfTest
{
    static async Task CheckDividers(MainWindow window, MainViewModel main, Action<bool, string> check, string outFile)
    {
        window.ShowPage("sessions");
        var page = window.Sessions;
        var layout = page.Layout!;
        var card = main.AllCards.First(c => c.Id == "demo-approval");
        if (!main.Sessions.Contains(card)) { main.SelectedMachine = main.Machines[0]; }
        main.Selected = card;
        layout.InspectorWanted = true;
        var list = page.ListDividerView;
        var inspector = page.InspectorDividerView;
        FrameworkElement? Pane(string id) => ById<FrameworkElement>(page, id);
        double Width(string id) => Pane(id)?.ActualWidth ?? 0;
        static bool Near(double a, double b) => Math.Abs(a - b) < 1;
        async Task<bool> At(double width, PaneMode mode)
        {
            Resize(window, width, 900);
            return await Until(() => layout.Mode == mode && Math.Abs(window.RootElement.XamlRoot.Size.Width - window.RootElement.ActualWidth) < 1, TimeSpan.FromSeconds(10)) && await Settled();
        }
        static async Task<bool> Settled() { await Task.Delay(400); return true; }
        static IRangeValueProvider? Range(PaneDividerView d) => FrameworkElementAutomationPeer.CreatePeerForElement(d)?.GetPattern(PatternInterface.RangeValue) as IRangeValueProvider;

        if (!await At(1440, PaneMode.Three) || !await Until(() => layout.ShowInspector && inspector.ActualWidth > 0, TimeSpan.FromSeconds(5)))
        {
            check(false, "shows three panes with the inspector at 1440 px, for the dividers");
            return;
        }

        // Found, named, focusable, with the resize cursor and a value: the pane's width.
        var described = new List<string>();
        foreach (var (d, pane) in new[] { (list, "ListPane"), (inspector, "Inspector") })
        {
            var r = Range(d);
            if (d.Visibility != Visibility.Visible || d.ActualWidth <= 0 || !d.IsTabStop || string.IsNullOrEmpty(AutomationProperties.GetName(d)) || !d.HasResizeCursor
                || r is null || !Near(r.Value, Width(pane)) || r.IsReadOnly)
            {
                described.Add($"{d.Divider}: {d.Visibility}, {d.ActualWidth:0} px, tab stop {d.IsTabStop}, \"{AutomationProperties.GetName(d)}\", resize cursor {d.HasResizeCursor}, value {r?.Value.ToString("0") ?? "none"} of {Width(pane):0}");
            }
        }
        check(described.Count == 0, $"offers both dividers at three panes, each named, focusable, with the resize cursor and its pane's width as its value{(described.Count > 0 ? $": not {string.Join("; ", described)}" : "")}");

        // The design's brushes: AbStroke2 while pointed at, Bridge while dragged or focused.
        var theme = window.RootElement.ActualTheme.ToString();
        var design = Application.Current.Resources.MergedDictionaries.FirstOrDefault(d => d.Source?.ToString().EndsWith(Design.DesignKeys.Source["ms-appx:///".Length..], StringComparison.OrdinalIgnoreCase) == true);
        var themed = design?.ThemeDictionaries.TryGetValue(theme, out var t) == true ? t as ResourceDictionary : null;
        check(themed is not null && ReferenceEquals(list.HoverBrush, themed["AbStroke2"]) && ReferenceEquals(list.ActiveBrush, themed["AbBridge"]),
            $"draws a divider in the design's {theme} AbStroke2 when pointed at and Bridge when dragged");

        // Dragged 80 px right: the list widens with it, the line in Bridge while it moves.
        var x = list.TransformToVisual(null).TransformPoint(default).X;
        list.Begin(x);
        list.Move(x + 80);
        await Settled();
        var dragging = list.ShowsActive;
        // Pictures for review, in both themes, while the line shows.
        foreach (var picture in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            window.RootElement.RequestedTheme = picture;
            await Settled();
            await Picture(window.RootElement, Path.ChangeExtension(outFile, $".dividers.{picture.ToString().ToLowerInvariant()}.png"));
        }
        window.RootElement.RequestedTheme = ElementTheme.Default;
        await Settled();
        list.End();
        await Settled();
        check(dragging && !list.ShowsActive && Near(Width("ListPane"), DesignLayout.List + 80), $"widens the list as its divider is dragged right, the line in Bridge until let go ({Width("ListPane"):0} px)");

        // The keyboard: focused, the line in Bridge; Right and Left a step each way.
        list.Focus(FocusState.Keyboard);
        await Settled();
        var focused = list.FocusState == FocusState.Keyboard && list.ShowsActive;
        list.Press(VirtualKey.Right);
        await Settled();
        var right = Width("ListPane");
        list.Press(VirtualKey.Left);
        await Settled();
        check(focused && Near(right, DesignLayout.List + 80 + PaneWidths.Step) && Near(Width("ListPane"), DesignLayout.List + 80),
            $"takes keyboard focus on a divider, shown in Bridge, and moves it a step with Right and Left ({right:0}, then {Width("ListPane"):0} px)");

        // A screen reader sets the inspector's width; a drag right narrows it.
        Range(inspector)?.SetValue(400);
        await Settled();
        var set = Width("Inspector");
        var ix = inspector.TransformToVisual(null).TransformPoint(default).X;
        inspector.Begin(ix);
        inspector.Move(ix + 40);
        inspector.End();
        await Settled();
        check(Near(set, 400) && Near(Width("Inspector"), 360), $"sizes the inspector from its divider's automation value, and narrows it as the divider is dragged right ({set:0}, then {Width("Inspector"):0} px)");

        // Both as wide as they go, then a narrower window: the inspector gives way, then the list, and
        // the session keeps its narrowest; nothing goes past the page's edge.
        Range(list)?.SetValue(DesignLayout.ListMax);
        Range(inspector)?.SetValue(DesignLayout.InspectorMax);
        await Settled();
        var wideList = Width("ListPane");
        var wideInspector = Width("Inspector");
        var squeezed = new List<string>();
        foreach (var (w, mode) in new[] { (1300.0, PaneMode.Three), (1000.0, PaneMode.Two), (800.0, PaneMode.Two) })
        {
            if (!await At(w, mode)) { squeezed.Add($"{w}: not {mode}"); continue; }
            var session = Width("SessionPane");
            var edge = (layout.ShowInspector ? Pane("Inspector") : Pane("SessionPane")) is { } last ? last.TransformToVisual(page).TransformPoint(new Windows.Foundation.Point(last.ActualWidth, 0)).X : double.MaxValue;
            if (session < DesignLayout.SessionMin - 1 || Width("ListPane") < DesignLayout.ListMin - 1 || (layout.ShowInspector && Width("Inspector") < DesignLayout.InspectorMin - 1) || edge > page.ActualWidth + 1)
            {
                squeezed.Add($"{w}: list {Width("ListPane"):0}, session {session:0}, inspector {(layout.ShowInspector ? Width("Inspector") : 0):0}, ending at {edge:0} of {page.ActualWidth:0}");
            }
        }
        var back = await At(1440, PaneMode.Three) && Near(Width("ListPane"), wideList) && Near(Width("Inspector"), wideInspector);
        check(squeezed.Count == 0 && back,
            $"keeps the session at its narrowest and every pane on the page at a narrower window, and the widths back once it widens ({(squeezed.Count > 0 ? string.Join("; ", squeezed) : "none squeezed")}; back: {Width("ListPane"):0} and {Width("Inspector"):0} of {wideList:0} and {wideInspector:0})");

        // Double-clicked: back to the defaults.
        list.Reset();
        inspector.Reset();
        await Settled();
        check(Near(Width("ListPane"), DesignLayout.List) && Near(Width("Inspector"), DesignLayout.Inspector) && page.Widths.KeptList is null && page.Widths.KeptInspector is null,
            $"puts both panes back to their defaults when their dividers are double-clicked ({Width("ListPane"):0} and {Width("Inspector"):0} px)");

        // One column has no dividers.
        var one = await At(400, PaneMode.One);
        check(one && list.Visibility == Visibility.Collapsed && inspector.Visibility == Visibility.Collapsed, "has no dividers in one column");
        await At(1440, PaneMode.Three);
    }
}
