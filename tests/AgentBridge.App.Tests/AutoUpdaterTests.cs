using System.Globalization;
using System.Net;
using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Host.Core.Updates;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// Updates without being asked, over a host on a real pipe: when an update waits (a turn running,
// a question waiting), when it goes anyway (six hours), what it never does (stop another profile's
// host, apply what it did not download, leave the host down), and that a read-only host beside
// agent-ha-bridge updates like any other. Velopack and Task Scheduler are fakes.
public sealed class AutoUpdaterTests : IAsyncLifetime
{
    readonly FakeHost host = new();
    readonly Updater updater = new();
    readonly FakeTasks tasks = new();
    readonly FakeProcesses processes = new();
    readonly FakeTimeProvider time = new(Make.T0);
    // The host manager's start and stop timeouts, on a clock that never moves: they wait for the
    // fake host however slow the runner, as HostManagerTests' held stop does (#425). On the wall
    // clock a stall like #909's 7.4 s ran a 2 s stop or 5 s start out with the host on its way.
    // A host that never comes ends at --blame-hang; a test that needs a wait to run out (a usage
    // probe's, #238) advances this clock itself.
    readonly FakeTimeProvider held = new(Make.T0);
    readonly List<string> log = [];
    readonly string app = Path.Combine(Path.GetTempPath(), "abu-" + Guid.NewGuid().ToString("N")[..8]);
    AppPreferences prefs = new();
    HostConnection connection = null!;
    HostManager manager = null!;
    AutoUpdater auto = null!;
    string? others;
    int? othersAskedFor;
    Func<string?>? othersProbe;
    // How the app's link reaches the host, when a test makes it hang.
    Func<CancellationToken, Task<LocalChannel>>? linkConnect;
    // The app's updater as Settings shares it, when a test plays Settings too.
    ObservedUpdater? shared;

    sealed class Updater : IUpdater
    {
        public int Checks;
        public int Downloads;
        public bool Applied;
        public Action? OnApply;
        public string? Found = "0.2.0";
        public string? Ready { get; set; }
        public Exception? ApplyFails;
        public Exception? DownloadFails;
        public Exception? CheckFails;
        // Held until released, as a check that takes its time.
        public TaskCompletionSource? CheckGate;
        // What the check asks first, as Velopack asks its source for the release list.
        public Func<Task>? Asks;
        public string? InstalledVersion { get; set; } = "0.1.0";
        public DateTimeOffset? DiscoveredAt { get; set; }
        public string? PendingNotes => Found is null ? null : $"What {Found} changes";
        public async Task<string?> CheckAsync()
        {
            Checks++;
            if (CheckGate is { } gate) { await gate.Task; }
            if (Asks is { } ask) { await ask(); }
            if (CheckFails is { } e) { throw e; }
            return Found;
        }
        public Task DownloadAsync(Action<int> progress)
        {
            Downloads++;
            if (DownloadFails is { } e) { throw e; }
            progress(100);
            Ready = Found;
            return Task.CompletedTask;
        }
        public void ApplyAndRestart() { OnApply?.Invoke(); if (ApplyFails is { } e) { throw e; } Applied = true; }
    }

