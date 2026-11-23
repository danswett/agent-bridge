using AgentBridge.App.Core;
using Xunit;

// #2869 G1 (problem 3): what to do when the app and the host report different releases. Pure: the newest VERIFIED cached package is an
// input, never discovered here; the clock is passed in. Heal(target) starts the same checksum-verified apply a press does (G2 wires it).
public sealed class VersionSkewHealTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    static SkewHealDecision Decide(string? app, string? host, string? cached, int failures = 0, DateTimeOffset? last = null, DateTimeOffset? now = null) =>
        VersionSkewHeal.Decide(app, host, cached, failures, last, now ?? Now);

    [Theory]
    [InlineData("0.1.7684", "0.1.7684")]
    [InlineData("v0.1.7684", "0.1.7684")]
    [InlineData("0.1.7684+abc", "V0.1.7684+def")]
    public void Equal_identities_ignoring_the_v_prefix_and_metadata_are_converged(string app, string host) =>
        Assert.Equal(SkewHealKind.Converged, Decide(app, host, "0.1.9000").Kind);

    [Theory]
    [InlineData("0.1.7684", "0.1.7522")]
    [InlineData("0.1.7522", "0.1.7684")]
    public void Either_side_behind_with_a_verified_cached_package_at_the_newer_release_heals_to_it(string app, string host)
    {
        var d = Decide(app, host, "0.1.7684");
        Assert.Equal(SkewHealKind.Heal, d.Kind);
        Assert.Equal("0.1.7684", d.Target);
    }

    [Fact]
    public void A_cached_package_newer_than_both_is_the_target()
    {
        var d = Decide("0.1.7684", "0.1.7522", "v0.1.7700+x");
        Assert.Equal((SkewHealKind.Heal, "0.1.7700"), (d.Kind, d.Target));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.1.7522")]
    [InlineData("0.1.7684-older")]
    public void Without_a_verified_cached_package_at_the_newer_release_it_holds_and_never_heals(string? cached)
    {
        var d = Decide("0.1.7684", "0.1.7522", cached);
        Assert.Equal((SkewHealKind.Hold, "no-verified-package"), (d.Kind, d.Reason));
        Assert.Null(d.Target);
    }

    [Theory]
    [InlineData(null, "0.1.7522")]
    [InlineData("0.1.7684", null)]
    [InlineData("0.0.0", "0.1.7522")]
    [InlineData("0.1.7684", " ")]
    [InlineData("not-a-version", "0.1.7522")]
    public void A_missing_zero_or_unreadable_identity_on_either_side_is_not_skew(string? app, string? host)
    {
        var d = Decide(app, host, "0.1.9000");
        Assert.Equal((SkewHealKind.Hold, "version-unknown"), (d.Kind, d.Reason));
    }

    [Fact]
    public void A_failed_heal_waits_out_the_backoff_then_heals_again()
    {
        var last = Now.AddMinutes(-3);
        var d = Decide("0.1.7684", "0.1.7522", "0.1.7684", failures: 1, last: last);
        Assert.Equal(SkewHealKind.Hold, d.Kind);
        Assert.Equal(last + UpdateBackoff.After(1), d.Until);

        Assert.Equal(SkewHealKind.Heal, Decide("0.1.7684", "0.1.7522", "0.1.7684", 1, last, last + UpdateBackoff.After(1)).Kind);
    }

    [Fact]
    public void Past_the_failure_bound_it_holds_for_the_backoff_then_heals_once_more_and_never_in_a_tight_loop()
    {
        var at = Now;
        var heals = 0;
        for (var failures = 0; failures <= 12; failures++)
        {
            // Every step the caller asks once a minute until the decision lets it through; each failure it records starts the wait again.
            var asks = 0;
            while (Decide("0.1.7684", "0.1.7522", "0.1.7684", failures, failures == 0 ? null : at, at.AddMinutes(asks)).Kind != SkewHealKind.Heal) { asks++; Assert.True(asks < 200, "never lets a heal through"); }
            heals++;
            if (failures > 0) { Assert.True(asks >= UpdateBackoff.After(failures).TotalMinutes - 1, $"failure {failures}: let through after {asks} min"); }
            at = at.AddMinutes(asks);
        }
        Assert.Equal(13, heals);
        var bound = Decide("0.1.7684", "0.1.7522", "0.1.7684", VersionSkewHeal.FailureBound, Now, Now.AddMinutes(1));
        Assert.Equal((SkewHealKind.Hold, "failed-repeatedly"), (bound.Kind, bound.Reason));
        Assert.Equal(Now + UpdateBackoff.After(VersionSkewHeal.FailureBound), bound.Until);
    }

    [Fact]
    public void A_last_attempt_in_the_future_after_the_clock_moved_back_waits_at_most_the_cap_from_now()
    {
        var d = Decide("0.1.7684", "0.1.7522", "0.1.7684", failures: 2, last: Now.AddDays(30));
        Assert.Equal((SkewHealKind.Hold, Now + UpdateBackoff.Cap), (d.Kind, d.Until));
    }

    [Fact]
    public void Converged_wins_over_everything_else()
    {
        var d = Decide("0.1.7684", "0.1.7684", null, failures: 5, last: Now);
        Assert.Equal(SkewHealKind.Converged, d.Kind);
    }
}
