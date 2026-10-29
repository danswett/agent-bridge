using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// What the local pipe serves: the sessions ops exactly as the relay's channels get them, and
// the host's management ops, which only the pipe reaches.
public static class LocalApi
{
    public static Func<IProtocolChannel, string, JsonElement, Task<object?>> Handler(SessionService sessions, HostControl control) => async (channel, op, data) =>
    {
        if (op.StartsWith("host.", StringComparison.Ordinal)) { return await control.HandleAsync(channel, op, data); }
        var result = await sessions.HandleAsync(channel, op, data);
        return result is HelloResult hello ? hello with { Capabilities = [.. hello.Capabilities, HostOps.Capability] } : result;
    };
}

// One host per profile on a machine: a lock file in the profile's folder, held for the life of
// the process and let go by the OS however it ends. A host restarting itself waits for the one
// it replaces.
public sealed class HostLock : IDisposable
{
    readonly FileStream file;

    HostLock(FileStream file) { this.file = file; }

    public static HostLock? TryAcquire(string folder, TimeSpan wait)
    {
        var path = Path.Combine(folder, "host.lock");
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try { return new HostLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(250); }
            catch (IOException) { return null; }
        }
    }

    public void Dispose() => file.Dispose();
}
