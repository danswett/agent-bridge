using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// CallerGuard and scoped credentials govern pipe ops; script management is host-CLI-only (docs/protocol/local-pipe.md).
public static class LocalApi
{
    // Management ops that only read.
    static readonly HashSet<string> Reads = [HostOps.Status, HostOps.Subscribe];

    // Session ops that act as the person.
    static readonly HashSet<string> Acts =
        [Ops.SessionAnswer, Ops.SessionReply, Ops.SessionStop, Ops.AttachmentStart, Ops.AttachmentChunk, Ops.AttachmentDiscard, LaunchProtocol.Launch];

    // What an agent's hello offers: reading only.
    static readonly HashSet<string> AgentCapabilities = [Capabilities.SessionsRead, UsageProtocol.Capability];

    public static Func<IProtocolChannel, string, JsonElement, Task<object?>> Handler(SessionService sessions, HostControl control, ICallerGuard callers, Action<string>? log = null,
        RemoteAuthorization? authorization = null, AgentCredentials? credentials = null, AgentBridge.Host.Lite.ILiteSender? lite = null, AgentBridge.Host.Fleet.FleetList? fleet = null,
        AgentAuditLog? audit = null, TimeProvider? auditTime = null, AgentPipeLaunch? agentLaunch = null,         SamePcPinHandoff? samePcPin = null, AgentPipeLaunch? grantLaunch = null, FleetStopCommands? fleetStop = null) =>
                Handler(sessions, control, callers, log, authorization, credentials, lite, fleet, audit, new RefusalAudits(auditTime ?? TimeProvider.System), agentLaunch, samePcPin, grantLaunch, fleetStop);

