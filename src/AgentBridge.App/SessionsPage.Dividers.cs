using System.ComponentModel;
using AgentBridge.App.Core;
using Microsoft.UI.Xaml;

namespace AgentBridge.App;

// The panes' dividers (ui.md §5.2, #297): the list's and the inspector's widths are PaneWidths', at
// the page's own width (inside the rail); which panes show stays PaneLayoutViewModel's, by the
// window's. Laid out again as the page is resized, as the panes shown change, and as a divider moves.
public sealed partial class SessionsPage : IPaneSizer
{
    PaneWidths widths = new(null);
    PaneLayoutViewModel? watched;

    // Where the widths are kept: set by the app once it knows its profile's folder; until then, and
    // in the self-test, only while it runs.
    internal PaneWidths Widths
    {
        get => widths;
        set { widths = value; SizePanes(); }
    }

    internal PaneDividerView ListDividerView => ListDivider;
    internal PaneDividerView InspectorDividerView => InspectorDivider;

    double PageWidth => ActualWidth;
    // The panes' visual state, which lays them out, decides when the columns are sized here: the
    // view model's mode follows the page's SizeChanged, so it lags a resize made while the page is
    // hidden (another page shown). Sized by the lagging mode, the list's column took its divider's
    // width in one column, overwriting the state's setter as a local value does, and the session,
    // spanning the columns there, ran past the window's edge.
    PaneMode Mode => Panes.CurrentState?.Name switch
    {
        "OneColumn" => PaneMode.One,
        "TwoPanes" => PaneMode.Two,
        "ThreePanes" => PaneMode.Three,
        _ => Layout?.Mode ?? PaneMode.One,
    };
    bool InspectorShown => Layout?.ShowInspector == true;
    bool stateWatched;

    void SizePanes()
    {
        Watch(Layout);
        if (!stateWatched)
        {
            stateWatched = true;
            Panes.CurrentStateChanged += (_, _) => SizePanes();
        }
        ListDivider.Sizer = this;
        InspectorDivider.Sizer = this;
        ListDivider.Visibility = Layout is not null && Mode != PaneMode.One ? Visibility.Visible : Visibility.Collapsed;
        // One column is the visual state's: the list's column fills the page.
        if (Layout is null || Mode == PaneMode.One || PageWidth <= 0) { return; }
        var now = widths.Arrange(PageWidth, Mode, InspectorShown);
        ListColumn.Width = new GridLength(now.List);
        if (now.Inspector > 0) { Inspector.Width = now.Inspector; }
    }

    void Watch(PaneLayoutViewModel? layout)
    {
        if (watched == layout) { return; }
        if (watched is not null) { watched.PropertyChanged -= OnPanes; }
        watched = layout;
        if (layout is not null) { layout.PropertyChanged += OnPanes; }
    }

    void OnPanes(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PaneLayoutViewModel.Mode) or nameof(PaneLayoutViewModel.ShowInspector) or "" or null) { SizePanes(); }
    }

    (double Value, double Min, double Max) IPaneSizer.Span(PaneDivider divider)
    {
        var (min, max) = widths.Range(divider, PageWidth, Mode, InspectorShown);
        return (widths.Width(divider, PageWidth, Mode, InspectorShown), min, max);
    }

    void IPaneSizer.Set(PaneDivider divider, double width)
    {
        widths.Set(divider, width, PageWidth, Mode, InspectorShown);
        SizePanes();
    }

    void IPaneSizer.Drag(PaneDivider divider, double from, double dx)
    {
        widths.Drag(divider, from, dx, PageWidth, Mode, InspectorShown);
        SizePanes();
    }

    void IPaneSizer.Nudge(PaneDivider divider, int direction)
    {
        widths.Nudge(divider, direction, PageWidth, Mode, InspectorShown);
        SizePanes();
    }

    void IPaneSizer.Reset(PaneDivider divider)
    {
        widths.Reset(divider);
        SizePanes();
    }

    void IPaneSizer.Keep() => widths.Save();
}