    sealed class Toasts : IToastSink
    {
        public List<ToastNotice> Shown { get; } = [];
        public void Show(ToastNotice notice) => Shown.Add(notice);
        public void Withdraw(string tag) { }
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, OperatingSystem.IsWindows() ? "agent-bridge-host.exe" : "agent-bridge-host"), "");
        File.WriteAllText(Path.Combine(app, OperatingSystem.IsWindows() ? "agent-bridge-hostw.exe" : "agent-bridge-hostw"), "");
        host.StopsWhenAsked();
        return Task.CompletedTask;
    }

    // The host as it is when the app starts: its status and sessions, then connected.
    async Task StartAsync(HostStatus? status = null, params Session[] sessions)
    {
        host.Status = status ?? FakeHost.StatusOf();
        host.Sessions.AddRange(sessions);
        host.Start();
        connection = linkConnect is { } connect
            ? new HostConnection(connect, new LockedDispatcher()) { RetryDelay = TimeSpan.FromMilliseconds(100) }
            : host.Connect(new LockedDispatcher(), start: false);
        connection.Start();
        manager = new HostManager(new InstallLayout(app, host.Folder, "test"), tasks, processes,
            ct => LocalClient.ConnectAsync(host.Endpoint, TimeSpan.FromMilliseconds(300), ct), userId: @"PC\dana", userSid: "S-1-5-21-1-2-3-1001")
        {
            StartTimeout = TimeSpan.FromSeconds(5),
            StopTimeout = TimeSpan.FromSeconds(2),
            Poll = TimeSpan.FromMilliseconds(50),
            // No machine's real processes: the package is what a test says it is.
            PackageHosts = () => [],
            ProbeWait = TimeSpan.FromSeconds(3),
            ProbePoll = TimeSpan.FromMilliseconds(50),
            Elevated = () => false,
            Time = held,
        };
        auto = new AutoUpdater((IUpdater?)shared ?? updater, connection, manager, () => prefs, p => prefs = p, log.Add, pid => { othersAskedFor = pid; return othersProbe is { } probe ? probe() : others; }, time);
        await Wait.UntilAsync(() => connection.State == LinkState.Connected && connection.Status is not null && connection.Sessions.All.Count == sessions.Length && connection.Sessions.Complete, "the connection");
    }

    public async Task DisposeAsync()
    {
        if (connection is not null) { await connection.DisposeAsync(); }
        await host.DisposeAsync();
        Directory.Delete(app, recursive: true);
    }

    // Idle at one tick, and still at the next once IdleFor has passed.
    async Task TwoIdleTicksAsync()
    {
        await auto.TickAsync();
        time.Advance(AutoUpdater.IdleFor);
        await auto.TickAsync();
    }

    async Task GoIdleAsync(string id)
    {
        await host.SendAsync(Events.SessionUpsert, Make.Session(id, SessionStatus.Idle));
        await Wait.UntilAsync(() => connection.Sessions.Get(id)?.Status == SessionStatus.Idle, "the session going idle");
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task An_update_found_while_no_session_is_busy_installs_once_the_host_has_stayed_idle_a_while()
    {
        await StartAsync(null, Make.Session("s1", SessionStatus.Idle), Make.Session("s2", SessionStatus.Ending));
        var stoppedFirst = false;
        var marked = false;
        updater.OnApply = () => (stoppedFirst, marked) = (host.Sent(HostOps.Stop).Count == 1, manager.UpdateUnderWay);

        await auto.TickAsync();
        Assert.Equal("0.2.0", auto.Pending);
        Assert.False(updater.Applied);

        time.Advance(AutoUpdater.IdleFor);
        await auto.TickAsync();

        Assert.True(updater.Applied);
        Assert.True(stoppedFirst);
        Assert.True(marked);
        Assert.Equal("0.1.0", prefs.UpdatedFrom);
        Assert.Equal("What 0.2.0 changes", prefs.UpdateNotes);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task An_update_due_while_a_Codex_probe_finishes_on_the_host_waits_until_the_host_says_it_is_done()
    {
        await StartAsync();
        var finishing = true;
        host.On(HostOps.ProbeHold, _ => new ProbeHoldResult(finishing, finishing ? "Codex's app-server, started to read its allowance, is still answering or finishing" : null));

        await TwoIdleTicksAsync();
        // Six hours on, it still waits: never cut short.
        time.Advance(AutoUpdater.WaitAtMost);
        await auto.TickAsync();

        Assert.False(updater.Applied);
        Assert.Empty(host.Sent(HostOps.Stop));
        Assert.True(host.Sent(HostOps.ProbeHold).Count >= 2);
        Assert.Single(log, l => l.Contains("waiting to install 0.2.0: Codex's app-server", StringComparison.Ordinal));

        finishing = false;
        await auto.TickAsync();

        Assert.True(updater.Applied);
        Assert.Single(host.Sent(HostOps.Stop));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Stopping_the_host_waits_until_it_says_no_usage_probe_is_finishing_before_asking_it_to_stop()
    {
        await StartAsync();
        var holds = 0;
        var holdsAtStop = -1;
        host.On(HostOps.ProbeHold, _ => new ProbeHoldResult(Interlocked.Increment(ref holds) < 3, "finishing"));
        host.On(HostOps.Stop, data =>
        {
            holdsAtStop = Volatile.Read(ref holds);
            _ = Task.Run(async () => { await Task.Delay(50); await host.StopAsync(); });
            return new { stopping = true };
        });

        await manager.StopAsync();

        Assert.Equal(3, holdsAtStop);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_stop_still_waiting_on_a_usage_probe_when_its_wait_is_up_refuses_rather_than_ends_the_host()
    {
        await StartAsync();
        host.On(HostOps.ProbeHold, _ => new ProbeHoldResult(true, "Codex's app-server is still finishing"));

        // The manager's clock never moves (held), so its wait is run out by hand: the deadline is
        // set before the first hold is asked, and is past once the clock goes beyond ProbeWait.
        var stopping = manager.StopAsync();
        await Wait.UntilAsync(() => host.Sent(HostOps.ProbeHold).Count >= 1, "the first hold");
        held.Advance(manager.ProbeWait + TimeSpan.FromSeconds(1));
        var busy = await Assert.ThrowsAsync<HostBusyException>(() => stopping);

        Assert.Contains("Codex's app-server is still finishing", busy.Message, StringComparison.Ordinal);
        Assert.Empty(host.Sent(HostOps.Stop));
        Assert.Empty(processes.Killed);
        Assert.True(await manager.IsRunningAsync());
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_hold_that_times_out_or_is_refused_never_reaches_the_stop_or_the_kill()
    {
        await StartAsync();
        // Only the hold is quick to give up: reaching a running host may take a loaded runner longer,
        // and a host not reached is taken for one already stopped.
        var quick = Manager(callTimeout: TimeSpan.FromMilliseconds(300), connectTimeout: TimeSpan.FromSeconds(5));

        // No answer in time.
        host.On(HostOps.ProbeHold, _ => { Thread.Sleep(2000); return new ProbeHoldResult(false); });
        var timedOut = await Assert.ThrowsAsync<HostBusyException>(() => quick.StopAsync());
        Assert.Contains("could not say whether a usage probe", timedOut.Message, StringComparison.Ordinal);

        // An answer that is an error.
        host.On(HostOps.ProbeHold, _ => throw new InvalidOperationException("the probes could not be looked at"));
        await Assert.ThrowsAsync<HostRefusedException>(() => quick.StopAsync());

        Assert.Empty(host.Sent(HostOps.Stop));
        Assert.Empty(processes.Killed);
        Assert.True(await quick.IsRunningAsync());
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task An_older_host_that_does_not_know_the_hold_is_stopped_as_before()
    {
        await StartAsync();

        await manager.StopAsync();

        Assert.Single(host.Sent(HostOps.ProbeHold));
        Assert.Single(host.Sent(HostOps.Stop));
        Assert.False(await manager.IsRunningAsync());
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task An_update_holds_and_waits_for_the_other_profiles_hosts_in_the_package_before_stopping_this_one()
    {
        await StartAsync();
        await using var other = new FakeHost();
        await using var elsewhere = new FakeHost();
        other.Start();
        elsewhere.Start();
        var otherHolds = 0;
        var otherHoldsAtStop = -1;
        other.On(HostOps.ProbeHold, _ => new ProbeHoldResult(Interlocked.Increment(ref otherHolds) < 3, "finishing"));
        elsewhere.Status = FakeHost.StatusOf() with { ProcessId = 5151 };
        host.On(HostOps.Stop, data =>
        {
            otherHoldsAtStop = Volatile.Read(ref otherHolds);
            _ = Task.Run(async () => { await Task.Delay(50); await host.StopAsync(); });
            return new { stopping = true };
        });
        other.Status = FakeHost.StatusOf() with { ProcessId = 6161 };
        var updating = Manager(others: [other.Folder, elsewhere.Folder, Path.Combine(app, "no-host-here")], package: [4242, 6161]);
        var applied = false;

        // Settings' Update and restart, and the automatic update, both come this way.
        await updating.StopAndApplyAsync(() => { }, () => applied = true);

        Assert.True(applied);
        Assert.Equal(3, otherHoldsAtStop);
        // Held again after this host stopped, just before the hand-over.
        Assert.True(Volatile.Read(ref otherHolds) > otherHoldsAtStop);
        // Left running for the update to end, not stopped; and one not from the package is not asked.
        Assert.Empty(other.Sent(HostOps.Stop));
        Assert.Empty(elsewhere.Sent(HostOps.ProbeHold));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Every_other_profiles_hold_is_renewed_while_another_is_waited_for()
    {
        await StartAsync();
        await using var slow = new FakeHost();
        await using var idle = new FakeHost();
        slow.Start();
        idle.Start();
        slow.Status = FakeHost.StatusOf() with { ProcessId = 6161 };
        idle.Status = FakeHost.StatusOf() with { ProcessId = 7171 };
        var slowHolds = 0;
        slow.On(HostOps.ProbeHold, _ => new ProbeHoldResult(Interlocked.Increment(ref slowHolds) < 6, "finishing"));
        idle.On(HostOps.ProbeHold, _ => new ProbeHoldResult(false));
        var updating = Manager(others: [slow.Folder, idle.Folder], package: [4242, 6161, 7171]);

        await updating.StopAndApplyAsync(() => { }, () => { });

        // Asked as often as the slow one in the first wait: a hold of 15 minutes never lapses
        // while another host's probe takes its 11.
        Assert.True(idle.Sent(HostOps.ProbeHold).Count >= 6, $"held {idle.Sent(HostOps.ProbeHold).Count} times");
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_profiles_host_started_while_the_last_hold_waits_is_held_before_the_update_goes_ahead()
    {
        await StartAsync();
        await using var busy = new FakeHost();
        await using var late = new FakeHost();
        busy.Start();
        busy.Status = FakeHost.StatusOf() with { ProcessId = 6161 };
        late.Status = FakeHost.StatusOf() with { ProcessId = 7171 };
        var started = false;
        var afterStop = 0;
        busy.On(HostOps.ProbeHold, _ =>
        {
            if (host.Sent(HostOps.Stop).Count == 0) { return new ProbeHoldResult(false); }
            // Busy a few rounds after this host stopped; another profile's host starts meanwhile.
            var n = Interlocked.Increment(ref afterStop);
            if (n == 2) { late.Start(); Volatile.Write(ref started, true); }
            return new ProbeHoldResult(n < 4, "finishing");
        });
        late.On(HostOps.ProbeHold, _ => new ProbeHoldResult(false));
        host.On(HostOps.Stop, data =>
        {
            _ = Task.Run(async () => { await Task.Delay(50); await host.StopAsync(); });
            return new { stopping = true };
        });
        var updating = Manager(others: [busy.Folder, late.Folder], packageNow: () => Volatile.Read(ref started) ? [4242, 6161, 7171] : [4242, 6161]);
        var applied = false;

        await updating.StopAndApplyAsync(() => { }, () => applied = true);

        Assert.True(applied);
        Assert.NotEmpty(late.Sent(HostOps.ProbeHold));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Quitting_while_an_update_waits_for_another_profiles_probe_after_stopping_this_host_starts_it_again_at_once()
    {
        await StartAsync();
        processes.OnStart = () => host.Start();
        await using var other = new FakeHost();
        other.Start();
        other.Status = FakeHost.StatusOf() with { ProcessId = 6161 };
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        other.On(HostOps.ProbeHold, _ =>
        {
            if (host.Sent(HostOps.Stop).Count == 0) { return new ProbeHoldResult(false); }
            waiting.TrySetResult();
            return new ProbeHoldResult(true, "finishing");
        });
        host.On(HostOps.Stop, data =>
        {
            _ = Task.Run(async () => { await Task.Delay(50); await host.StopAsync(); });
            return new { stopping = true };
        });
        // Waits as long as a real update would for that probe.
        var updating = Manager(others: [other.Folder], package: [4242, 6161], probeWait: TimeSpan.FromMinutes(11));
        var applied = false;
        var update = Task.Run(() => updating.StopAndApplyAsync(() => { }, () => applied = true));
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var quitLog = new List<string>();
        await Task.Run(() => updating.Quit(TimeSpan.FromSeconds(10), quitLog.Add));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(applied);
        Assert.Empty(quitLog);
        Assert.Single(processes.Started);
        // The other profile's probe is left to finish.
        Assert.Empty(other.Sent(HostOps.Stop));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task An_update_stops_nothing_when_a_host_of_the_package_cannot_be_reached()
    {
        await StartAsync();
        var updating = Manager(others: [Path.Combine(app, "refuses-this-app")], package: [4242, 8181]);
        var applied = false;

        var busy = await Assert.ThrowsAsync<HostBusyException>(() => updating.StopAndApplyAsync(() => { }, () => applied = true));

        Assert.Contains("pid 8181", busy.Message, StringComparison.Ordinal);
        Assert.False(applied);
        Assert.Empty(host.Sent(HostOps.Stop));
        Assert.Empty(processes.Killed);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task An_update_stops_nothing_when_another_profiles_host_cannot_confirm_its_usage_probe_is_done()
    {
        await StartAsync();
        await using var other = new FakeHost();
        other.Start();
        other.On(HostOps.ProbeHold, _ => new ProbeHoldResult(true, "Codex's app-server is still finishing"));
        other.Status = FakeHost.StatusOf() with { ProcessId = 6161 };
        var updating = Manager(others: [other.Folder], package: [4242, 6161]);
        var applied = false;

        var busy = await Assert.ThrowsAsync<HostBusyException>(() => updating.StopAndApplyAsync(() => { }, () => applied = true));

        Assert.Contains("still finishing", busy.Message, StringComparison.Ordinal);
        Assert.False(applied);
        Assert.Empty(host.Sent(HostOps.Stop));
        Assert.Empty(host.Sent(HostOps.ProbeHold));
        Assert.Empty(processes.Killed);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Velopacks_hooks_begin_no_stop_they_would_abandon()
    {
        await StartAsync();
        var hook = Manager();

        // Busy past what the hook has: left running, unasked, for Velopack to end.
        host.On(HostOps.ProbeHold, _ => new ProbeHoldResult(true, "Codex's app-server is still finishing"));
        Assert.Contains("still finishing", await hook.StopWithinAsync(TimeSpan.FromMilliseconds(300)), StringComparison.Ordinal);
        Assert.Empty(host.Sent(HostOps.Stop));
        Assert.Empty(processes.Killed);

        // Done: stopped as any stop is.
        host.On(HostOps.ProbeHold, _ => new ProbeHoldResult(false));
        Assert.Null(await hook.StopWithinAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Single(host.Sent(HostOps.Stop));

        // Already stopped, as the app leaves it before every update it starts: nothing is asked.
        var holds = host.Sent(HostOps.ProbeHold).Count;
        Assert.Null(await hook.StopWithinAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Equal(holds, host.Sent(HostOps.ProbeHold).Count);
        Assert.Single(host.Sent(HostOps.Stop));
    }

    HostManager Manager(IReadOnlyList<string>? others = null, IReadOnlyCollection<int>? package = null, TimeSpan? callTimeout = null, Func<IReadOnlyCollection<int>>? packageNow = null, TimeSpan? probeWait = null, TimeSpan? connectTimeout = null) =>
        new(new InstallLayout(app, host.Folder, "test"), tasks, processes,
            ct => LocalClient.ConnectAsync(host.Endpoint, connectTimeout ?? TimeSpan.FromMilliseconds(300), ct), userId: @"PC\dana", userSid: "S-1-5-21-1-2-3-1001")
        {
            StartTimeout = TimeSpan.FromSeconds(5),
            StopTimeout = TimeSpan.FromSeconds(2),
            Poll = TimeSpan.FromMilliseconds(50),
            ProbeWait = probeWait ?? TimeSpan.FromSeconds(3),
            ProbePoll = TimeSpan.FromMilliseconds(50),
            ProbeCallTimeout = callTimeout ?? TimeSpan.FromSeconds(5),
            OtherProfiles = () => others ?? [],
            ConnectTo = (folder, ct) => LocalClient.ConnectAsync(LocalEndpoint.For(folder), TimeSpan.FromMilliseconds(300), ct),
            PackageHosts = packageNow ?? (() => package ?? []),
            Elevated = () => false,
        };

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_read_only_host_beside_a_running_agent_ha_bridge_updates_when_idle_like_any_other()
    {
        var status = FakeHost.StatusOf(agentHaBridge: "agent-ha-bridge's daemon is running") with
        {
            Machine = new MachineInfo("host-id", "Test PC", "windows", "0.1.0", ReadOnly: "delivery.readOnly is set"),
        };
        await StartAsync(status);
        Assert.Null(auto.WhyNot());

        await TwoIdleTicksAsync();

        Assert.True(updater.Applied);
        Assert.Single(host.Sent(HostOps.Stop));
    }

    // A session with no status reads as idle (v1-sessions.md "Malformed records", #446), which is
    // what to show, but no evidence the turn has ended: the update waits for one read in full.
    [Fact]
    [Trait("Category", "Network")]
    public async Task A_session_read_as_idle_only_because_its_status_could_not_be_read_holds_the_update()
    {
        await StartAsync(null, Make.Session("s1", SessionStatus.Working));
        var unreadable = JsonSerializer.SerializeToNode(Make.Session("s1", SessionStatus.Working), ProtocolJson.Options)!.AsObject();
        unreadable["status"] = 3;
        await host.SendAsync(Events.SessionUpsert, unreadable);
        await Wait.UntilAsync(() => connection.Sessions.Get("s1")?.Status == SessionStatus.Idle, "the session read as idle");

        await TwoIdleTicksAsync();
        Assert.False(updater.Applied);

        // Read in full this time: idle is now the host's word.
        await host.SendAsync(Events.SessionUpsert, Make.Session("s1", SessionStatus.Idle));
        await Wait.UntilAsync(() => !connection.Sessions.Unsure, "the session read in full");
        await TwoIdleTicksAsync();
        Assert.True(updater.Applied);
    }

    // An unreadable session followed at once by one read in full, both between ticks, used to keep
    // the idle time from before it: the update installed without IdleFor of readable idle.
    [Fact]
    [Trait("Category", "Network")]
    public async Task A_session_read_past_a_field_restarts_the_idle_wait_even_when_it_is_read_in_full_before_the_next_tick()
    {
        await StartAsync(null, Make.Session("s1", SessionStatus.Idle));
        await auto.TickAsync();
        time.Advance(AutoUpdater.IdleFor - TimeSpan.FromSeconds(1));

        var unreadable = JsonSerializer.SerializeToNode(Make.Session("s1", SessionStatus.Working), ProtocolJson.Options)!.AsObject();
        unreadable["status"] = 3;
        await host.SendAsync(Events.SessionUpsert, unreadable);
        await Wait.UntilAsync(() => connection.Sessions.Unsure, "the session read past its status");
        await host.SendAsync(Events.SessionUpsert, Make.Session("s1", SessionStatus.Idle));
        await Wait.UntilAsync(() => !connection.Sessions.Unsure, "the session read in full");

        time.Advance(TimeSpan.FromSeconds(2));
        await auto.TickAsync();
        Assert.False(updater.Applied);

        await TwoIdleTicksAsync();
        Assert.True(updater.Applied);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_turn_running_holds_the_update_until_the_session_has_been_idle_a_while()
    {
        await StartAsync(null, Make.Session("s1", SessionStatus.Working));
        await auto.TickAsync();
        time.Advance(AutoUpdater.IdleFor);
        await auto.TickAsync();
        Assert.Equal("0.2.0", auto.Pending);
        Assert.False(updater.Applied);

        await GoIdleAsync("s1");
        await auto.TickAsync();
        time.Advance(AutoUpdater.IdleFor - TimeSpan.FromSeconds(1));
        await auto.TickAsync();
        Assert.False(updater.Applied);

        time.Advance(TimeSpan.FromSeconds(1));
        await auto.TickAsync();
        Assert.True(updater.Applied);
        Assert.Equal(1, updater.Downloads);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_turn_that_starts_and_ends_between_ticks_starts_the_idle_wait_again()
    {
        await StartAsync(null, Make.Session("s1", SessionStatus.Idle));
        await auto.TickAsync();
        time.Advance(AutoUpdater.IdleFor - TimeSpan.FromSeconds(10));

        await host.SendAsync(Events.SessionUpsert, Make.Session("s1", SessionStatus.Working));
        await Wait.UntilAsync(() => connection.Sessions.Get("s1")?.Status == SessionStatus.Working, "the turn starting");
        await GoIdleAsync("s1");
        time.Advance(TimeSpan.FromSeconds(10));
        await auto.TickAsync();
        Assert.False(updater.Applied);

        time.Advance(AutoUpdater.IdleFor);
        await auto.TickAsync();
        Assert.True(updater.Applied);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_reconnect_between_ticks_starts_the_idle_wait_again()
    {
        await StartAsync(null, Make.Session("s1", SessionStatus.Idle));
        await auto.TickAsync();
        time.Advance(AutoUpdater.IdleFor);

        // The host restarts between ticks, and its sessions are read in full again: whatever they
        // did while the app could not see them, idle from before does not count.
        await host.StopAsync();
        await Wait.UntilAsync(() => connection.State != LinkState.Connected, "the host gone");
        host.Start();
        await Wait.UntilAsync(() => connection.State == LinkState.Connected && connection.Sessions.Complete, "the host back");
        await auto.TickAsync();
        Assert.False(updater.Applied);

        time.Advance(AutoUpdater.IdleFor);
        await auto.TickAsync();
        Assert.True(updater.Applied);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_question_waiting_holds_the_update_too()
    {
        await StartAsync(null, Make.Session("s1") with { Status = SessionStatus.Idle, Question = Make.Approval() });
        Assert.False(auto.Idle);

        await TwoIdleTicksAsync();

        Assert.Equal("0.2.0", auto.Pending);
        Assert.False(updater.Applied);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_session_busy_for_six_hours_does_not_hold_an_update_any_longer()
    {
        await StartAsync(null, Make.Session("s1", SessionStatus.Working));
        await auto.TickAsync();

        time.Advance(AutoUpdater.WaitAtMost - TimeSpan.FromMinutes(1));
        await auto.TickAsync();
        Assert.False(updater.Applied);

        time.Advance(TimeSpan.FromMinutes(1));
        await auto.TickAsync();
        Assert.True(updater.Applied);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task It_checks_on_start_and_then_every_half_hour_not_every_tick()
    {
        updater.Found = null;
        await StartAsync();

        await auto.TickAsync();
        time.Advance(TimeSpan.FromMinutes(29));
        await auto.TickAsync();
        Assert.Equal(1, updater.Checks);

        time.Advance(TimeSpan.FromMinutes(1));
        await auto.TickAsync();
        Assert.Equal(2, updater.Checks);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_host_whose_sessions_are_not_yet_read_in_full_is_not_idle()
    {
        await StartAsync(null, Make.Session("s1", SessionStatus.Idle));
        Assert.True(auto.Idle);

        connection.Sessions.BeginSnapshot();

        Assert.False(auto.Idle);
        await TwoIdleTicksAsync();
        Assert.False(updater.Applied);
        // Nor six hours on: an update that no longer waits for idle still waits for the whole list.
        time.Advance(AutoUpdater.WaitAtMost);
        await auto.TickAsync();
        Assert.False(updater.Applied);
        Assert.Empty(host.Sent(HostOps.Stop));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Without_a_running_host_to_say_what_its_sessions_are_doing_nothing_is_installed_though_releases_are_still_found()
    {
        await StartAsync();
        // Stopped on purpose, so the app does not start it again.
        await manager.StopAsync();
        await Wait.UntilAsync(() => connection.State != LinkState.Connected, "the host gone");

        await TwoIdleTicksAsync();

        // Found while the host is gone, so an update it holds for long is told to the owner (#800).
        Assert.Equal(1, updater.Checks);
        Assert.Equal("0.2.0", auto.Pending);
        Assert.False(updater.Applied);
        Assert.Equal(AutoUpdater.HostGone, auto.State.Waiting);
    }

    // #800: VM2's app lost its link to a live host and sat on an old release for ~20 h, the six
    // hours restarted at every tick and one log line said so.
    [Fact]
    [Trait("Category", "Network")]
    public async Task An_update_the_lost_host_holds_past_six_hours_is_told_to_the_owner_then_and_not_before_and_again_only_daily()
    {
        await StartAsync();
        await auto.TickAsync();
        Assert.Equal("0.2.0", auto.Pending);
        await manager.StopAsync();
        await Wait.UntilAsync(() => connection.State != LinkState.Connected, "the host gone");

        for (var i = 0; i < 6 * 60 - 2; i++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            await auto.TickAsync();
        }
        Assert.DoesNotContain(log, l => l.Contains("Needs you", StringComparison.Ordinal));
        Assert.Equal(AutoUpdater.HostGone, auto.State.Waiting);

        time.Advance(TimeSpan.FromMinutes(2));
        await auto.TickAsync();
        var told = Assert.Single(log, l => l.Contains("Needs you", StringComparison.Ordinal));
        Assert.Contains("the update to 0.2.0 has waited since", told);
        Assert.Contains(AutoUpdater.HostGone, told);
        Assert.StartsWith("Needs you: the update to 0.2.0", auto.State.Waiting);
        Assert.Equal(UpdatePhase.Waiting, auto.State.Phase);

        for (var i = 0; i < 23; i++)
        {
            time.Advance(TimeSpan.FromHours(1));
            await auto.TickAsync();
        }
        Assert.Single(log, l => l.Contains("Needs you", StringComparison.Ordinal));
        time.Advance(TimeSpan.FromHours(1));
        await auto.TickAsync();
        Assert.Equal(2, log.Count(l => l.Contains("Needs you", StringComparison.Ordinal)));
    }

    // The host back after holding an update past six hours: idle sessions take it as ever, but
    // it is not forced at the first tick (#800 review).
    [Fact]
    [Trait("Category", "Network")]
    public async Task The_host_back_after_holding_an_update_six_hours_installs_once_idle_not_at_the_first_tick()
    {
        await StartAsync(null, Make.Session("s1", SessionStatus.Idle));
        await auto.TickAsync();
        Assert.Equal("0.2.0", auto.Pending);
        await HostGoneForAsync(TimeSpan.FromHours(7));

        await HostBackAsync();
        await auto.TickAsync();
        Assert.False(updater.Applied);

        time.Advance(AutoUpdater.IdleFor);
        await auto.TickAsync();
        Assert.True(updater.Applied);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task The_host_back_after_holding_an_update_six_hours_with_busy_sessions_forces_it_only_after_twenty_minutes()
    {
        await StartAsync(null, Make.Session("s1", SessionStatus.Working));
        await auto.TickAsync();
        await HostGoneForAsync(TimeSpan.FromHours(7));

        await HostBackAsync();
        await auto.TickAsync();
        Assert.False(updater.Applied);
        Assert.Contains($"installs anyway at {(time.GetUtcNow() + AutoUpdater.ReturnGrace).ToLocalTime():g}", auto.State.Waiting);
        time.Advance(AutoUpdater.ReturnGrace - TimeSpan.FromMinutes(1));
        await auto.TickAsync();
        Assert.False(updater.Applied);

        time.Advance(TimeSpan.FromMinutes(1));
        await auto.TickAsync();
        Assert.True(updater.Applied);
    }

    async Task HostGoneForAsync(TimeSpan gone)
    {
        await manager.StopAsync();
        await Wait.UntilAsync(() => connection.State != LinkState.Connected, "the host gone", TimeSpan.FromSeconds(30));
        for (var at = TimeSpan.Zero; at < gone; at += TimeSpan.FromHours(1))
        {
            time.Advance(TimeSpan.FromHours(1));
            await auto.TickAsync();
        }
    }

    async Task HostBackAsync()
    {
        host.Start();
        await Wait.UntilAsync(() => connection.State == LinkState.Connected && connection.Sessions.Complete, "the host back", TimeSpan.FromSeconds(30));
        time.Advance(TimeSpan.FromMinutes(1));
    }

    // #800 review: a start from the updater raced Settings' Stop and updates, and undid hosts left
    // down on purpose. Whether a host runs is the person's, and the app's own start's.
    [Fact]
    [Trait("Category", "Network")]
    public async Task The_updater_never_starts_a_host_however_long_the_link_is_down()
    {
        await StartAsync();
        await host.StopAsync();
        await Wait.UntilAsync(() => connection.State != LinkState.Connected, "the host gone", TimeSpan.FromSeconds(30));

        for (var i = 0; i < 3 * 60; i++)
        {
            await auto.TickAsync();
            time.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Empty(processes.Started);
        Assert.Empty(tasks.Ran);
        Assert.Contains(log, l => l.Contains("does not answer on its pipe", StringComparison.Ordinal) && l.Contains("not starting it", StringComparison.Ordinal));
    }

    // VM2's case (#800): the host alive, the app's link to it stuck in an attempt that never ends.
    [Fact]
    [Trait("Category", "Network")]
    public async Task A_link_stuck_while_its_host_answers_is_made_again_before_the_update_is_refused_again()
    {
        var hang = 0;
        linkConnect = async ct =>
        {
            if (Interlocked.Exchange(ref hang, 0) == 1) { await Task.Delay(Timeout.Infinite, ct); }
            return await LocalClient.ConnectAsync(host.Endpoint, TimeSpan.FromSeconds(1), ct);
        };
        await StartAsync();
        Volatile.Write(ref hang, 1);
        await host.StopAsync();
        host.Start();
        await Wait.UntilAsync(() => connection.State != LinkState.Connected && Volatile.Read(ref hang) == 0, "the link's attempt hung", TimeSpan.FromSeconds(30));

        await auto.TickAsync();

        Assert.Contains(log, l => l.Contains("the host answers on its pipe", StringComparison.Ordinal) && l.Contains("ended the link's attempt under way and started a new one", StringComparison.Ordinal));
        await Wait.UntilAsync(() => connection.State == LinkState.Connected, "the link made again", TimeSpan.FromSeconds(30));
        Assert.Empty(processes.Started);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_refusal_that_lasts_is_logged_every_hour_and_re_attaching_no_more_often()
    {
        await StartAsync();
        await host.StopAsync();
        await Wait.UntilAsync(() => connection.State != LinkState.Connected, "the host gone", TimeSpan.FromSeconds(30));

        for (var i = 0; i < 121; i++)
        {
            await auto.TickAsync();
            time.Advance(TimeSpan.FromMinutes(1));
        }

        // At 0, 60 and 120 minutes; re-attaching (at 0, 1, 3, 7, 15, 31, 61 and 91) at 0 and 61.
        Assert.Equal(3, log.Count(l => l.Contains($"not installing updates: {AutoUpdater.HostGone}", StringComparison.Ordinal)));
        Assert.Equal(2, log.Count(l => l.Contains("does not answer on its pipe", StringComparison.Ordinal)));
    }

    // #800 review: a connected host whose sessions are never read in full held the update with
    // nothing said, and showed an "installs anyway" time already past.
    [Fact]
    [Trait("Category", "Network")]
    public async Task Sessions_never_read_in_full_are_told_as_the_machine_holding_the_update_and_a_press_stays_queued()
    {
        await StartAsync(null, Make.Session("s1", SessionStatus.Idle));
        await auto.TickAsync();
        Assert.Equal("0.2.0", auto.Pending);
        connection.Sessions.BeginSnapshot();
        await AskedAsync(UpdateAskKind.Install, "5e1f0c2a-7d3b-4c9e-8a61-0f2b3c4d5e6f");

        time.Advance(TimeSpan.FromMinutes(1));
        await auto.TickAsync();
        Assert.Equal(AutoUpdater.SessionsUnread, auto.State.Waiting);

        time.Advance(AutoUpdater.WaitAtMost);
        await auto.TickAsync();

        Assert.False(updater.Applied);
        Assert.Empty(host.Sent(HostOps.Stop));
        Assert.StartsWith("Needs you: the update to 0.2.0", auto.State.Waiting);
        Assert.DoesNotContain("installs anyway at", auto.State.Waiting);
        Assert.Contains(log, l => l.Contains("Needs you", StringComparison.Ordinal) && l.Contains(AutoUpdater.SessionsUnread, StringComparison.Ordinal));
        Assert.Equal("5e1f0c2a-7d3b-4c9e-8a61-0f2b3c4d5e6f", auto.State.Request);
    }

    // GitHub's limit as the machine's ReleaseDiscovery says it (#530), with GitHub's reset.
    static ReleaseDiscoveryException Limited(DateTimeOffset retryAt) =>
        new("GitHub is limiting how often this network can check for updates.", retryAt, limited: true);

    // GitHub's API, answering the release list as a test says.
    sealed class FakeGitHub(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Answer = answer;
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(Answer());
        }
    }

    // #709 review (M1): GitHub's list comes through ReleaseDiscovery, whose limit the hold did not
    // know, so the checks went on every half hour and the devices never heard when the next one
    // was. A fake GitHub's 403, through the real ReleaseDiscovery, to the update state.
    [Fact]
    [Trait("Category", "Network")]
    public async Task GitHubs_limit_as_release_discovery_says_it_holds_the_checks_until_its_reset_and_tells_the_devices_when()
    {
        updater.Found = null;
        var reset = DateTimeOffset.FromUnixTimeSeconds((time.GetUtcNow() + TimeSpan.FromMinutes(45)).ToUnixTimeSeconds());
        using var github = new FakeGitHub(() => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Headers = { { "X-RateLimit-Remaining", "0" }, { "X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) } },
        });
        using var discovery = new ReleaseDiscovery(Path.Combine(app, "discovery"), github, time, random: () => 0);
        updater.Asks = () => discovery.ReleasesAsync(ReleaseDiscovery.ReleasesUrl("https://github.com/danswett/agent-bridge"), CancellationToken.None);
        await StartAsync();

        await auto.TickAsync();

        Assert.Equal(1, updater.Checks);
        Assert.Equal(UpdateOutcome.Failed, auto.State.Last!.Outcome);
        Assert.Contains($"checking again after {reset.ToLocalTime():t}", auto.State.Last.Reason);

        // Not at the next half hour: at GitHub's reset.
        time.Advance(TimeSpan.FromMinutes(44));
        await auto.TickAsync();
        Assert.Equal(1, updater.Checks);
        github.Answer = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        time.Advance(TimeSpan.FromMinutes(1));
        await auto.TickAsync();
        Assert.Equal((2, 2), (updater.Checks, github.Requests));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_rate_limit_reset_five_minutes_away_is_retried_then_even_after_a_press_just_before_it()
    {
        updater.Found = null;
        await StartAsync();
        updater.CheckFails = Limited(time.GetUtcNow() + TimeSpan.FromMinutes(5));
        await auto.TickAsync();
        Assert.Equal(1, updater.Checks);
        updater.CheckFails = null;

        time.Advance(TimeSpan.FromMinutes(4));
        await AskedAsync(UpdateAskKind.Check, "0c1d2e3f-4a5b-4c6d-8e7f-9a0b1c2d3e4f");
        Assert.Equal(1, updater.Checks);
        Assert.Equal((UpdateOutcome.Failed, "0c1d2e3f-4a5b-4c6d-8e7f-9a0b1c2d3e4f"), (prefs.LastUpdate!.Outcome, prefs.LastUpdate.Request));
        Assert.Contains("rate limit", prefs.LastUpdate.Reason);

        // Over at its end: a check then that fails for anything else leaves the next to CheckEvery.
        updater.CheckFails = new HttpRequestException("No such host is known.");
        time.Advance(TimeSpan.FromMinutes(1));
        await auto.TickAsync();
        Assert.Equal(2, updater.Checks);
        time.Advance(TimeSpan.FromMinutes(1));
        await auto.TickAsync();
        time.Advance(AutoUpdater.CheckEvery - TimeSpan.FromMinutes(2));
        await auto.TickAsync();
        Assert.Equal(2, updater.Checks);
        time.Advance(TimeSpan.FromMinutes(1));
        await auto.TickAsync();
        Assert.Equal(3, updater.Checks);
    }

    // #923 review: a reset under CheckAgainAfter away, and a press at it, was answered from the
    // cached failure, and the feed was not asked for CheckEvery more.
    [Fact]
    [Trait("Category", "Network")]
    public async Task A_check_pressed_as_a_short_rate_limit_hold_ends_asks_the_feed_rather_than_the_cached_failure()
    {
        updater.Found = null;
        await StartAsync();
        updater.CheckFails = Limited(time.GetUtcNow() + TimeSpan.FromSeconds(30));
        await auto.TickAsync();
        Assert.Equal(1, updater.Checks);
        updater.CheckFails = null;

        time.Advance(TimeSpan.FromSeconds(30));
        await AskedAsync(UpdateAskKind.Check, "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d");

        Assert.Equal(2, updater.Checks);
        Assert.Equal((UpdateOutcome.Current, "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d"), (prefs.LastUpdate!.Outcome, prefs.LastUpdate.Request));
    }

    // #923 review: a tick after another profile's refusal cleared it unasked, started the grace
    // and dropped "Needs you" though that profile still ran.
    [Fact]
    [Trait("Category", "Network")]
    public async Task An_overdue_update_another_profile_still_holds_keeps_needs_you_and_its_grace_starts_only_once_that_profile_stops()
    {
        await StartAsync(null, Make.Session("s1", SessionStatus.Working));
        await auto.TickAsync();
        others = "another profile's host (pid 77) runs from this installation";
        time.Advance(AutoUpdater.WaitAtMost);
        await auto.TickAsync();
        Assert.StartsWith("Needs you: the update to 0.2.0", auto.State.Waiting);

        time.Advance(TimeSpan.FromMinutes(1));
        await auto.TickAsync();
        Assert.StartsWith("Needs you: the update to 0.2.0", auto.State.Waiting);
        Assert.Contains(others, auto.State.Waiting);

        others = null;
        time.Advance(TimeSpan.FromMinutes(1));
        await auto.TickAsync();
        Assert.Contains($"installs anyway at {(time.GetUtcNow() + AutoUpdater.ReturnGrace).ToLocalTime():g}", auto.State.Waiting);
        time.Advance(AutoUpdater.ReturnGrace - TimeSpan.FromMinutes(1));
        await auto.TickAsync();
        Assert.False(updater.Applied);
        time.Advance(TimeSpan.FromMinutes(1));
        await auto.TickAsync();
        Assert.True(updater.Applied);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Checks_the_feeds_rate_limit_refused_back_off_and_say_why()
    {
        updater.CheckFails = new HttpRequestException("Response status code does not indicate success: 403 (rate limit exceeded).", null, System.Net.HttpStatusCode.Forbidden);
        updater.Found = null;
        await StartAsync();

        await auto.TickAsync();
        Assert.Equal(1, updater.Checks);
        Assert.Equal(UpdateOutcome.Failed, auto.State.Last!.Outcome);
        Assert.Contains("rate limit", auto.State.Last.Reason);

        // Not at the next half hour: an hour, then two, and never longer.
        time.Advance(AutoUpdater.CheckEvery);
        await auto.TickAsync();
        Assert.Equal(1, updater.Checks);
        time.Advance(AutoUpdater.CheckEvery);
        await auto.TickAsync();
        Assert.Equal(2, updater.Checks);
        time.Advance(AutoUpdater.CheckBackoffAtMost - TimeSpan.FromMinutes(1));
        await auto.TickAsync();
        Assert.Equal(2, updater.Checks);
        time.Advance(TimeSpan.FromMinutes(1));
        await auto.TickAsync();
        Assert.Equal(3, updater.Checks);
        time.Advance(AutoUpdater.CheckBackoffAtMost);
        await auto.TickAsync();
        Assert.Equal(4, updater.Checks);

        // The feed's own word on when to ask again is taken, and success ends the backoff.
        time.Advance(AutoUpdater.CheckBackoffAtMost);
        updater.CheckFails = Limited(time.GetUtcNow() + TimeSpan.FromMinutes(45));
        await auto.TickAsync();
        Assert.Equal(5, updater.Checks);
        updater.CheckFails = null;
        time.Advance(TimeSpan.FromMinutes(44));
        await auto.TickAsync();
        Assert.Equal(5, updater.Checks);
        time.Advance(TimeSpan.FromMinutes(1));
        await auto.TickAsync();
        Assert.Equal(6, updater.Checks);
        time.Advance(AutoUpdater.CheckEvery);
        await auto.TickAsync();
        Assert.Equal(7, updater.Checks);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_rate_limited_check_right_after_an_update_leaves_the_update_for_the_devices_to_see()
    {
        updater.CheckFails = new HttpRequestException("Response status code does not indicate success: 403 (rate limit exceeded).", null, System.Net.HttpStatusCode.Forbidden);
        prefs = prefs with { LastUpdate = new UpdateResult { Outcome = UpdateOutcome.Updated, From = "0.0.9", To = "0.1.0", Request = "r1", At = time.GetUtcNow() } };
        await StartAsync();

        await auto.TickAsync();

        Assert.Equal(1, updater.Checks);
        Assert.Equal((UpdateOutcome.Updated, "r1"), (prefs.LastUpdate!.Outcome, prefs.LastUpdate.Request));
        Assert.Equal(UpdateOutcome.Updated, auto.State.Last!.Outcome);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Another_profile_running_from_the_installation_holds_the_update()
    {
        await StartAsync();
        others = "another profile's host (pid 77) runs from this installation";

        await TwoIdleTicksAsync();

        Assert.False(updater.Applied);
        Assert.Equal(4242, othersAskedFor);
        Assert.Empty(host.Sent(HostOps.Stop));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_release_replaced_since_it_was_downloaded_does_not_stop_the_host()
    {
        await StartAsync(null, Make.Session("s1", SessionStatus.Working));
        await auto.TickAsync();
        Assert.Equal("0.2.0", auto.Pending);

        // Settings checked meanwhile and found another release, not yet downloaded.
        updater.Ready = null;
        await GoIdleAsync("s1");
        await TwoIdleTicksAsync();

        Assert.False(updater.Applied);
        Assert.Empty(host.Sent(HostOps.Stop));
        Assert.Null(auto.Pending);

        // Then checked and downloaded again, and installed once the host has stayed idle.
        await TwoIdleTicksAsync();
        Assert.True(updater.Applied);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_release_replaced_during_the_last_checks_does_not_stop_the_host()
    {
        await StartAsync();
        // Settings checks during the last of the checks and finds another release.
        othersProbe = () => { updater.Ready = null; return null; };

        await TwoIdleTicksAsync();

        Assert.False(updater.Applied);
        Assert.Empty(host.Sent(HostOps.Stop));
        Assert.Null(auto.Pending);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_failed_install_brings_the_host_back_and_waits_for_the_next_check()
    {
        await StartAsync();
        // The host the failed install stopped is started again, as its launcher would.
        processes.OnStart = () => host.Start();
        updater.ApplyFails = new InvalidOperationException("Update.exe is missing");

        await TwoIdleTicksAsync();

        Assert.Single(host.Sent(HostOps.Stop));
        Assert.Single(processes.Started);
        Assert.False(manager.UpdateUnderWay);
        Assert.Null(auto.Pending);
        Assert.Null(prefs.UpdatedFrom);
        // Said to the devices, though none asked for it: the old release still runs.
        Assert.Equal((UpdateOutcome.Failed, "Update.exe is missing", (string?)null), (prefs.LastUpdate!.Outcome, prefs.LastUpdate.Reason, prefs.LastUpdate.Request));
        await Wait.UntilAsync(() => connection.State == LinkState.Connected && connection.Sessions.Complete, "the host back");
        var checks = updater.Checks;
        time.Advance(TimeSpan.FromMinutes(1));
        await auto.TickAsync();
        Assert.Equal(checks, updater.Checks);
        Assert.Single(host.Sent(HostOps.Stop));
    }

    // A host slow to go: its pipe still answers after it was asked to stop, until the test lets it
    // go. It went by itself 400 ms on, which a stalled runner could reach before the test's step
    // meant to come between. Asked completes when it has been asked.
    (Task Asked, Action LetGo) HeldStop()
    {
        var asked = new TaskCompletionSource();
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.On(HostOps.Stop, data =>
        {
            asked.TrySetResult();
            _ = Task.Run(async () => { await go.Task; await host.StopAsync(); });
            return new { stopping = true };
        });
        return (asked.Task, () => go.TrySetResult());
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Quitting_while_the_host_stops_for_an_update_applies_nothing_and_starts_the_host_again_once_it_has_stopped()
    {
        await StartAsync();
        processes.OnStart = () => host.Start();
        var (stopAsked, letGo) = HeldStop();
        await auto.TickAsync();
        time.Advance(AutoUpdater.IdleFor);

        var tick = Task.Run(auto.TickAsync);
        await stopAsked;
        // What App.Quit does on its way out, at the worst moment (off the test's own context, which
        // the fake host's pipe loops run on): it takes the stop under way, then waits for it.
        var quit = Task.Run(() => manager.Quit(TimeSpan.FromSeconds(10), log.Add));
        await Wait.UntilAsync(() => !manager.UpdateUnderWay, "the quit to begin");
        letGo();
        await quit;
        await tick;

        Assert.False(updater.Applied);
        Assert.True(processes.Started.Count == 1, string.Join("\n", log));
        Assert.Single(host.Sent(HostOps.Stop));
        Assert.False(manager.UpdateUnderWay);
        await Wait.UntilAsync(() => connection.State == LinkState.Connected, "the host back");
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Update_and_restart_pressed_while_the_automatic_update_installs_is_refused_and_leaves_it_alone()
    {
        await StartAsync();
        var (stopAsked, letGo) = HeldStop();
        await auto.TickAsync();
        time.Advance(AutoUpdater.IdleFor);

        var tick = Task.Run(auto.TickAsync);
        await stopAsked;
        // Settings' Update and restart, meanwhile.
        var second = false;
        await Assert.ThrowsAsync<UpdateUnderWayException>(() => manager.StopAndApplyAsync(() => second = true, () => second = true));
        letGo();
        await tick;

        Assert.False(second);
        Assert.True(updater.Applied);
        Assert.Single(host.Sent(HostOps.Stop));
        Assert.Equal("0.1.0", prefs.UpdatedFrom);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Quitting_after_the_host_stopped_for_an_update_but_before_it_was_applied_starts_the_host_again()
    {
        await StartAsync();
        processes.OnStart = () => host.Start();
        // Quit from another thread just as the update is applied, which then fails: the host comes
        // back once, not twice.
        updater.OnApply = () => _ = Task.Run(() => manager.Quit(TimeSpan.FromSeconds(10), log.Add));
        updater.ApplyFails = new InvalidOperationException("Update.exe is missing");

        await TwoIdleTicksAsync();

        Assert.Single(host.Sent(HostOps.Stop));
        await Wait.UntilAsync(() => connection.State == LinkState.Connected, "the host back");
        Assert.Single(processes.Started);
        Assert.False(manager.UpdateUnderWay);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Quitting_with_no_update_under_way_starts_nothing_and_no_update_stops_the_host_afterwards()
    {
        await StartAsync();

        manager.Quit(TimeSpan.FromSeconds(10), log.Add);
        await TwoIdleTicksAsync();

        Assert.Empty(processes.Started);
        Assert.Empty(host.Sent(HostOps.Stop));
        Assert.False(updater.Applied);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Turning_it_off_while_it_asks_the_host_one_last_time_stops_the_install()
    {
        await StartAsync();
        // The last of the checks before stopping the host, during which the switch goes off.
        othersProbe = () => { prefs = prefs with { AutoUpdate = false }; return null; };

        await TwoIdleTicksAsync();

        Assert.False(updater.Applied);
        Assert.Empty(host.Sent(HostOps.Stop));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Turning_it_off_while_the_host_is_asked_about_its_usage_probes_stops_the_install()
    {
        await StartAsync();
        // The switch goes off while the host answers whether a probe is finishing: none is.
        host.On(HostOps.ProbeHold, _ => { prefs = prefs with { AutoUpdate = false }; return new ProbeHoldResult(false); });

        await TwoIdleTicksAsync();

        Assert.NotEmpty(host.Sent(HostOps.ProbeHold));
        Assert.False(updater.Applied);
        Assert.Empty(host.Sent(HostOps.Stop));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Turned_off_it_neither_checks_nor_installs()
    {
        prefs = prefs with { AutoUpdate = false };
        await StartAsync();

        await TwoIdleTicksAsync();

        Assert.Equal(0, updater.Checks);
        Assert.False(updater.Applied);
    }

    [Fact]
    public void The_first_start_after_an_update_says_what_changed_once()
    {
        var toasts = new Toasts();
        prefs = prefs with { UpdatedFrom = "0.1.0", UpdateNotes = "Offline machines and **Forget**" };
        updater.InstalledVersion = "0.2.0";

        AutoUpdater.Announce(() => prefs, p => prefs = p, updater, toasts, log.Add, time);
        AutoUpdater.Announce(() => prefs, p => prefs = p, updater, toasts, log.Add, time);

        var toast = Assert.Single(toasts.Shown);
        Assert.Contains($"{Product.Name} updated to 0.2.0", toast.Xml);
        Assert.Contains("from 0.1.0", toast.Xml);
        Assert.Contains("Offline machines and Forget", toast.Xml);
        Assert.Null(prefs.UpdatedFrom);
    }

    [Fact]
    public void An_update_that_did_not_take_announces_nothing()
    {
        var toasts = new Toasts();
        prefs = prefs with { UpdatedFrom = "0.1.0" };

        AutoUpdater.Announce(() => prefs, p => prefs = p, updater, toasts, log.Add, time);

        Assert.Empty(toasts.Shown);
        Assert.Null(prefs.UpdatedFrom);
    }

    // Asked from another device (#51): the press stands in for the switch, for that release, and
    // waits for the same idle as an automatic update.
    const string Press = "3f2a1b4c-5d6e-4f70-8192-a3b4c5d6e7f8";

    async Task AskedAsync(UpdateAskKind kind, string request = Press)
    {
        await auto.Ask(kind, request);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Install_asked_for_from_another_device_installs_once_idle_even_with_automatic_updates_off_and_says_so_after_the_restart()
    {
        prefs = prefs with { AutoUpdate = false };
        await StartAsync(null, Make.Session("s1", SessionStatus.Working));
        var states = new List<UpdateState>();
        auto.StateChanged += () => states.Add(auto.State);

        await AskedAsync(UpdateAskKind.Install);
        // Found and downloaded at once, then waiting for the turn to end, as an automatic one does.
        Assert.Equal(1, updater.Checks);
        Assert.Contains(states, s => s.Phase == UpdatePhase.Checking);
        Assert.Contains(states, s => s.Phase == UpdatePhase.Downloading && s.Available == "0.2.0");
        var waiting = auto.State;
        Assert.Equal((UpdatePhase.Waiting, "0.2.0", Press), (waiting.Phase, waiting.Available, waiting.Request));
        Assert.Contains("a session on this machine has a turn running", waiting.Waiting);
        Assert.False(updater.Applied);

        await GoIdleAsync("s1");
        await TwoIdleTicksAsync();

        Assert.True(updater.Applied);
        Assert.Contains(states, s => s.Phase == UpdatePhase.Installing);
        Assert.Equal(("0.1.0", Press), (prefs.UpdatedFrom, prefs.UpdateRequest));

        // The new version's first start: the outcome, with the press it answers, for the devices.
        updater.InstalledVersion = "0.2.0";
        AutoUpdater.Announce(() => prefs, p => prefs = p, updater, new Toasts(), log.Add, time);
        var last = prefs.LastUpdate!;
        Assert.Equal((UpdateOutcome.Updated, "0.1.0", "0.2.0", Press, time.GetUtcNow()), (last.Outcome, last.From, last.To, last.Request, last.At));
        Assert.Null(prefs.UpdateRequest);
        // Its updater then starts, and the press the update answered stays answered.
        var restarted = new AutoUpdater(updater, connection, manager, () => prefs, p => prefs = p, log.Add, _ => null, time);
        Assert.Equal(UpdateOutcome.Updated, restarted.State.Last!.Outcome);
        Assert.Null(prefs.UpdateInstallAsked);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Install_asked_for_while_another_profile_runs_from_the_installation_is_refused_at_once_and_nothing_is_downloaded()
    {
        prefs = prefs with { AutoUpdate = false };
        await StartAsync();
        others = "another profile's host (pid 77) runs from this installation";

        await AskedAsync(UpdateAskKind.Install);

        Assert.Equal((0, 0), (updater.Checks, updater.Downloads));
        var last = auto.State.Last!;
        Assert.Equal((UpdateOutcome.Refused, others, Press), (last.Outcome, last.Reason, last.Request));
        Assert.Null(auto.State.Request);
        await TwoIdleTicksAsync();
        Assert.False(updater.Applied);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Install_asked_for_with_nothing_newer_says_it_is_current_and_stops_nothing()
    {
        updater.Found = null;
        await StartAsync();

        await AskedAsync(UpdateAskKind.Install);
        await TwoIdleTicksAsync();

        Assert.Equal((UpdateOutcome.Current, Press), (auto.State.Last!.Outcome, auto.State.Last.Request));
        Assert.Empty(host.Sent(HostOps.Stop));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Check_asked_for_finds_the_release_without_installing_it_when_automatic_updates_are_off()
    {
        prefs = prefs with { AutoUpdate = false };
        await StartAsync();

        await AskedAsync(UpdateAskKind.Check);
        await TwoIdleTicksAsync();

        var state = auto.State;
        Assert.Equal(1, updater.Checks);
        Assert.Equal(("0.2.0", "What 0.2.0 changes", UpdatePhase.Idle, time.GetUtcNow() - AutoUpdater.IdleFor), (state.Available, state.Notes, state.Phase, state.CheckedAt));
        Assert.False(state.Automatic);
        Assert.Null(state.Request);
        Assert.False(updater.Applied);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_check_answered_from_the_machines_shared_copy_tells_the_devices_when_GitHub_was_asked_not_when_it_was_read()
    {
        prefs = prefs with { AutoUpdate = false };
        var asked = time.GetUtcNow() - TimeSpan.FromMinutes(8);
        updater.DiscoveredAt = asked;
        await StartAsync();

        await AskedAsync(UpdateAskKind.Check);
        await auto.TickAsync();

        Assert.Equal(1, updater.Checks);
        Assert.Equal(("0.2.0", asked), (auto.State.Available, auto.State.CheckedAt));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Install_asked_for_that_fails_says_so_and_the_host_comes_back()
    {
        await StartAsync();
        processes.OnStart = () => host.Start();
        updater.ApplyFails = new InvalidOperationException("the package is damaged");

        await AskedAsync(UpdateAskKind.Install);
        await TwoIdleTicksAsync();

        var last = auto.State.Last!;
        Assert.Equal((UpdateOutcome.Failed, "the package is damaged", "0.2.0", Press), (last.Outcome, last.Reason, last.To, last.Request));
        Assert.Null(prefs.UpdateRequest);
        Assert.Single(processes.Started);
    }

    List<UpdateReport> Reports() => [.. host.Sent(HostOps.UpdateReport).Select(r => r.Deserialize<UpdateReport>(ProtocolJson.Options)!)];

    [Fact]
    [Trait("Category", "Network")]
    public async Task The_hosts_asks_reach_the_updater_once_each_and_the_host_hears_what_it_did_and_which_it_took()
    {
        prefs = prefs with { AutoUpdate = false };
        host.On(HostOps.UpdateReport, _ => new { ok = true });
        await StartAsync(FakeHost.StatusOf() with { UpdateAsks = [new UpdateAsk(Press, UpdateAskKind.Check, "pwa 1a2b3c4d", Make.T0)] });

        using var reporter = new UpdateReporter(connection, auto, log.Add);

        await Wait.UntilAsync(() => Reports().Any(r => r.Taken?.Contains(Press) == true) && Reports().Any(r => r.State.Available == "0.2.0"), "the check reported");
        Assert.Equal(1, updater.Checks);
        // The host's status again with the ask still in it (the report not yet applied there): it
        // is not asked for twice. Once the connection holds that status, the reporter has read it.
        var before = TestUi.Read(() => connection.Status);
        await host.SendAsync(HostOps.StatusEvent, host.Status);
        await Wait.UntilAsync(() => !ReferenceEquals(connection.Status, before), "the status again");
        Assert.Equal(1, updater.Checks);
        Assert.Single(log, l => l == "update: check asked for");
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_press_the_app_comes_to_after_it_lapsed_at_the_host_is_refused_not_carried_out_late()
    {
        prefs = prefs with { AutoUpdate = false };
        host.On(HostOps.UpdateReport, _ => new { ok = true });
        var pressed = Make.T0 - UpdateProtocol.Lapses - TimeSpan.FromSeconds(1);
        await StartAsync(FakeHost.StatusOf() with { UpdateAsks = [new UpdateAsk(Press, UpdateAskKind.Install, "pwa 1a2b3c4d", pressed)] });

        using var reporter = new UpdateReporter(connection, auto, log.Add);

        await Wait.UntilAsync(() => Reports().Any(r => r.Taken?.Contains(Press) == true && r.State.Last?.Request == Press), "the lapse reported");
        var last = auto.State.Last!;
        Assert.Equal((UpdateOutcome.Refused, AutoUpdater.Lapsed), (last.Outcome, last.Reason));
        Assert.Equal(0, updater.Checks);
        Assert.Null(auto.State.Request);
        Assert.False(updater.Applied);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_restarted_host_is_told_at_once_and_an_unchanged_state_is_not_sent_again()
    {
        host.On(HostOps.UpdateReport, _ => new { ok = true });
        updater.Found = null;
        await StartAsync();
        using var reporter = new UpdateReporter(connection, auto, log.Add);
        await Wait.UntilAsync(() => Reports().Count >= 1, "the first report");
        await reporter.ReportAsync();
        var sent = Reports().Count;

        await host.StopAsync();
        await Wait.UntilAsync(() => connection.State != LinkState.Connected, "the host gone");
        host.Status = host.Status with { StartedAt = host.Status.StartedAt.AddMinutes(1) };
        host.Start();

        await Wait.UntilAsync(() => Reports().Count > sent, "the restarted host told");
        Assert.Equal("0.1.0", Reports()[^1].State.Current);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_copy_the_updater_cannot_update_says_so_and_takes_no_asks()
    {
        host.On(HostOps.UpdateReport, _ => new { ok = true });
        await StartAsync(FakeHost.StatusOf() with { UpdateAsks = [new UpdateAsk(Press, UpdateAskKind.Install, "pwa 1a2b3c4d", Make.T0)] });

        using var reporter = new UpdateReporter(connection, null, log.Add);

        await Wait.UntilAsync(() => Reports().Count >= 1, "the report");
        var report = Reports()[0];
        Assert.Equal((false, AutoUpdater.NotInstalled), (report.State.Installer, report.State.Unavailable));
        Assert.Null(report.Taken);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Turning_automatic_updates_off_while_a_release_waits_tells_the_devices_it_now_waits_for_them()
    {
        await StartAsync(null, Make.Session("s1", SessionStatus.Working));
        await auto.TickAsync();
        Assert.Equal(UpdatePhase.Waiting, auto.State.Phase);
        var told = new List<UpdateState>();
        auto.StateChanged += () => told.Add(auto.State);

        prefs = prefs with { AutoUpdate = false };
        await auto.TickAsync();

        var now = Assert.Single(told.TakeLast(1));
        Assert.Equal((UpdatePhase.Idle, "0.2.0"), (now.Phase, now.Available));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_check_that_finds_nothing_newer_drops_the_release_an_earlier_check_found()
    {
        prefs = prefs with { AutoUpdate = false };
        await StartAsync();
        await AskedAsync(UpdateAskKind.Check);
        Assert.Equal("0.2.0", auto.State.Available);

        // The feed changed, or pre-releases were turned off: nothing newer now.
        updater.Found = null;
        updater.Ready = null;
        time.Advance(AutoUpdater.CheckAgainAfter);
        await AskedAsync(UpdateAskKind.Check, "9b8c7d6e-5f40-4321-8abc-def012345678");

        Assert.Null(auto.State.Available);
        Assert.Equal(UpdateOutcome.Current, auto.State.Last!.Outcome);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_pipe_that_drops_under_a_host_still_running_is_told_again_when_it_comes_back()
    {
        host.On(HostOps.UpdateReport, _ => new { ok = true });
        updater.Found = null;
        await StartAsync();
        using var reporter = new UpdateReporter(connection, auto, log.Add);
        await Wait.UntilAsync(() => Reports().Count >= 1, "the first report");
        await reporter.ReportAsync();
        var sent = Reports().Count;

        // The same run of the host (its status unchanged), a new connection to it: the host forgot
        // the app when the old one closed.
        await host.StopAsync();
        await Wait.UntilAsync(() => connection.State != LinkState.Connected, "the pipe gone");
        host.Start();

        await Wait.UntilAsync(() => Reports().Count > sent, "the host told again");
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task What_Settings_checks_and_downloads_is_what_the_devices_are_told()
    {
        prefs = prefs with { AutoUpdate = false };
        shared = new ObservedUpdater(updater, time);
        await StartAsync();
        var told = new List<UpdateState>();
        auto.StateChanged += () => told.Add(auto.State);

        // Settings' Check, then its Download, through the updater they share.
        Assert.Equal("0.2.0", await shared.CheckAsync());
        await shared.DownloadAsync(_ => { });

        Assert.Contains(told, s => s.Phase == UpdatePhase.Checking);
        Assert.Contains(told, s => s.Phase == UpdatePhase.Downloading);
        Assert.Equal(("0.2.0", UpdatePhase.Idle), (auto.State.Available, auto.State.Phase));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task An_update_from_Settings_that_fails_is_what_the_devices_are_told_at_once()
    {
        prefs = prefs with { AutoUpdate = false };
        shared = new ObservedUpdater(updater, time);
        await StartAsync();
        var told = new List<UpdateState>();
        auto.StateChanged += () => told.Add(auto.State);

        AutoUpdater.Failed(() => prefs, p => prefs = p, shared, "0.2.0", "the host did not stop", time: time);

        var last = Assert.Single(told).Last!;
        Assert.Equal((UpdateOutcome.Failed, "0.2.0", "the host did not stop"), (last.Outcome, last.To, last.Reason));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_check_pressed_beside_an_install_an_update_answered_is_reported_unfinished_after_the_restart()
    {
        await StartAsync();
        prefs = prefs with
        {
            UpdateInstallAsked = Press,
            UpdateCheckAsked = "5d0c1e2f-3a4b-4c5d-8e6f-7a8b9c0d1e2f",
            LastUpdate = new UpdateResult { Outcome = UpdateOutcome.Updated, Request = Press, At = time.GetUtcNow() },
        };

        var restarted = new AutoUpdater(updater, connection, manager, () => prefs, p => prefs = p, log.Add, _ => null, time);

        var last = restarted.State.Last!;
        Assert.Equal((UpdateOutcome.Refused, AutoUpdater.Restarted, "5d0c1e2f-3a4b-4c5d-8e6f-7a8b9c0d1e2f"), (last.Outcome, last.Reason, last.Request));
        Assert.Null(prefs.UpdateCheckAsked);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_release_whose_download_failed_is_still_reported_as_there()
    {
        prefs = prefs with { AutoUpdate = false };
        updater.DownloadFails = new IOException("the connection was reset");
        await StartAsync();

        await AskedAsync(UpdateAskKind.Check);

        Assert.Equal("0.2.0", auto.State.Available);
        Assert.Equal((UpdateOutcome.Failed, "the connection was reset"), (auto.State.Last!.Outcome, auto.State.Last.Reason));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_check_pressed_while_another_runs_gets_a_check_of_its_own()
    {
        prefs = prefs with { AutoUpdate = false };
        updater.Found = null;
        await StartAsync();
        updater.CheckGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const string Second = "9b8c7d6e-5f40-4321-8abc-def012345678";

        var first = auto.Ask(UpdateAskKind.Check, Press);
        await Wait.UntilAsync(() => updater.Checks == 1, "the first check under way");
        // A minute on, so the first check does not answer for it.
        time.Advance(AutoUpdater.CheckAgainAfter);
        await auto.Ask(UpdateAskKind.Check, Second);
        updater.CheckGate.SetResult();
        await first;

        Assert.Equal(2, updater.Checks);
        Assert.Equal((UpdateOutcome.Current, Second), (auto.State.Last!.Outcome, auto.State.Last.Request));
        Assert.Null(auto.State.Request);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task An_install_pressed_while_another_is_looked_at_is_looked_at_too_before_anything_downloads()
    {
        prefs = prefs with { AutoUpdate = false };
        await StartAsync();
        const string Second = "9b8c7d6e-5f40-4321-8abc-def012345678";
        others = "another profile's host (pid 77) runs from this installation";
        var pressed = false;
        // The second press comes while the first is being looked at.
        othersProbe = () =>
        {
            if (!pressed) { pressed = true; _ = auto.Ask(UpdateAskKind.Install, Second); }
            return others;
        };

        await auto.Ask(UpdateAskKind.Install, Press);
        await Wait.UntilAsync(() => auto.State.Last?.Request == Second, "the second press answered");

        Assert.Equal((0, 0), (updater.Checks, updater.Downloads));
        Assert.Equal((UpdateOutcome.Refused, others), (auto.State.Last!.Outcome, auto.State.Last.Reason));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Automatic_installs_turned_on_hours_after_a_check_found_a_release_still_wait_for_the_busy_sessions()
    {
        prefs = prefs with { AutoUpdate = false };
        await StartAsync(null, Make.Session("s1", SessionStatus.Working));
        await AskedAsync(UpdateAskKind.Check);
        Assert.Equal("0.2.0", auto.Pending);
        time.Advance(TimeSpan.FromMinutes(1));
        await auto.TickAsync();

        time.Advance(TimeSpan.FromHours(7));
        prefs = prefs with { AutoUpdate = true };
        await auto.TickAsync();
        time.Advance(TimeSpan.FromMinutes(1));
        await auto.TickAsync();

        Assert.False(updater.Applied);
        Assert.Empty(host.Sent(HostOps.Stop));
        Assert.Contains("a session on this machine has a turn running", auto.State.Waiting);

        processes.OnStart = () => host.Start();
        await GoIdleAsync("s1");
        await TwoIdleTicksAsync();
        Assert.True(updater.Applied);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Network")]
    public async Task An_install_pressed_hours_after_the_release_was_found_still_waits_for_the_busy_sessions(bool automaticWhenFound)
    {
        prefs = prefs with { AutoUpdate = automaticWhenFound };
        await StartAsync(null, Make.Session("s1", SessionStatus.Working));
        // Found by a check pressed from a device, or by the automatic updates, which were then
        // turned off: either way never installed by itself.
        if (automaticWhenFound) { await auto.TickAsync(); } else { await AskedAsync(UpdateAskKind.Check); }
        Assert.Equal("0.2.0", auto.Pending);
        prefs = prefs with { AutoUpdate = false };

        time.Advance(AutoUpdater.WaitAtMost + TimeSpan.FromHours(1));
        await AskedAsync(UpdateAskKind.Install, "9b8c7d6e-5f40-4321-8abc-def012345678");
        time.Advance(TimeSpan.FromMinutes(1));
        await auto.TickAsync();

        Assert.False(updater.Applied);
        Assert.Empty(host.Sent(HostOps.Stop));
        Assert.Equal(UpdatePhase.Waiting, auto.State.Phase);
        Assert.Contains("a session on this machine has a turn running", auto.State.Waiting);

        // Six hours from the press, it goes as an automatic one would.
        processes.OnStart = () => host.Start();
        time.Advance(AutoUpdater.WaitAtMost);
        await auto.TickAsync();
        Assert.True(updater.Applied);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Presses_within_a_minute_of_a_check_are_answered_from_it_without_asking_the_feed_again()
    {
        prefs = prefs with { AutoUpdate = false };
        updater.Found = null;
        await StartAsync();

        await AskedAsync(UpdateAskKind.Check);
        await AskedAsync(UpdateAskKind.Check, "9b8c7d6e-5f40-4321-8abc-def012345678");
        await AskedAsync(UpdateAskKind.Install, "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d");
        Assert.Equal(1, updater.Checks);
        Assert.Equal((UpdateOutcome.Current, "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d"), (auto.State.Last!.Outcome, auto.State.Last.Request));

        time.Advance(AutoUpdater.CheckAgainAfter);
        await AskedAsync(UpdateAskKind.Check, "1b2c3d4e-5f60-4b7c-8d9e-0f1a2b3c4d5e");
        Assert.Equal(2, updater.Checks);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_check_from_Settings_that_succeeds_ends_the_failure_a_press_soon_after_would_be_answered_with()
    {
        prefs = prefs with { AutoUpdate = false };
        shared = new ObservedUpdater(updater, time);
        updater.CheckFails = new HttpRequestException("No such host is known.");
        await StartAsync();
        await AskedAsync(UpdateAskKind.Check);
        Assert.Equal(UpdateOutcome.Failed, auto.State.Last!.Outcome);

        // Settings checks, a few seconds on, and the feed answers this time.
        updater.CheckFails = null;
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("0.2.0", await shared.CheckAsync());
        await AskedAsync(UpdateAskKind.Check, "9b8c7d6e-5f40-4321-8abc-def012345678");

        Assert.Equal(Press, auto.State.Last!.Request);
        Assert.Equal("0.2.0", auto.State.Available);
        Assert.Equal(2, updater.Checks);
    }

    // Downloads as Velopack's does: one at a time, a second while one runs failing at once (its
    // global update lock), not waiting.
    sealed class GatedDownloads : IUpdater
    {
        public readonly TaskCompletionSource Gate = new();
        public int Downloads;
        bool running;
        public string? InstalledVersion => "0.1.0";
        public string? PendingNotes => null;
        public string? Ready { get; private set; }
        public Task<string?> CheckAsync() => Task.FromResult<string?>("0.2.0");
        public async Task DownloadAsync(Action<int> progress)
        {
            if (running) { throw new IOException("could not acquire the update lock: another update is under way"); }
            running = true;
            Downloads++;
            try
            {
                progress(40);
                await Gate.Task;
                Ready = "0.2.0";
            }
            finally { running = false; }
        }
        public void ApplyAndRestart() { }
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_check_pressed_while_Settings_downloads_waits_for_that_download_rather_than_failing()
    {
        prefs = prefs with { AutoUpdate = false };
        var gated = new GatedDownloads();
        shared = new ObservedUpdater(gated, time);
        await StartAsync();
        // Settings' Check, then its Download, still running.
        await shared.CheckAsync();
        var settingsDownload = shared.DownloadAsync(_ => { });

        var ask = auto.Ask(UpdateAskKind.Check, Press);
        gated.Gate.SetResult();
        await settingsDownload;
        await ask;

        Assert.Equal(1, gated.Downloads);
        Assert.Null(auto.State.Last);
        Assert.Equal(("0.2.0", "0.2.0"), (auto.State.Available, auto.Pending));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_download_that_failed_in_Settings_is_not_tried_again_for_a_while_by_a_devices_press()
    {
        prefs = prefs with { AutoUpdate = false };
        shared = new ObservedUpdater(updater, time);
        await StartAsync();
        updater.DownloadFails = new IOException("the connection was reset");
        await shared.CheckAsync();
        await Assert.ThrowsAsync<IOException>(() => shared.DownloadAsync(_ => { }));
        updater.DownloadFails = null;

        time.Advance(TimeSpan.FromMinutes(2));
        await AskedAsync(UpdateAskKind.Check);

        Assert.Equal(1, updater.Downloads);
        Assert.Equal(UpdateOutcome.Failed, auto.State.Last!.Outcome);
        Assert.Contains("tried again after", auto.State.Last.Reason);
        time.Advance(AutoUpdater.DownloadAgainAfter);
        await AskedAsync(UpdateAskKind.Check, "9b8c7d6e-5f40-4321-8abc-def012345678");
        Assert.Equal(2, updater.Downloads);
    }

    [Fact]
    public async Task A_download_waiting_on_one_that_fails_does_not_try_again_at_once()
    {
        var inner = new GatedDownloads();
        var observed = new ObservedUpdater(inner, time);
        await observed.CheckAsync();
        var first = observed.DownloadAsync(_ => { });
        var second = observed.DownloadAsync(_ => { });

        inner.Gate.SetException(new IOException("the connection was reset"));

        await Assert.ThrowsAsync<IOException>(() => first);
        await Assert.ThrowsAsync<DownloadHeldException>(() => second);
        Assert.Equal(1, inner.Downloads);
    }

    [Fact]
    public async Task Overlapping_downloads_from_Settings_and_the_app_both_end_well_with_the_release_downloaded_once()
    {
        var inner = new GatedDownloads();
        var observed = new ObservedUpdater(inner, time);
        Assert.Equal("0.2.0", await observed.CheckAsync());
        var first = observed.DownloadAsync(_ => { });
        var second = observed.DownloadAsync(_ => { });
        Assert.False(second.IsCompleted);
        Assert.Equal((UpdatePhase.Downloading, (int?)40), (observed.Phase, observed.Progress));

        inner.Gate.SetResult();
        await first;
        await second;

        Assert.Equal(1, inner.Downloads);
        Assert.Equal("0.2.0", observed.Ready);
        Assert.Equal((UpdatePhase.Idle, (int?)null), (observed.Phase, observed.Progress));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_download_that_failed_is_not_tried_again_for_a_while_however_often_a_device_presses()
    {
        prefs = prefs with { AutoUpdate = false };
        updater.DownloadFails = new IOException("the connection was reset");
        await StartAsync();
        await AskedAsync(UpdateAskKind.Install);
        Assert.Equal(1, updater.Downloads);

        time.Advance(TimeSpan.FromMinutes(2));
        await AskedAsync(UpdateAskKind.Install, "9b8c7d6e-5f40-4321-8abc-def012345678");
        Assert.Equal(1, updater.Downloads);
        Assert.Equal(UpdateOutcome.Failed, auto.State.Last!.Outcome);
        Assert.Contains("tried again after", auto.State.Last.Reason);

        updater.DownloadFails = null;
        time.Advance(AutoUpdater.DownloadAgainAfter);
        await AskedAsync(UpdateAskKind.Check, "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d");
        Assert.Equal(2, updater.Downloads);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task An_install_pressed_and_not_carried_out_when_the_app_restarts_is_reported_as_such_and_not_installed()
    {
        prefs = prefs with { AutoUpdate = false };
        await StartAsync(null, Make.Session("s1", SessionStatus.Working));
        await AskedAsync(UpdateAskKind.Install);
        Assert.Equal((UpdatePhase.Waiting, Press), (auto.State.Phase, auto.State.Request));

        // The app quit, or the machine restarted, while the turn ran.
        var restarted = new AutoUpdater(updater, connection, manager, () => prefs, p => prefs = p, log.Add, _ => null, time);
        await GoIdleAsync("s1");
        await restarted.TickAsync();
        time.Advance(AutoUpdater.IdleFor);
        await restarted.TickAsync();

        var last = restarted.State.Last!;
        Assert.Equal((UpdateOutcome.Refused, AutoUpdater.Restarted, Press), (last.Outcome, last.Reason, last.Request));
        Assert.Null(restarted.State.Request);
        Assert.False(updater.Applied);
        Assert.Null(prefs.UpdateInstallAsked);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_press_taken_before_the_app_restarted_is_not_carried_out_again()
    {
        prefs = prefs with { AutoUpdate = false };
        updater.Found = null;
        await StartAsync();
        await AskedAsync(UpdateAskKind.Check);
        Assert.Equal(1, updater.Checks);

        // The app restarted while the host still listed the press (the report it was taken lost).
        var restarted = new AutoUpdater(updater, connection, manager, () => prefs, p => prefs = p, log.Add, _ => null, time);
        time.Advance(AutoUpdater.CheckAgainAfter);
        await restarted.Ask(UpdateAskKind.Check, Press);

        Assert.Equal(1, updater.Checks);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task An_install_asked_for_is_not_carried_out_once_the_machine_has_become_read_only()
    {
        prefs = prefs with { AutoUpdate = false };
        await StartAsync(null, Make.Session("s1", SessionStatus.Working));
        await AskedAsync(UpdateAskKind.Install);
        Assert.Equal(UpdatePhase.Waiting, auto.State.Phase);

        // agent-ha-bridge started there while the install waited for the session.
        host.Status = host.Status with { ReadOnly = "agent-ha-bridge is running for this user" };
        await GoIdleAsync("s1");
        await TwoIdleTicksAsync();

        Assert.False(updater.Applied);
        Assert.Empty(host.Sent(HostOps.Stop));
        var last = auto.State.Last!;
        Assert.Equal((UpdateOutcome.Refused, Press), (last.Outcome, last.Request));
        Assert.Contains("read-only for other devices now: agent-ha-bridge is running", last.Reason);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task An_install_asked_for_on_a_read_only_machine_is_refused_before_anything_downloads()
    {
        prefs = prefs with { AutoUpdate = false };
        await StartAsync(FakeHost.StatusOf() with { ReadOnly = "delivery.readOnly is set in config.json" });

        await AskedAsync(UpdateAskKind.Install);

        Assert.Equal((0, 0), (updater.Checks, updater.Downloads));
        Assert.Equal(UpdateOutcome.Refused, auto.State.Last!.Outcome);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_development_copy_refuses_what_it_is_asked()
    {
        updater.InstalledVersion = null;
        await StartAsync();

        await AskedAsync(UpdateAskKind.Install);

        Assert.Equal(UpdateOutcome.Refused, auto.State.Last!.Outcome);
        Assert.Equal(AutoUpdater.NotInstalled, auto.State.Unavailable);
        Assert.False(auto.State.Installer);
        Assert.Equal(0, updater.Checks);
    }
}
