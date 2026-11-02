using AgentBridge.Host.Core.DevBox;
using AgentBridge.Host.Delivery.Windows;
using Xunit;

public sealed class AgentHaBridgeProbeTests
{
    [Fact]
    public void Only_the_daemon_itself_counts_as_agent_ha_bridge_seen_running()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        const string id = "0123456789abcdef0123456789abcdef";
        // Its supervisor, or its task, can run while the daemon fails to start.
        Assert.False(AgentHaBridgeProbe.DaemonAmong(["CopilotBridgeSupervisor_" + id, $"task AgentBridgeDaemon_{id} (running)"], id));
        Assert.True(AgentHaBridgeProbe.DaemonAmong(["CopilotBridgeSupervisor_" + id, "AgentBridgeDaemon_" + id], id));
        // Nor another installation's daemon, or a legacy one.
        Assert.False(AgentHaBridgeProbe.DaemonAmong(["AgentBridgeDaemon_fedcba9876543210fedcba9876543210", "CopilotBridgeDaemon", "AgentBridgeDaemon"], id));
    }

    // Two writers to one Dev Box's schedule is the double-writer problem G5 exists for: the host's
    // keep-awake stands back whenever agent-ha-bridge's own could run, read-only host or not.
    [Fact]
    public void The_hosts_keep_awake_stands_back_while_agent_ha_bridges_own_is_enabled_running_or_unknown()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        const string task = "AgentBridgeDevBoxKeepAwake_0123456789abcdef0123456789abcdef";

        var enabled = AgentHaBridgeProbe.KeepAwakeReason([(task, true, false)], []);
        Assert.Contains("this host stands back", enabled!.Reason);
        Assert.True(enabled.Known);
        Assert.Contains($"task {task} (running)", AgentHaBridgeProbe.KeepAwakeReason([(task, false, true)], [])!.Reason);
        // A pass of it holds the task's mutex, even with the task just disabled.
        Assert.Contains("a pass is running", AgentHaBridgeProbe.KeepAwakeReason([(task, false, false)], [task])!.Reason);
        // Not being able to read the scheduler is not permission, but nor is it evidence that
        // agent-ha-bridge keeps the Dev Box awake: the stand-back on it is an alarm.
        var unknown = AgentHaBridgeProbe.KeepAwakeReason(null, []);
        Assert.Contains("could not be read", unknown!.Reason);
        Assert.False(unknown.Known);
        // Migrate disables the task (rollback enables it again), and then the host keeps the Dev Box awake.
        Assert.Null(AgentHaBridgeProbe.KeepAwakeReason([(task, false, false)], []));
        Assert.Null(AgentHaBridgeProbe.KeepAwakeReason([], []));
    }

    [Fact]
    public void A_keep_awake_pass_of_agent_ha_bridge_running_now_is_seen_by_its_mutex()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var name = "AgentBridgeDevBoxKeepAwake_" + Guid.NewGuid().ToString("N");
        using var pass = new Mutex(true, @"Local\" + name);

        Assert.Contains($"{name} (a pass is running)", new AgentHaBridgeProbe().KeepAwakeActive()!.Reason);
    }

    // A rollback re-enables and starts agent-ha-bridge's keep-awake task, perhaps while a pass of
    // this host's is in flight: holding that task's own pass mutex makes its pass exit untouched,
    // as its own second pass would, and one of its passes already running keeps the host's out.
    [Fact]
    public async Task A_pass_of_the_hosts_holds_agent_ha_bridges_pass_mutex_and_none_starts_while_one_of_its_runs()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var name = "AgentBridgeDevBoxKeepAwake_" + Guid.NewGuid().ToString("N");

        using (var held = AgentHaBridgeProbe.HoldMutexes([name]))
        {
            Assert.NotNull(held);
            // agent-ha-bridge's worker: Mutex(false, Local\<task>).WaitOne(0), on a thread of its own.
            Assert.False(await Task.Run(() => { using var m = new Mutex(false, @"Local\" + name); return m.WaitOne(0); }));
        }
        using (var legacy = new Mutex(true, @"Local\" + name))
        {
            Assert.Null(AgentHaBridgeProbe.HoldMutexes([name]));
            legacy.ReleaseMutex();
        }
        using var free = AgentHaBridgeProbe.HoldMutexes([name]);
        Assert.NotNull(free);
    }

    // Unable to read the scheduler, a pass cannot tell whether one of agent-ha-bridge's runs, and
    // must not read as busy (quiet): it fails, which the keep-awake raises as an alarm.
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task A_pass_whose_scheduler_cannot_be_read_fails_loudly_instead_of_reading_as_busy()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        Assert.Throws<IOException>(() => AgentHaBridgeProbe.HoldKeepAwake(null));

        var root = Path.Combine(Path.GetTempPath(), "abprobe-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var file = new FileKeepAwakeLock(Path.Combine(root, "devbox-keepawake.lock"));
            await using var service = new KeepAwakeService(new KeepAwakeOptions
            {
                Settings = new DevBoxSettings { KeepAwake = true },
                FindIdentity = () => (new DevBoxIdentity(new Uri("https://tenant-fake-dc.westus3.devcenter.azure.com"), "FakeProject", "fake-box"), null),
                Token = new NoCalls(),
                Http = new NoCalls(),
                Lock = new KeepAwakeLocks(file, new DelegateKeepAwakeLock(() => AgentHaBridgeProbe.HoldKeepAwake(null))),
            });

            var result = await service.PassAsync();

            Assert.Equal(KeepAwakeResult.Failed, result.Outcome);
            Assert.True(result.Alarming);
            Assert.Contains("Task Scheduler could not be read", result.Detail);
            // The lock file it took first was let go.
            using var again = file.TryTake();
            Assert.NotNull(again);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    sealed class NoCalls : IDevCenterHttp, IDevCenterToken
    {
        public Task<DevCenterResponse> SendAsync(HttpMethod method, Uri uri, string token, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("no Dev Center call may be made");

        public Task<string> GetAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("no token may be asked for");
    }
}