    static Func<IProtocolChannel, string, JsonElement, Task<object?>> Handler(SessionService sessions, HostControl control, ICallerGuard callers, Action<string>? log,
        RemoteAuthorization? authorization, AgentCredentials? credentials, AgentBridge.Host.Lite.ILiteSender? lite, AgentBridge.Host.Fleet.FleetList? fleet,
        AgentAuditLog? audit, RefusalAudits refusals, AgentPipeLaunch? agentLaunch, SamePcPinHandoff? samePcPin, AgentPipeLaunch? grantLaunch, FleetStopCommands? fleetStop) => async (channel, op, data) =>
    {
        if (op == LaunchProtocol.Launch && credentials?.IsScriptConnection(channel) == true)
        {
            if (credentials.Of(channel)?.FleetLaunch != true) { credentials.CheckScriptOperation(channel, op, log); }
            if (data.ValueKind != JsonValueKind.Object || System.Text.Encoding.UTF8.GetByteCount(data.GetRawText()) > LaunchProtocol.MaxRequest
                || data.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1)
                || data.EnumerateObject().Any(p => p.Name is not ("request" or "agent" or "workspace" or "permissions"
                    or "model" or "effort" or "context" or "prompt" or "role" or "fleetName")))
            { throw Refuse(op, callers.Classify(channel), "fleet launch contains unsupported fields or exceeds its size limit", log); }
            var request = data.Deserialize<LaunchRequest>(ProtocolJson.Options) ?? throw new ArgumentException("fleet launch needs a request");
            string? Refusal() => credentials.Of(channel) is not { } script ? "the script credential is no longer live"
                : callers.Classify(channel, fresh: true).Kind == CallerKind.Agent ? "fleet launch is refused inside an agent session"
                : credentials.FleetGrant.CheckLaunch(script, (channel as LocalChannel)?.Peer?.Ancestry ?? ProcessAncestry.Unknown, request);
            if (Refusal() is { } reason) { throw Refuse(op, callers.Classify(channel), reason, log); }
            var live = credentials.Of(channel) ?? throw Refuse(op, callers.Classify(channel), "the script credential is no longer live", log);
            return await sessions.LaunchFleetAsync(request, credentials.PrincipalOf(live), audit, Refusal,
                actual => credentials.FleetGrant.CheckDispatch(actual, request));
        }
        credentials?.CheckScriptOperation(channel, op, log);
        if (op is "launches.status" or "launches.list") { return sessions.LaunchStatus(data, op == "launches.list"); }
        if (op == FleetWorkerSettings.Op)
        {
            var caller = callers.Classify(channel, fresh: true);
            if (channel is not LocalChannel || caller.Kind != CallerKind.App || credentials?.Of(channel) is not null)
            { throw Refuse(op, caller, "only the local app may read or change workspace fleet workers", log); }
            return control.WorkspaceFleetWorkers(data);
        }
        if (op == SamePcPinProtocol.Op)
        {
            // Ruling item 3 (#3450): the person's signed-in app on this machine, never an agent, script, CLI or relay peer.
            var caller = callers.Classify(channel, fresh: true);
            if (samePcPin is null || channel is not LocalChannel || caller.Kind != CallerKind.App || credentials?.Of(channel) is not null)
            { throw Refuse(op, caller, "only the signed-in app on this machine may hand this host its roster root", log); }
            return await samePcPin.HandleAsync(data, CancellationToken.None);
        }
        if (op is "scripts.add" or "scripts.revoke" or "scripts.grant-fleet" or "scripts.revoke-fleet" or "scripts.list" or "scripts.confirm")
        { return Cli.ScriptCredentialCommands.Handle(channel, op, data, callers, credentials, audit); }
        if (op is AgentProtocol.Adopt or AgentProtocol.Revoke or AgentProtocol.Credential)
        {
            if (credentials is null || !OperatingSystem.IsWindows() || channel is not LocalChannel local)
            {
                throw new InvalidOperationException($"refused: {op} isn't available on this host");
            }
            if (op == AgentProtocol.Credential)
            {
                if (credentials.Of(channel) is not null) { throw new InvalidOperationException("refused: this connection already has an agent credential"); }
                var claim = credentials.Claim(local.Peer?.Ancestry ?? ProcessAncestry.Unknown);
                if (claim is not { Token: { } token, Credential: { } issued }) { throw new InvalidOperationException($"refused: {claim.Refusal}"); }
                return new AgentCredentialResult(token, credentials.PrincipalOf(issued), issued.Scope, issued.ExpiresAt);
            }
            var person = callers.Classify(channel, fresh: true);
            if (person.Kind is not (CallerKind.App or CallerKind.HostCli))
            {
                throw Refuse(op, person, "adopting a session, or ending its adoption, is for the person at this machine, from the app or the host's own command line", log);
            }
            try
            {
                if (credentials.Problem is not null) { throw new InvalidOperationException("refused: the credential store is unavailable"); }
                if (op == AgentProtocol.Revoke)
                {
                    var revoke = data.Deserialize<AgentRevokeRequest>(ProtocolJson.Options);
                    if (string.IsNullOrEmpty(revoke?.Session) || !AgentCredentials.ValidSession(revoke.Session))
                    { throw new ArgumentException("agent.revoke needs a valid session"); }
                    var revoked = credentials.Revoke(revoke.Session, $"revoked by a person ({person.Description})");
                    if (credentials.Problem is not null) { throw new InvalidOperationException("refused: the credential store is unavailable"); }
                    return new AgentRevokeResult(revoke.Session, revoked);
                }
                var adopt = data.Deserialize<AgentAdoptRequest>(ProtocolJson.Options);
                if (string.IsNullOrEmpty(adopt?.Session)) { throw new ArgumentException("agent.adopt needs a session"); }
                if (sessions.ProcessForAdoption(adopt.Session) is not { Id: > 0, StartedAt: { } started } process)
                {
                    throw new InvalidOperationException("refused: the host has no running process for that session to adopt");
                }
                var adoption = credentials.Adopt(adopt.Session, process.Id, started, adopt.Scope ?? AgentProtocol.ScopeRead);
                if (credentials.Problem is not null) { throw new InvalidOperationException("refused: the credential store is unavailable"); }
                return new AgentAdoptResult(adoption.Session, adoption.Scope, adoption.ExpiresAt);
            }
            catch (JsonException) { throw new ArgumentException($"{op} needs a session"); }
            catch (ArgumentException e) { throw new InvalidOperationException($"refused: {e.Message}"); }
        }
        if (op == Fleet.FleetNames.Op)
        {
            var caller = callers.Classify(channel, fresh: true);
            if (caller.Kind is not (CallerKind.App or CallerKind.HostCli))
            {
                throw Refuse(op, caller, "only the app or host command line may set a fleet name", log);
            }
            return await sessions.RenameFleetSessionAsync(data, fleet?.WarningRoster, channel.Closed);
        }
            if (op == AgentBridge.Host.Lite.LiteAgentSend.Op) { return await AgentBridge.Host.Lite.LiteAgentSend.HandleAsync(credentials, lite, channel, data, appendAttempt: audit is null ? null : audit.Append); }
            if (op == AgentBridge.Host.Fleet.FleetList.Op) { return await (fleet ?? new AgentBridge.Host.Fleet.FleetList(credentials, null)).HandleAsync(channel, data, auditLog: audit); }
            if (op == AgentBridge.Host.Fleet.FleetList.FindOp) { return await (fleet ?? new AgentBridge.Host.Fleet.FleetList(credentials, null)).FindAsync(channel, data, auditLog: audit); }
            // Pipe only, and Windows only for now (§3.4): a relay channel never reaches this handler.
            if (op == AgentProtocol.Hello)
            {
                if (credentials is null || !OperatingSystem.IsWindows()) { throw new InvalidOperationException("refused: agent.hello isn't available on this host"); }
                AgentHelloRequest request;
                try { request = data.Deserialize<AgentHelloRequest>(ProtocolJson.Options) ?? throw new ArgumentException("agent.hello needs a token"); }
                catch (JsonException) { throw new ArgumentException("agent.hello needs a token"); }
                var ancestry = (channel as LocalChannel)?.Peer?.Ancestry ?? ProcessAncestry.Unknown;
                var decision = credentials.Check(request.Token, ancestry, agentCaller: callers.Classify(channel, fresh: true).Kind == CallerKind.Agent);
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
        if (op == LaunchProtocol.Launch && callers.Classify(channel, fresh: true) is { Kind: CallerKind.Other } untraced)
        {
            const string why = "untraceableCaller: launching sessions requires the app or the host's own command line";
            AuditRefusal("unknown:local", [], op, data, audit, why, refusals);
            throw Refuse(op, untraced, why, log);
        }
        if (op == Ops.SessionAnswer && credentials?.Of(channel) is { } answering)
        {
            // Refusals must not copy arbitrary answer fields into the audit trail.
            var target = AnswerAuditTarget(data);
            AuditAgentAction(credentials, answering, op, target, "refused", audit, sessions?.RefusedAnswerGate(target, data));
            throw Refuse(op, new Caller(CallerKind.Agent, credentials.PrincipalOf(answering)),
                "answering, replying, ending and launching sessions are for the person, from the app or a phone", log);
        }
        if (op == FleetStopCommands.Op)
        {
            // Same grant gate as a granted launch: only a live agent session credential, never a script, the app or the CLI.
            var ending = callers.Classify(channel, fresh: true);
            if (fleetStop is null || credentials is null || ending.Kind != CallerKind.Agent || credentials.Of(channel) is not { Kind: "session" } ender)
            { throw Refuse(op, ending, "fleet end needs a live agent session credential", log); }
            return await fleetStop.HandleAsync(ender, data);
        }
        if (grantLaunch is not null && op == LaunchProtocol.Launch && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("grant", out _)
            && credentials is { } grantIssuer && callers.Classify(channel, fresh: true) is { Kind: CallerKind.Agent } granted
            && grantIssuer.Of(channel) is { Kind: "session" } grantee)
        {
            if (!grantIssuer.IsLive(grantee)) { throw Refuse(op, granted, "the credential is no longer live", log); }
            return await grantLaunch.LaunchGrantedAsync(grantee, data);
        }
        if (op is LaunchProtocol.Launch or LaunchProtocol.Options
            && callers.Classify(channel).Kind == CallerKind.Agent)
        {
            if (credentials is { } store && store.Of(channel) is { Kind: "session" } launchReader
                && launchReader.Scope != AgentPolicy.ScopeNone)
            {
                // With the durable record, its outcome answers a repeat (AgentPipeLaunch), not the launcher's memory.
                if (op == LaunchProtocol.Launch && agentLaunch is null
                    && sessions.ReplayAgentLaunch(data, store.PrincipalOf(launchReader)) is { } replay)
                {
                    var result = await replay;
                    if (store.IsLive(launchReader)) { return result; }
                    throw Refuse(op, callers.Classify(channel), "the credential is no longer live", log);
                }
                if (op == LaunchProtocol.Options)
                {
                    var offered = await sessions.HandleAsync(channel, op, data);
                    if (!store.IsLive(launchReader)) { throw Refuse(op, callers.Classify(channel), "the credential is no longer live", log); }
                    if (offered is not LaunchOptions choices) { throw Refuse(op, callers.Classify(channel), "launch options are unavailable", log); }
                    return store.LaunchGate.ProjectOptions(choices);
                }
            }
            if (op == LaunchProtocol.Options)
            { throw Refuse(op, callers.Classify(channel), "a live session read credential is required", log); }
        }
        if (agentLaunch is not null && op == AgentPipeLaunch.StatusOp)
        {
            var asking = callers.Classify(channel, fresh: true);
            if (asking.Kind != CallerKind.Agent || credentials?.Of(channel) is not { Kind: "session" } statusReader || !credentials.IsLive(statusReader))
            { throw Refuse(op, asking, "a live session read+reply+launch credential is required", log); }
            return agentLaunch.Status(statusReader, data);
        }
        if (agentLaunch is not null && op == LaunchProtocol.Launch && credentials is { } launchIssuer
            && callers.Classify(channel, fresh: true) is { Kind: CallerKind.Agent } launching && launchIssuer.Of(channel) is { Kind: "session" } launcher)
        {
            if (!launchIssuer.IsLive(launcher)) { throw Refuse(op, launching, "the credential is no longer live", log); }
            return data.ValueKind == JsonValueKind.Object && data.TryGetProperty("grant", out _) ? await agentLaunch.LaunchGrantedAsync(launcher, data)
                : await agentLaunch.LaunchAsync(launcher, data);
        }
        if (Acts.Contains(op) && callers.Classify(channel, fresh: op == Ops.SessionReply) is { Kind: CallerKind.Agent } agent)
        {
            const string why = "answering, replying, ending and launching sessions are for the person, from the app or a phone";
            if (op is LaunchProtocol.Launch or Ops.SessionStop && credentials?.Of(channel) is { } credential)
            {
                AuditRefusal(credentials, credential, op, data, audit, why, refusals);
            }
            throw Refuse(op, agent, why, log);
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
        var replyCaller = op == Ops.SessionReply && channel is LocalChannel && credentials?.Of(channel) is null
            ? callers.Classify(channel, fresh: true).Kind : CallerKind.Other;
        ReplyIntent ReplyIntentNow() => replyCaller is (CallerKind.App or CallerKind.HostCli)
            && !channel.Closed.IsCompleted && credentials?.Of(channel) is null
            && callers.Classify(channel, fresh: true).Kind == replyCaller ? ReplyIntent.Steer : ReplyIntent.Unknown;
        // Shaped inside the service, so hello.changed compares what this caller is told (#651).
        return await sessions.HandleAsync(channel, op, data, hello => callers.Classify(channel).Kind switch
        {
            CallerKind.App => hello with { Capabilities = [.. hello.Capabilities, HostOps.Capability,
                .. credentials is not null && OperatingSystem.IsWindows() ? new[] { AgentProtocol.AdoptCapability, HostOps.ScriptCredentialsCapability } : [],
                .. control.HasAgentReadiness ? new[] { HostOps.AgentReadinessCapability } : [],
                .. control.HasAgentSetup ? new[] { HostOps.AgentSetupCapability } : [],
                .. control.HasAgentInstall ? new[] { HostOps.AgentInstallCapability } : [],
                .. control.HasAgentVerification ? new[] { HostOps.AgentVerificationCapability } : [],
                .. control.HasAgentSignIn ? new[] { HostOps.AgentSignInCapability } : []] },
            CallerKind.Agent => hello with { Capabilities = [.. hello.Capabilities.Where(AgentCapabilities.Contains)] },
            _ => hello,
        }, actionAudit: op is (LaunchProtocol.Launch or Ops.SessionAnswer or Ops.SessionStop)
            && callers.Classify(channel).Kind is (CallerKind.App or CallerKind.HostCli) ? audit : null,
            replyIntent: op == Ops.SessionReply ? ReplyIntentNow : null);
    };

    internal sealed class RefusalAudits(TimeProvider time)
    {
        public TimeProvider Time { get; } = time;
        public Dictionary<string, (long At, int Written, int Suppressed, long Last)> Windows { get; } = new(StringComparer.Ordinal);
    }

    static void AuditRefusal(AgentCredentials credentials, AgentCredential caller, string op, JsonElement data, AgentAuditLog? audit, string why, RefusalAudits refusals)
        => AuditRefusal(credentials.PrincipalOf(caller), caller.Chain, op, data, audit, why, refusals);

    internal static void AuditRefusal(string principal, string[] chain, string op, JsonElement data, AgentAuditLog? audit, string why, RefusalAudits refusals)
    {
        lock (refusals.Windows)
        {
            var now = refusals.Time.GetTimestamp();
            // Fixed hours start at the first successful record; expired principals consume no memory.
            foreach (var stale in refusals.Windows.Where(w => refusals.Time.GetElapsedTime(w.Value.At, now) >= TimeSpan.FromHours(1)).Select(w => w.Key).ToArray())
            { refusals.Windows.Remove(stale); }
            var state = refusals.Windows.GetValueOrDefault(principal, (At: now, Written: 0, Suppressed: 0, Last: now));
            var summary = state.Written >= 60;
            if (summary)
            {
                if (state.Suppressed < int.MaxValue) { state.Suppressed++; }
                refusals.Windows[principal] = state;
                if (refusals.Time.GetElapsedTime(state.Last, now) < TimeSpan.FromMinutes(1)) { return; }
            }
            string? Field(string name)
            {
                if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) { return null; }
                try { return v.GetString(); }
                catch (InvalidOperationException) { return null; } // An unpaired surrogate is not a usable field.
            }
            // A refused request is unvalidated: never copy arbitrary text as an id into the audit.
            var request = Guid.TryParse(Field("request"), out var r) ? r.ToString("D") : Guid.NewGuid().ToString("D");
            var target = op == LaunchProtocol.Launch ? "local" : Guid.TryParse(Field("id"), out var id) ? id.ToString("D") : "unknown";
            var reason = summary ? $"{state.Suppressed} additional launch or stop attempts were refused" : why;
            var entry = new AgentAuditEntry("local", request, principal, chain, op, target, "refused", reason);
            bool written;
            try { written = audit?.Append(entry, !summary && op == LaunchProtocol.Launch ? Field("prompt") : null) == true; }
            catch (Exception) { throw AuditUnavailable(); }
            if (!written) { throw AuditUnavailable(); }
            refusals.Windows[principal] = (state.At, Math.Min(60, state.Written + 1), 0, now);
        }
    }

    static InvalidOperationException AuditUnavailable() =>
        new("refused: auditUnwritable: the agent audit log could not be written; nothing was launched or stopped");

    static string AnswerAuditTarget(JsonElement data)
    {
        try
        {
            return data.ValueKind == JsonValueKind.Object && data.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String && id.TryGetGuid(out var session) ? session.ToString("D") : "unknown";
        }
        catch (InvalidOperationException) { return "unknown"; }
    }

    internal static void AuditAgentAction(AgentCredentials credentials, AgentCredential caller, string op, string target, string outcome, AgentAuditLog? audit, GateAuditMetadata? gate = null)
    {
        var entry = new AgentAuditEntry("local", Guid.NewGuid().ToString("D"), credentials.PrincipalOf(caller), caller.Chain, op, target, outcome, Gate: gate);
        bool written;
        try { written = audit?.Append(entry) == true; }
        catch (Exception) { throw AgentActionAuditUnavailable(); }
        if (!written) { throw AgentActionAuditUnavailable(); }
    }

    static InvalidOperationException AgentActionAuditUnavailable() =>
        new("refused: auditUnwritable: the agent audit log could not be written; nothing was done");

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
