using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class InstallationScanTimeoutTests
{
    [Fact]
    public async Task A_clear_59_second_scan_is_reused_by_the_remaining_preflight_checks()
    {
        var clock = new FakeTimeProvider();
        var budget = new InstallationFiles.Budget(clock);
        var scans = 0;
        string? Scan()
        {
            Interlocked.Increment(ref scans);
            clock.Advance(TimeSpan.FromSeconds(59));
            return null;
        }

        Assert.Null(await InstallationFiles.AwaitReleaseAsync(Scan, clock, default, budget: budget));
        Assert.Null(await budget.ProbeOnceAsync(Scan, default));
        Assert.Null(await budget.ProbeOnceAsync(Scan, default));

        Assert.Equal(1, scans);
        Assert.Equal(TimeSpan.FromSeconds(59), budget.Elapsed);
    }

    [Fact]
    public async Task A_later_scan_gets_its_own_budget_not_the_remainder_of_an_earlier_scan()
    {
        var clock = new FakeTimeProvider();
        var budget = new InstallationFiles.Budget(clock);
        var scans = 0;
        string? Scan()
        {
            clock.Advance(TimeSpan.FromSeconds(59));
            return Interlocked.Increment(ref scans) == 1 ? "fixture (pid 42)" : null;
        }

        Assert.Equal("fixture (pid 42)", await budget.ProbeOnceAsync(Scan, default));
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(await budget.ProbeOnceAsync(Scan, default));
        Assert.Equal(2, scans);
    }

    [Fact]
    public async Task A_scan_timeout_names_the_step_and_timing_without_claiming_a_file_holder()
    {
        var clock = new FakeTimeProvider();
        var budget = new InstallationFiles.Budget(clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scan = budget.ProbeOnceAsync(() =>
        {
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
            return null;
        }, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(InstallationFiles.ReleaseWait);
            var detail = Assert.IsType<string>(await scan.WaitAsync(TimeSpan.FromSeconds(5)));
            var failure = new UpdateBlockedException(detail).Message;
            Assert.Contains("Restart Manager", failure, StringComparison.Ordinal);
            Assert.Contains("timed out", failure, StringComparison.Ordinal);
            Assert.Contains("120 s", failure, StringComparison.Ordinal);
            Assert.DoesNotContain(UpdateBlockedException.Prefix, failure, StringComparison.Ordinal);
            Assert.Contains("timed out", AppUpdateViewModel.Reason(failure), StringComparison.Ordinal);
            Assert.DoesNotContain("holding", AppUpdateViewModel.Reason(failure), StringComparison.Ordinal);
        }
        finally { release.TrySetResult(); }
    }
}
