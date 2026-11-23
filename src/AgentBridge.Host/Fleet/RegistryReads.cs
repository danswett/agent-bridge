namespace AgentBridge.Host.Fleet;

// Whether the caller of the current fleet read needs one that starts after its request (the send-time name fence
// asks with `fresh: true`; docs/design/fleet-session-registry.md "One registry read for every local caller").
// Flows with the async call, and is restored when the op returns.
static class RegistryReads
{
    static readonly AsyncLocal<bool> fresh = new();
    public static bool FreshRequested => fresh.Value;

    public static IDisposable Scope(bool requested)
    {
        var before = fresh.Value;
        fresh.Value = requested;
        return new Restore(before);
    }

    sealed class Restore(bool before) : IDisposable { public void Dispose() => fresh.Value = before; }
}
