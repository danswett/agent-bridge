using System.Diagnostics;
using AgentBridge.App.Core;
using AgentBridge.Host.Core.Processes;
using AgentBridge.HostLauncher;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class UpdaterStartAdmissionTests
{
    [Fact]
    public async Task Cancellation_before_native_admission_never_invokes_the_native_start()
    {
        using var cancel = new CancellationTokenSource();
        var calls = 0;
        var result = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(ProcessLaunch.Start(new ProcessStartInfo("unused"),
                _ => Interlocked.Increment(ref calls), cancel.Token)); }
            catch (Exception error) { result.SetException(error); }
        }) { IsBackground = true };
        using (ProcessLaunch.HoldInheritableHandles())
        {
            thread.Start();
            Assert.True(SpinWait.SpinUntil(() => (thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(5)));
            cancel.Cancel();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cancelled_or_unknown_apply_cannot_start_after_the_native_gate_reopens(bool deadline)
    {
        var root = Path.Combine(Path.GetTempPath(), "admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "packages"));
        var package = Path.Combine(root, "target.nupkg");
        File.WriteAllText(package, "verified");
        using var cancel = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        using var held = new ManualResetEventSlim();
        var pinReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new FakeTimeProvider();
        var calls = 0;
        var adapter = new UpdateCoordinatorAdapter(new(root, Path.Combine(root, "packages"), package,
            new(PackageDigest.Sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(package)))),
            "AgentBridgeApp", "0.1.200", [], 11, 123), _ => null,
            _ => { Interlocked.Increment(ref calls); throw new InvalidOperationException("native start forbidden"); },
            clock, _ => { pinReached.SetResult(); return new MemoryStream(); });
        var holder = Task.Factory.StartNew(() =>
        {
            using var lease = ProcessLaunch.HoldInheritableHandles();
            held.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(5)));
            var run = adapter.RunAsync(cancel.Token);
            await pinReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (deadline)
            {
                clock.Advance(TimeSpan.FromMinutes(10));
                Assert.Equal(UpdateApplyEnd.Unknown, (await run.WaitAsync(TimeSpan.FromSeconds(5))).End);
            }
            else
            {
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            }
            release.Set();
            await holder;
            try { await adapter.Ownership.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            Assert.Equal(0, calls);
            Assert.Null(adapter.ApplyProcessId);
        }
        finally
        {
            release.Set();
            await holder;
            Directory.Delete(root, recursive: true);
        }
    }
}
