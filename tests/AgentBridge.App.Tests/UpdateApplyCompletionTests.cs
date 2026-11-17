using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class UpdateApplyCompletionTests
{
    const string Target = "0.1.200";
    readonly FakeTimeProvider time = new(Make.T0);
    UpdateApplyEvidence evidence = new(null, null, null);

    UpdateApplyCompletion Watch(Func<CancellationToken, Task<UpdateApplyEvidence>>? probe = null) =>
        new(probe ?? (_ => Task.FromResult(evidence)), time)
        {
            Window = TimeSpan.FromSeconds(5),
            Poll = TimeSpan.FromSeconds(1)
        };

    [Fact]
    public async Task Launcher_exit_and_target_files_do_not_complete_an_unfinished_apply()
    {
        evidence = new(false, 0, Target);
        var watch = Watch();
        var run = watch.RunAsync(Target);
        Assert.False(run.IsCompleted);

        evidence = new(true, 0, Target);
        time.Advance(watch.Poll);
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(UpdateApplyEnd.Completed, result.End);
        Assert.Null(result.Code);
        Assert.Equal(Target, result.ActiveVersion);
        Assert.Equal(TimeSpan.FromSeconds(1), result.Elapsed);
        Assert.Equal(time.GetUtcNow(), result.At);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Missing_worker_completion_remains_unknown_after_the_deadline(bool? finished)
    {
        evidence = new(finished, 0, Target);
        var watch = Watch();
        var run = watch.RunAsync(Target);
        Assert.False(run.IsCompleted);
        time.Advance(watch.Window);

        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(UpdateApplyEnd.Unknown, result.End);
        Assert.Equal("timeout", result.Code);
        Assert.Equal("apply-completion", result.Step);
        Assert.Equal(watch.Window, result.Elapsed);
        Assert.Equal(time.GetUtcNow(), result.At);
    }

    [Theory]
    [InlineData(null, "version-unknown")]
    [InlineData("", "version-unknown")]
    [InlineData("0.1.199", "version-skew")]
    public async Task Successful_worker_exit_needs_a_fresh_matching_active_version(string? version, string code)
    {
        evidence = new(true, 0, version);

        var result = await Watch().RunAsync(Target);

        Assert.Equal(UpdateApplyEnd.Unknown, result.End);
        Assert.Equal(code, result.Code);
        Assert.Equal("active-version", result.Step);
        Assert.Equal(version, result.ActiveVersion);
        Assert.Equal(0, result.ApplyExitCode);
    }

    [Theory]
    [InlineData(null, UpdateApplyEnd.Unknown, "exit-unknown")]
    [InlineData(7, UpdateApplyEnd.Failed, "exit-failed")]
    [InlineData(0, UpdateApplyEnd.Completed, null)]
    public async Task Actual_worker_exit_is_required_even_when_the_target_is_active(
        int? exitCode, UpdateApplyEnd end, string? code)
    {
        evidence = new(true, exitCode, Target);

        var result = await Watch().RunAsync(Target);

        Assert.Equal(end, result.End);
        Assert.Equal(code, result.Code);
        Assert.Equal(exitCode, result.ApplyExitCode);
        Assert.Equal("apply-completion", result.Step);
    }

    [Theory]
    [InlineData(false, "IOException")]
    [InlineData(true, "UnauthorizedAccessException")]
    public async Task Unreadable_completion_is_unknown_without_exposing_raw_paths(bool unauthorized, string type)
    {
        Exception error = unauthorized
            ? new UnauthorizedAccessException("private installation path")
            : new IOException("private installation path");

        var result = await Watch(_ => throw error).RunAsync(Target);

        Assert.Equal(UpdateApplyEnd.Unknown, result.End);
        Assert.Equal("probe-unreadable", result.Code);
        Assert.Equal("apply-completion", result.Step);
        Assert.Equal(type, result.ErrorType);
        Assert.Null(result.ApplyExitCode);
        Assert.Null(result.ActiveVersion);
        Assert.DoesNotContain("private installation path", result.ToString());
    }

    [Fact]
    public async Task A_probe_that_never_returns_cannot_extend_the_observation_budget()
    {
        var worker = new TaskCompletionSource<UpdateApplyEvidence>();
        var watch = Watch(_ => worker.Task);
        var run = watch.RunAsync(Target);
        time.Advance(watch.Window);

        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(UpdateApplyEnd.Unknown, result.End);
        Assert.Equal("timeout", result.Code);
        Assert.Equal(watch.Window, result.Elapsed);
        Assert.False(worker.Task.IsCompleted);
    }

    [Fact]
    public async Task Person_cancellation_ends_observation_without_ending_the_worker()
    {
        var worker = new TaskCompletionSource<UpdateApplyEvidence>();
        using var cancel = new CancellationTokenSource();
        var run = Watch(_ => worker.Task).RunAsync(Target, cancel.Token);

        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(worker.Task.IsCompleted);
    }

    [Fact]
    public async Task An_already_cancelled_observation_does_not_probe()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var probes = 0;
        var watch = Watch(_ => { probes++; return Task.FromResult(evidence); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watch.RunAsync(Target, cancel.Token));

        Assert.Equal(0, probes);
    }
}
