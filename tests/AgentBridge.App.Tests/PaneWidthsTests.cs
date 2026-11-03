using System.Text.Json;
using AgentBridge.App.Core;
using Xunit;

// The panes' dividers (ui.md §5.2, #297): the list's and the inspector's widths, kept on the device,
// each within its own limits, and giving way to the session's narrowest at a narrower window
// without losing the width kept.
public sealed class PaneWidthsTests : IDisposable
{
    readonly string file = Path.Combine(Path.GetTempPath(), "abp-" + Guid.NewGuid().ToString("N")[..8] + ".json");

    public void Dispose() => File.Delete(file);

    // A three-pane window's page, inside the rail, with room for both panes at their widest.
    const double Wide = 1872;

    static double SessionOf(double page, PaneSizes s) => page - s.List - s.Inspector;

    [Fact]
    public void Panes_never_resized_take_their_default_widths()
    {
        var widths = new PaneWidths(file);
        Assert.Equal(new PaneSizes(DesignLayout.List, DesignLayout.Inspector), widths.Arrange(Wide, PaneMode.Three, inspectorShown: true));
        Assert.Equal(new PaneSizes(DesignLayout.List, 0), widths.Arrange(1000, PaneMode.Two, inspectorShown: true));
    }

    [Fact]
    public void Widths_a_divider_left_come_back_after_a_restart()
    {
        var widths = new PaneWidths(file);
        widths.Set(PaneDivider.List, 400, Wide, PaneMode.Three, true);
        widths.Set(PaneDivider.Inspector, 300, Wide, PaneMode.Three, true);
        widths.Save();

        Assert.Equal(new PaneSizes(400, 300), new PaneWidths(file).Arrange(Wide, PaneMode.Three, true));
    }

    [Fact]
    public void A_drag_is_kept_only_once_it_ends()
    {
        var widths = new PaneWidths(file);
        widths.Drag(PaneDivider.List, DesignLayout.List, 60, Wide, PaneMode.Three, true);
        Assert.Null(new PaneWidths(file).KeptList);
        widths.Save();
        Assert.Equal(DesignLayout.List + 60, new PaneWidths(file).KeptList);
    }

    [Theory]
    [InlineData("{\"list\":2000,\"inspector\":10}", 480, 280)]
    [InlineData("{\"list\":-5,\"inspector\":0}", 320, 336)]
    [InlineData("not json", 320, 336)]
    public void A_width_in_the_file_is_brought_within_its_pane_s_limits(string json, double list, double inspector)
    {
        File.WriteAllText(file, json);
        Assert.Equal(new PaneSizes(list, inspector), new PaneWidths(file).Arrange(Wide, PaneMode.Three, true));
    }

    [Fact]
    public void Dragging_the_list_s_divider_right_widens_the_list_and_the_inspector_s_narrows_the_inspector()
    {
        var widths = new PaneWidths(null);
        Assert.Equal(DesignLayout.List + 40, widths.Drag(PaneDivider.List, DesignLayout.List, 40, Wide, PaneMode.Three, true));
        Assert.Equal(DesignLayout.Inspector - 40, widths.Drag(PaneDivider.Inspector, DesignLayout.Inspector, 40, Wide, PaneMode.Three, true));
    }

    [Fact]
    public void A_divider_stops_at_its_pane_s_narrowest_and_widest()
    {
        var widths = new PaneWidths(null);
        Assert.Equal(DesignLayout.ListMin, widths.Set(PaneDivider.List, 10, Wide, PaneMode.Three, true));
        Assert.Equal(DesignLayout.ListMax, widths.Set(PaneDivider.List, 5000, Wide, PaneMode.Three, true));
        Assert.Equal(DesignLayout.InspectorMin, widths.Set(PaneDivider.Inspector, 10, Wide, PaneMode.Three, true));
        Assert.Equal(DesignLayout.InspectorMax, widths.Set(PaneDivider.Inspector, 5000, Wide, PaneMode.Three, true));
    }

    [Fact]
    public void A_divider_cannot_push_the_session_under_its_narrowest()
    {
        var widths = new PaneWidths(null);
        // Three panes at their breakpoint, inside the rail: the inspector at its default.
        const double page = 1232;
        var list = widths.Set(PaneDivider.List, 5000, page, PaneMode.Three, true);
        Assert.Equal(DesignLayout.SessionMin, SessionOf(page, widths.Arrange(page, PaneMode.Three, true)));
        Assert.Equal(page - DesignLayout.SessionMin - DesignLayout.Inspector, list);
        // Two panes at theirs: little room for the list beyond its narrowest.
        Assert.Equal(712 - DesignLayout.SessionMin, widths.Set(PaneDivider.List, 5000, 712, PaneMode.Two, false));
    }

