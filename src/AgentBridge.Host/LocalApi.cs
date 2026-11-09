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

    public static Func<IProtocolChannel, string, JsonElement, Task<object?>> Handler(SessionService sessions, HostControl control, ICallerGuard callers, Action<string>? log = null,     RemoteAuthorization? authorization = null,     AgentCredentials? credentials = null, AgentBridge.Host.Lite.ILiteSender? lite = null) => async (channel, op, data) =>
    {
            if (op == AgentBridge.Host.Lite.LiteAgentSend.Op) { return await AgentBridge.Host.Lite.LiteAgentSend.HandleAsync(credentials, lite, channel, data); }
            // Pipe only, and Windows only for now (§3.4): a relay channel never reaches this handler.
            if (op == AgentProtocol.Hello)
            {
                if (credentials is null || !OperatingSystem.IsWindows()) { throw new InvalidOperationException("refused: agent.hello isn't available on this host"); }
                AgentHelloRequest request;
                try { request = data.Deserialize<AgentHelloRequest>(ProtocolJson.Options) ?? throw new ArgumentException("agent.hello needs a token"); }
                catch (JsonException) { throw new ArgumentException("agent.hello needs a token"); }
                var ancestry = (channel as LocalChannel)?.Peer?.Ancestry ?? ProcessAncestry.Unknown;
                var decision = credentials.Check(request.Token, ancestry);
                if (decision.Accepted is not { } credential)
                {
                    log?.Invoke($"local pipe: refused agent.hello: {decision.Refusal}");
                    throw new InvalidOperationException($"refused: {decision.Refusal}");
                }
                return credentials.Accept(channel, credential);
            }
        // Managing a machine from another device (#801) is for the relay alone. Refused here for
        // every caller, the app included, so nothing on this machine, an agent least of all, can
        // reach it, and the #67 guard below stays exactly as it was.
        if (ManageProtocol.IsOp(op))
        {
            throw Refuse(op, callers.Classify(channel), ManageService.ThroughTheRelay, log);
        }
        if (op.StartsWith("host.", StringComparison.Ordinal))
        {
            if (!Reads.Contains(op) && callers.Classify(channel, fresh: true) is { Kind: not CallerKind.App } caller
                && !(caller.Kind == CallerKind.HostCli && op == HostOps.Restart))
            {
                throw Refuse(op, caller, op is HostOps.UpdateReport or HostOps.UpdateDepart
                    ? $"only the {Product.App} beside this host reports its updates, and takes other devices' presses of Install"
                    : caller.Kind == CallerKind.Agent
                    ? $"stopping, restarting, signing in, changing settings and reading the relay account are for the person, from the {Product.App}"
                    : $"only the {Product.App} beside this host may stop, restart, sign in, change settings or read the relay account", log);
            }
            if (authorization is not null && op.StartsWith("host.peers.", StringComparison.Ordinal)) { return authorization.Manage(op, data); }
            return await control.HandleAsync(channel, op, data);
        }
        if (Acts.Contains(op) && callers.Classify(channel) is { Kind: CallerKind.Agent } agent)
        {
            throw Refuse(op, agent, "answering, replying, ending and launching sessions are for the person, from the app or a phone", log);
        }
        // What a device says it shows decides whether the person is using it, and a device in use
        // holds back the person's pushes (#1190): an agent saying it would silence them.
        if (op == Ops.SessionsFocus && callers.Classify(channel) is { Kind: CallerKind.Agent } focused)
        {
            throw Refuse(op, focused, "telling the host the person is using the app is for the person, from the app or a phone", log);
        }
        // A whole past conversation of every session, words the person pasted into other sessions
        // included, is more than an agent reading the latest exchange needs, until agents have a
        // principal of their own (design §5.5, #45; docs/protocol/v1-history.md).
        if (op == HistoryProtocol.Op && callers.Classify(channel) is { Kind: CallerKind.Agent } reader)
        {
            throw Refuse(op, reader, "reading a session's history is for the person, from the app or a phone", log);
        }
        // Shaped inside the service, so hello.changed compares what this caller is told (#651).
        return await sessions.HandleAsync(channel, op, data, hello => callers.Classify(channel).Kind switch
        {
            CallerKind.App => hello with { Capabilities = [.. hello.Capabilities, HostOps.Capability,
                .. control.HasAgentReadiness ? new[] { HostOps.AgentReadinessCapability } : [],
                .. control.HasAgentSetup ? new[] { HostOps.AgentSetupCapability } : [],
                .. control.HasAgentInstall ? new[] { HostOps.AgentInstallCapability } : [],
                .. control.HasAgentVerification ? new[] { HostOps.AgentVerificationCapability } : [],
                .. control.HasAgentSignIn ? new[] { HostOps.AgentSignInCapability } : []] },
            CallerKind.Agent => hello with { Capabilities = [.. hello.Capabilities.Where(AgentCapabilities.Contains)] },
            _ => hello,
        });
    };

    static InvalidOperationException Refuse(string op, Caller caller, string why, Action<string>? log)
    {
        log?.Invoke($"local pipe: refused {op} from {caller.Description}");
        return new InvalidOperationException($"refused: this connection is from {caller.Description}; {why}");
    }
}

