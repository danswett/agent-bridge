using AgentBridge.App.Core;
using Xunit;

// Where the window opens (#180): where it was left, unless that is off every screen, never
// smaller than the smallest window ui.md allows, and never larger than the screen it is on.
public sealed class WindowPlacementTests : IDisposable
{
    static readonly Display Primary = new(new PixelRect(0, 0, 1920, 1040), 1);
    static readonly Display Right = new(new PixelRect(1920, 0, 2560, 1400), 1.5);
    readonly string folder = Path.Combine(Path.GetTempPath(), "abw-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void A_window_comes_back_where_it_was_left_at_the_size_it_was()
    {
        var left = new WindowPlacement(2100, 120, 1500, 900);
        Assert.Equal(left, WindowPlacement.Place(left, [Primary, Right], Primary));
    }

    [Fact]
    public void A_window_left_on_a_screen_that_has_gone_opens_centred_on_the_primary_screen_at_the_first_size()
    {
        var p = WindowPlacement.Place(new WindowPlacement(2100, 120, 1500, 900), [Primary], Primary);
        Assert.Equal(new WindowPlacement(320, 90, 1280, 860), p);
    }

    [Fact]
    public void A_window_whose_title_bar_is_above_the_screen_or_barely_on_it_is_brought_back()
    {
        Assert.Equal(new WindowPlacement(320, 90, 1280, 860), WindowPlacement.Place(new WindowPlacement(100, -400, 1000, 700), [Primary], Primary));
        Assert.Equal(new WindowPlacement(320, 90, 1280, 860), WindowPlacement.Place(new WindowPlacement(1900, 100, 1000, 700), [Primary], Primary));
    }

    [Fact]
    public void The_first_start_opens_wide_enough_for_three_panes_or_as_large_as_a_smaller_screen_allows()
    {
        Assert.True(WindowPlacement.FirstWidth >= DesignLayout.ThreePane);
        Assert.Equal(new WindowPlacement(320, 90, 1280, 860), WindowPlacement.Place(null, [Primary], Primary));
        var small = new Display(new PixelRect(0, 0, 1366, 728), 1);
        Assert.Equal(new WindowPlacement(43, 0, 1280, 728), WindowPlacement.Place(null, [small], small));
        Assert.Equal(new WindowPlacement(2240, 55, 1920, 1290), WindowPlacement.Place(null, [Right], Right));
    }

    [Fact]
    public void A_window_larger_than_its_screen_is_cut_to_fit_and_pulled_back_onto_it()
    {
        var p = WindowPlacement.Place(new WindowPlacement(400, 10, 2400, 1500), [Primary], Primary);
        Assert.Equal(new WindowPlacement(0, 0, 1920, 1040), p);
    }

    [Fact]
    public void A_screen_shorter_than_the_smallest_window_at_its_scale_keeps_the_window_on_it()
    {
        // 1040 px at 200 % is 520 effective pixels: under 600, so the window is the screen's height.
        var scaled = new Display(new PixelRect(0, 0, 1920, 1040), 2);
        Assert.Equal((720, 1040), WindowPlacement.Smallest(scaled));
        Assert.Equal(new WindowPlacement(0, 0, 1920, 1040), WindowPlacement.Place(null, [scaled], scaled));
    }
    [Fact]
    public void A_window_never_opens_smaller_than_360_by_600_at_its_screens_scale()
    {
        Assert.Equal((360, 600), WindowPlacement.Smallest(1));
        Assert.Equal((540, 900), WindowPlacement.Smallest(1.5));
        var p = WindowPlacement.Place(new WindowPlacement(2200, 100, 200, 300), [Primary, Right], Primary);
        Assert.Equal((540, 900), (p.Width, p.Height));
    }

    [Fact]
    public void A_maximised_window_is_remembered_as_maximised_over_where_it_was_before()
    {
        var p = WindowPlacement.Place(new WindowPlacement(100, 100, 1200, 800, Maximized: true), [Primary], Primary);
        Assert.True(p.Maximized);
        Assert.Equal(new PixelRect(100, 100, 1200, 800), p.Rect);
    }

    [Fact]
    public void A_placement_is_kept_across_restarts_and_an_unreadable_or_empty_file_is_none()
    {
        var file = Path.Combine(folder, "default-window.json");
        Assert.Null(WindowPlacement.Load(file));
        new WindowPlacement(10, 20, 900, 700, true).Save(file);
        Assert.Equal(new WindowPlacement(10, 20, 900, 700, true), WindowPlacement.Load(file));
        File.WriteAllText(file, "{ not json");
        Assert.Null(WindowPlacement.Load(file));
        File.WriteAllText(file, """{"X":0,"Y":0,"Width":0,"Height":0}""");
        Assert.Null(WindowPlacement.Load(file));
        Assert.Null(WindowPlacement.Load(null));
    }
}
