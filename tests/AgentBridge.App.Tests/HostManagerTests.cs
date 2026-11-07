using System.Text.RegularExpressions;
using System.Xml.Linq;
using AgentBridge.App.Core;
using AgentBridge.Host.Core.Usage;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// Installing and running the host: the logon task's definition and name, and starting,
// stopping and restarting through the pipe, the task or the launcher. Task Scheduler and
// processes are fakes: these tests never register anything on the machine they run on.
public sealed class HostManagerTests : IAsyncLifetime
{
    readonly FakeHost host = new();
    readonly FakeTasks tasks = new();
    readonly FakeProcesses processes = new();
    readonly string app = Path.Combine(Path.GetTempPath(), "abm-" + Guid.NewGuid().ToString("N")[..8]);
    HostManager manager = null!;

    bool elevated;

    sealed class CleanupCommands(CommandResult result) : ICommandRunner
    {
        public string? Program;
        public IReadOnlyList<string>? Arguments;
        public Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Program = program;
            Arguments = arguments;
            return Task.FromResult(result);
        }
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, OperatingSystem.IsWindows() ? "agent-bridge-host.exe" : "agent-bridge-host"), "");
        File.WriteAllText(Path.Combine(app, OperatingSystem.IsWindows() ? "agent-bridge-hostw.exe" : "agent-bridge-hostw"), "");
        manager = new HostManager(new InstallLayout(app, host.Folder, "test"), tasks, processes,
            ct => LocalClient.ConnectAsync(host.Endpoint, TimeSpan.FromMilliseconds(300), ct), userId: @"PC\dana", userSid: "S-1-5-21-1-2-3-1001")
        {
            StartTimeout = TimeSpan.FromSeconds(5),
            StopTimeout = TimeSpan.FromSeconds(1),
            Poll = TimeSpan.FromMilliseconds(50),
            // No machine's real processes: the package is what a test says it is.
            PackageHosts = () => [],
            Elevated = () => elevated,
        };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await host.DisposeAsync();
        Directory.Delete(app, recursive: true);
    }

    [Fact]
    public async Task Uninstall_hook_cleanup_uses_this_apps_host_and_profile_without_guessing_agent_homes()
    {
        var commands = new CleanupCommands(new CommandResult(true, false, 0, "Owned hook cleanup finished.\n"));

        var detail = await manager.RemoveOwnedHooksAsync(commands);

        Assert.Equal("Owned hook cleanup finished.", detail);
        Assert.Equal(manager.Layout.HostExe, commands.Program);
        Assert.Equal(["hooks", "cleanup", "--profile", manager.Layout.Profile], commands.Arguments);
    }

    [Theory]
    [InlineData(false, false, -1)]
    [InlineData(true, true, -1)]
    [InlineData(true, false, 1)]
    public async Task Failed_uninstall_cleanup_is_explicit_and_never_looks_successful(bool ran, bool timedOut, int exit)
    {
        var commands = new CleanupCommands(new CommandResult(ran, timedOut, exit, "sensitive output is not included in an error"));

        Exception error = timedOut
            ? await Assert.ThrowsAsync<TimeoutException>(() => manager.RemoveOwnedHooksAsync(commands))
            : await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RemoveOwnedHooksAsync(commands));

        Assert.Contains("repair", error.Message);
        Assert.DoesNotContain("sensitive output", error.Message);
    }

    [Fact]
    public void Starting_at_sign_in_registers_an_unelevated_interactive_logon_task_that_runs_the_launcher()
    {
        manager.SetStartAtSignIn(true);

        var (name, xml) = Assert.Single(tasks.Registered);
        Assert.Equal(manager.TaskName, name);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var task = XDocument.Parse(xml).Root!;
        Assert.Equal(@"PC\dana", task.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value);
        var principal = task.Descendants(ns + "Principal").Single();
        Assert.Equal(("InteractiveToken", "LeastPrivilege"), (principal.Element(ns + "LogonType")!.Value, principal.Element(ns + "RunLevel")!.Value));
        Assert.Equal("PT0S", task.Descendants(ns + "ExecutionTimeLimit").Single().Value);
        Assert.Equal("false", task.Descendants(ns + "DisallowStartIfOnBatteries").Single().Value);
        Assert.Equal("false", task.Descendants(ns + "StopIfGoingOnBatteries").Single().Value);
        Assert.Equal(manager.Layout.LauncherExe, task.Descendants(ns + "Command").Single().Value.Trim('"'));
        Assert.Equal("serve --profile test", task.Descendants(ns + "Arguments").Single().Value);
        Assert.True(manager.StartsAtSignIn);

        manager.SetStartAtSignIn(false);
        Assert.Empty(tasks.Registered);
    }

    [Fact]
    public void The_task_is_named_for_this_product_profile_and_user_and_never_like_agent_ha_bridges()
    {
        var name = LogonTaskXml.TaskName("default", "S-1-5-21-1-2-3-1001");

        Assert.Matches("^AgentBridgeHost_default_[0-9a-f]{8}$", name);
        Assert.NotEqual(name, LogonTaskXml.TaskName("default", "S-1-5-21-1-2-3-1002"));
        // agent-ha-bridge's task names, as its G5 probe matches them.
        Assert.DoesNotMatch(new Regex("^(AgentBridgeDaemon|CopilotBridgeDaemon)(_[0-9A-Fa-f]{1,64})?$"), name);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Starting_runs_the_task_when_there_is_one_and_waits_until_the_host_answers()
    {
        manager.SetStartAtSignIn(true);
        tasks.OnRun = () => host.Start();

        await manager.StartAsync();

        Assert.Equal([manager.TaskName], tasks.Ran);
        Assert.Empty(processes.Started);
        Assert.True(await manager.IsRunningAsync());
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Without_a_task_starting_runs_the_launcher_with_the_profile()
    {
        processes.OnStart = () => host.Start();

        await manager.StartAsync();

        var (path, args) = Assert.Single(processes.Started);
        Assert.Equal(manager.Layout.LauncherExe, path);
        Assert.Equal(["serve", "--profile", "test"], args);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task An_elevated_app_starts_the_host_only_through_its_logon_task_which_runs_it_unelevated()
    {
        elevated = true;

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync());
        Assert.Contains("administrator", refused.Message);
        Assert.Empty(processes.Started);

        manager.SetStartAtSignIn(true);
        tasks.OnRun = () => host.Start();
        await manager.StartAsync();
        Assert.Equal([manager.TaskName], tasks.Ran);
        Assert.Empty(processes.Started);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Starting_a_running_host_starts_nothing_more()
    {
        host.Start();

        await manager.StartAsync();

        Assert.Empty(processes.Started);
        Assert.Empty(tasks.Ran);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_host_that_never_answers_is_reported_rather_than_waited_on_forever()
    {
        await Assert.ThrowsAsync<TimeoutException>(() => manager.StartAsync());
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Restarting_waits_for_the_stopped_hosts_task_run_to_end_rather_than_running_the_task_into_it()
    {
        manager.SetStartAtSignIn(true);
        host.On(HostOps.Stop, d => { _ = Task.Run(host.StopAsync); return new { stopping = true }; });
        host.Start();
        tasks.OnRun = () => host.Start();
        // hostw outlives the host it waited for by a few looks (#280).
        tasks.StillRunningLooks = 3;

        await manager.RestartAsync();

        Assert.Empty(tasks.Ignored);
        Assert.Equal([manager.TaskName], tasks.Ran);
        Assert.True(await manager.IsRunningAsync());
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task An_update_that_fails_to_apply_starts_the_host_again_once_its_task_run_has_ended()
    {
        manager.SetStartAtSignIn(true);
        host.On(HostOps.Stop, d => { _ = Task.Run(host.StopAsync); return new { stopping = true }; });
        host.Start();
        tasks.OnRun = () => host.Start();
        tasks.StillRunningLooks = 3;

        var failed = await Assert.ThrowsAsync<IOException>(() => manager.StopAndApplyAsync(() => { }, () => throw new IOException("the package is damaged")));

        Assert.Equal("the package is damaged", failed.Message);
        Assert.Empty(tasks.Ignored);
        Assert.True(await manager.IsRunningAsync());
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_task_run_that_ends_just_as_the_start_timeout_runs_out_still_gives_its_host_the_whole_timeout()
    {
        var clock = new FakeTimeProvider(Make.T0);
        var starting = false;
        // Each look at the pipe takes a second; the host a run starts answers from the second look.
        var timed = new HostManager(manager.Layout, tasks, processes, async ct =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            if (starting) { starting = false; host.Start(); throw new IOException("not listening yet"); }
            return await LocalClient.ConnectAsync(host.Endpoint, TimeSpan.FromMilliseconds(300), ct);
        }, userId: @"PC\dana", userSid: "S-1-5-21-1-2-3-1001")
        {
            StartTimeout = TimeSpan.FromSeconds(5),
            Poll = TimeSpan.FromMilliseconds(10),
            Time = clock,
        };
        timed.SetStartAtSignIn(true);
        tasks.OnRun = () => starting = true;
        // The first look (is it running?) moves the clock to T0+1 s, so the wait for the task ends
        // at T0+6 s, the very moment the old run is over.
        tasks.StillRunningWhile = () => clock.GetUtcNow() < Make.T0.AddSeconds(6);

        await timed.StartAsync();

        Assert.Equal([timed.TaskName], tasks.Ran);
        Assert.Empty(tasks.Ignored);
        Assert.True(await timed.IsRunningAsync());
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Quitting_during_an_update_brings_the_host_back_once_its_task_run_has_ended()
    {
        // The update's stop is held on a clock that never moves, so it waits for this test to let
        // the host go however slow the runner is. On the wall clock its 1 s ran out first on Arm64,
        // and the update ended in "the host did not stop" rather than the quit (#425).
        var held = new HostManager(manager.Layout, tasks, processes,
            ct => LocalClient.ConnectAsync(host.Endpoint, TimeSpan.FromMilliseconds(300), ct), userId: @"PC\dana", userSid: "S-1-5-21-1-2-3-1001")
        {
            StartTimeout = TimeSpan.FromSeconds(5),
            StopTimeout = TimeSpan.FromSeconds(1),
            Poll = TimeSpan.FromMilliseconds(50),
            Time = new FakeTimeProvider(Make.T0),
        };
        held.SetStartAtSignIn(true);
        var stopping = new TaskCompletionSource();
        host.On(HostOps.Stop, d => { _ = Task.Run(async () => { await stopping.Task; await host.StopAsync(); }); return new { stopping = true }; });
        host.Start();
        tasks.OnRun = () => host.Start();
        tasks.StillRunningLooks = 3;
        var applied = false;

        var update = held.StopAndApplyAsync(() => { }, () => applied = true);
        await Wait.UntilAsync(() => host.Sent(HostOps.Stop).Count == 1, "the update's stop");
        var logged = new List<string>();
        var quit = Task.Run(() => held.Quit(TimeSpan.FromSeconds(10), logged.Add));
        await Wait.UntilAsync(() => !held.UpdateUnderWay, "the quit to begin");
        stopping.SetResult();
        await quit;

        await Assert.ThrowsAsync<OperationCanceledException>(() => update.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.False(applied);
        Assert.Empty(logged);
        Assert.Empty(tasks.Ignored);
        Assert.True(await held.IsRunningAsync());
    }

    string StopMarker => Path.Combine(manager.Layout.ProfileFolder, HostLauncherContract.StopMarker);

    [Fact]
    [Trait("Category", "Network")]
    public async Task Stopping_while_the_launcher_waits_to_restart_a_crashed_host_ends_its_task_run()
    {
        var clock = new FakeTimeProvider(Make.T0);
        var timed = new HostManager(manager.Layout, tasks, processes,
            ct => LocalClient.ConnectAsync(host.Endpoint, TimeSpan.FromMilliseconds(300), ct), userId: @"PC\dana", userSid: "S-1-5-21-1-2-3-1001")
        {
            StartTimeout = TimeSpan.FromSeconds(5),
            StopTimeout = TimeSpan.FromSeconds(1),
            Poll = TimeSpan.FromMilliseconds(10),
            Time = clock,
        };
        timed.SetStartAtSignIn(true);
        // No host answers, while the launcher's run goes on (a launcher that never looks at the
        // marker): each look at it takes a second.
        tasks.StillRunningWhile = () => { clock.Advance(TimeSpan.FromSeconds(1)); return true; };

        await timed.StopAsync();

        Assert.Equal([timed.TaskName], tasks.Ended);
        Assert.True(File.Exists(StopMarker));
        Assert.False(await timed.IsRunningAsync());
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Stopping_while_the_launcher_is_still_starting_a_restarted_host_stops_that_host_once_it_answers()
    {
        manager.SetStartAtSignIn(true);
        var stopped = false;
        var started = false;
        host.On(HostOps.Stop, d => { stopped = true; _ = Task.Run(host.StopAsync); return new { stopping = true }; });
        // The launcher's run lasts until its host ends; the host it was starting opens its pipe the
        // next time anyone looks, and does whether or not the stop waited for it.
        tasks.StillRunningWhile = () =>
        {
            if (!started && !stopped) { started = true; host.Start(); }
            return !stopped;
        };

        await manager.StopAsync();
        _ = tasks.IsRunning(manager.TaskName);

        Assert.True(stopped);
        Assert.Empty(tasks.Ended);
        Assert.False(await manager.IsRunningAsync());
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_host_is_asked_to_stop_only_once_the_stop_marker_is_there_so_a_crash_while_stopping_is_not_restarted()
    {
        var markedFirst = false;
        host.On(HostOps.Stop, d => { markedFirst = File.Exists(StopMarker); _ = Task.Run(host.StopAsync); return new { stopping = true }; });
        host.Start();

        await manager.StopAsync();

        Assert.True(markedFirst);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Starting_again_while_a_launcher_this_app_started_waits_to_restart_its_host_starts_no_second_launcher()
    {
        var up = false;
        var crashed = false;
        // The launcher's next host comes up at the first look after the crash.
        var m = new HostManager(manager.Layout, tasks, processes, async ct =>
        {
            if (crashed) { crashed = false; up = true; host.Start(); throw new IOException("not listening yet"); }
            return await LocalClient.ConnectAsync(host.Endpoint, TimeSpan.FromMilliseconds(300), ct);
        }, userId: @"PC\dana", userSid: "S-1-5-21-1-2-3-1001")
        {
            StartTimeout = TimeSpan.FromSeconds(5),
            StopTimeout = TimeSpan.FromSeconds(1),
            Poll = TimeSpan.FromMilliseconds(50),
            Elevated = () => false,
        };
        processes.OnStart = () => { if (!up) { up = true; host.Start(); } };
        await m.StartAsync();
        // Its host crashed; the launcher (pid 99) is still there, waiting to start it again.
        processes.Alive.Add(99);
        await host.StopAsync();
        up = false;
        crashed = true;

        await m.StartAsync();

        Assert.Single(processes.Started);
        Assert.True(await m.IsRunningAsync());
    }

    [Theory]
    [Trait("Category", "Network")]
    // Refused at the stop itself, with or without its launcher there, or at a look after it (a
    // host still starting when stop came).
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_host_that_refuses_this_apps_connection_is_left_running_and_its_launcher_left_alone(bool refusedAtTheStop, bool launcherThere)
    {
        var clock = new FakeTimeProvider(Make.T0);
        var connects = 0;
        // One of the host and this app elevated, the other not: the host's pipe refuses the app.
        var m = new HostManager(manager.Layout, tasks, processes,
            _ =>
            {
                if (++connects == 1 && !refusedAtTheStop) { throw new IOException("not listening yet"); }
                throw new LocalRefusedException("the host's pipe belongs to another user, or one of the host and this app runs as administrator and the other does not");
            },
            userId: @"PC\dana", userSid: "S-1-5-21-1-2-3-1001")
        {
            StartTimeout = TimeSpan.FromSeconds(5),
            StopTimeout = TimeSpan.FromSeconds(1),
            Poll = TimeSpan.FromMilliseconds(10),
            Time = clock,
        };
        m.SetStartAtSignIn(true);
        tasks.StillRunningWhile = () => { clock.Advance(TimeSpan.FromSeconds(1)); return launcherThere; };

        await Assert.ThrowsAsync<HostRefusedException>(() => m.StopAsync());

        Assert.Empty(tasks.Ended);
        Assert.False(File.Exists(StopMarker));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_stop_given_up_on_a_usage_probe_still_finishing_leaves_no_stop_marker_so_a_crash_is_still_restarted()
    {
        var quick = new HostManager(manager.Layout, tasks, processes,
            ct => LocalClient.ConnectAsync(host.Endpoint, TimeSpan.FromMilliseconds(300), ct), userId: @"PC\dana", userSid: "S-1-5-21-1-2-3-1001")
        {
            StartTimeout = TimeSpan.FromSeconds(5),
            StopTimeout = TimeSpan.FromSeconds(1),
            Poll = TimeSpan.FromMilliseconds(50),
            ProbeWait = TimeSpan.FromMilliseconds(300),
            ProbePoll = TimeSpan.FromMilliseconds(50),
            PackageHosts = () => [],
        };
        quick.SetStartAtSignIn(true);
        host.On(HostOps.ProbeHold, _ => new ProbeHoldResult(true, "Codex's app-server is still finishing"));
        host.Start();

        await Assert.ThrowsAsync<HostBusyException>(() => quick.StopAsync());

        Assert.False(File.Exists(StopMarker));
        Assert.Empty(host.Sent(HostOps.Stop));
        Assert.True(await quick.IsRunningAsync());
    }

    HostManager QuickProbes(TimeSpan? probeWait = null, TimeSpan? callTimeout = null) => new(manager.Layout, tasks, processes,
        ct => LocalClient.ConnectAsync(host.Endpoint, TimeSpan.FromMilliseconds(300), ct), userId: @"PC\dana", userSid: "S-1-5-21-1-2-3-1001")
    {
        StartTimeout = TimeSpan.FromSeconds(5),
        StopTimeout = TimeSpan.FromSeconds(1),
        Poll = TimeSpan.FromMilliseconds(50),
        ProbeWait = probeWait ?? TimeSpan.FromSeconds(5),
        ProbePoll = TimeSpan.FromMilliseconds(50),
        ProbeCallTimeout = callTimeout ?? TimeSpan.FromSeconds(10),
        PackageHosts = () => [],
    };

    static ProbeHoldResult Finishing => new(true, "Codex's app-server is still finishing");

    // A stop that never comes back is a failure of its own, not a test that hangs.
    static async Task<T> Within<T>(Task<T> work, TimeSpan limit)
    {
        Assert.Same(work, await Task.WhenAny(work, Task.Delay(limit)));
        return await work;
    }

    static async Task Within(Task work, TimeSpan limit)
    {
        Assert.Same(work, await Task.WhenAny(work, Task.Delay(limit)));
        await work;
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_settings_stop_waiting_on_a_usage_probe_is_given_up_when_the_app_quits_and_leaves_no_marker()
    {
        var m = QuickProbes(probeWait: TimeSpan.FromMinutes(5));
        var waiting = new TaskCompletionSource();
        host.On(HostOps.ProbeHold, _ => { waiting.TrySetResult(); return Finishing; });
        host.Start();

        var stop = m.StopAsync();
        await waiting.Task;
        m.Quit(TimeSpan.FromSeconds(1), _ => { });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(stop, TimeSpan.FromSeconds(10)));
        Assert.False(File.Exists(StopMarker));
        Assert.Empty(host.Sent(HostOps.Stop));
        Assert.True(await m.IsRunningAsync());
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_host_its_launcher_brings_back_during_a_stop_is_waited_for_only_as_long_as_the_caller_allows()
    {
        // The first host stops at once; its launcher (the logon task) starts another, which is
        // still finishing a usage probe. The caller (Velopack's hook) allows 300 ms, not ProbeWait.
        var m = QuickProbes(probeWait: TimeSpan.FromMinutes(5));
        m.SetStartAtSignIn(true);
        var stopped = 0;
        host.On(HostOps.ProbeHold, _ => Volatile.Read(ref stopped) > 0 ? Finishing : new ProbeHoldResult(false, null));
        host.On(HostOps.Stop, d => { Interlocked.Increment(ref stopped); _ = Task.Run(host.StopAsync); return new { stopping = true }; });
        var restarted = false;
        tasks.StillRunningWhile = () =>
        {
            if (Volatile.Read(ref stopped) > 0 && !restarted) { restarted = true; host.Start(); }
            return true;
        };
        host.Start();

        var why = await Within(m.StopWithinAsync(TimeSpan.FromMilliseconds(300)), TimeSpan.FromSeconds(30));

        Assert.True(restarted);
        Assert.Contains("still finishing", why, StringComparison.Ordinal);
        Assert.Single(host.Sent(HostOps.Stop));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_launcher_brought_host_still_finishing_a_probe_is_given_up_on_when_the_app_quits()
    {
        var m = QuickProbes(probeWait: TimeSpan.FromMinutes(5));
        m.SetStartAtSignIn(true);
        var stopped = 0;
        var waiting = new TaskCompletionSource();
        host.On(HostOps.ProbeHold, _ =>
        {
            if (Volatile.Read(ref stopped) == 0) { return new ProbeHoldResult(false, null); }
            waiting.TrySetResult();
            return Finishing;
        });
        host.On(HostOps.Stop, d => { Interlocked.Increment(ref stopped); _ = Task.Run(host.StopAsync); return new { stopping = true }; });
        var restarted = false;
        tasks.StillRunningWhile = () =>
        {
            if (Volatile.Read(ref stopped) > 0 && !restarted) { restarted = true; host.Start(); }
            return true;
        };
        host.Start();

        var stop = m.StopAsync();
        await Within(waiting.Task, TimeSpan.FromSeconds(30));
        m.Quit(TimeSpan.FromSeconds(1), _ => { });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(stop, TimeSpan.FromSeconds(10)));
        Assert.Single(host.Sent(HostOps.Stop));
    }

    [Theory]
    [Trait("Category", "Network")]
    // The same process still there, or its pid since given to another.
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_update_whose_host_stops_answering_mid_stop_but_still_runs_is_refused_and_nothing_is_ended(bool pidReused)
    {
        // The host answers when the update begins, then its pipe hangs while its process runs on.
        var answering = true;
        var m = new HostManager(manager.Layout, tasks, processes,
            ct =>
            {
                if (Volatile.Read(ref answering)) { return LocalClient.ConnectAsync(host.Endpoint, TimeSpan.FromMilliseconds(300), ct); }
                if (pidReused) { lock (processes) { processes.StartTimes[4242] = Make.T0.AddHours(1); } }
                throw new TimeoutException("the pipe did not answer");
            },
            userId: @"PC\dana", userSid: "S-1-5-21-1-2-3-1001")
        {
            StartTimeout = TimeSpan.FromSeconds(1),
            StopTimeout = TimeSpan.FromSeconds(1),
            Poll = TimeSpan.FromMilliseconds(50),
            ProbeWait = TimeSpan.FromSeconds(5),
            ProbePoll = TimeSpan.FromMilliseconds(50),
            PackageHosts = () => [4242],
        };
        processes.Alive.Add(4242);
        processes.StartTimes[4242] = Make.T0;
        host.On(HostOps.Status, _ =>
        {
            Volatile.Write(ref answering, false);
            return FakeHost.StatusOf();
        });
        host.Start();
        var applied = false;

        var update = m.StopAndApplyAsync(() => { }, () => applied = true);

        if (pidReused)
        {
            await update;
            Assert.True(applied);
        }
        else
        {
            var e = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => update);
            Assert.Contains("could not be reached to stop it and is still running", e.Message, StringComparison.Ordinal);
            Assert.False(applied);
            Assert.False(File.Exists(StopMarker));
        }
        Assert.Empty(processes.Killed);
        Assert.Empty(host.Sent(HostOps.Stop));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Two_stops_waiting_on_the_same_usage_probe_both_end_with_the_host_stopped_and_the_marker_kept()
    {
        var m = QuickProbes();
        var asked = 0;
        host.On(HostOps.ProbeHold, _ => Interlocked.Increment(ref asked) > 6 ? new ProbeHoldResult(false, null) : Finishing);
        host.On(HostOps.Stop, d => { _ = Task.Run(host.StopAsync); return new { stopping = true }; });
        host.Start();

        var first = m.StopAsync();
        var second = m.StopAsync();
        await Within(Task.WhenAll(first, second), TimeSpan.FromSeconds(30));

        Assert.True(File.Exists(StopMarker));
        Assert.Single(host.Sent(HostOps.Stop));
        Assert.False(await m.IsRunningAsync());
    }

    [Theory]
    [Trait("Category", "Network")]
    // Its process still there, the host stops answering: a hold that never comes back, or a pipe
    // that goes. Neither is the host ending, whatever its pipe says.
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_host_that_stops_answering_while_a_stop_waits_for_its_usage_probe_is_said_to_be_running_not_ended(bool pipeGone)
    {
        var m = QuickProbes(callTimeout: TimeSpan.FromMilliseconds(500));
        processes.Alive.Add(4242);
        var asked = 0;
        host.On(HostOps.ProbeHold, d =>
        {
            if (Interlocked.Increment(ref asked) == 2)
            {
                if (pipeGone) { _ = Task.Run(host.StopAsync); }
                else { Thread.Sleep(TimeSpan.FromSeconds(2)); }
            }
            return Finishing;
        });
        host.Start();

        var e = await Assert.ThrowsAsync<HostBusyException>(() => m.StopAsync());

        Assert.StartsWith("the host could not say whether a usage probe it started is still finishing, so nothing was stopped", e.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(StopMarker));
        Assert.Empty(host.Sent(HostOps.Stop));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task No_stop_marker_is_there_while_a_stop_waits_for_the_hosts_usage_probe_only_once_it_is_done()
    {
        var m = QuickProbes();
        var asked = 0;
        var markedWhileBusy = false;
        host.On(HostOps.ProbeHold, _ =>
        {
            markedWhileBusy |= File.Exists(StopMarker);
            return ++asked < 4 ? new ProbeHoldResult(true, "Codex's app-server is still finishing") : new ProbeHoldResult(false, null);
        });
        var markedAtStop = false;
        host.On(HostOps.Stop, d => { markedAtStop = File.Exists(StopMarker); _ = Task.Run(host.StopAsync); return new { stopping = true }; });
        host.Start();

        await m.StopAsync();

        Assert.False(markedWhileBusy);
        Assert.True(markedAtStop);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_host_that_crashes_while_a_restart_waits_for_its_usage_probe_is_left_to_its_launcher_and_the_restart_says_it_ended()
    {
        var m = QuickProbes();
        m.SetStartAtSignIn(true);
        var asked = 0;
        var markedAtCrash = false;
        host.On(HostOps.ProbeHold, d =>
        {
            if (++asked == 2) { markedAtCrash = File.Exists(StopMarker); _ = Task.Run(host.StopAsync); }
            return new ProbeHoldResult(true, "Codex's app-server is still finishing");
        });
        host.Start();

        var e = await Assert.ThrowsAsync<HostBusyException>(() => m.RestartAsync());

        Assert.StartsWith("the host ended unexpectedly while the stop waited for its usage probe", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing was stopped", e.Message, StringComparison.Ordinal);
        // The launcher looks at the marker the moment its host ends: none there, so it starts the
        // crashed host again, as after any crash.
        Assert.False(markedAtCrash);
        Assert.False(File.Exists(StopMarker));
        Assert.Empty(host.Sent(HostOps.Stop));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Starting_after_a_stop_clears_the_stop_marker_and_the_host_runs()
    {
        manager.SetStartAtSignIn(true);
        host.On(HostOps.Stop, d => { _ = Task.Run(host.StopAsync); return new { stopping = true }; });
        host.Start();
        tasks.OnRun = () => host.Start();

        await manager.StopAsync();
        Assert.True(File.Exists(StopMarker));
        await manager.StartAsync();

        Assert.False(File.Exists(StopMarker));
        Assert.True(await manager.IsRunningAsync());
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_start_through_the_task_that_brings_no_host_up_is_still_reported_and_not_run_again()
    {
        manager.SetStartAtSignIn(true);

        var failed = await Assert.ThrowsAsync<TimeoutException>(() => manager.StartAsync());

        Assert.Equal("the host did not start; see its log", failed.Message);
        Assert.Equal([manager.TaskName], tasks.Ran);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_task_run_that_never_ends_is_reported_within_the_start_timeout()
    {
        manager.SetStartAtSignIn(true);
        tasks.StillRunningLooks = int.MaxValue;

        var failed = await Assert.ThrowsAsync<TimeoutException>(() => manager.StartAsync());

        Assert.Contains("its logon task's last run had not ended", failed.Message);
        Assert.Empty(tasks.Ran);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Stopping_does_not_forcibly_end_a_host_that_will_not_exit()
    {
        host.On(HostOps.Stop, _ => new { stopping = true });
        host.Start();

        await Assert.ThrowsAsync<HostBusyException>(() => manager.StopAsync());

        Assert.Single(host.Sent(HostOps.Stop));
        Assert.Empty(processes.Killed);
        Assert.True(File.Exists(StopMarker));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_lost_stop_response_leaves_a_running_host_and_keeps_the_stop_marker()
    {
        processes.Alive.Add(4242);
        host.OnAsync(HostOps.Stop, async _ => { await host.StopAsync(); return new { stopping = true }; });
        host.Start();

        await Assert.ThrowsAsync<HostBusyException>(() => manager.StopAsync());

        Assert.Single(host.Sent(HostOps.Stop));
        Assert.Empty(processes.Killed);
        Assert.True(File.Exists(StopMarker));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_host_whose_pipe_closes_but_process_remains_is_not_reported_stopped()
    {
        processes.Alive.Add(4242);
        host.StopsWhenAsked();
        host.Start();

        await Assert.ThrowsAsync<HostBusyException>(() => manager.StopAsync());

        Assert.Single(host.Sent(HostOps.Stop));
        Assert.Empty(processes.Killed);
        Assert.True(File.Exists(StopMarker));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_host_that_refuses_to_stop_or_restart_is_left_running_and_never_ended()
    {
        host.On(HostOps.Stop, _ => throw new InvalidOperationException("refused: this connection is from pid 30, inside a copilot process (pid 20); stopping, restarting, signing in and changing settings are for the person, from the Agent Bridge app"));
        host.Start();

        var stop = await Assert.ThrowsAsync<HostRefusedException>(() => manager.StopAsync());
        var restart = await Assert.ThrowsAsync<HostRefusedException>(() => manager.RestartAsync());

        Assert.StartsWith("refused: this connection is from pid 30", stop.Message);
        Assert.Equal(stop.Message, restart.Message);
        Assert.Empty(processes.Killed);
        Assert.Empty(processes.Started);
        Assert.False(File.Exists(StopMarker));
        Assert.True(await manager.IsRunningAsync());
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Stopping_a_host_that_goes_kills_nothing()
    {
        host.On(HostOps.Stop, d => { _ = Task.Run(host.StopAsync); return new { stopping = true }; });
        host.Start();

        await manager.StopAsync();

        Assert.Empty(processes.Killed);
        Assert.False(await manager.IsRunningAsync());
    }

    [Fact]
    public void The_log_tail_is_the_end_of_the_hosts_file_even_when_the_host_is_not_running()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(manager.Layout.LogFile)!);
        File.WriteAllLines(manager.Layout.LogFile, Enumerable.Range(1, 500).Select(i => $"line {i}"));

        var tail = manager.LogTail(3);

        Assert.Equal("line 498\nline 499\nline 500", tail.Replace("\r", ""));
    }

    [Fact]
    public void A_task_scheduler_failure_reaches_callers_as_itself_not_wrapped_by_late_binding()
    {
        if (!OperatingSystem.IsWindows()) { return; }

        Assert.Throws<UnauthorizedAccessException>(() =>
        {
            if (OperatingSystem.IsWindows()) { WindowsLogonTasks.Call(new Refusing(), nameof(Refusing.RegisterTask)); }
        });
    }

    [Fact]
    public void Looking_for_a_task_that_does_not_exist_answers_no()
    {
        if (!OperatingSystem.IsWindows()) { return; }

        Assert.False(new WindowsLogonTasks().Exists("agent-bridge-test-" + Guid.NewGuid().ToString("N")));
        Assert.False(new WindowsLogonTasks().IsRunning("agent-bridge-test-" + Guid.NewGuid().ToString("N")));
    }

    public sealed class Refusing
    {
        public void RegisterTask() => throw new UnauthorizedAccessException("denied by policy");
    }
}
