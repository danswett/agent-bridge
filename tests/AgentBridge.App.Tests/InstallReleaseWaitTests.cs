using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class InstallReleaseWaitTests : IAsyncLifetime
{
    readonly FakeHost host = new();
    readonly FakeTasks tasks = new();
    readonly FakeProcesses processes = new();
    readonly string app = Path.Combine(Path.GetTempPath(), "abwait-" + Guid.NewGuid().ToString("N")[..8]);

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "runtime.dll"), "the old install");
        File.WriteAllText(Path.Combine(app, OperatingSystem.IsWindows() ? "agent-bridge-host.exe" : "agent-bridge-host"), "");
        File.WriteAllText(Path.Combine(app, OperatingSystem.IsWindows() ? "agent-bridge-hostw.exe" : "agent-bridge-hostw"), "");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await host.DisposeAsync();
        Directory.Delete(app, recursive: true);
    }

    HostManager Manager(FakeTimeProvider clock) =>
        new(new InstallLayout(app, host.Folder, "test"), tasks, processes,
            ct => LocalClient.ConnectAsync(host.Endpoint, TimeSpan.FromMilliseconds(300), ct),
            userId: @"PC\dana", userSid: "S-1-5-21-1-2-3-1001")
        {
            Time = clock, PackageHosts = () => [],
            StartTimeout = TimeSpan.FromSeconds(5), StopTimeout = TimeSpan.FromSeconds(2),
            Poll = TimeSpan.FromMilliseconds(50), Elevated = () => false,
        };

    internal static async Task FinishAsync(Task work, Func<bool> advance, params FakeTimeProvider[] clocks)
    {
        await Wait.UntilAsync(() => work.IsCompleted, "the bounded installation file wait",
            TimeSpan.FromSeconds(5), null, TimeProvider.System, () =>
            {
                if (advance()) { foreach (var clock in clocks) { clock.Advance(TimeSpan.FromSeconds(1)); } }
                return Task.Delay(5);
            });
        await work;
    }

    [Theory]
    [InlineData("a fixture holding runtime.dll")]
    [InlineData("Connected User Experiences and Telemetry (DiagTrack, pid 4242)")]
    public async Task A_holder_that_never_releases_is_waited_for_two_minutes_without_recording_or_stopping_the_install(string holder)
    {
        var clock = new FakeTimeProvider(Make.T0);
        var begun = false;
        var applied = false;
        var probes = 0;
        var update = Manager(clock).StopAndApplyAsync(() => begun = true, () => applied = true,
            _ => { Interlocked.Increment(ref probes); return holder; });

        var failed = await Assert.ThrowsAsync<UpdateBlockedException>(() => FinishAsync(update, () => true, clock));

        Assert.True(clock.GetUtcNow() - Make.T0 >= TimeSpan.FromMinutes(2));
        Assert.True(probes > 1);
        Assert.Contains(holder, failed.Message, StringComparison.Ordinal);
        Assert.False(begun);
        Assert.False(applied);
        Assert.Empty(host.Sent(HostOps.Stop));
        Assert.Empty(processes.Started);
        Assert.Equal("the old install", File.ReadAllText(Path.Combine(app, "runtime.dll")));
    }

    [Fact]
    public async Task An_unreadable_holder_probe_is_never_treated_as_clear_or_as_permission_to_stop_the_host()
    {
        var clock = new FakeTimeProvider(Make.T0);
        var begun = false;
        var update = Manager(clock).StopAndApplyAsync(() => begun = true, () => begun = true,
            _ => throw new UnauthorizedAccessException("private-path must stay out of recovery text"));

        var failed = await Assert.ThrowsAsync<UpdateBlockedException>(() => FinishAsync(update, () => true, clock));

        Assert.True(clock.GetUtcNow() - Make.T0 >= TimeSpan.FromMinutes(2));
        Assert.DoesNotContain("private-path", failed.Message, StringComparison.Ordinal);
        Assert.Contains("could not be checked", failed.Message, StringComparison.Ordinal);
        Assert.False(begun);
        Assert.Empty(host.Sent(HostOps.Stop));
    }

    [Fact]
    public async Task A_failed_locked_image_listing_at_the_last_check_blocks_as_unchecked_not_as_a_raw_error()
    {
        var clock = new FakeTimeProvider(Make.T0);
        var budget = new InstallationFiles.Budget(clock) { LockedFiles = () => throw new IOException("private-path") };

        var failed = await Assert.ThrowsAsync<UpdateBlockedException>(() => Manager(clock).CheckInstallationFilesAsync(_ => null, budget));

        Assert.Contains("could not be checked", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-path", failed.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_changed_probe_gets_a_fresh_scan_deadline_and_can_be_quit(bool quitting)
    {
        var clock = new FakeTimeProvider(Make.T0);
        var manager = Manager(clock);
        var budget = new InstallationFiles.Budget(clock);
        await manager.WaitForInstallationFilesAsync(_ => null, fileBudget: budget);
        clock.Advance(TimeSpan.FromSeconds(90));
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var begun = false;
        var update = manager.StopAndApplyAsync(() => begun = true, () => begun = true,
            token => { entered.TrySetResult(token); release.Task.GetAwaiter().GetResult(); return null; }, waitForFiles: false, fileBudget: budget);
        try
        {
            var scan = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (quitting) { manager.Quit(TimeSpan.FromSeconds(1), _ => { }); }
            else { clock.Advance(InstallationFiles.ReleaseWait); }
            if (quitting) { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update.WaitAsync(TimeSpan.FromSeconds(5))); }
            else { await Assert.ThrowsAsync<UpdateBlockedException>(() => update.WaitAsync(TimeSpan.FromSeconds(5))); }
            // The stuck scan itself is told to stop, so it makes no further Restart Manager query.
            Assert.True(scan.IsCancellationRequested);
            Assert.False(begun);
            Assert.Empty(host.Sent(HostOps.Stop));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_real_held_file_that_releases_is_rechecked_before_the_host_stops_and_the_update_continues()
    {
        var clock = new ArmedTimerClock(Make.T0);
        var file = Path.Combine(app, "runtime.dll");
        using var holder = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var probes = 0;
        var probed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? Probe()
        {
            string? reason;
            try { using var check = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None); reason = null; }
            catch (IOException) { reason = "a fixture holding runtime.dll"; }
            Interlocked.Increment(ref probes);
            return reason;
        }
        Assert.NotNull(Probe());
        probes = 0;
        host.StopsWhenAsked();
        host.Start();
        var begun = false;
        var applied = false;
        // Signalled only from the update's own probe call, after the pre-call above, so it cannot be
        // satisfied before the update has seen the held file; the wait keeps the helper's patience as its bound.
        var update = Manager(clock).StopAndApplyAsync(() => begun = true, () => applied = true,
            _ => { var reason = Probe(); probed.TrySetResult(); return reason; });
        await probed.Task.WaitAsync(FakeHost.Patience);
        Assert.Empty(host.Sent(HostOps.Stop));
        holder.Dispose();

        // The fake clock moves only while the update has a timer armed to move: a tick that lands between
        // two of its waits moves nothing and used up one of FinishAsync's 250 pauses (#2558). FinishAsync's
        // own bound stays, as the watchdog.
        await FinishAsync(update, () => !begun && clock.TimersArmed > 0, clock);

        Assert.True(probes > 1);
        Assert.True(applied);
        Assert.Single(host.Sent(HostOps.Stop));
        Assert.Equal("the old install", File.ReadAllText(file));
    }

    // A fake clock that knows how many wait timers are armed (created and not yet disposed), so a test can
    // advance it only when something is waiting on it. A timer due after the whole two-minute release
    // wait is a scan's own deadline (CancellationTokenSource(ReleaseWait, Time)), armed for the length of
    // every probe, so it says nothing about the update waiting: it is not counted.
    sealed class ArmedTimerClock(DateTimeOffset start) : FakeTimeProvider(start)
    {
        int armed;

        public int TimersArmed => Volatile.Read(ref armed);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var inner = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime >= InstallationFiles.ReleaseWait) { return inner; }
            Interlocked.Increment(ref armed);
            return new Counted(inner, () => Interlocked.Decrement(ref armed));
        }

        sealed class Counted(ITimer inner, Action disposed) : ITimer
        {
            int done;

            public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);

            public void Dispose()
            {
                inner.Dispose();
                if (Interlocked.Exchange(ref done, 1) == 0) { disposed(); }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
