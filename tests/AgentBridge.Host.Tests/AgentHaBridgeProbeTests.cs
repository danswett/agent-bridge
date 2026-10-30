using AgentBridge.Host.Delivery.Windows;
using Xunit;

public sealed class AgentHaBridgeProbeTests
{
    [Fact]
    public void Only_the_daemon_itself_counts_as_agent_ha_bridge_seen_running()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        const string id = "_0123456789abcdef0123456789abcdef";
        // Its supervisor, or its task, can run while the daemon fails to start.
        Assert.False(AgentHaBridgeProbe.DaemonAmong(["CopilotBridgeSupervisor" + id, $"task AgentBridgeDaemon{id} (running)"]));
        Assert.True(AgentHaBridgeProbe.DaemonAmong(["CopilotBridgeSupervisor" + id, "AgentBridgeDaemon" + id]));
        Assert.True(AgentHaBridgeProbe.DaemonAmong(["CopilotBridgeDaemon"]));
    }
}
