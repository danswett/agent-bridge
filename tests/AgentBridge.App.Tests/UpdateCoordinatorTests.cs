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

    UpdateCoordinator Coordinator(Func<IDisposable>? acquire = null) => new(
        ct => { steps.Add("wait-app"); return appExited.Task.WaitAsync(ct); },
        async (generation, ct) =>
        {
            steps.Add("apply");
            applyStarted.TrySetResult();
            return (await applied.Task.WaitAsync(ct)) with { Generation = generation };
        },
        acquire ?? (() => { steps.Add("lock"); return new Lease(() => steps.Add("unlock")); }),
        () => { steps.Add("fresh-version"); return active; },
        () => steps.Add("respawn"),
        time);

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

    sealed class Lease(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
