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
}
