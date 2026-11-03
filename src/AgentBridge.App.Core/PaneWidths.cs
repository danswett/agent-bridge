using System.Text.Json;

namespace AgentBridge.App.Core;

// The two dividers on the Sessions page (#297): between the list and the session, and between the
// session and the inspector.
public enum PaneDivider { List, Inspector }

// The widths the list and the inspector take, in effective pixels; the session has the rest. The
// inspector's is 0 where it doesn't show.
public readonly record struct PaneSizes(double List, double Inspector);

// The list's and the inspector's widths as the person left them, kept on this device
// (<profile>-panes.json, beside the order), and what they come to at a page this wide (ui.md §5.2,
// #297). The breakpoints still decide which panes show (PaneLayoutViewModel); this only sizes them:
//
//   * Each pane is its default until its divider is moved, and again once it is double-clicked.
//   * Each stays within its own narrowest and widest, whatever the file says.
//   * The session never gives way below its narrowest: short of room, the inspector gives way
//     first, down to its narrowest, then the list. So a width kept from a wide window never pushes
//     a pane off a narrower one, and the width kept is not changed by it: the window widened again
//     gets it back.
//   * A divider moves only within what that leaves, so it can't push the session below its
//     narrowest either. The list's may take the inspector down to its narrowest, as a narrower
//     window would, without changing the inspector's kept width.
//
// It knows nothing of XAML, so its rules run on Linux CI.
public sealed class PaneWidths
{
    // An arrow key's move: four steps of the 4 px grid.
    public const double Step = 16;

    readonly string? path;
    double? list;
    double? inspector;

    sealed record Stored(double? List, double? Inspector);

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // path: where the widths are kept, or null to keep them only while the app runs.
    public PaneWidths(string? path)
    {
        this.path = path;
        var stored = Load(path);
        list = Usable(stored?.List);
        inspector = Usable(stored?.Inspector);
    }

    // The width each divider was left at, or null for the pane's default.
    public double? KeptList => list;
    public double? KeptInspector => inspector;

    // A width from the file that isn't a size at all is the default; one out of range is kept, and
    // brought within range wherever it is used.
    static double? Usable(double? w) => w is { } v && double.IsFinite(v) && v > 0 ? v : null;

    static Stored? Load(string? path)
    {
        try { return path is not null && File.Exists(path) ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Json) : null; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    // The list and inspector at a page this wide (the Sessions page's own width, inside the rail).
    // One column has no dividers: the list or the session fills it.
    public PaneSizes Arrange(double page, PaneMode mode, bool inspectorShown)
    {
        if (mode == PaneMode.One || !double.IsFinite(page) || page <= 0) { return new(0, 0); }
        var l = Math.Clamp(list ?? DesignLayout.List, DesignLayout.ListMin, DesignLayout.ListMax);
        var i = mode == PaneMode.Three && inspectorShown ? Math.Clamp(inspector ?? DesignLayout.Inspector, DesignLayout.InspectorMin, DesignLayout.InspectorMax) : 0;
        var over = l + i - (page - DesignLayout.SessionMin);
        if (over > 0 && i > 0)
        {
            var give = Math.Min(over, i - DesignLayout.InspectorMin);
            i -= give;
            over -= give;
        }
        if (over > 0) { l = Math.Max(DesignLayout.ListMin, l - over); }
        return new(l, i);
    }

    // How far a divider can take its pane now: never under the pane's narrowest, nor so wide that
    // the session would go under its own. The list's room is measured with the inspector at its
    // narrowest, as the inspector gives way to it first: measured with the inspector as it shows,
    // the inspector coming back towards its kept width as the list narrowed took the room the list
    // had left, and a drag or an arrow key could not be reversed. The inspector's is measured with
    // the list as it shows: the list doesn't give way to it.
    public (double Min, double Max) Range(PaneDivider divider, double page, PaneMode mode, bool inspectorShown)
    {
        var now = Arrange(page, mode, inspectorShown);
        var (min, max, other) = divider == PaneDivider.List
            ? (DesignLayout.ListMin, DesignLayout.ListMax, now.Inspector > 0 ? DesignLayout.InspectorMin : 0)
            : (DesignLayout.InspectorMin, DesignLayout.InspectorMax, now.List);
        return (min, Math.Max(min, Math.Min(max, page - DesignLayout.SessionMin - other)));
    }

    // The width a divider's pane has now.
    public double Width(PaneDivider divider, double page, PaneMode mode, bool inspectorShown)
    {
        var now = Arrange(page, mode, inspectorShown);
        return divider == PaneDivider.List ? now.List : now.Inspector;
    }

    // A divider put at a width (dragged, or set by a screen reader): kept within its range, in
    // whole pixels, and remembered until Save. Returns the width it took.
    public double Set(PaneDivider divider, double width, double page, PaneMode mode, bool inspectorShown)
    {
        if (!double.IsFinite(width)) { return Width(divider, page, mode, inspectorShown); }
        var (min, max) = Range(divider, page, mode, inspectorShown);
        var w = Math.Round(Math.Clamp(width, min, max));
        if (divider == PaneDivider.List) { list = w; } else { inspector = w; }
        return w;
    }

    // A divider dragged dx to the right of where it was pressed, its pane then from wide: the list
    // is left of its divider and widens with it, the inspector right of its own and narrows.
    public double Drag(PaneDivider divider, double from, double dx, double page, PaneMode mode, bool inspectorShown) =>
        Set(divider, divider == PaneDivider.List ? from + dx : from - dx, page, mode, inspectorShown);

    // An arrow key on a divider: one step left (-1) or right (+1), the divider moving as it would
    // under the pointer. Kept at once.
    public double Nudge(PaneDivider divider, int direction, double page, PaneMode mode, bool inspectorShown)
    {
        var w = Drag(divider, Width(divider, page, mode, inspectorShown), Math.Sign(direction) * Step, page, mode, inspectorShown);
        Save();
        return w;
    }

    // A divider double-clicked: its pane back to its default, here and on the next start.
    public void Reset(PaneDivider divider)
    {
        if (divider == PaneDivider.List) { list = null; } else { inspector = null; }
        Save();
    }

    public void Save()
    {
        if (path is null) { return; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new Stored(list, inspector), Json));
        }
        // A nicety: the panes still resize, only not across a restart.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
