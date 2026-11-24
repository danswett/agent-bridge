using AgentBridge.Transport;

namespace AgentBridge.Host;

// The serving connection's roster read, for HostEnrollment.Finish: never a second connection as this device.
internal sealed class LiveRosterSource
{
    Func<Task<RelayRoster?>>? read;

    public void Set(Func<Task<RelayRoster?>> source) => Volatile.Write(ref read, source);

    public void Clear() => Volatile.Write(ref read, null);

    // The source is cleared the moment serve ends, on every exit path: the caller may go on to wait for a sign-in
    // (after CloseCodes.Replaced) and Finish must not keep reading an ended connection until then.
    public async Task ServeAsync(Func<Task<RelayRoster?>> source, Func<Task> serve)
    {
        Set(source);
        try { await serve(); }
        finally { Clear(); }
    }

    public Func<Task<RelayRoster?>> Reader => () => Volatile.Read(ref read) is { } source ? source() : Task.FromResult<RelayRoster?>(null);
}