    [Fact]
    public void Short_of_room_the_inspector_gives_way_first_then_the_list_never_under_their_narrowest()
    {
        var widths = new PaneWidths(null);
        widths.Set(PaneDivider.List, DesignLayout.ListMax, Wide, PaneMode.Three, true);
        widths.Set(PaneDivider.Inspector, DesignLayout.InspectorMax, Wide, PaneMode.Three, true);

        var some = widths.Arrange(1232, PaneMode.Three, true);
        Assert.Equal(DesignLayout.ListMax, some.List);
        Assert.Equal(1232 - DesignLayout.SessionMin - DesignLayout.ListMax, some.Inspector);

        var less = widths.Arrange(1100, PaneMode.Three, true);
        Assert.Equal(DesignLayout.InspectorMin, less.Inspector);
        Assert.Equal(1100 - DesignLayout.SessionMin - DesignLayout.InspectorMin, less.List);
        Assert.Equal(DesignLayout.SessionMin, SessionOf(1100, less));

        // Two panes: no inspector, so the list has the room it left.
        var two = widths.Arrange(800, PaneMode.Two, true);
        Assert.Equal(new PaneSizes(800 - DesignLayout.SessionMin, 0), two);
        Assert.True(two.List >= DesignLayout.ListMin);
    }

    [Fact]
    public void A_narrower_window_does_not_change_the_widths_kept()
    {
        var widths = new PaneWidths(file);
        widths.Set(PaneDivider.List, 460, Wide, PaneMode.Three, true);
        widths.Set(PaneDivider.Inspector, 520, Wide, PaneMode.Three, true);
        widths.Save();

        widths.Arrange(1232, PaneMode.Three, true);
        widths.Arrange(760, PaneMode.Two, true);

        Assert.Equal(new PaneSizes(460, 520), widths.Arrange(Wide, PaneMode.Three, true));
        Assert.Equal(((double?)460, (double?)520), (new PaneWidths(file).KeptList, new PaneWidths(file).KeptInspector));
    }

    [Fact]
    public void Hiding_the_inspector_gives_its_room_to_the_list_s_divider()
    {
        var widths = new PaneWidths(null);
        Assert.Equal((DesignLayout.ListMin, 1100 - DesignLayout.SessionMin - DesignLayout.Inspector), widths.Range(PaneDivider.List, 1100, PaneMode.Three, inspectorShown: true));
        Assert.Equal((DesignLayout.ListMin, DesignLayout.ListMax), widths.Range(PaneDivider.List, 1100, PaneMode.Three, inspectorShown: false));
    }

    [Fact]
    public void One_column_sizes_no_pane()
    {
        var widths = new PaneWidths(null);
        widths.Set(PaneDivider.List, 400, Wide, PaneMode.Three, true);
        Assert.Equal(new PaneSizes(0, 0), widths.Arrange(400, PaneMode.One, true));
    }

    [Fact]
    public void An_arrow_key_moves_a_divider_a_step_and_is_kept_at_once()
    {
        var widths = new PaneWidths(file);
        Assert.Equal(DesignLayout.List + PaneWidths.Step, widths.Nudge(PaneDivider.List, 1, Wide, PaneMode.Three, true));
        // Right moves the inspector's divider right too, so its pane narrows.
        Assert.Equal(DesignLayout.Inspector - PaneWidths.Step, widths.Nudge(PaneDivider.Inspector, 1, Wide, PaneMode.Three, true));
        Assert.Equal(DesignLayout.Inspector, widths.Nudge(PaneDivider.Inspector, -1, Wide, PaneMode.Three, true));

        var again = new PaneWidths(file);
        Assert.Equal(((double?)(DesignLayout.List + PaneWidths.Step), (double?)DesignLayout.Inspector), (again.KeptList, again.KeptInspector));
    }

    [Fact]
    public void An_arrow_key_at_a_pane_s_limit_leaves_it_there()
    {
        var widths = new PaneWidths(null);
        widths.Set(PaneDivider.List, DesignLayout.ListMin, Wide, PaneMode.Three, true);
        Assert.Equal(DesignLayout.ListMin, widths.Nudge(PaneDivider.List, -1, Wide, PaneMode.Three, true));
    }

    [Fact]
    public void A_double_click_puts_a_pane_back_to_its_default_and_forgets_its_width()
    {
        var widths = new PaneWidths(file);
        widths.Set(PaneDivider.List, 400, Wide, PaneMode.Three, true);
        widths.Set(PaneDivider.Inspector, 300, Wide, PaneMode.Three, true);
        widths.Save();

        widths.Reset(PaneDivider.List);

        Assert.Equal(new PaneSizes(DesignLayout.List, 300), widths.Arrange(Wide, PaneMode.Three, true));
        using var kept = JsonDocument.Parse(File.ReadAllText(file));
        Assert.Equal(JsonValueKind.Null, kept.RootElement.GetProperty("list").ValueKind);
        Assert.Equal(300, kept.RootElement.GetProperty("inspector").GetDouble());
    }
}
