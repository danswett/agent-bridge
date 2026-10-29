using System.Text.RegularExpressions;
using System.Xml.Linq;
using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
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
    public async Task Without_a_task_starting_runs_the_launcher_with_the_profile()
    {
        processes.OnStart = () => host.Start();

        await manager.StartAsync();

        var (path, args) = Assert.Single(processes.Started);
        Assert.Equal(manager.Layout.LauncherExe, path);
        Assert.Equal(["serve", "--profile", "test"], args);
    }

    [Fact]
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
    public async Task Starting_a_running_host_starts_nothing_more()
    {
        host.Start();

        await manager.StartAsync();

        Assert.Empty(processes.Started);
        Assert.Empty(tasks.Ran);
    }

    [Fact]
    public async Task A_host_that_never_answers_is_reported_rather_than_waited_on_forever()
    {
        await Assert.ThrowsAsync<TimeoutException>(() => manager.StartAsync());
    }

    [Fact]
    public async Task Stopping_asks_the_host_to_stop_and_a_host_that_will_not_is_ended()
    {
        host.On(HostOps.Stop, _ => new { stopping = true });
        host.Start();

        await Assert.ThrowsAsync<TimeoutException>(() => manager.StopAsync());

        Assert.Single(host.Sent(HostOps.Stop));
        Assert.Equal([4242], processes.Killed);
    }

    [Fact]
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
}
