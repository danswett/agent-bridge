using AgentBridge.App.Core;
using Xunit;

// The self-test's live-resize pass (#765) reads its frames this way: each interval by the width
// class it was drawn at, percentiles that are frames actually drawn, and #765's bar of a 60 Hz frame.
public sealed class FrameTimesTests
{
    [Fact]
    public void Percentiles_are_frames_that_were_drawn_by_nearest_rank()
    {
        var s = FrameSummary.Of(Enumerable.Range(1, 20).Select(i => (double)i));

        Assert.Equal((20, 10.0, 19.0, 20.0), (s.Frames, s.P50, s.P95, s.Max));
    }

    [Fact]
    public void A_frame_over_two_refreshes_counts_as_long_and_misses_the_bar_however_rare()
    {
        var steady = Enumerable.Repeat(16.6, 98).ToList();
        var s = FrameSummary.Of([.. steady, 20, 34]);

        Assert.Equal((2, 1), (s.OverBudget, s.OverLong));
        Assert.True(s.P95 <= FrameTimes.Budget);
        Assert.False(s.Meets);
        Assert.True(FrameSummary.Of(steady).Meets);
    }

    [Fact]
    public void Frames_are_grouped_by_the_window_width_they_were_drawn_at_and_a_class_change_is_counted_as_switching()
    {
        var times = new FrameTimes();
        times.Add(10, DesignLayout.ThreePane);
        times.Add(40, DesignLayout.ThreePane - 1);
        times.Add(12, DesignLayout.TwoPane);
        times.Add(30, DesignLayout.TwoPane - 1);
        times.Add(14, DesignLayout.MinWidth);

        Assert.Equal(1, times.In(PaneMode.Three).Frames);
        Assert.Equal(new[] { 12.0, 40.0 }, new[] { times.In(PaneMode.Two).P50, times.In(PaneMode.Two).Max });
        Assert.Equal(2, times.In(PaneMode.One).Frames);
        Assert.Equal((2, 40.0), (times.Switching.Frames, times.Switching.Max));
        Assert.Equal(5, times.All.Frames);
    }

    [Fact]
    public void No_frames_measured_never_meets_the_bar()
    {
        Assert.False(new FrameTimes().All.Meets);
    }
}
