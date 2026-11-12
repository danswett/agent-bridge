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
            () => { Interlocked.Increment(ref probes); return holder; });

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
            () => throw new UnauthorizedAccessException("private-path must stay out of recovery text"));

        var failed = await Assert.ThrowsAsync<UpdateBlockedException>(() => FinishAsync(update, () => true, clock));

        Assert.True(clock.GetUtcNow() - Make.T0 >= TimeSpan.FromMinutes(2));
        Assert.DoesNotContain("private-path", failed.Message, StringComparison.Ordinal);
        Assert.Contains("could not be checked", failed.Message, StringComparison.Ordinal);
        Assert.False(begun);
        Assert.Empty(host.Sent(HostOps.Stop));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_clear_scan_followed_by_a_stalled_final_probe_uses_the_remaining_deadline_and_can_be_quit(bool quitting)
    {
        var clock = new FakeTimeProvider(Make.T0);
        var manager = Manager(clock);
        var budget = new InstallationFiles.Budget(clock);
        await manager.WaitForInstallationFilesAsync(() => null, fileBudget: budget);
        clock.Advance(TimeSpan.FromSeconds(90));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var begun = false;
        var update = manager.StopAndApplyAsync(() => begun = true, () => begun = true,
            () => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); return null; }, waitForFiles: false, fileBudget: budget);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (quitting) { manager.Quit(TimeSpan.FromSeconds(1), _ => { }); }
            else { clock.Advance(TimeSpan.FromSeconds(30)); }
            if (quitting) { await Assert.ThrowsAsync<OperationCanceledException>(() => update.WaitAsync(TimeSpan.FromSeconds(5))); }
            else { await Assert.ThrowsAsync<UpdateBlockedException>(() => update.WaitAsync(TimeSpan.FromSeconds(5))); }
            Assert.False(begun);
            Assert.Empty(host.Sent(HostOps.Stop));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_real_held_file_that_releases_is_rechecked_before_the_host_stops_and_the_update_continues()
    {
        var clock = new FakeTimeProvider(Make.T0);
        var file = Path.Combine(app, "runtime.dll");
        using var holder = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var probes = 0;
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
        var update = Manager(clock).StopAndApplyAsync(() => begun = true, () => applied = true, Probe);
        await Wait.UntilAsync(() => Volatile.Read(ref probes) > 0, "the held file being checked");
        Assert.Empty(host.Sent(HostOps.Stop));
        holder.Dispose();

        await FinishAsync(update, () => !begun, clock);

        Assert.True(probes > 1);
        Assert.True(applied);
        Assert.Single(host.Sent(HostOps.Stop));
        Assert.Equal("the old install", File.ReadAllText(file));
    }
}
