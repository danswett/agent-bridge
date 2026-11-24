using System.Threading.Channels;
using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class UpdateApplyRetryTests
{
    readonly Clock time = new();
    int attempts;
    static UpdateApplyResult Result(UpdateApplyEnd end, int? exit = null, string? code = null,
        string step = "apply-completion") =>
        new(end, code, step, "0.1.200", exit, null, Make.T0, TimeSpan.FromSeconds(7));

    [Fact]
    public async Task A_finished_failed_apply_can_succeed_after_backoff_without_changing_the_result()
    {
        var completed = Result(UpdateApplyEnd.Completed, 0);
        var run = new UpdateApplyRetry(_ => Task.FromResult(++attempts == 1
            ? Result(UpdateApplyEnd.Failed, 32, "exit-failed") : completed), time).RunAsync();

        Assert.Equal(1, attempts);
        await time.Delay(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(1, attempts);
        time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Same(completed, await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Distinct_failures_stop_after_three_attempts_with_exponential_backoff()
    {
        UpdateApplyResult? last = null;
        var run = new UpdateApplyRetry(_ => Task.FromResult(last =
            Result(UpdateApplyEnd.Failed, ++attempts, "exit-failed")), time).RunAsync();

        await time.Delay(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));
        await time.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal(2, attempts);
        time.Advance(TimeSpan.FromSeconds(2));

        var actual = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(last, actual);
        Assert.Equal(UpdateApplyEnd.Failed, actual.End);
        Assert.Equal(3, attempts);
        Assert.Equal(TimeSpan.FromSeconds(7), actual.Elapsed);
        Assert.False(time.Delays.Reader.TryRead(out _));
    }

    [Fact]
    public async Task An_identical_failure_stops_even_when_its_timestamp_and_elapsed_time_changed()
    {
        var failure = Result(UpdateApplyEnd.Failed, 32, "exit-failed");
        UpdateApplyResult? last = null;
        var run = new UpdateApplyRetry(_ =>
        {
            attempts++;
            last = failure with { At = time.GetUtcNow(), Elapsed = TimeSpan.FromSeconds(attempts) };
            return Task.FromResult(last);
        }, time).RunAsync();
        await time.Delay(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Same(await run.WaitAsync(TimeSpan.FromSeconds(5)), last);
        Assert.Equal(2, attempts);
        Assert.False(time.Delays.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(UpdateApplyEnd.Unknown, "timeout", -1, "apply-completion")]
    [InlineData(UpdateApplyEnd.Unknown, "exit-failed", 32, "apply-completion")]
    [InlineData(UpdateApplyEnd.Unknown, "version-skew", 0, "active-version")]
    [InlineData(UpdateApplyEnd.Completed, null, 0, "respawn")]
    [InlineData(UpdateApplyEnd.Failed, "exit-failed", null, "apply-completion")]
    [InlineData(UpdateApplyEnd.Failed, "exit-failed", 0, "apply-completion")]
    [InlineData(UpdateApplyEnd.Failed, "checksum-mismatch", 32, "package-custody")]
    [InlineData(UpdateApplyEnd.Failed, "exit-failed", 32, "respawn")]
    public async Task Only_a_known_failed_finished_apply_authorizes_another_attempt(
        UpdateApplyEnd end, string? code, int? exit, string step)
    {
        var result = Result(end, exit, code, step);
        var actual = await new UpdateApplyRetry(_ => { attempts++; return Task.FromResult(result); }, time).RunAsync();

        Assert.Same(result, actual);
        Assert.Equal(1, attempts);
        Assert.False(time.Delays.Reader.TryRead(out _));
    }

    [Fact]
    public async Task A_checksum_custody_exception_is_not_retried_or_reclassified()
    {
        var error = new PackageMismatchException("verified package changed");
        var actual = await Assert.ThrowsAsync<PackageMismatchException>(() => new UpdateApplyRetry(_ =>
        {
            attempts++;
            throw error;
        }, time).RunAsync());

        Assert.Same(error, actual);
        Assert.Equal(1, attempts);
        Assert.False(time.Delays.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Cancellation_during_backoff_never_starts_another_attempt()
    {
        using var cancel = new CancellationTokenSource();
        var run = new UpdateApplyRetry(ct =>
        {
            Assert.Equal(cancel.Token, ct);
            attempts++;
            return Task.FromResult(Result(UpdateApplyEnd.Failed, 32, "exit-failed"));
        }, time).RunAsync(cancel.Token);
        await time.Delay(TimeSpan.FromSeconds(1));

        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(1, attempts);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Cancellation_before_the_first_attempt_does_no_work()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UpdateApplyRetry(_ =>
        {
            attempts++;
            return Task.FromResult(Result(UpdateApplyEnd.Completed, 0));
        }, time).RunAsync(cancel.Token));
        Assert.Equal(0, attempts);
    }

    [Theory]
    [InlineData(UpdateApplyEnd.Completed)]
    [InlineData(UpdateApplyEnd.Unknown)]
    [InlineData(UpdateApplyEnd.Failed)]
    public async Task Cancellation_arriving_with_a_terminal_result_does_not_reinterpret_that_result(UpdateApplyEnd end)
    {
        using var cancel = new CancellationTokenSource();
        var result = Result(end, 0);
        var actual = await new UpdateApplyRetry(_ =>
        {
            cancel.Cancel();
            return Task.FromResult(result);
        }, time).RunAsync(cancel.Token);

        Assert.Same(result, actual);
        Assert.False(time.Delays.Reader.TryRead(out _));
    }

    static IOException Sharing(int code = 32) => new("fixture holder", unchecked((int)0x80070000) | code);

    [Fact]
    public async Task A_holder_released_during_backoff_allows_a_closed_precommit_attempt_to_succeed()
    {
        var held = true;
        var run = UpdateApplyRetry.BeforeCommitAsync(commit =>
        {
            attempts++;
            if (held) throw Sharing();
            commit();
            return Task.FromResult("verified target");
        }, time);
        await time.Delay(TimeSpan.FromSeconds(1));
        held = false;
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("verified target", await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Repeated_precommit_holder_failure_is_rethrown_unchanged_without_a_third_attempt()
    {
        var error = Sharing();
        var run = UpdateApplyRetry.BeforeCommitAsync<string>(_ =>
        {
            attempts++;
            throw error;
        }, time);
        await time.Delay(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => run));
        Assert.Equal(2, attempts);
        Assert.False(time.Delays.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(true, 32)]
    [InlineData(false, 5)]
    public async Task Crossing_the_commit_boundary_or_an_unrecognized_failure_never_authorizes_retry(bool committed, int code)
    {
        var error = Sharing(code);
        var run = UpdateApplyRetry.BeforeCommitAsync<string>(commit =>
        {
            attempts++;
            if (committed) commit();
            throw error;
        }, time);

        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => run));
        Assert.Equal(1, attempts);
        Assert.False(time.Delays.Reader.TryRead(out _));
    }

    [Fact]
    public async Task An_unfinished_precommit_attempt_cannot_start_a_second_attempt_after_cancellation()
    {
        using var cancel = new CancellationTokenSource();
        var finished = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = UpdateApplyRetry.BeforeCommitAsync(_ => { attempts++; return finished.Task; }, time, cancel.Token);
        cancel.Cancel();
        Assert.False(run.IsCompleted);
        finished.SetException(Sharing());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(1, attempts);
        Assert.False(time.Delays.Reader.TryRead(out _));
    }

    sealed class Clock() : FakeTimeProvider(Make.T0)
    {
        public Channel<TimeSpan> Delays { get; } = Channel.CreateUnbounded<TimeSpan>();
        public async Task Delay(TimeSpan expected) =>
            Assert.Equal(expected, await Delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Delays.Writer.TryWrite(dueTime);
            return timer;
        }
    }
}
