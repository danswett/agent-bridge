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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_scan_timeout_names_the_step_and_timing_without_claiming_a_file_holder(bool earlierHolder)
    {
        var clock = new FakeTimeProvider();
        var budget = new InstallationFiles.Budget(clock);
        if (earlierHolder) { Assert.NotNull(await budget.ProbeOnceAsync(() => "earlier holder (pid 42)", default)); }
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
            Assert.DoesNotContain("earlier holder", failure, StringComparison.Ordinal);
            Assert.Contains("timed out", AppUpdateViewModel.Reason(failure), StringComparison.Ordinal);
            Assert.DoesNotContain("holding", AppUpdateViewModel.Reason(failure), StringComparison.Ordinal);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(FileShare.Read)]
    [InlineData(FileShare.ReadWrite)]
    public async Task A_newly_locked_image_invalidates_a_cached_clear_scan_before_the_final_check(FileShare sharing)
    {
        var folder = Path.Combine(Path.GetTempPath(), "ab-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var image = Path.Combine(folder, "fixture.DLL");
        File.WriteAllText(image, "old install");
        var clock = new FakeTimeProvider();
        var budget = new InstallationFiles.Budget(clock) { LockedFiles = () => InstallationFiles.LockedImages(folder) };
        var scans = 0;
        string? Scan() => Interlocked.Increment(ref scans) == 1 ? null : "fixture (pid 42)";
        try
        {
            Assert.Null(await budget.ProbeOnceAsync(Scan, default));
            Assert.Null(await budget.ProbeOnceAsync(Scan, default));
            Assert.Equal(1, scans);
            using var holder = new FileStream(image, FileMode.Open, FileAccess.Read, sharing);
            Assert.Equal("fixture (pid 42)", await budget.ProbeOnceAsync(Scan, default));
            Assert.Equal(2, scans);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    // #3241: a budget given no lock footprint (the app always gives one) could not see an image
    // newly held, so it keeps no clear scan to reuse.
    [Fact]
    public async Task A_clear_scan_without_a_lock_footprint_is_not_reused()
    {
        var budget = new InstallationFiles.Budget(new FakeTimeProvider());
        var scans = 0;
        string? Scan() => Interlocked.Increment(ref scans) == 1 ? null : "fixture (pid 42)";

        Assert.Null(await budget.ProbeOnceAsync(Scan, default));
        Assert.Equal("fixture (pid 42)", await budget.ProbeOnceAsync(Scan, default));
    }

    [Fact]
    public void A_held_non_executable_resource_is_scanned_like_a_held_image()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ab-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var resource = Path.Combine(folder, "resources.pri");
        File.WriteAllText(resource, "old install");
        File.WriteAllText(Path.Combine(folder, "free.dll"), "old install");
        try
        {
            using var holder = new FileStream(resource, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Equal([resource], InstallationFiles.LockedImages(folder));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void A_confirmed_holder_beside_an_unchecked_one_is_still_reported_as_a_held_file()
    {
        var mixed = new UpdateBlockedException($"fixture (pid 42) holding current\\a.dll; {InstallationFiles.Unknown} for 1 other").Message;
        Assert.Equal("another program is holding a file in the installation", AppUpdateViewModel.Reason(mixed));
        Assert.Equal("Windows file holders could not be checked; no file holder was confirmed",
            AppUpdateViewModel.Reason(new UpdateBlockedException(InstallationFiles.Unknown).Message));
    }

    [Fact]
    public async Task A_rescan_timing_out_does_not_report_the_previous_holder_as_the_cause()
    {
        var clock = new FakeTimeProvider();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scans = 0;
        var wait = InstallationFiles.AwaitReleaseAsync(() =>
        {
            if (Interlocked.Increment(ref scans) == 1) { return "earlier holder (pid 42)"; }
            release.Task.GetAwaiter().GetResult();
            return null;
        }, clock, default);
        try
        {
            await InstallReleaseWaitTests.FinishAsync(wait, () => true, clock);
            var failure = new UpdateBlockedException(Assert.IsType<string>(await wait)).Message;
            Assert.Contains("timed out", failure, StringComparison.Ordinal);
            Assert.DoesNotContain("earlier holder", failure, StringComparison.Ordinal);
            Assert.DoesNotContain(UpdateBlockedException.Prefix, failure, StringComparison.Ordinal);
        }
        finally { release.TrySetResult(); }
    }
}
