using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

public sealed class TimeoutDiagnosticsTests
{
    [Fact]
    public async Task A_timed_out_Host_poll_and_outer_task_wait_report_running_collections_and_thread_counts()
    {
        var poll = await Assert.ThrowsAsync<TimeoutException>(() =>
            HostWait.UntilAsync(() => false, "diagnostic condition", TimeSpan.Zero));
        Assert.StartsWith("timed out waiting for diagnostic condition (", poll.Message);
        AssertSnapshot(poll.Message);

        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outer = await Assert.ThrowsAsync<TimeoutException>(() =>
            TestTimeoutDiagnostics.WaitAsync(pending.Task, TimeSpan.Zero));
        AssertSnapshot(outer.Message);
        Assert.IsType<TimeoutException>(outer.InnerException);
        Assert.False(pending.Task.IsCompleted);
    }

    static void AssertSnapshot(string message)
    {
        var snapshot = Regex.Match(message,
            @"pool threads \d+, queued \d+; pool workers available (\d+), busy (\d+), max (\d+); "
            + @"active test collections [1-9]\d*; total process threads [1-9]\d*\)");
        Assert.True(snapshot.Success, message);
        Assert.Equal(int.Parse(snapshot.Groups[3].Value),
            int.Parse(snapshot.Groups[1].Value) + int.Parse(snapshot.Groups[2].Value));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void Collection_diagnostics_unwind_when_lifecycle_reporting_is_refused_or_throws(bool atStart, bool throws)
    {
        var assembly = new TestAssembly(Reflector.Wrap(typeof(TimeoutDiagnosticsTests).Assembly));
        var collection = new TestCollection(assembly, null, "diagnostic lifecycle");
        var activity = new TestCollectionActivity();
        using var bus = new TestCollectionMessageBus(new CallbackBus(message =>
        {
            if ((message is ITestCollectionStarting) != atStart) { return true; }
            if (throws) { throw new InvalidOperationException("synthetic reporting failure"); }
            return false;
        }), activity);
        var starting = new TestCollectionStarting([], collection);
        if (!atStart)
        {
            Assert.True(bus.QueueMessage(starting));
            Assert.Equal(1, activity.Count);
        }

        IMessageSinkMessage boundary = atStart ? starting : new TestCollectionFinished([], collection, 0, 0, 0, 0);
        if (throws) { Assert.Throws<InvalidOperationException>(() => bus.QueueMessage(boundary)); }
        else { Assert.False(bus.QueueMessage(boundary)); }
        Assert.Equal(0, activity.Count);
    }

    sealed class CallbackBus(Func<IMessageSinkMessage, bool> send) : IMessageBus
    {
        public bool QueueMessage(IMessageSinkMessage message) => send(message);
        public void Dispose() { }
    }
}
