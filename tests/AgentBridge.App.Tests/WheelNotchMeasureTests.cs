using AgentBridge.App.Core;
using Xunit;

namespace AgentBridge.App.Tests;

// SelfTest.WheelNotch measures what the person sees, so the published-app check does not flake on layout it
// does not judge (main runs 45346121000 and others: "quiet gap 771" while the view never moved back).
public sealed class WheelNotchMeasureTests
{
    [Fact]
    public void A_notch_is_measured_by_the_block_nearest_the_middle_that_is_drawn_before_and_after()
    {
        var before = new Dictionary<int, double> { [10] = -40, [11] = 250, [12] = 520 };
        var after = new Dictionary<int, double> { [10] = 8, [11] = 298, [13] = 900 };
        // Rows below re-measured (13 is new, 12 recycled) and the extent changed: the move is still 48.
        Assert.Equal(48, WheelNotchMeasure.OnScreenMove(before, after, viewport: 600));
    }

    [Fact]
    public void No_block_drawn_both_times_gives_no_measurement_rather_than_a_guess()
    {
        Assert.Null(WheelNotchMeasure.OnScreenMove(new Dictionary<int, double> { [1] = 0 }, new Dictionary<int, double> { [2] = 0 }, 600));
    }

    [Theory]
    [InlineData(new[] { 100.0, 100, 100 }, new[] { 900.0, 900, 900 }, new[] { false, false, false }, true)]
    [InlineData(new[] { 100.0, 100, 100 }, new[] { 900.0, 900, 960 }, new[] { false, false, false }, false)]
    [InlineData(new[] { 100.0, 100, 101 }, new[] { 900.0, 900, 900 }, new[] { false, false, false }, false)]
    [InlineData(new[] { 100.0, 100, 100 }, new[] { 900.0, 900, 900 }, new[] { false, true, false }, false)]
    [InlineData(new[] { 100.0, 100 }, new[] { 900.0, 900 }, new[] { false, false }, false)]
    public void The_view_is_settled_only_when_offset_and_extent_hold_still_for_the_last_frames(double[] offsets, double[] extents, bool[] moving, bool settled)
    {
        var frames = offsets.Select((o, i) => (o, extents[i], moving[i])).ToList();
        Assert.Equal(settled, WheelNotchMeasure.Settled(frames, frames: 3));
    }
}