// One host per profile on a machine: a lock file in the profile's folder, held for the life of
// the process and let go by the OS however it ends. A host restarting itself starts the one
// that replaces it only after letting go (HostHandover).
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

// host.restart (#239): the replacement is started once this host has finished stopping, every
// disposal done and its lock let go, never when the restart is asked for. It was started at once
// and waited 15 s for the lock, while the old host's shutdown could take far longer (a usage
// monitor's disposal alone waits up to 90 s for a probe): the replacement gave up, the old host
// exited, and the restart ended with no host at all, which nothing brings back until sign-in
// (#106). Started this late it finds the lock free, so a restart ends with exactly one host: this
// one is no longer serving, and a host something else started meanwhile keeps the lock and the
// replacement stands back. A host that fails on its way out still hands over.
public sealed class HostHandover
{
    int requested;

    HostHandover() { }

    public bool Requested => Volatile.Read(ref requested) == 1;

    // Asked for by host.restart, before the host is told to stop.
    public void Request() => Volatile.Write(ref requested, 1);

    // Run by the logon task's launcher (#106).
    public bool Supervised { get; private init; }

    bool checkStop;

    // Whether the app stopped this host while it was starting, which no pipe could tell it (#106's
    // review): the app's Stop leaves the launcher's stop marker, and a host the launcher started is
    // to look for it once its pipe is open, always (#986: a host that skipped the look resumed
    // agents that outlive it). So is a host its launcher could not vouch for, one started outside
    // the launcher's kill-on-close job or under a launcher the app cannot find by its record
    // (HostLauncherContract.CheckStopVariable, #420's review), and its replacement. Never for a
    // host started any other way (by hand, after the app's Stop), which the marker does not concern.
    public bool StoppedFromTheApp(string profileFolder) =>
        (Supervised || checkStop) && File.Exists(Path.Combine(profileFolder, HostLauncherContract.StopMarker));

    public static async Task<int> RunAsync(Func<HostHandover, Task<int>> serve, Action<bool> startReplacement, bool supervised = false, bool checkStop = false)
    {
        var handover = new HostHandover { Supervised = supervised, checkStop = checkStop };
        try
        {
            var code = await serve(handover);
            // Under the logon task's launcher (#106) the launcher starts the replacement, and keeps
            // watching it: one this host started would be watched by nothing.
            return handover.Requested && supervised ? HostLauncherContract.Restart : code;
        }
        finally
        {
            // A supervised host that fails on its way out ends as a crash instead, which the
            // launcher starts again too.
            if (handover.Requested && !supervised) { startReplacement(checkStop); }
        }
    }
}
