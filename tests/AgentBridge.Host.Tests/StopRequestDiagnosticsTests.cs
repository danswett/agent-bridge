using Xunit;

public sealed class StopRequestDiagnosticsTests
{
    [Fact]
    public async Task A_stop_timeout_keeps_its_cause_and_observed_stage_without_retrying_the_press()
    {
        var response = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var presses = 0;
        var snapshots = 0;
        var stopping = StopFlowTests.ObserveStopAsync(
            () => { presses++; return response.Task; },
            () => { snapshots++; return "handler entered; injections 0"; });
        Assert.Equal(1, presses);
        Assert.Equal(0, snapshots);

        var cause = new TimeoutException("the operation timed out");
        response.SetException(cause);
        var error = await Assert.ThrowsAsync<TimeoutException>(() => stopping);

        Assert.Same(cause, error.InnerException);
        Assert.Contains("handler entered; injections 0", error.Message);
        Assert.Equal(1, presses);
        Assert.Equal(1, snapshots);
    }

    [Fact]
    public async Task A_stop_failure_other_than_timeout_is_not_relabelled_or_retried()
    {
        var cause = new IOException("channel closed");
        var presses = 0;
        var error = await Assert.ThrowsAsync<IOException>(() => StopFlowTests.ObserveStopAsync<int>(
            () => { presses++; return Task.FromException<int>(cause); },
            () => throw new InvalidOperationException("timeout diagnostics must not run")));

        Assert.Same(cause, error);
        Assert.Equal(1, presses);
    }
}
