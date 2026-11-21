using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class VersionSkewHealTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 18, 5, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("0.2.0", "0.2.0")]
    [InlineData("v0.2.0+app", "V0.2.0+host")]
    [InlineData(" 0.2.0 ", "v0.2.0")]
    [InlineData("0.2.0-RC.1", "0.2.0-rc.1+host")]
    public void Equal_release_identities_are_converged_even_without_a_package_or_after_failures(string app, string host)
    {
        Assert.IsType<VersionSkewHeal.Converged>(
            VersionSkewHeal.Decide(app, host, null, 3, Now, Now));
    }

    [Theory]
    [InlineData("0.2.0", "0.1.0", "0.2.0")]
    [InlineData("0.1.0", "0.2.0", "0.2.0")]
    [InlineData("v0.2.0+app", "0.1.0", "v0.2.0+verified")]
    [InlineData("0.1.9", "0.1.10", "0.1.10")]
    [InlineData("0.2.0", "0.1.0", "0.3.0")]
    public void A_verified_package_at_least_as_new_as_both_sides_is_the_exact_heal_target(
        string app, string host, string package)
    {
        var heal = Assert.IsType<VersionSkewHeal.Heal>(
            VersionSkewHeal.Decide(app, host, package, 0, null, Now));

        Assert.Equal(package, heal.Target);
    }

    [Theory]
    [InlineData("0.2.0", "0.1.0", null)]
    [InlineData("0.2.0", "0.1.0", "")]
    [InlineData("0.2.0", "0.1.0", "v0.0.0+unknown")]
    [InlineData("0.2.0", "0.1.0", "invalid")]
    [InlineData("0.2.0", "0.1.0", "0.1.0")]
    [InlineData("0.1.0", "0.2.0", "0.1.0")]
    [InlineData("0.1.10", "0.1.9", "0.1.9")]
    public void A_missing_or_older_verified_package_holds_instead_of_downgrading(
        string app, string host, string? package)
    {
        var hold = Assert.IsType<VersionSkewHeal.Hold>(
            VersionSkewHeal.Decide(app, host, package, 0, null, Now));

        Assert.Equal("no-verified-package", hold.Reason);
        Assert.Null(hold.Until);
    }

    [Theory]
    [InlineData(null, "0.2.0")]
    [InlineData("0.2.0", null)]
    [InlineData("", "0.2.0")]
    [InlineData("0.2.0", " ")]
    [InlineData("0.0.0", "0.2.0")]
    [InlineData("0.2.0", "v0.0.0+host")]
    [InlineData(null, null)]
    [InlineData("invalid", "0.2.0")]
    public void Unknown_versions_hold_and_never_invent_skew(string? app, string? host)
    {
        var hold = Assert.IsType<VersionSkewHeal.Hold>(
            VersionSkewHeal.Decide(app, host, "0.3.0", 0, null, Now));

        Assert.Equal("version-unknown", hold.Reason);
        Assert.Null(hold.Until);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void The_three_failure_bound_waits_for_backoff_and_a_recorded_retry_cannot_spin(int failures)
    {
        var clock = new FakeTimeProvider(Now);
        var retryAt = Now + UpdateBackoff.After(failures);
        var hold = Assert.IsType<VersionSkewHeal.Hold>(
            VersionSkewHeal.Decide("0.1.0", "0.2.0", "0.2.0", failures, Now, clock.GetUtcNow()));
        Assert.Equal("failure-backoff", hold.Reason);
        Assert.Equal(retryAt, hold.Until);

        clock.Advance(UpdateBackoff.After(failures) - TimeSpan.FromTicks(1));
        Assert.Equal(hold,
            VersionSkewHeal.Decide("0.1.0", "0.2.0", "0.2.0", failures, Now, clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.IsType<VersionSkewHeal.Heal>(
            VersionSkewHeal.Decide("0.1.0", "0.2.0", "0.2.0", failures, Now, clock.GetUtcNow()));

        var attemptedAt = clock.GetUtcNow();
        var next = Assert.IsType<VersionSkewHeal.Hold>(
            VersionSkewHeal.Decide("0.1.0", "0.2.0", "0.2.0", failures, attemptedAt, clock.GetUtcNow()));
        Assert.Equal(attemptedAt + UpdateBackoff.After(failures), next.Until);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void A_recent_attempt_waits_even_before_the_failure_bound(int failures)
    {
        var hold = Assert.IsType<VersionSkewHeal.Hold>(
            VersionSkewHeal.Decide("0.2.0", "0.1.0", "0.2.0", failures, Now, Now));

        Assert.Equal("attempt-backoff", hold.Reason);
        Assert.Equal(Now + UpdateBackoff.First, hold.Until);
        Assert.IsType<VersionSkewHeal.Heal>(
            VersionSkewHeal.Decide("0.2.0", "0.1.0", "0.2.0", failures, Now, Now + UpdateBackoff.First));
    }

    [Fact]
    public void Failures_without_an_attempt_time_hold_instead_of_removing_the_bound()
    {
        var hold = Assert.IsType<VersionSkewHeal.Hold>(
            VersionSkewHeal.Decide("0.2.0", "0.1.0", "0.2.0", 3, null, Now));

        Assert.Equal("attempt-time-unknown", hold.Reason);
        Assert.Null(hold.Until);
    }

    [Fact]
    public void Negative_failure_counts_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VersionSkewHeal.Decide("0.2.0", "0.1.0", "0.2.0", -1, null, Now));
    }
}
