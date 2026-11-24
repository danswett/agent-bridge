using AgentBridge.App.Core;
using Xunit;

namespace AgentBridge.App.Tests;

// #4500 / #4243, the owner's call (".5px seems overly precise and prone to flakiness"): a notch is judged within 4 px of 48, and the eight together within 8 px of 384, so one estimate-sized
// notch passes but a steady drift (last night's 40 to 44 px) and a big jump (402) still fail. "Never moves back up" is ScrollSettle's and is not loosened.
public sealed class WheelNotchRuleTests
{
    static readonly double[] Steady = [48, 48, 48, 48, 48, 48, 48, 48];

    [Fact]
    public void Exact_notches_pass() => Assert.Null(WheelNotchRule.Failure(Steady, 48));

    [Fact]
    public void One_notch_a_few_pixels_short_passes()
    {
        Assert.Null(WheelNotchRule.Failure([48, 48, 48, 48, 48, 46, 48, 48], 48));            // the red that opened #4500
        Assert.Null(WheelNotchRule.Failure([46, 46, 47, 48, 48, 48, 48, 48], 48));           // 7 short in all, within the 8 px for the sum
    }

    [Fact]
    public void A_notch_that_jumps_fails_the_per_notch_rule_and_names_it()
    {
        var failure = WheelNotchRule.Failure([48, 48, 402, 48, 48, 48, 48, 48], 48);

        Assert.Contains("per-notch rule", failure, StringComparison.Ordinal);
        Assert.Contains("402", failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(43.9)]
    [InlineData(52.1)]
    public void A_notch_just_outside_four_pixels_fails(double notch) =>
        Assert.Contains("per-notch rule", WheelNotchRule.Failure([48, 48, notch, 48, 48, 48, 48, 48], 48), StringComparison.Ordinal);

    [Fact]
    public void A_steady_drift_fails_the_sum_rule_though_each_notch_is_within_four()
    {
        var failure = WheelNotchRule.Failure([44, 44, 44, 44, 44, 44, 44, 44], 48);

        Assert.Contains("sum rule", failure, StringComparison.Ordinal);
        Assert.Contains("352", failure, StringComparison.Ordinal);           // the total, against 384
    }

    [Fact]
    public void A_steady_drift_the_other_way_fails_the_sum_rule_too() =>
        Assert.Contains("sum rule", WheelNotchRule.Failure([52, 52, 52, 52, 52, 52, 52, 52], 48), StringComparison.Ordinal);

    [Fact]
    public void Too_few_notches_fail()
    {
        Assert.Contains("notches", WheelNotchRule.Failure([48, 48, 48], 48), StringComparison.Ordinal);
        Assert.Contains("notches", WheelNotchRule.Failure([], 48), StringComparison.Ordinal);
    }

    // The sum is judged over the notches that were measured: with 4 to 7 of them it scales (1 px each on average).
    [Fact]
    public void Fewer_than_eight_notches_scale_the_sum_allowance()
    {
        Assert.Null(WheelNotchRule.Failure([47, 47, 47, 47, 47, 47], 48));           // 6 short of 288, allowed 6
        Assert.Contains("sum rule", WheelNotchRule.Failure([45, 45, 45, 45, 45, 45], 48), StringComparison.Ordinal);       // 18 short, allowed 6
    }

    // Rule (c) is unchanged: ScrollSettle's tolerance (1.5 px) is what "moving back up" means. A 2 px fall still fails; a 1 px one has always been inside it.
    [Fact]
    public void Moving_back_up_beyond_the_settle_tolerance_still_fails()
    {
        var back = new ScrollSettle(high: 39000);
        var within = new ScrollSettle(high: 39000);
        foreach (var (settle, fall) in new[] { (back, 2.0), (within, 1.0) })
        {
            settle.Observe(39200, 39200, moving: true);
            settle.Observe(39200 - fall, 39200, moving: true);
            for (var n = 0; n < 40; n++) { settle.Observe(39200, 39200, moving: false); }
        }

        Assert.False(back.Passed);
        Assert.True(within.Passed);
    }
}
