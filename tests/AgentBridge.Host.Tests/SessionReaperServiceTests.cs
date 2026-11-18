using System.Text.Json;
using System.Threading.Channels;
using AgentBridge.Host.Reaping;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class SessionReaperServiceTests : IAsyncLifetime
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ab-reaper-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-11-18T05:00:00Z"));
    readonly List<string> log = [];
    static SessionReaperSettings Settings => new() { MachineId = "fixture-vm", IsVm = true, OwnerLaptop = false, Live = true };
    SessionReaperService Service(Func<CancellationToken, Task<ReaperSnapshot>> read) =>
        new(folder, "fixture-vm", read, () => Settings, log.Add, clock);
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } return Task.CompletedTask; }
    static Task<ReaperSnapshot> Read(CancellationToken _) =>
        Task.FromResult(SessionReaperPolicyTests.Snapshot(SessionReaperPolicyTests.Builder()));

    [Fact]
    public async Task First_cycle_is_persistent_dry_run_even_when_live_is_requested()
    {
        await using var service = Service(Read);
        var result = Assert.IsType<SessionReaperReceipt>(await service.CycleAsync());
        Assert.Equal("dry-run", result.Mode);
        Assert.Empty(result.Ended);
        Assert.Equal("dry-run: eligible", Assert.Single(result.Kept).Reason);
        var file = Assert.Single(Directory.GetFiles(Path.Combine(folder, "reaper", "receipts"), "*.json"));
        using var json = JsonDocument.Parse(File.ReadAllText(file));
        Assert.Equal(result.Id, json.RootElement.GetProperty("id").GetString());
        Assert.Equal("fixture-vm", json.RootElement.GetProperty("machine").GetString());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp"));
        Assert.NotEmpty(log);
    }

    [Fact]
    public async Task Timer_starts_once_ticks_at_fifteen_minutes_and_stops_with_host_lifetime()
    {
        var calls = 0;
        var receipts = Channel.CreateUnbounded<SessionReaperReceipt>();
        await using var service = Service(ct => { Interlocked.Increment(ref calls); return Read(ct); });
        service.Completed += receipt => receipts.Writer.TryWrite(receipt);
        service.Start(); service.Start();
        await receipts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromMinutes(1));
        await receipts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, calls);
        await service.DisposeAsync();
        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Overlapping_cycles_do_not_start_a_second_read_and_timeout_leaves_an_explicit_receipt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ReaperSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = Service(_ => { entered.TrySetResult(); return release.Task; });
        var first = service.CycleAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(await service.CycleAsync());
        clock.Advance(TimeSpan.FromMinutes(2));
        var failed = Assert.IsType<SessionReaperReceipt>(await first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.NotNull(failed.Problem);
        Assert.Empty(failed.Ended);
        Assert.NotEmpty(log);
        var blocked = Assert.IsType<SessionReaperReceipt>(await service.CycleAsync());
        Assert.NotNull(blocked.Problem);
        release.SetResult(await Read(default));
    }

    [Fact]
    public async Task Synchronous_read_runs_on_background_pool_and_deadline_does_not_wait_for_it()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var service = Service(ct =>
        {
            Interlocked.Increment(ref calls);
            entered.SetResult(Thread.CurrentThread);
            release.Wait();
            return Read(ct);
        });
        var cycle = Task.Run(() => service.CycleAsync());
        try
        {
            var reader = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            clock.Advance(TimeSpan.FromMinutes(2));
            var failed = Assert.IsType<SessionReaperReceipt>(await cycle.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.NotNull(failed.Problem);
            Assert.Empty(failed.Kept);
            Assert.True(reader.IsThreadPoolThread);
            Assert.True(reader.IsBackground);
            Assert.NotNull(Assert.IsType<SessionReaperReceipt>(await service.CycleAsync()).Problem);
            Assert.Equal(1, calls);
        }
        finally
        {
            release.Set();
            await cycle.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Read_completing_after_deadline_is_never_planned_or_surfaced()
    {
        await using var service = Service(ct =>
        {
            clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromTicks(1));
            return Task.FromResult(SessionReaperPolicyTests.Snapshot(SessionReaperPolicyTests.Builder("late-evidence")));
        });
        var receipts = new List<SessionReaperReceipt>();
        service.Completed += receipts.Add;
        var failed = Assert.IsType<SessionReaperReceipt>(await service.CycleAsync());
        Assert.NotNull(failed.Problem);
        Assert.Empty(failed.Kept);
        Assert.Empty(failed.Ended);
        Assert.Same(failed, Assert.Single(receipts));
        var file = Assert.Single(Directory.GetFiles(Path.Combine(folder, "reaper", "receipts"), "*.json"));
        Assert.DoesNotContain("late-evidence", File.ReadAllText(file));
    }

    [Fact]
    public async Task Shutdown_does_not_wait_for_a_synchronous_read_to_return()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = Service(ct =>
        {
            entered.SetResult();
            release.Wait();
            return Read(ct);
        });
        service.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var shutdown = service.DisposeAsync().AsTask();
        try { await shutdown.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally
        {
            release.Set();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Unreadable_registry_is_not_an_empty_success_and_receipt_IO_failure_is_reported()
    {
        await using var service = Service(_ => throw new IOException("fixture-private"));
        var failed = Assert.IsType<SessionReaperReceipt>(await service.CycleAsync());
        Assert.NotNull(failed.Problem);
        Assert.DoesNotContain("fixture-private", failed.Problem);
        File.WriteAllText(Path.Combine(folder, "reaper", "receipts", "blocked"), "");
        var root = Path.Combine(folder, "not-a-directory");
        File.WriteAllText(root, "");
        await using var blocked = new SessionReaperService(root, "fixture-vm", Read, () => Settings, log.Add, clock);
        await Assert.ThrowsAnyAsync<IOException>(() => blocked.CycleAsync());
    }
}
