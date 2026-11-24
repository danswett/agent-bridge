using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed partial class UpdateCoordinatorTests
{
    [Fact]
    public async Task An_unfinished_worker_cannot_read_the_manifest_or_respawn_even_with_exit_zero()
    {
        appExited.SetResult();
        applied.SetResult(new(false, 0, Target));
        var run = Coordinator().RunAsync(Target);
        await applyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromMinutes(10));

        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(UpdateApplyEnd.Unknown, result.End);
        Assert.Equal("timeout", result.Code);
        Assert.DoesNotContain("lock", steps);
        Assert.DoesNotContain("respawn", steps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_lock_release_failure_is_not_reported_as_success(bool cancelOnUnlock)
    {
        using var cancel = new CancellationTokenSource();
        appExited.SetResult();
        applied.SetResult(new(true, 0, Target));

        var result = await Coordinator(() => new Lease(
            () => { if (cancelOnUnlock) cancel.Cancel(); throw new IOException("private lock path"); })).RunAsync(Target, cancel.Token);

        Assert.Equal(UpdateApplyEnd.Unknown, result.End);
        Assert.Equal("package-unlock", result.Step);
        Assert.Equal("IOException", result.ErrorType);
        Assert.DoesNotContain("private lock path", result.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_blocked_native_respawn_cannot_hold_the_lock_past_observation(bool cancellation)
    {
        using var cancel = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = false;
        respawning = () => { entered.SetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        appExited.SetResult();
        applied.SetResult(new(true, 0, Target));
        var coordinator = Coordinator(() => { held = true; return new Lease(() => held = false); });
        var run = Task.Run(() => coordinator.RunAsync(Target, cancel.Token));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            if (cancellation) cancel.Cancel();
            else time.Advance(TimeSpan.FromMinutes(10));
            if (cancellation) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(1)));
            else
            {
                var result = await run.WaitAsync(TimeSpan.FromSeconds(1));
                Assert.Equal(UpdateApplyEnd.Unknown, result.End);
                Assert.Equal("timeout", result.Code);
            }
            Assert.False(held);
        }
        finally
        {
            release.Set();
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            await coordinator.RespawnFinished.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task A_package_lease_arriving_after_timeout_is_disposed_without_respawn()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        appExited.SetResult();
        applied.SetResult(new(true, 0, Target));
        var run = Task.Run(() => Coordinator(() =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return new Lease(() => disposed.SetResult());
        }).RunAsync(Target));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { time.Advance(TimeSpan.FromMinutes(10)); Assert.Equal(UpdateApplyEnd.Unknown, (await run.WaitAsync(TimeSpan.FromSeconds(1))).End); }
        finally { release.Set(); }
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("respawn", steps);
    }
}
