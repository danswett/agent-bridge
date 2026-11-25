using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class UpdateApplyCompletionTests
{
    const string Target = "0.1.200";
    readonly FakeTimeProvider time = new(Make.T0);
    UpdateApplyEvidence evidence = new(null, null, null);

    UpdateApplyCompletion Watch(Func<CancellationToken, Task<UpdateApplyEvidence>>? probe = null, int pollSeconds = 1) =>
        new((generation, ct) => probe is null
            ? Task.FromResult(evidence with { Generation = generation }) : probe(ct), time)
        {
            Window = TimeSpan.FromSeconds(5),
            Poll = TimeSpan.FromSeconds(pollSeconds)
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
        Assert.Equal(Target, result.TargetVersion);
        Assert.Equal(Target, result.ActiveVersion);
        Assert.Equal(TimeSpan.FromSeconds(1), result.Elapsed);
        Assert.Equal(time.GetUtcNow(), result.At);
    }

    [Theory]
    [InlineData(false, 0, 1)]
    [InlineData(null, 0, 1)]
    [InlineData(false, 7, 1)]
    [InlineData(false, 0, 30)]
    public async Task Missing_worker_completion_remains_unknown_after_the_deadline(bool? finished, int exitCode, int pollSeconds)
    {
        evidence = new(finished, exitCode, Target);
        var watch = Watch(pollSeconds: pollSeconds);
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
    [InlineData("0.0.0")]
    [InlineData(" v0.0.0+build123 ")]
    public async Task An_unknown_target_version_is_never_reported_as_version_skew(string target)
    {
        evidence = new(true, 0, Target);

        var result = await Watch().RunAsync(target);

        Assert.Equal(UpdateApplyEnd.Unknown, result.End);
        Assert.Equal("target-unknown", result.Code);
        Assert.Equal("target-version", result.Step);
        Assert.Equal(target, result.TargetVersion);
        Assert.Equal(Target, result.ActiveVersion);
    }

    [Theory]
    [InlineData(null, UpdateApplyEnd.Unknown, "exit-unknown")]
    [InlineData(7, UpdateApplyEnd.Failed, "exit-failed")]
    [InlineData(-1, UpdateApplyEnd.Failed, "exit-failed")]
    [InlineData(int.MinValue, UpdateApplyEnd.Failed, "exit-failed")]
    [InlineData(int.MaxValue, UpdateApplyEnd.Failed, "exit-failed")]
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_result_arriving_after_the_deadline_cannot_replace_the_timeout(bool unreadable)
    {
        var watch = Watch(_ =>
        {
            time.Advance(TimeSpan.FromSeconds(5));
            if (unreadable) throw new IOException("late read failure");
            return Task.FromResult(new UpdateApplyEvidence(true, 0, Target));
        });

        var result = await watch.RunAsync(Target);

        Assert.Equal(UpdateApplyEnd.Unknown, result.End);
        Assert.Equal("timeout", result.Code);
        Assert.Equal(watch.Window, result.Elapsed);
        Assert.Equal(unreadable ? "IOException" : null, result.ErrorType);
    }

    [Fact]
    public async Task A_process_query_failure_retains_the_native_code_but_not_its_message()
    {
        var error = new System.ComponentModel.Win32Exception(5, "private installation path");

        var result = await Watch(_ => throw error).RunAsync(Target);

        Assert.Equal(UpdateApplyEnd.Unknown, result.End);
        Assert.Equal("probe-unreadable", result.Code);
        Assert.Equal("Win32Exception", result.ErrorType);
        Assert.Equal(5, result.ErrorCode);
        Assert.DoesNotContain("private installation path", result.ToString());
    }

    [Fact]
    public async Task A_probe_cancelling_itself_is_not_person_cancellation_or_an_apply_failure()
    {
        var result = await Watch(_ => Task.FromCanceled<UpdateApplyEvidence>(
            new CancellationToken(true))).RunAsync(Target);

        Assert.Equal(UpdateApplyEnd.Unknown, result.End);
        Assert.Equal("probe-cancelled", result.Code);
        Assert.Equal("apply-completion", result.Step);
    }

    [Theory]
    [InlineData("invalid-operation")]
    [InlineData("invalid-data")]
    [InlineData("format")]
    [InlineData("not-supported")]
    [InlineData("security")]
    [InlineData("overflow")]
    public async Task Every_probe_exception_is_unknown_without_its_sensitive_message(string kind)
    {
        const string message = "private probe detail";
        Exception error = kind switch
        {
            "invalid-operation" => new InvalidOperationException(message),
            "invalid-data" => new InvalidDataException(message),
            "format" => new FormatException(message),
            "not-supported" => new NotSupportedException(message),
            "security" => new System.Security.SecurityException(message),
            _ => new OverflowException(message),
        };
        var result = await Watch(_ => throw error).RunAsync(Target);
        Assert.Equal(UpdateApplyEnd.Unknown, result.End);
        Assert.Equal("probe-failed", result.Code);
        Assert.Equal(error.GetType().Name, result.ErrorType);
        Assert.DoesNotContain(message, result.ToString());
    }

    [Theory]
    [InlineData("0.1.200", "v0.1.200")]
    [InlineData("0.1.200", " 0.1.200")]
    [InlineData("0.1.200", "0.1.200+build123")]
    [InlineData("0.1.200-RC.1", "0.1.200-rc.1")]
    [InlineData("0.1.200-RC.1", " V0.1.200-rc.1+build123 ")]
    public async Task Completion_compares_normalized_release_identities(string target, string active)
    {
        evidence = new(true, 0, active);
        Assert.Equal(UpdateApplyEnd.Completed, (await Watch().RunAsync(target)).End);
    }

    [Fact]
    public async Task A_cached_equal_snapshot_without_this_operations_generation_is_not_completion()
    {
        var cached = new UpdateApplyEvidence(true, 0, Target, Guid.NewGuid());
        var result = await Watch(_ => Task.FromResult(cached)).RunAsync(Target);
        Assert.Equal(UpdateApplyEnd.Unknown, result.End);
        Assert.Equal("stale-evidence", result.Code);
        Assert.Null(result.ApplyExitCode);
        Assert.Null(result.ActiveVersion);
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
