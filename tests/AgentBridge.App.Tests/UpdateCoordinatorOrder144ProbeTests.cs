using AgentBridge.App.Core;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class UpdateCoordinatorOrder144ProbeTests
{
    [Fact]
    public async Task Cancellation_while_respawning_must_not_hide_an_unlock_failure()
    {
        using var cancel = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new UpdateCoordinator(_ => Task.CompletedTask,
            (generation, _) => Task.FromResult(new UpdateApplyEvidence(true, 0, "0.1.200", generation)),
            () => new BrokenLease(), () => "0.1.200", () =>
            {
                entered.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            });
        var run = coordinator.RunAsync("0.1.200", cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(UpdateApplyEnd.Unknown, result.End);
            Assert.Equal("package-unlock", result.Step);
            Assert.Equal(nameof(IOException), result.ErrorType);
        }
        finally
        {
            release.Set();
            await coordinator.RespawnFinished.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    sealed class BrokenLease : IDisposable
    {
        public void Dispose() => throw new IOException("private lock path");
    }
}
