using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// Updates without being asked, over a host on a real pipe: when an update waits (a turn running,
// a question waiting), when it goes anyway (six hours), and the hosts it never touches (read-only,
// agent-ha-bridge's, one that cannot say). Velopack and Task Scheduler are fakes.
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
    string? agentHaBridge;
    string? others;
    int? othersAskedFor;
    Func<string?>? othersProbe;

    sealed class Updater : IUpdater
    {
        public int Checks;
        public int Downloads;
        public bool Applied;
        public Action? OnApply;
        public string? Found = "0.2.0";
        public string? Ready { get; set; }
        public Exception? ApplyFails;
        public string? InstalledVersion { get; set; } = "0.1.0";
        public string? PendingNotes => Found is null ? null : $"What {Found} changes";
        public Task<string?> CheckAsync() { Checks++; return Task.FromResult(Found); }
        public Task DownloadAsync(Action<int> progress) { Downloads++; progress(100); Ready = Found; return Task.CompletedTask; }
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
        auto = new AutoUpdater(updater, connection, manager, () => prefs, p => prefs = p, log.Add, () => agentHaBridge, pid => { othersAskedFor = pid; return othersProbe is { } probe ? probe() : others; }, time);
        await Wait.UntilAsync(() => connection.State == LinkState.Connected && connection.Status is not null && connection.Sessions.All.Count == sessions.Length && connection.Sessions.Complete, "the connection");
    }

    public async Task DisposeAsync()
    {
        if (connection is not null) { await connection.DisposeAsync(); }
        await host.DisposeAsync();
        Directory.Delete(app, recursive: true);
    }

    static HostStatus ReadOnly() => FakeHost.StatusOf() with { Machine = new MachineInfo("host-id", "Test PC", "windows", "0.1.0", ReadOnly: "delivery.readOnly is set") };

    [Fact]
    public async Task An_update_found_when_no_session_is_busy_stops_the_host_then_installs()
    {
        await StartAsync(null, Make.Session("s1", SessionStatus.Idle), Make.Session("s2", SessionStatus.Ending));
        var stoppedFirst = false;
        updater.OnApply = () => stoppedFirst = host.Sent(HostOps.Stop).Count == 1;

        await auto.TickAsync();

        Assert.True(updater.Applied);
        Assert.True(stoppedFirst);
        Assert.Equal("0.1.0", prefs.UpdatedFrom);
        Assert.Equal("What 0.2.0 changes", prefs.UpdateNotes);
    }

    [Fact]
    public async Task A_turn_running_holds_the_update_until_the_session_is_idle()
    {
        await StartAsync(null, Make.Session("s1", SessionStatus.Working));

        await auto.TickAsync();
        Assert.Equal("0.2.0", auto.Pending);
        Assert.False(updater.Applied);

        time.Advance(TimeSpan.FromMinutes(1));
        await auto.TickAsync();
        Assert.False(updater.Applied);

        await host.SendAsync(Events.SessionUpsert, Make.Session("s1", SessionStatus.Idle));
        await Wait.UntilAsync(() => connection.Sessions.Get("s1")?.Status == SessionStatus.Idle, "the session going idle");
        time.Advance(TimeSpan.FromMinutes(1));
        await auto.TickAsync();
        Assert.True(updater.Applied);
        Assert.Equal(1, updater.Downloads);
    }

    [Fact]
    public async Task A_question_waiting_holds_the_update_too()
    {
        await StartAsync(null, Make.Session("s1") with { Status = SessionStatus.Idle, Question = Make.Approval() });
        Assert.False(auto.Idle);

        await auto.TickAsync();

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
    public async Task A_read_only_host_is_never_updated_however_long_it_waits()
    {
        await StartAsync(ReadOnly());

        await auto.TickAsync();
        time.Advance(AutoUpdater.WaitAtMost + TimeSpan.FromHours(1));
        await auto.TickAsync();

        Assert.Equal(0, updater.Checks);
        Assert.False(updater.Applied);
        Assert.Empty(host.Sent(HostOps.Stop));
        Assert.Contains("read-only", auto.WhyNot());
    }

    [Fact]
    public async Task A_host_whose_machine_agent_ha_bridge_owns_is_never_updated()
    {
        await StartAsync(FakeHost.StatusOf(agentHaBridge: "agent-ha-bridge's daemon is running"));

        await auto.TickAsync();
        time.Advance(AutoUpdater.WaitAtMost + TimeSpan.FromHours(1));
        await auto.TickAsync();

        Assert.Equal(0, updater.Checks);
        Assert.False(updater.Applied);
        Assert.Contains("G5", auto.WhyNot());
    }

    [Fact]
    public async Task A_host_that_turns_read_only_while_an_update_waits_is_not_updated()
    {
        await StartAsync(null, Make.Session("s1", SessionStatus.Working));
        await auto.TickAsync();
        Assert.Equal("0.2.0", auto.Pending);

        await host.SendAsync(HostOps.StatusEvent, ReadOnly());
        await Wait.UntilAsync(() => connection.Status?.Machine.ReadOnly is not null, "the read-only status");
        time.Advance(AutoUpdater.WaitAtMost);
        await auto.TickAsync();

        Assert.False(updater.Applied);
    }

    [Fact]
    public async Task Without_a_running_host_to_say_what_it_is_nothing_is_installed()
    {
        await StartAsync();
        await host.StopAsync();
        await Wait.UntilAsync(() => connection.State != LinkState.Connected, "the host gone");

        await auto.TickAsync();

        Assert.Equal(0, updater.Checks);
        Assert.False(updater.Applied);
    }

    [Fact]
    public async Task A_host_whose_sessions_are_not_yet_read_in_full_is_not_idle()
    {
        await StartAsync(null, Make.Session("s1", SessionStatus.Idle));
        Assert.True(auto.Idle);

        connection.Sessions.BeginSnapshot();

        Assert.False(auto.Idle);
        await auto.TickAsync();
        Assert.False(updater.Applied);
    }

    [Fact]
    public async Task Agent_ha_bridge_starting_during_the_download_stops_the_install()
    {
        await StartAsync();
        agentHaBridge = "agent-ha-bridge's daemon is running";

        await auto.TickAsync();

        Assert.Equal("0.2.0", auto.Pending);
        Assert.False(updater.Applied);
        Assert.Empty(host.Sent(HostOps.Stop));
        Assert.Contains(log, l => l.Contains("G5"));
    }

    [Fact]
    public async Task A_host_read_only_by_its_status_now_is_not_updated_though_the_last_report_said_otherwise()
    {
        await StartAsync();
        // What host.status answers now; the connection has not heard of it yet.
        host.Status = ReadOnly();

        await auto.TickAsync();

        Assert.False(updater.Applied);
        Assert.Empty(host.Sent(HostOps.Stop));
    }

    [Fact]
    public async Task Another_profile_running_from_the_installation_holds_the_update()
    {
        await StartAsync();
        others = "another profile's host (pid 77) runs from this installation";

        await auto.TickAsync();

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
        await host.SendAsync(Events.SessionUpsert, Make.Session("s1", SessionStatus.Idle));
        await Wait.UntilAsync(() => connection.Sessions.Get("s1")?.Status == SessionStatus.Idle, "the session going idle");
        await auto.TickAsync();

        Assert.False(updater.Applied);
        Assert.Empty(host.Sent(HostOps.Stop));
        Assert.Null(auto.Pending);

        // The next tick checks and downloads again, then installs.
        await auto.TickAsync();
        Assert.True(updater.Applied);
    }

    [Fact]
    public async Task A_failed_install_brings_the_host_back_and_waits_for_the_next_check()
    {
        await StartAsync();
        // The host the failed install stopped is started again, as its logon task would.
        tasks.OnRun = () => host.Start();
        processes.OnStart = () => host.Start();
        updater.ApplyFails = new InvalidOperationException("Update.exe is missing");

        await auto.TickAsync();

        Assert.Single(host.Sent(HostOps.Stop));
        Assert.Null(auto.Pending);
        Assert.Null(prefs.UpdatedFrom);
        await Wait.UntilAsync(() => connection.State == LinkState.Connected && connection.Sessions.Complete, "the host back");
        var checks = updater.Checks;
        time.Advance(TimeSpan.FromMinutes(1));
        await auto.TickAsync();
        Assert.Equal(checks, updater.Checks);
        Assert.Single(host.Sent(HostOps.Stop));
    }

    [Fact]
    public async Task Turning_it_off_while_it_asks_the_host_one_last_time_stops_the_install()
    {
        await StartAsync();
        // The last of the checks before stopping the host, during which the switch goes off.
        othersProbe = () => { prefs = prefs with { AutoUpdate = false }; return null; };

        await auto.TickAsync();

        Assert.False(updater.Applied);
        Assert.Empty(host.Sent(HostOps.Stop));
    }

    [Fact]
    public async Task Turned_off_it_neither_checks_nor_installs()
    {
        prefs = prefs with { AutoUpdate = false };
        await StartAsync();

        await auto.TickAsync();

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
}
