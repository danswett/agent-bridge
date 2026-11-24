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

    // The ordering is the updater's own (ReleaseVersions.TryRun, 0.1.N by value), not text: 10 is above 9, and a prerelease is no release it can order.
    [Theory]
    [InlineData("0.1.10", "0.1.9")]
    [InlineData("0.1.7684", "0.1.7522")]
    [InlineData("0.1.100", "0.1.99")]
    public void Releases_are_ordered_by_run_number_not_by_text(string higher, string lower)
    {
        var down = Decide(higher, lower, lower);
        Assert.Equal((SkewHealKind.Hold, "no-verified-package"), (down.Kind, down.Reason));
        var up = Decide(lower, higher, higher);
        Assert.Equal((SkewHealKind.Heal, higher), (up.Kind, up.Target));
    }

    [Theory]
    [InlineData("0.1.7-rc.10", "0.1.6", "0.1.7-rc.9")]
    [InlineData("0.1.7-rc.9", "0.1.6", "0.1.7-rc.10")]
    [InlineData("0.1.7-1", "0.1.6", "0.1.7-alpha")]
    [InlineData("0.1.7-alpha", "0.1.6", "0.1.7-alpha.1")]
    [InlineData("0.1.7", "0.1.6", "0.1.7-rc.9")]
    [InlineData("0.1.7-rc.9", "0.1.6", "0.1.7")]
    [InlineData("0.1.7", "0.1.7-rc.9", "0.1.7")]
    [InlineData("0.1.7.1", "0.1.7", "0.1.7.1")]
    [InlineData("0.2.0", "0.1.6", "0.2.0")]
    public void A_prerelease_or_another_scheme_is_never_ordered_so_it_can_never_be_a_downgrade_or_a_heal_target(string app, string host, string cached)
    {
        var d = Decide(app, host, cached);
        Assert.NotEqual(SkewHealKind.Heal, d.Kind);
        Assert.Null(d.Target);
    }

    [Fact]
    public void A_release_outranks_its_own_prerelease_so_the_prerelease_package_is_not_enough()
    {
        // App on the release, host older: a cached package that is only a prerelease of it must not stand in for it.
        var d = Decide("0.1.7684", "0.1.7522", "0.1.7684-rc.1");
        Assert.Equal((SkewHealKind.Hold, "no-verified-package"), (d.Kind, d.Reason));
    }

    [Fact]
    public void The_fourth_attempt_holds_once_three_have_failed_and_the_third_still_waits_only_for_the_backoff()
    {
        var last = Now.AddMinutes(-1);
        var third = Decide("0.1.7684", "0.1.7522", "0.1.7684", failures: 2, last: last);
        Assert.Equal((SkewHealKind.Hold, "backoff"), (third.Kind, third.Reason));

        var fourth = Decide("0.1.7684", "0.1.7522", "0.1.7684", failures: VersionSkewHeal.FailureBound, last: last);
        Assert.Equal((SkewHealKind.Hold, "failed-repeatedly"), (fourth.Kind, fourth.Reason));
        Assert.Equal(last + UpdateBackoff.After(VersionSkewHeal.FailureBound), fourth.Until);

        // Past the backoff, one more is let through.
        Assert.Equal(SkewHealKind.Heal, Decide("0.1.7684", "0.1.7522", "0.1.7684", VersionSkewHeal.FailureBound, last, last + UpdateBackoff.After(VersionSkewHeal.FailureBound)).Kind);
    }

    // "Unknown never heals" on every side: the app, the host and the cached package alike. Leading zeros are not a canonical run number, and an N
    // too big for a long (19 digits, over long.MaxValue) or too long is not one either (ReleaseVersions.TryRun).
    [Theory]
    [InlineData("0.1.07")]
    [InlineData("0.1.007684")]
    [InlineData("0.1.9223372036854775808")]
    [InlineData("0.1.99999999999999999999")]
    [InlineData("0.1.")]
    [InlineData("0.1.-5")]
    [InlineData("0.1.+5")]
    [InlineData("0.2.5")]
    [InlineData("1.1.5")]
    public void An_unreadable_run_number_is_unknown_on_the_app_the_host_and_the_cached_package_and_never_heals(string odd)
    {
        var app = Decide(odd, "0.1.7522", "0.1.9000");
        var host = Decide("0.1.7522", odd, "0.1.9000");
        var cached = Decide("0.1.7684", "0.1.7522", odd);
        foreach (var d in new[] { app, host, cached })
        {
            Assert.Equal(SkewHealKind.Hold, d.Kind);
            Assert.Null(d.Target);
        }
        Assert.Equal("version-unknown", app.Reason);
        Assert.Equal("version-unknown", host.Reason);
        Assert.Equal("no-verified-package", cached.Reason);
    }

    // GitHub marks every v0.1.N release a "prerelease" (a flag on the release, not part of its version). Nothing here reads that flag: the versions
    // are plain 0.1.N text, tag-prefixed or not, and they order and heal as releases.
    [Theory]
    [InlineData("v0.1.7684", "v0.1.7522", "v0.1.7684")]
    [InlineData("0.1.7684", "v0.1.7522", "0.1.7684")]
    public void A_v_tagged_release_is_an_ordinary_release_whatever_GitHub_calls_it(string app, string host, string cached)
    {
        var d = Decide(app, host, cached);
        Assert.Equal((SkewHealKind.Heal, "0.1.7684"), (d.Kind, d.Target));
    }
}
