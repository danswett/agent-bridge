using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class UpdateCoordinatorTests
{
    const string Target = "0.1.200";
    readonly FakeTimeProvider time = new(Make.T0);
    readonly List<string> steps = [];
    readonly TaskCompletionSource appExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource applyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<UpdateApplyEvidence> applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
    string? active = Target;
    Exception? readError, respawnError;
    bool stale;
    Action? respawning, reading;

    UpdateCoordinator Coordinator(Func<IDisposable>? acquire = null) => new(
        ct => { steps.Add("wait-app"); return appExited.Task.WaitAsync(ct); },
        async (generation, ct) =>
        {
            steps.Add("apply");
            applyStarted.TrySetResult();
            return (await applied.Task.WaitAsync(ct)) with { Generation = stale ? Guid.Empty : generation };
        },
        acquire ?? (() => { steps.Add("lock"); return new Lease(() => steps.Add("unlock")); }),
        () =>
        {
            steps.Add("fresh-version");
            reading?.Invoke();
            if (readError is not null) throw readError;
            return active;
        },
        () =>
        {
            steps.Add("respawn");
            respawning?.Invoke();
            if (respawnError is not null) throw respawnError;
        },
        time);

    [Fact]
    public async Task A_fresh_slot_resolution_still_in_flight_is_owned_after_observation_times_out()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reading = () => { entered.SetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        appExited.SetResult();
        applied.SetResult(new(true, 0, Target));
        var coordinator = Coordinator();
        var run = Task.Run(() => coordinator.RunAsync(Target));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            time.Advance(TimeSpan.FromMinutes(10));
            Assert.Equal(UpdateApplyEnd.Unknown, (await run.WaitAsync(TimeSpan.FromSeconds(1))).End);
            Assert.False(coordinator.ObservationFinished.IsCompleted);
            Assert.DoesNotContain("respawn", steps);
        }
        finally
        {
            release.Set();
            try { await coordinator.ObservationFinished.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Respawn_waits_for_the_original_app_then_real_apply_then_a_fresh_manifest_under_the_lock()
    {
        var run = Coordinator().RunAsync(Target);
        Assert.Equal(["wait-app"], steps);
        appExited.SetResult();
        await applyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("fresh-version", steps);
        Assert.DoesNotContain("respawn", steps);

        applied.SetResult(new(true, 0, "cached-old-version"));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(UpdateApplyEnd.Completed, result.End);
        Assert.Equal(Target, result.ActiveVersion);
        Assert.Equal(["wait-app", "apply", "lock", "fresh-version", "respawn", "unlock"], steps);
    }

    [Theory]
    [InlineData(null, UpdateApplyEnd.Unknown)]
    [InlineData("0.1.199", UpdateApplyEnd.Unknown)]
    [InlineData(" V0.1.200+build123 ", UpdateApplyEnd.Completed)]
    public async Task The_post_completion_manifest_not_cached_target_evidence_authorizes_respawn(
        string? fresh, UpdateApplyEnd expected)
    {
        active = fresh;
        appExited.SetResult();
        applied.SetResult(new(true, 0, Target));

        var result = await Coordinator().RunAsync(Target);

        Assert.Equal(expected, result.End);
        Assert.Equal(expected == UpdateApplyEnd.Completed, steps.Contains("respawn"));
        Assert.Equal("unlock", steps[^1]);
    }

    [Fact]
    public async Task A_known_failed_apply_does_not_read_the_manifest_or_respawn()
    {
        appExited.SetResult();
        applied.SetResult(new(true, -1, Target));

        var result = await Coordinator().RunAsync(Target);

        Assert.Equal(UpdateApplyEnd.Failed, result.End);
        Assert.Equal(-1, result.ApplyExitCode);
        Assert.Equal(["wait-app", "apply"], steps);
    }

    [Fact]
    public async Task An_unavailable_package_lock_is_unknown_and_never_permission_to_respawn()
    {
        appExited.SetResult();
        applied.SetResult(new(true, 0, Target));

        var result = await Coordinator(() => throw new IOException("private lock path")).RunAsync(Target);

        Assert.Equal(UpdateApplyEnd.Unknown, result.End);
        Assert.DoesNotContain("fresh-version", steps);
        Assert.DoesNotContain("respawn", steps);
        Assert.DoesNotContain("private lock path", result.ToString());
    }

    [Fact]
    public async Task App_exit_wait_is_bounded_and_timeout_does_not_start_the_installer()
    {
        var run = Coordinator().RunAsync(Target);
        time.Advance(TimeSpan.FromMinutes(10));

        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(UpdateApplyEnd.Unknown, result.End);
        Assert.Equal("timeout", result.Code);
        Assert.Equal(["wait-app"], steps);
        Assert.False(appExited.Task.IsCompleted);
    }

    [Fact]
    public async Task Cancellation_while_apply_runs_does_not_complete_or_cancel_the_worker_or_respawn()
    {
        appExited.SetResult();
        using var cancel = new CancellationTokenSource();
        var run = Coordinator().RunAsync(Target, cancel.Token);
        await applyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(applied.Task.IsCompleted);
        Assert.DoesNotContain("respawn", steps);
    }

    [Fact]
    public async Task Stale_equal_evidence_cannot_even_acquire_the_package_lock()
    {
        stale = true;
        appExited.SetResult();
        applied.SetResult(new(true, 0, Target));

        var result = await Coordinator().RunAsync(Target);

        Assert.Equal(UpdateApplyEnd.Unknown, result.End);
        Assert.Equal("stale-evidence", result.Code);
        Assert.Equal(["wait-app", "apply"], steps);
    }

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
    [InlineData(false, "active-version")]
    [InlineData(true, "respawn")]
    public async Task Manifest_or_respawn_failure_is_unknown_and_releases_the_lock(bool restarting, string step)
    {
        var error = new InvalidOperationException("private coordinator detail");
        if (restarting) respawnError = error;
        else readError = error;
        appExited.SetResult();
        applied.SetResult(new(true, 0, Target));

        var result = await Coordinator().RunAsync(Target);

        Assert.Equal(UpdateApplyEnd.Unknown, result.End);
        Assert.Equal(step, result.Step);
        Assert.Equal("InvalidOperationException", result.ErrorType);
        Assert.Equal("unlock", steps[^1]);
        Assert.Equal(restarting, steps.Contains("respawn"));
        Assert.DoesNotContain(error.Message, result.ToString());
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
    public async Task A_respawn_that_returns_after_the_window_is_never_completed()
    {
        respawning = () => time.Advance(TimeSpan.FromMinutes(10));
        appExited.SetResult();
        applied.SetResult(new(true, 0, Target));
        var result = await Coordinator().RunAsync(Target);
        Assert.Equal(UpdateApplyEnd.Unknown, result.End);
        Assert.Equal("timeout", result.Code);
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

    sealed class Lease(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
