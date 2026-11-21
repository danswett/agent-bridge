using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Fleet.Grants;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

public sealed record FleetStopInput(int Version, Guid Request, Guid Grant, string Session, string? Target = null);
public sealed record FleetStopReceipt(Guid Request, Guid Grant, long Generation, string Outcome, string? Reason,
    string? SessionId = null, GrantUsage? Usage = null, GrantCaps? Caps = null);

public sealed class FleetStopCommands(string folder, P256KeyPair key, Func<string?> account, string device,
    AgentCredentials credentials, SessionService sessions, ISessionSource source, StopService stops,
    Func<int, GrantProcessObservation>? inspect = null)
{
    public const string Op = "fleet.end";
    readonly GrantStore store = new(folder, key, new(folder), new([], [], [], [], [], [], [], []));
    readonly AgentAuditLog audit = new(folder);
    readonly SemaphoreSlim serial = new(1);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };

    public async Task<FleetStopReceipt> HandleAsync(AgentCredential caller, JsonElement data)
    {
        await serial.WaitAsync();
        FleetStopInput? input = null;
        GrantDocument? grant = null;
        StopTarget? target = null;
        string principal = credentials.PrincipalOf(caller);
        AgentAuditEntry? intent = null;
        FleetStopReceipt Receipt(string outcome, string? reason) =>
            new(input?.Request ?? Guid.Empty, input?.Grant ?? Guid.Empty, grant?.Generation ?? 0, outcome, reason, target?.Session);
        AgentAuditEntry Entry(string phase, string outcome, string reason) =>
            new("local", input!.Request.ToString(), principal, caller.Chain, Ops.SessionStop,
                target?.Session ?? input.Request.ToString(), outcome, Gate: new(1, Guid.NewGuid(), Guid.Empty, 0,
                    phase, "local", "agent", target is null ? input.Grant
                        : StopAudit.Identity(Guid.Parse(target.Session), target.ProcessInstance.Pid, target.ProcessInstance.StartedAt),
                    reason is "none" ? "none" : outcome == "unconfirmed" ? "unconfirmed" : "refused")) { Grant = input.Grant.ToString() };
        FleetStopReceipt Refused(string reason)
        {
            var entry = input is not null ? Entry("terminal", "refused", reason)
                : new AgentAuditEntry("local", "", principal, caller.Chain, Ops.SessionStop, "", "refused", reason);
            return Receipt("refused", audit.Append(entry) ? reason : "audit-unavailable");
        }
        try
        {
            if (data.ValueKind != JsonValueKind.Object || ProtocolJson.ByteCount(data) > 4096
                || data.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1))
            { return Refused("bad-argument"); }
            input = data.Deserialize<FleetStopInput>(Json);
            if (input is null || input.Version != 1 || input.Request == Guid.Empty || input.Grant == Guid.Empty
                || !(FleetSessionId.IsCanonical(input.Session) || input.Session is { Length: 16 }
                    && input.Session.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            { input = null; return Refused("bad-argument"); }
            var subject = FleetGrantLaunch.Subject(credentials, account() ?? "", device, caller);
            var (_, complete) = GateAuditProjection.Load(folder);
            if (!complete) { return Refused("audit-unavailable"); }
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
                var (settings, problem) = HostSettings.Load(folder);
                if (problem is not null || settings.SectionProblems.Any() || !credentials.IsLive(caller)) { return "policy"; }
                if (settings.ReadOnly || sessions.ReadOnly?.Invoke() is not null || !settings.Agents.Enabled || !settings.Launch.Enabled) { return "kill-switch"; }
                if (settings.Agents.ExcludedSessions.Contains(caller.Session) || settings.Agents.ExcludedSessions.Contains(target.Session)
                    || settings.Agents.ExcludedSessions.Contains(sessions.FleetNames?.Get(target.Session) ?? "")
                    || grant.Scope.Workspaces.Any(settings.Agents.ExcludedWorkspaces.Contains)
                    || source.Get(target.Session)?.Cwd is { } cwd && settings.Agents.ExcludedWorkspaces.Contains(cwd))
                { return "scope"; }
                var policy = new HostPolicySnapshot(account() ?? "", new(device, Path.GetFileName(folder)), subject,
                    grant.Scope, grant.Caps, false, true, Launches: [.. own], StopTargets: [target]);
                if (!GrantEvaluator.Evaluate(grant, policy, new(subject, "stop", device, grant.Scope.Workspaces[0],
                    grant.Scope.Agents[0], Target: target), DateTimeOffset.UtcNow).Allowed) { return "scope"; }
                if (state.Counters?.Reservations.Any(r => r.Request == input.Request) == true) { return "duplicate-id"; }
                return null;
            }
            string? denial = null;
            var prepared = store.Transaction(state => { denial = Check(state); return (null, new(GrantStoreProblem.None)); }, allowNew: false);
            if (!prepared.Usable || denial is not null) { return Refused(denial ?? FleetGrantLaunch.StoreReason(prepared.Problem)); }
            bool Dispatch(int pid, DateTimeOffset at)
            {
                var authorized = store.Transaction(state =>
                {
                    denial = Check(state);
                    if (denial is not null || target!.ProcessInstance != new ProcessInstance(pid, at)) { denial ??= "scope"; return (null, new(GrantStoreProblem.None)); }
                    intent = Entry("intent", "attempted", "none");
                    if (!audit.Append(intent)) { intent = null; denial = "audit-unavailable"; }
                    return (null, new(GrantStoreProblem.None));
                }, allowNew: false);
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
                return audit.Append(new("local", input.Request.ToString(), principal, caller.Chain, "fleet.end.replay",
                    target.Session, outcome, reason) { Grant = input.Grant.ToString() })
                    ? Receipt(outcome, reason == "none" ? null : reason) : Receipt("unconfirmed", "audit-unavailable");
            }
            if (intent is null) { return Refused(reason); }
            if (!audit.Append(intent with { Outcome = outcome, Gate = intent.Gate! with
                { Phase = "terminal", ReasonCode = reason == "none" ? "none" : outcome == "unconfirmed" ? "unconfirmed" : "refused" } }))
            { return Receipt("unconfirmed", "audit-unavailable"); }
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
