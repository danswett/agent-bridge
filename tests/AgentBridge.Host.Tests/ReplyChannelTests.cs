using Xunit;

public sealed class ReplyChannelTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Opening_a_reply_channel_waits_for_each_peers_presence_before_sending_a_handshake(bool holdCaller)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var opening = ReplyChannel.OpenAsync(
            () => holdCaller ? ready.Task : Task.CompletedTask,
            () => holdCaller ? Task.CompletedTask : ready.Task,
            () => { Interlocked.Increment(ref attempts); return Task.FromResult("opened"); },
            () => "fixture state");
        try { Assert.Equal(0, Volatile.Read(ref attempts)); }
        finally { ready.TrySetResult(); }

        Assert.Equal("opened", await opening);
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_reply_channel_timeout_keeps_its_cause_and_reports_the_fixture_state(bool duringOpen)
    {
        var cause = new TimeoutException("the operation timed out");
        var error = await Assert.ThrowsAsync<TimeoutException>(() => ReplyChannel.OpenAsync(
            () => duringOpen ? Task.CompletedTask : Task.FromException(cause),
            () => Task.CompletedTask,
            () => Task.FromException<string>(cause),
            () => "fixture state: target pending, no response frame"));

        Assert.Same(cause, error.InnerException);
        Assert.Contains("fixture state: target pending, no response frame", error.Message);
    }
}
