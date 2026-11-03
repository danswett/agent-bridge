using AgentBridge.App.Core;
using Xunit;

// The machine tabs over the list (ui.md §6.1, #290): never scrolled sideways. The tabs that fit
// show, the chosen one always among them, and the rest wait under More.
public sealed class MachineTabsTests
{
    [Fact]
    public void Tabs_that_all_fit_all_show_in_their_order_and_more_has_nothing()
    {
        var split = MachineTabFit.Split([40, 100, 100], selected: 0, room: 248, more: 60, gap: 4);

        Assert.Equal([0, 1, 2], split.Shown);
        Assert.Empty(split.More);
    }

    [Fact]
    public void Tabs_that_dont_fit_wait_under_more_in_their_order_and_what_shows_never_overflows_the_bar()
    {
        double[] widths = [40, 100, 100, 100];

        var split = MachineTabFit.Split(widths, selected: 0, room: 260, more: 60);

        Assert.Equal([0, 1], split.Shown);
        Assert.Equal([2, 3], split.More);
        Assert.True(split.Shown.Sum(i => widths[i]) + 60 <= 260);
    }

    [Fact]
    public void The_chosen_tab_shows_in_its_own_place_however_far_along_it_is_and_the_others_keep_their_order()
    {
        var split = MachineTabFit.Split([40, 100, 100, 100], selected: 3, room: 260, more: 60);

        Assert.Equal([0, 3], split.Shown);
        Assert.Equal([1, 2], split.More);
    }

    [Fact]
    public void The_chosen_tab_shows_even_where_nothing_else_fits_beside_more()
    {
        var split = MachineTabFit.Split([40, 300], selected: 1, room: 200, more: 60);

        Assert.Equal([1], split.Shown);
        Assert.Equal([0], split.More);
    }

    [Fact]
    public void A_chosen_tab_too_wide_to_sit_beside_more_is_cut_to_what_more_leaves_so_more_stays_on_the_bar()
    {
        // Windows' text made larger: a 128 px name and its padding, and a wider More, at the narrowest list.
        var split = MachineTabFit.Split([46, 154, 120], selected: 1, room: 247, more: 134, gap: 0);

        Assert.Equal([1], split.Shown);
        Assert.Equal(113, split.ChosenWidth);
        Assert.True(split.ChosenWidth + 134 <= 247);
    }

    [Fact]
    public void A_chosen_tab_that_fits_beside_more_keeps_its_own_width()
    {
        Assert.Null(MachineTabFit.Split([40, 100, 100, 100], selected: 3, room: 260, more: 60).ChosenWidth);
        Assert.Null(MachineTabFit.Split([40, 100], selected: 1, room: 260, more: 60).ChosenWidth);
    }

    [Fact]
    public void The_space_between_tabs_counts_toward_whether_they_fit()
    {
        Assert.Empty(MachineTabFit.Split([100, 100], selected: 0, room: 200, more: 40).More);
        Assert.Equal([1], MachineTabFit.Split([100, 100], selected: 0, room: 200, more: 40, gap: 4).More);
    }
}
