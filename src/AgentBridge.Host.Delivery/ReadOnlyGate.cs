namespace AgentBridge.Host.Delivery;

// What every delivery service asks before it acts for another device: why it must not, or null.
// A host made read-only in its config (delivery.readOnly, the shadow host beside agent-ha-bridge)
// gives its own reason whatever agent-ha-bridge is doing; otherwise agent-ha-bridge running for
// this user is the reason (design G5). Read-only through G5 alone lasted only while its daemon ran:
// an update, a crash or a logoff race restarting it left the shadow host acting on the phone in
// the gap.
public sealed class ReadOnlyGate(IAgentHaBridgeProbe agentHaBridge, string? readOnly) : IAgentHaBridgeProbe
{
    public string? Active() => readOnly ?? agentHaBridge.Active();
}
