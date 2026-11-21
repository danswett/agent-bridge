using AgentBridge.Transport;

namespace AgentBridge.Host;

// The serving connection's roster read, for HostEnrollment.Finish: never a second connection as this device.
internal sealed class LiveRosterSource
{
    Func<Task<RelayRoster?>>? read;

    public void Set(Func<Task<RelayRoster?>> source) => Volatile.Write(ref read, source);

    public void Clear() => Volatile.Write(ref read, null);

    public Func<Task<RelayRoster?>> Reader => () => Volatile.Read(ref read) is { } source ? source() : Task.FromResult<RelayRoster?>(null);
}
