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

        Assert.Contains("this host stands back", AgentHaBridgeProbe.KeepAwakeReason([(task, true, false)], []));
        Assert.Contains($"task {task} (running)", AgentHaBridgeProbe.KeepAwakeReason([(task, false, true)], []));
        // A pass of it holds the task's mutex, even with the task just disabled.
        Assert.Contains("a pass is running", AgentHaBridgeProbe.KeepAwakeReason([(task, false, false)], [task]));
        // Not being able to read the scheduler is not permission.
        Assert.Contains("could not be checked", AgentHaBridgeProbe.KeepAwakeReason(null, []));
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

        Assert.Contains($"{name} (a pass is running)", new AgentHaBridgeProbe().KeepAwakeActive());
    }
}
