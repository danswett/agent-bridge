using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Fleet.Grants;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

public sealed record FleetStopInput(int Version, Guid Request, Guid Grant, string Session, string? Target = null);
public sealed record FleetStopReceipt(Guid Request, Guid Grant, long Generation, string Outcome, string? Reason,
    string? SessionId = null, GrantUsage? Usage = null, GrantCaps? Caps = null);

public sealed class FleetStopCommands(string folder, P256KeyPair key, Func<string?> account, string device,
    AgentCredentials credentials, SessionService sessions, ISessionSource source, StopService stops,
    Func<int, GrantProcessObservation>? inspect = null, Func<string, bool>? unpushed = null)
{
    public const string Op = "fleet.end";
    // What the stop audit records of the request instead of its content: a digest of its fields, no secret.
    static string Digest(FleetStopInput i) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes($"fleet.end/1|{i.Request}|{i.Grant}|{i.Session}|{i.Target}")));
    readonly GrantStore store = new(folder, key, new(folder), new([], [], [], [], [], [], [], []));
    readonly AgentAuditLog audit = new(folder);
    readonly LocalApi.RefusalAudits refusals = new(TimeProvider.System);
    readonly SemaphoreSlim serial = new(1);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };

    public Task<FleetStopReceipt> HandleAsync(AgentCredential caller, JsonElement data) =>
        HandleCoreAsync(new(credentials.PrincipalOf(caller), caller.Chain, "local", caller, null), data);
    // G10 (docs/design/fleet-grants.md, "Remote targets"): a stop from another host of this account, with the subject the grant names. Same
    // grant gate, counters, audit and stopper as a local stop; the credential-liveness check belongs to the source host, which attested it.
    // A remote stop always names a grant: ending on the host's own launch record alone is local-only.
    internal Task<FleetStopReceipt> HandleRemoteAsync(GrantSubject subject, JsonElement data) =>
        HandleCoreAsync(new(AgentProtocol.Principal(subject.SourceHost, subject.Session), [], "relay", null, subject), data);
    // A refusal after the envelope verified is the peer device's own refused use: one refused audit row through the same function and hourly
    // limiter as every other refused stop; if the row cannot be written, that is the refusal (as for the launch receiver).
    internal string AuditRemoteRefusal(string device, FleetStopInput input, string reason)
    {
        try { LocalApi.AuditRefusal(AgentProtocol.Principal(device, "unmatched"), [], Ops.SessionStop, JsonSerializer.SerializeToElement(input, ProtocolJson.Options), audit, reason, refusals); }
        catch (InvalidOperationException) { return "audit-unavailable"; }
        return reason;
    }
    sealed record StopCaller(string Principal, string[] Chain, string Path, AgentCredential? Credential, GrantSubject? Remote);
    GrantSubject RemoteSubject(GrantSubject s) => account() is { Length: > 0 } mine && s.Account == mine && s.SourceHost != device
        && s.SourceHost.Length == 32 && s.SourceHost.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
        && !string.IsNullOrWhiteSpace(s.Session) && !string.IsNullOrWhiteSpace(s.Credential) && s.ProcessInstance.Pid > 0
        ? s : throw new FleetLaunchDenied("policy");
    async Task<FleetStopReceipt> HandleCoreAsync(StopCaller caller, JsonElement data)
    {
        await serial.WaitAsync();
        FleetStopInput? input = null;
        GrantDocument? grant = null;
        StopTarget? target = null;
        WorkspaceConfig? workspace = null;
        string principal = caller.Principal;
        AgentAuditEntry? intent = null;
        FleetStopReceipt Receipt(string outcome, string? reason) =>
            new(input?.Request ?? Guid.Empty, input?.Grant ?? Guid.Empty, grant?.Generation ?? 0, outcome, reason, target?.Session);
        AgentAuditEntry Entry(string phase, string outcome, string reason) =>
            new(caller.Path, input!.Request.ToString(), principal, caller.Chain, Ops.SessionStop,
                target?.Session ?? input.Request.ToString(), outcome, Gate: new(1, Guid.NewGuid(), Guid.Empty, 0,
                    phase, caller.Path, "agent", target is null ? input.Grant == Guid.Empty ? Guid.Parse(input.Session) : input.Grant
                        : StopAudit.Identity(Guid.Parse(target.Session), target.ProcessInstance.Pid, target.ProcessInstance.StartedAt),
                    reason is "none" ? "none" : outcome == "unconfirmed" ? "unconfirmed" : "refused",
                    Generation: grant?.Generation, PayloadDigest: grant is null ? null : Digest(input), Caps: grant?.Caps))
            { Grant = input.Grant.ToString() };
        FleetStopReceipt Refused(string reason)
        {
            var entry = input is not null ? Entry("terminal", "refused", reason)
                : new AgentAuditEntry(caller.Path, "", principal, caller.Chain, Ops.SessionStop, "", "refused", reason);
            return Receipt("refused", audit.Append(entry) ? reason : "audit-unavailable");
        }
        try
        {
            if (data.ValueKind != JsonValueKind.Object || ProtocolJson.ByteCount(data) > 4096
                || data.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1))
            { return Refused("bad-argument"); }
            input = data.Deserialize<FleetStopInput>(Json);
            if (input is null || input.Version != 1 || input.Request == Guid.Empty
                || input.Grant == Guid.Empty && !FleetSessionId.IsCanonical(input.Session)
                || !(FleetSessionId.IsCanonical(input.Session) || input.Session is { Length: 16 }
                    && input.Session.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            { input = null; return Refused("bad-argument"); }
            // No grant: the session is ended on the host's own launch record alone (#2741), by its exact id.
            var byRecord = input.Grant == Guid.Empty;
            var subject = caller.Remote is { } remote ? RemoteSubject(remote) : FleetGrantLaunch.Subject(credentials, account() ?? "", device, caller.Credential!);
            if (caller.Remote is not null && byRecord) { return Refused("scope"); }
            string? Machine()
            {
                var (settings, problem) = HostSettings.Load(folder);
                if (problem is not null || settings.SectionProblems.Any() || caller.Credential is { } live && !credentials.IsLive(live)) { return "policy"; }
                return settings.ReadOnly || sessions.ReadOnly?.Invoke() is not null || !settings.Agents.Enabled || !settings.Launch.Enabled ? "kill-switch" : null;
            }
            var (rows, complete) = GateAuditProjection.Load(folder);
            if (!complete) { return Refused("audit-unavailable"); }
            // The request id is bound by its durable intent row, so a restart (an empty in-memory memory) never
            // dispatches it again, and a retry after the stop ended returns what was recorded instead of
            // meeting a session that is gone. A request refused before dispatch has no intent and may be sent
            // again; one bound to another grant, caller, session or payload refuses.
            var request = input.Request.ToString();
            var bound = rows.Where(r => r.Action == Ops.SessionStop && r.Request == request
                && r.Gate is { Phase: "intent" }).ToArray();
            if (bound.Length != 0)
            {
                var digest = Digest(input);
                if (bound.Any(r => r.Grant != input.Grant.ToString() || r.Principal != principal
                    || r.Target is not { Length: >= 16 } at || !(at == input.Session || at.Replace("-", "")[..16] == input.Session)
                    || r.Gate!.PayloadDigest is { } recorded && recorded != digest))
                { return Refused("duplicate-id"); }
                // A replay is still a use of the grant: its validity, the caller's standing and the kill-switch
                // are rechecked now (only the session-scope part of the full check cannot apply, the session
                // being gone), and the generation it was bound under must still be the grant's.
                string? denied = byRecord ? Machine() : null;
                var standing = byRecord ? new GrantStoreResult(GrantStoreProblem.None) : store.Transaction(state =>
                {
                    var found = store.Find(state, input.Grant, state.Grants.SingleOrDefault(g => g.Id == input.Grant)?.Generation ?? 1, checkTime: true);
                    if (!found.Usable) { denied = FleetGrantLaunch.StoreReason(found.Problem); }
                    else if (state.Counters is { } seen && seen.ObservedAt > DateTimeOffset.UtcNow) { denied = "grant-expired"; }
                    else if (!found.Grant!.Subjects.Contains(subject)) { denied = "scope"; }
                    else if (bound.Any(r => r.Gate!.Generation is { } was && was != found.Grant.Generation)) { denied = "duplicate-id"; }
                    else { denied = Machine(); }
                    return (null, new(GrantStoreProblem.None));
                }, allowNew: false);
                if (!standing.Usable || denied is not null) { return Refused(denied ?? FleetGrantLaunch.StoreReason(standing.Problem)); }
                // The outcome is the terminal row of that very operation (the intent's Gate.Operation) under the
                // same grant, caller and session; more than one distinct terminal cannot be trusted.
                var session = bound[0].Target!;
                var operations = bound.Select(r => r.Gate!.Operation).ToHashSet();
                var terminal = rows.Where(r => r.Action == Ops.SessionStop && r.Request == request
                        && r.Gate is { Phase: "terminal" } g && operations.Contains(g.Operation)
                        && r.Grant == input.Grant.ToString() && r.Principal == principal && r.Target == session)
                    .Select(r => (r.Outcome, r.Gate!.ReasonCode)).Distinct().ToArray();
                if (terminal is not [("ended", _)])
                { return new(input.Request, input.Grant, bound[0].Gate!.Generation ?? 0, "unconfirmed", "duplicate-id", session); }
                return audit.Append(new(caller.Path, request, principal, caller.Chain, "fleet.end.replay",
                    session, "ended", "none") { Grant = input.Grant.ToString() })
                    ? new(input.Request, input.Grant, bound[0].Gate!.Generation ?? 0, "ended", null, session)
                    : new(input.Request, input.Grant, bound[0].Gate!.Generation ?? 0, "unconfirmed", "audit-unavailable", session);
            }
            // The host's own view of where the session runs: an approved workspace and a launch agent, neither excluded.
            (string? Denied, string? Agent, WorkspaceConfig? Place) Placed(string session)
            {
                var (settings, _) = HostSettings.Load(folder);
                var observed = source.GetForStop(session);
                var agent = observed?.Agent.ToString().ToLowerInvariant();
                var approved = observed?.Cwd is { } cwd && Path.IsPathFullyQualified(cwd)
                    ? Folders.Exactly(settings.Launch.Workspaces, cwd) ?? Worktrees.Managed(settings.Launch.Workspaces, cwd)?.Workspace : null;
                if (approved is null || agent is null || !LaunchProtocol.Agents.Contains(agent) || workspace is not null && workspace != approved
                    || settings.Agents.ExcludedWorkspaces.Contains(approved.Label)) { return ("scope", null, null); }
                workspace = approved;
                if (settings.Agents.ExcludedSessions.Contains(subject.Session) || settings.Agents.ExcludedSessions.Contains(session)
                    || settings.Agents.ExcludedSessions.Contains(sessions.FleetNames?.Get(session) ?? ""))
                { return ("scope", null, null); }
                return (null, agent, approved);
            }
            // One git read per request: prepare and dispatch share it.
            bool? pending = null;
            // No grant: the launch record must show an agent started this exact process; no record, or one that cannot be read, refuses.
            string? ByRecord()
            {
                if (sessions.GrantTarget(input.Session) is not { } found || target is not null && target != found || source.Held is not null
                    || string.Equals(found.Session, subject.Session, StringComparison.OrdinalIgnoreCase) || input.Target is not null && input.Target != device) { return "scope"; }
                target = found;
                if (Machine() is { } machine) { return machine; }
                if (Placed(found.Session).Denied is { } placed) { return placed; }
                try { if (!sessions.IsFleetWorker(found.Session)) { return "scope"; } }
                catch (InvalidOperationException) { return "scope"; }
                // Uncommitted or unpushed work in its folder is never ended over; an unreadable folder counts as pending.
                return source.GetForStop(found.Session)?.Cwd is { } cwd && !(pending ??= (unpushed ?? UnpushedWork.Pending)(cwd)) ? null : "policy";
            }
            string? Check(GrantStore.State state)
            {
                var found = store.Find(state, input.Grant, grant?.Generation
                    ?? state.Grants.SingleOrDefault(g => g.Id == input.Grant)?.Generation ?? 1, checkTime: true);
                if (!found.Usable) { return FleetGrantLaunch.StoreReason(found.Problem); }
                if (state.Counters is { } counters && counters.ObservedAt > DateTimeOffset.UtcNow) { return "grant-expired"; }
                grant = found.Grant!;
                var own = (state.Counters?.Reservations ?? []).Where(r => r.Grant == grant.Id && r.Subject == subject
                    && r.State == GrantReservationState.Committed && r.Target is not null).Select(r => new GrantLaunch(grant.Id, subject, r.Target!)).ToArray();
                var candidates = own.Select(l => l.Target).Concat(grant.StopTargets.IsDefault ? [] : grant.StopTargets).Distinct()
                    .Where(t => t.Session == input.Session || t.Session.Replace("-", "")[..16] == input.Session).ToArray();
                if (candidates.Length != 1 || target is not null && target != candidates[0]) { return "scope"; }
                target = candidates[0];
                if (target != sessions.GrantTarget(target.Session) || source.Held is not null || input.Target is not null && input.Target != device)
                { return "scope"; }
                if (Machine() is { } machine) { return machine; }
                var (denied, agent, approved) = Placed(target.Session);
                if (denied is not null) { return denied; }
                // Stop checks machine/workspace/agent; launch-only selections are inert.
                var scope = new GrantScope([device], [approved!.Label], [agent!],
                    ["default"], ["default"], ["medium"], ["default"], ["ask"]);
                var policy = new HostPolicySnapshot(account() ?? "", new(device, Path.GetFileName(folder)), subject,
                    scope, grant.Caps, false, true, Launches: [.. own], StopTargets: [target]);
                if (!GrantEvaluator.Evaluate(grant, policy, new(subject, "stop", device, approved!.Label,
                    agent!, Target: target), DateTimeOffset.UtcNow).Allowed) { return "scope"; }
                if (state.Counters?.Reservations.Any(r => r.Request == input.Request) == true) { return "duplicate-id"; }
                return null;
            }
            string? denial = null;
            var prepared = byRecord ? new GrantStoreResult(GrantStoreProblem.None) : store.Transaction(state => { denial = Check(state); return (null, new(GrantStoreProblem.None)); }, allowNew: false);
            if (byRecord) { denial = ByRecord(); }
            if (!prepared.Usable || denial is not null) { return Refused(denial ?? FleetGrantLaunch.StoreReason(prepared.Problem)); }
            bool Dispatch(int pid, DateTimeOffset at)
            {
                (GrantStore.State? Next, GrantStoreResult Result) Bind(GrantStore.State? state)
                {
                    denial = byRecord ? ByRecord() : Check(state!);
                    if (denial is not null || target!.ProcessInstance != new ProcessInstance(pid, at)) { denial ??= "scope"; return (null, new(GrantStoreProblem.None)); }
                    if (intent is null)
                    {
                        intent = Entry("intent", "attempted", "none");
                        if (!audit.Append(intent)) { intent = null; denial = "audit-unavailable"; }
                    }
                    return (null, new(GrantStoreProblem.None));
                }
                var authorized = byRecord ? Bind(null).Item2 : store.Transaction(Bind, allowNew: false);
                if (!authorized.Usable) { denial = FleetGrantLaunch.StoreReason(authorized.Problem); }
                return authorized.Usable && denial is null;
            }
            var replayed = false;
            var result = await stops.StopAuditedAsync(new() { Id = target!.Session, Request = input.Request.ToString() },
                Dispatch, principal: principal, onReplay: () => replayed = true, personConfirmation: false);
            var reason = denial ?? (result.Outcome == StopOutcome.Confirm ? "person-required"
                : result.Outcome == StopOutcome.Ended ? "none" : result.Reason == "still-running" ? "still-running" : "policy");
            var outcome = result.Outcome == StopOutcome.Ended ? "ended" : result.Outcome == StopOutcome.Failed ? "unconfirmed" : "refused";
            if (replayed)
            {
                return audit.Append(new(caller.Path, input.Request.ToString(), principal, caller.Chain, "fleet.end.replay",
                    target.Session, outcome, reason) { Grant = input.Grant.ToString() })
                    ? Receipt(outcome, reason == "none" ? null : reason) : Receipt("unconfirmed", "audit-unavailable");
            }
            if (intent is null) { return Refused(reason); }
            if (!audit.Append(intent with { Outcome = outcome, Gate = intent.Gate! with
                { Phase = "terminal", ReasonCode = reason == "none" ? "none" : outcome == "unconfirmed" ? "unconfirmed" : "refused" } }))
            { return Receipt("unconfirmed", "audit-unavailable"); }
            if (outcome == "refused") { return Receipt(outcome, reason); }
            if (byRecord)
            {
                // Frees the launch's live slot, as the grant path's reconcile does, or repeated stops would exhaust the cap.
                if (outcome == "ended") { credentials.LaunchGate.SessionEnded(target!.Session); }
                return Receipt(outcome, reason == "none" ? null : reason);
            }
            var counters = new GrantCounters(store);
            if (outcome == "ended" && !counters.Reconcile(input.Grant, _ => new(null),
                inspect ?? FleetGrantLaunch.Observe, session => credentials.LaunchGate.SessionEnded(session)).Accepted)
            { return Receipt("unconfirmed", "audit-unavailable"); }
            var usage = counters.Read(input.Grant, grant!.Generation);
            if (!usage.Accepted) { return Receipt("unconfirmed", "audit-unavailable"); }
            return Receipt(outcome, reason == "none" ? null : reason) with { Usage = usage.Usage, Caps = grant.Caps };
        }
        catch (Exception e) when (e is JsonException or FleetLaunchDenied or IOException or UnauthorizedAccessException)
        { return intent is null ? Refused(e is FleetLaunchDenied ? e.Message : e is JsonException ? "bad-argument" : "audit-unavailable")
            : Receipt("unconfirmed", "audit-unavailable"); }
        finally { serial.Release(); }
    }
}
