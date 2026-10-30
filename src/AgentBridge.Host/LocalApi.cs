using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// What the local pipe serves: the sessions ops exactly as the relay's channels get them, and
// the host's management ops, which only the pipe reaches. Who may do what (CallerGuard):
//   * the Agent Bridge app: everything, the management ops included;
//   * anything inside an agent session: reading sessions and usage, nothing else. Not managing
//     the host, and not answering, replying, ending or launching sessions as if it were the
//     person, until agents have a principal of their own (design §5.5);
//   * anything else of the user's: the sessions ops as before, and the host's status, but not
//     managing it.
public static class LocalApi
{
    // Management ops that only read.
    static readonly HashSet<string> Reads = [HostOps.Status, HostOps.Subscribe];

    // Session ops that act as the person.
    static readonly HashSet<string> Acts =
        [Ops.SessionAnswer, Ops.SessionReply, Ops.SessionStop, Ops.AttachmentStart, Ops.AttachmentChunk, Ops.AttachmentDiscard, LaunchProtocol.Launch];

    // What an agent's hello offers: reading only.
    static readonly HashSet<string> AgentCapabilities = [Capabilities.SessionsRead, UsageProtocol.Capability];

    public static Func<IProtocolChannel, string, JsonElement, Task<object?>> Handler(SessionService sessions, HostControl control, ICallerGuard callers, Action<string>? log = null) => async (channel, op, data) =>
    {
        if (op.StartsWith("host.", StringComparison.Ordinal))
        {
            if (!Reads.Contains(op) && callers.Classify(channel, fresh: true) is { Kind: not CallerKind.App } caller
                && !(caller.Kind == CallerKind.HostCli && op == HostOps.Restart))
            {
                throw Refuse(op, caller, caller.Kind == CallerKind.Agent
                    ? "stopping, restarting, signing in, changing settings and reading the relay account are for the person, from the Agent Bridge app"
                    : "only the Agent Bridge app beside this host may stop, restart, sign in, change settings or read the relay account", log);
            }
            return await control.HandleAsync(channel, op, data);
        }
        if (Acts.Contains(op) && callers.Classify(channel) is { Kind: CallerKind.Agent } agent)
        {
            throw Refuse(op, agent, "answering, replying, ending and launching sessions are for the person, from the app or a phone", log);
        }
        var result = await sessions.HandleAsync(channel, op, data);
        if (result is not HelloResult hello) { return result; }
        return callers.Classify(channel).Kind switch
        {
            CallerKind.App => hello with { Capabilities = [.. hello.Capabilities, HostOps.Capability] },
            CallerKind.Agent => hello with { Capabilities = [.. hello.Capabilities.Where(AgentCapabilities.Contains)] },
            _ => hello,
        };
    };

    static InvalidOperationException Refuse(string op, Caller caller, string why, Action<string>? log)
    {
        log?.Invoke($"local pipe: refused {op} from {caller.Description}");
        return new InvalidOperationException($"refused: this connection is from {caller.Description}; {why}");
    }
}

// One host per profile on a machine: a lock file in the profile's folder, held for the life of
// the process and let go by the OS however it ends. A host restarting itself waits for the one
// it replaces.
public sealed class HostLock : IDisposable
{
    readonly FileStream file;

    HostLock(FileStream file) { this.file = file; }

    public static HostLock? TryAcquire(string folder, TimeSpan wait, string name = "host.lock")
    {
        var path = Path.Combine(folder, name);
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
