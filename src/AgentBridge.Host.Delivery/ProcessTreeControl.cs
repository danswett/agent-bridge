namespace AgentBridge.Host.Delivery;

internal sealed class ProcessTreeControl(Func<int, DateTimeOffset, HeldOpen> open,
    ProcessTreeEvidence evidence, TimeProvider? time = null) : IProcessControl
{
    internal ProcessTreeEvidence Evidence { get; } = evidence;
    internal TimeProvider Time { get; } = time ?? TimeProvider.System;
    public HeldOpen Open(int processId, DateTimeOffset startedAt) => open(processId, startedAt);
}
