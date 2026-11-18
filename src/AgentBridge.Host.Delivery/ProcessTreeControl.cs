namespace AgentBridge.Host.Delivery;

internal sealed class ProcessTreeControl(
    Func<int, DateTimeOffset, HeldOpen> open,
    Func<IReadOnlyDictionary<int, int>?> parents,
    Func<int, DateTimeOffset?> created) : IProcessControl
{
    public HeldOpen Open(int processId, DateTimeOffset startedAt)
    {
        _ = parents;
        _ = created;
        return open(processId, startedAt);
    }
}
