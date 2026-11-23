using AgentBridge.App.Core;
using Xunit;

// #2869 E1 (problem 4, "Auto-apply path"; docs/app-windows.md, Updates: "Completion evidence"): whether the checksum-verified cached-package apply
// is tried again after it ended, and when. Pure: the end (UpdateApplyResult, A's seam), whether the package still verifies, who held the files, the
// attempts so far and the clock are arguments. Retry only a Failed end with a holder seen, never a package that fails its checksum, never an Unknown
// end (the apply may still be running: retrying would be a second authority), bounded, delays from UpdateBackoff.After.
public sealed class UpdateApplyRetryTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    static readonly ApplyHolder Holder = new(4321, @"C:\Windows\System32\svchost.exe");

    static UpdateApplyResult Ended(UpdateApplyEnd end, string? code = null, string step = "apply-completion") =>
        new(end, code, step, "0.1.7684", end == UpdateApplyEnd.Failed ? 1 : null, "0.1.7522", T0, TimeSpan.FromSeconds(9));

    static ApplyRetryDecision Decide(UpdateApplyResult r, bool verified = true, ApplyHolder? holder = null, int failedBefore = 0, DateTimeOffset? first = null, DateTimeOffset? now = null) =>
        UpdateApplyRetry.Decide(r, verified, holder, failedBefore, first ?? T0, now ?? T0);

    [Fact]
    public void A_completed_apply_is_done_whatever_else_is_said()
    {
        Assert.Equal(ApplyRetryKind.Done, Decide(Ended(UpdateApplyEnd.Completed), verified: false, holder: Holder, failedBefore: 9).Kind);
    }

    [Fact]
    public void A_failure_with_a_holder_is_tried_again_after_the_backoff_and_then_succeeds()
    {
        var first = Decide(Ended(UpdateApplyEnd.Failed, "exit-failed"), holder: Holder, failedBefore: 0, now: T0);
        Assert.Equal(ApplyRetryKind.Retry, first.Kind);
        Assert.Equal(UpdateBackoff.After(1), first.After);
        Assert.Equal(T0 + UpdateBackoff.After(1), first.At);

        // The holder let go: the retry's own end is Completed.
        Assert.Equal(ApplyRetryKind.Done, Decide(Ended(UpdateApplyEnd.Completed), failedBefore: 1, now: first.At).Kind);
    }

    [Fact]
    public void Each_further_failure_waits_as_long_as_UpdateBackoff_says_capped()
    {
        for (var before = 0; before < UpdateApplyRetry.MaxAttempts - 1; before++)
        {
            var d = Decide(Ended(UpdateApplyEnd.Failed, "exit-failed"), holder: Holder, failedBefore: before);
            Assert.Equal((ApplyRetryKind.Retry, UpdateBackoff.After(before + 1)), (d.Kind, d.After));
            Assert.True(d.After <= UpdateBackoff.Cap);
        }
    }

    [Fact]
    public void At_the_bound_it_gives_up_naming_the_step_the_holder_and_the_time()
    {
        var now = T0.AddMinutes(37);
        var d = Decide(Ended(UpdateApplyEnd.Failed, "exit-failed", "apply-completion"), holder: Holder, failedBefore: UpdateApplyRetry.MaxAttempts - 1, first: T0, now: now);
        Assert.Equal((ApplyRetryKind.GiveUp, "retries-exhausted"), (d.Kind, d.Reason));
        Assert.Equal("apply-completion", d.Step);
        Assert.Equal(Holder, d.Holder);
        Assert.Equal(TimeSpan.FromMinutes(37), d.Elapsed);
        Assert.Null(d.After);
        Assert.Contains("apply-completion", d.Message);
        Assert.Contains("4321", d.Message);
        Assert.Contains("svchost.exe", d.Message);
        Assert.Contains("37", d.Message);
    }

    [Theory]
    [InlineData(UpdateApplyEnd.Failed)]
    [InlineData(UpdateApplyEnd.Unknown)]
    public void A_package_that_no_longer_matches_its_checksum_is_never_applied_again(UpdateApplyEnd end)
    {
        var d = Decide(Ended(end, "exit-failed"), verified: false, holder: Holder);
        Assert.Equal((ApplyRetryKind.GiveUp, "checksum"), (d.Kind, d.Reason));
        Assert.Null(d.After);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("exit-unknown")]
    [InlineData("version-skew")]
    [InlineData("probe-failed")]
    public void An_unknown_end_is_never_retried_because_the_apply_may_still_be_running(string code)
    {
        var d = Decide(Ended(UpdateApplyEnd.Unknown, code), holder: Holder);
        Assert.Equal(ApplyRetryKind.GiveUp, d.Kind);
        Assert.Equal($"unknown-end:{code}", d.Reason);
        Assert.Null(d.After);
    }

    [Fact]
    public void A_failure_with_no_holder_seen_is_not_retried_since_nothing_says_it_would_differ()
    {
        var d = Decide(Ended(UpdateApplyEnd.Failed, "exit-failed"), holder: null);
        Assert.Equal((ApplyRetryKind.GiveUp, "no-holder-seen"), (d.Kind, d.Reason));
    }

    [Fact]
    public void A_holder_without_a_readable_path_is_still_named_by_its_pid()
    {
        var d = Decide(Ended(UpdateApplyEnd.Failed, "exit-failed"), holder: new ApplyHolder(77, null), failedBefore: UpdateApplyRetry.MaxAttempts - 1);
        Assert.Contains("77", d.Message);
        Assert.DoesNotContain("null", d.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_first_attempt_after_now_after_the_clock_moved_back_reports_no_negative_elapsed()
    {
        var d = Decide(Ended(UpdateApplyEnd.Failed, "exit-failed"), holder: Holder, failedBefore: UpdateApplyRetry.MaxAttempts - 1, first: T0.AddHours(2), now: T0);
        Assert.Equal(TimeSpan.Zero, d.Elapsed);
    }
}
