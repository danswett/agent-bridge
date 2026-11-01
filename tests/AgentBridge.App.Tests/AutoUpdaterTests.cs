using System.Text.Json;
using AgentBridge.App.Core;
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
    readonly List<string> log = [];
    readonly string app = Path.Combine(Path.GetTempPath(), "abu-" + Guid.NewGuid().ToString("N")[..8]);
    AppPreferences prefs = new();
    HostConnection connection = null!;
    HostManager manager = null!;
    AutoUpdater auto = null!;
    string? others;
    int? othersAskedFor;
    Func<string?>? othersProbe;
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
        // Held until released, as a check that takes its time.
        public TaskCompletionSource? CheckGate;
        public string? InstalledVersion { get; set; } = "0.1.0";
        public string? PendingNotes => Found is null ? null : $"What {Found} changes";
        public async Task<string?> CheckAsync()
        {
            Checks++;
            if (CheckGate is { } gate) { await gate.Task; }
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
        host.On(HostOps.Stop, data =>
        {
            _ = Task.Run(async () => { await Task.Delay(50); await host.StopAsync(); });
            return new { stopping = true };
        });
        return Task.CompletedTask;
    }

    // The host as it is when the app starts: its status and sessions, then connected.
    async Task StartAsync(HostStatus? status = null, params Session[] sessions)
    {
        host.Status = status ?? FakeHost.StatusOf();
        host.Sessions.AddRange(sessions);
        host.Start();
        connection = host.Connect(new LockedDispatcher());
        manager = new HostManager(new InstallLayout(app, host.Folder, "test"), tasks, processes,
            ct => LocalClient.ConnectAsync(host.Endpoint, TimeSpan.FromMilliseconds(300), ct), userId: @"PC\dana", userSid: "S-1-5-21-1-2-3-1001")
        {
            StartTimeout = TimeSpan.FromSeconds(5),
            StopTimeout = TimeSpan.FromSeconds(2),
            Poll = TimeSpan.FromMilliseconds(50),
            Elevated = () => false,
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

    [Fact]
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
    public async Task A_question_waiting_holds_the_update_too()
    {
        await StartAsync(null, Make.Session("s1") with { Status = SessionStatus.Idle, Question = Make.Approval() });
        Assert.False(auto.Idle);

        await TwoIdleTicksAsync();

        Assert.Equal("0.2.0", auto.Pending);
        Assert.False(updater.Applied);
    }

    [Fact]
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
    public async Task Without_a_running_host_to_say_what_its_sessions_are_doing_nothing_is_installed()
    {
        await StartAsync();
        await host.StopAsync();
        await Wait.UntilAsync(() => connection.State != LinkState.Connected, "the host gone");

        await TwoIdleTicksAsync();

        Assert.Equal(0, updater.Checks);
        Assert.False(updater.Applied);
    }

    [Fact]
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

    // A host slow to go: its pipe still answers for a while after it was asked to stop. Completes
    // when it has been asked.
    Task SlowToStop()
    {
        var asked = new TaskCompletionSource();
        host.On(HostOps.Stop, data =>
        {
            asked.TrySetResult();
            _ = Task.Run(async () => { await Task.Delay(400); await host.StopAsync(); });
            return new { stopping = true };
        });
        return asked.Task;
    }

    [Fact]
    public async Task Quitting_while_the_host_stops_for_an_update_applies_nothing_and_starts_the_host_again_once_it_has_stopped()
    {
        await StartAsync();
        processes.OnStart = () => host.Start();
        var stopAsked = SlowToStop();
        await auto.TickAsync();
        time.Advance(AutoUpdater.IdleFor);

        var tick = Task.Run(auto.TickAsync);
        await stopAsked;
        // What App.Quit does on its way out, at the worst moment (off the test's own context, which
        // the fake host's pipe loops run on).
        await Task.Run(() => manager.Quit(TimeSpan.FromSeconds(10), log.Add));
        await tick;

        Assert.False(updater.Applied);
        Assert.True(processes.Started.Count == 1, string.Join("\n", log));
        Assert.Single(host.Sent(HostOps.Stop));
        Assert.False(manager.UpdateUnderWay);
        await Wait.UntilAsync(() => connection.State == LinkState.Connected, "the host back");
    }

    [Fact]
    public async Task Update_and_restart_pressed_while_the_automatic_update_installs_is_refused_and_leaves_it_alone()
    {
        await StartAsync();
        var stopAsked = SlowToStop();
        await auto.TickAsync();
        time.Advance(AutoUpdater.IdleFor);

        var tick = Task.Run(auto.TickAsync);
        await stopAsked;
        // Settings' Update and restart, meanwhile.
        var second = false;
        await Assert.ThrowsAsync<UpdateUnderWayException>(() => manager.StopAndApplyAsync(() => second = true, () => second = true));
        await tick;

        Assert.False(second);
        Assert.True(updater.Applied);
        Assert.Single(host.Sent(HostOps.Stop));
        Assert.Equal("0.1.0", prefs.UpdatedFrom);
    }

    [Fact]
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

        AutoUpdater.Announce(() => prefs, p => prefs = p, updater, toasts, log.Add);
        AutoUpdater.Announce(() => prefs, p => prefs = p, updater, toasts, log.Add);

        var toast = Assert.Single(toasts.Shown);
        Assert.Contains("Agent Bridge updated to 0.2.0", toast.Xml);
        Assert.Contains("from 0.1.0", toast.Xml);
        Assert.Contains("Offline machines and Forget", toast.Xml);
        Assert.Null(prefs.UpdatedFrom);
    }

    [Fact]
    public void An_update_that_did_not_take_announces_nothing()
    {
        var toasts = new Toasts();
        prefs = prefs with { UpdatedFrom = "0.1.0" };

        AutoUpdater.Announce(() => prefs, p => prefs = p, updater, toasts, log.Add);

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
    }

    [Fact]
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
    public async Task The_hosts_asks_reach_the_updater_once_each_and_the_host_hears_what_it_did_and_which_it_took()
    {
        prefs = prefs with { AutoUpdate = false };
        host.On(HostOps.UpdateReport, _ => new { ok = true });
        await StartAsync(FakeHost.StatusOf() with { UpdateAsks = [new UpdateAsk(Press, UpdateAskKind.Check, "pwa 1a2b3c4d", Make.T0)] });

        using var reporter = new UpdateReporter(connection, auto, log.Add);

        await Wait.UntilAsync(() => Reports().Any(r => r.Taken?.Contains(Press) == true) && Reports().Any(r => r.State.Available == "0.2.0"), "the check reported");
        Assert.Equal(1, updater.Checks);
        // The host's status again with the ask still in it (the report not yet applied there): it
        // is not asked for twice.
        await host.SendAsync(HostOps.StatusEvent, host.Status);
        await Task.Delay(300);
        Assert.Equal(1, updater.Checks);
        Assert.Single(log, l => l == "update: check asked for");
    }

    [Fact]
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
    public async Task A_check_that_finds_nothing_newer_drops_the_release_an_earlier_check_found()
    {
        prefs = prefs with { AutoUpdate = false };
        await StartAsync();
        await AskedAsync(UpdateAskKind.Check);
        Assert.Equal("0.2.0", auto.State.Available);

        // The feed changed, or pre-releases were turned off: nothing newer now.
        updater.Found = null;
        updater.Ready = null;
        await AskedAsync(UpdateAskKind.Check, "9b8c7d6e-5f40-4321-8abc-def012345678");

        Assert.Null(auto.State.Available);
        Assert.Equal(UpdateOutcome.Current, auto.State.Last!.Outcome);
    }

    [Fact]
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
    public async Task A_check_pressed_while_another_runs_gets_a_check_of_its_own()
    {
        prefs = prefs with { AutoUpdate = false };
        updater.Found = null;
        await StartAsync();
        updater.CheckGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const string Second = "9b8c7d6e-5f40-4321-8abc-def012345678";

        var first = auto.Ask(UpdateAskKind.Check, Press);
        await Wait.UntilAsync(() => updater.Checks == 1, "the first check under way");
        await auto.Ask(UpdateAskKind.Check, Second);
        updater.CheckGate.SetResult();
        await first;

        Assert.Equal(2, updater.Checks);
        Assert.Equal((UpdateOutcome.Current, Second), (auto.State.Last!.Outcome, auto.State.Last.Request));
        Assert.Null(auto.State.Request);
    }

    [Fact]
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