using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host.Core.Launch;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

// #3237 S3: session.launch on the pipe from a live read+reply+launch agent credential, with the
// caller's request id on the record HA's launch card uses (#2743).
// The record keeps no principal, so an id is kept under a hash of the principal and the id:
// another caller's id never matches, and reading it finds nothing.
public sealed class AgentPipeLaunch(string folder, AgentCredentials credentials, SessionService sessions,
    AgentAuditLog audit, LaunchRequestStore? store, string? storeProblem, TimeProvider? time = null, FleetGrantLaunch? fleetGrants = null)
{
    readonly AgentLaunchCallerCheck callers = new(audit, time);
    readonly LocalApi.RefusalAudits refusals = new(time ?? TimeProvider.System);
    readonly Dictionary<Guid, (string Principal, string Request, string Key, FleetLaunchContext? Fleet)> unconfirmed = [];

    const string Prefix = "pipe-";
    internal LaunchRequestStore? Requests => store;

    static string Key(string principal, string request) =>
        Prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(principal + "\n" + request)))[..59];

    internal static Guid LaunchId(string principal, string request) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes("launcher\n" + Key(principal, request))).AsSpan(0, 16));

    // Again at dispatch, after the launch service's own waits: the credential, grant and policy as they are now.
    string? Still(AgentCredential caller, LaunchRequest r)
    {
        if (!credentials.IsLive(caller)) { return "the credential is no longer live"; }
        var (policy, problem) = HostSettings.CurrentAgents(folder);
        var grant = problem is null && policy.Enabled ? policy.InboundLocal.Launch : null;
        if (grant is null || !grant.Agents.Contains(r.Agent ?? "", StringComparer.Ordinal) || !grant.Workspaces.Contains("approved", StringComparer.Ordinal)
            && !grant.Workspaces.Contains(r.Workspace ?? "", StringComparer.Ordinal))
        { return "this machine no longer lets agents launch this agent in this workspace"; }
        return credentials.LaunchGate.CheckPolicy() is { Allowed: false } off ? off.Reason ?? "agent launch is off on this machine" : null;
    }

    public Task<LaunchResult> LaunchAsync(AgentCredential caller, JsonElement data) => LaunchAsync(caller, data, null);
    public async Task<FleetLaunchReceipt> LaunchGrantedAsync(AgentCredential caller, JsonElement data)
    {
        FleetLaunchInput? input = null;
        FleetLaunchContext? context = null;
        try
        {
            input = FleetGrantLaunch.Parse(data);
            if (fleetGrants is null) { throw new FleetLaunchDenied("unavailable"); }
            context = await fleetGrants.PrepareAsync(caller, input);
            if (context.Replay is { } replay) { return replay; }
            var result = await LaunchAsync(caller, JsonSerializer.SerializeToElement(context.Request, ProtocolJson.Options), context);
            return fleetGrants.Finish(context, result);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            if (context?.Starting == true) { return fleetGrants!.Finish(context, new(LaunchOutcome.Unconfirmed)); }
            var reason = e is FleetLaunchDenied ? e.Message : e is JsonException ? "bad-argument" : "policy";
            if (!audit.Append(new("local", input?.Request.ToString() ?? "", credentials.PrincipalOf(caller), caller.Chain,
                "session.launch", input?.Request.ToString() ?? "", "refused", reason) { Grant = input?.Grant.ToString() }))
            { reason = "audit-unavailable"; }
            return context?.Admitted == true ? fleetGrants!.Finish(context, new(LaunchOutcome.Refused, reason))
                : FleetGrantLaunch.Receipt(context, new(LaunchOutcome.Refused), input, reason);
        }
    }
    async Task<LaunchResult> LaunchAsync(AgentCredential caller, JsonElement data, FleetLaunchContext? fleet)
    {
        var principal = credentials.PrincipalOf(caller);
        if (store is null) { throw Refused(caller, data, $"the launch request record can't be used ({storeProblem ?? "not open"}); no request with an id is launched"); }
        LaunchRequest? request = null;
        try { request = data.ValueKind == JsonValueKind.Object ? data.Deserialize<LaunchRequest>(ProtocolJson.Options) : null; }
        catch (JsonException) { }
        if (request is null || !Guid.TryParseExact(request.Request, "D", out var asked))
        { throw Refused(caller, data, "a launch needs its own request id, a UUID"); }
        request = request with { Request = asked.ToString("D") };
        var (settings, settingsProblem) = HostSettings.Load(folder);
        var (policy, policyProblem) = HostSettings.CurrentAgents(folder);
        if (settingsProblem is not null || policyProblem is not null) { throw Refused(caller, data, "the launch settings cannot be read safely"); }
        if (AgentLaunchRequestCheck.Check(data, request.Agent is { } named ? settings.Launch.Agent(named).ExtraArgs : [], fleet is not null) is { } invalid)
        { throw Refused(caller, data, invalid); }
        var grant = policy.Enabled ? policy.InboundLocal.Launch : null;
        var approved = settings.Launch.Workspaces.Where(w => w.Isolate && grant is not null
            && (grant.Workspaces.Contains("approved", StringComparer.Ordinal) || grant.Workspaces.Contains(w.Label, StringComparer.Ordinal)))
            .Select(w => w.Label).ToArray();
        if (callers.Check(caller, principal, request.Request, request.Workspace, approved) is { Allowed: false } denied)
        { throw new InvalidOperationException($"refused: {denied.Reason}"); }
        if (grant is null || request.Agent is null || !grant.Agents.Contains(request.Agent, StringComparer.Ordinal))
        { throw Refused(caller, data, "the launch must name an agent this machine lets agents start (agents.inbound.local.launch.agents)"); }

        var key = Key(principal, request.Request);
        var gate = credentials.LaunchGate;
        if (gate.CheckPolicy() is { Allowed: false } off) { throw Refused(caller, data, off.Reason ?? "agent launch is off on this machine"); }
        var binding = fleet?.Binding ?? LaunchService.RequestBinding(request);
        EnqueueResult held;
        try { held = store.Enqueue(key, binding); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw Refused(caller, data, "the launch request record could not be written; nothing was launched"); }
        if (held.Status == EnqueueStatus.Existing) { return Told(held.Record!); }
        if (held.Status != EnqueueStatus.Added)
        {
            throw Refused(caller, data, held.Status switch
            {
                EnqueueStatus.Conflict => "requestReused: this request id was used already for a different launch; nothing was launched",
                EnqueueStatus.Used => "requestUsed: this request id was used already and its outcome is no longer kept; it is not launched again",
                _ => "busy: too many launch requests are kept on this machine; try again in a moment",
            });
        }
        var verdict = gate.CheckLaunch(principal, request.Request, binding);
        if (!verdict.Allowed)
        {
            Save(() => store.Refuse(key, "refused"));
            var why = verdict.Reason + (verdict.RetryAfterSeconds > 0 ? $"; retry after {verdict.RetryAfterSeconds}s" : "");
            // The gate audits its own rate and live-limit refusals.
            if (verdict.Reason is AgentLimitVerdict.RateLimited || verdict.Reason?.StartsWith(AgentLimitVerdict.LiveLimited, StringComparison.Ordinal) == true)
            { throw new InvalidOperationException($"refused: {why}"); }
            throw Refused(caller, data, why ?? "agent launch is refused");
        }
        // The launcher hears an id of this caller's own, so no other launch (HA's or another caller's)
        // shares its events or audit rows. Pending first: a registration may come before the launch returns.
        var id = LaunchId(principal, request.Request);
        if (fleet is not null)
        {
            try { fleetGrants!.Admit(fleet); }
            catch (FleetLaunchDenied) { gate.LaunchFinished(principal, request.Request); Save(() => store.Refuse(key, "refused")); throw; }
        }
        sessions.PublishLaunchAlias(principal, request.Request, request with { Request = id.ToString("D") }, new(LaunchOutcome.Unconfirmed));
        Pending(id, principal, request.Request, key, fleet);
        // Launching on the record first: a restart from here reads it as unconfirmed, never as one to start.
        try { store.Begin(key); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (Take(id) is not null) { gate.LaunchFinished(principal, request.Request); }
            throw Refused(caller, data, "the launch request record could not be written; nothing was launched");
        }
        // An exception from here leaves the record launching and the slot held: something may have started.
        if (fleet is not null) { fleet.Starting = true; }
        var result = await sessions.LaunchAgentAsync(request with { Request = id.ToString("D") }, principal, audit,
            () => fleet is null ? Still(caller, request) : fleetGrants!.Check(fleet, caller), fleet?.Input.Grant.ToString(),
            confirmed: fleet is null ? null : session => Registered(id, session, confirmed: true));
        sessions.PublishLaunchAlias(principal, request.Request, request with { Request = id.ToString("D") }, result);
        if (result is { Outcome: LaunchOutcome.Started, Session: { } session })
        {
            // A registration is told before its outcome, so one still pending here is the audit's replay of
            // an earlier start (perhaps long ended): nothing new is live, and its slot is let go.
            if (Take(id) is not null) { gate.LaunchFinished(principal, request.Request); }
            Save(() => store.Launched(key, session));
        }
        else if (result.Outcome is LaunchOutcome.Busy or LaunchOutcome.Refused)
        {
            if (Take(id) is not null) { gate.LaunchFinished(principal, request.Request); }
            Save(() => store.Refuse(key, result.Outcome == LaunchOutcome.Busy ? "busy" : "refused"));
            try { LocalApi.AuditRefusal(principal, caller.Chain, LaunchProtocol.Launch, data, audit, result.Reason ?? "the launch service refused", refusals); }
            catch (InvalidOperationException) { }
        }
        else if (result.Outcome == LaunchOutcome.Failed) { Failed(id); }
        // Unconfirmed stays launching and holds its live slot: the launcher's registration or failure settles
        // both, and one it stops following without either keeps them until the host restarts (closed, not open).
        return result;
    }

    // The launcher's word on a launch (ISessionLauncher's events), also after it answered unconfirmed.
    public void Registered(Guid id, string session) => Registered(id, session, confirmed: false);

    void Registered(Guid id, string session, bool confirmed)
    {
        if (Take(id, unverifiedRegistration: !confirmed) is not { } p) { return; }
        try { credentials.LaunchGate.LaunchStarted(p.Principal, p.Request, session); } catch (InvalidOperationException) { }
        Save(() => store!.Launched(p.Key, session));
        if (p.Fleet is { } fleet) { fleetGrants!.Finish(fleet, new(LaunchOutcome.Started) { Session = session }); }
    }

    public void Failed(Guid id)
    {
        if (Take(id) is not { } p) { return; }
        credentials.LaunchGate.LaunchFinished(p.Principal, p.Request);
        Save(() => store!.Fail(p.Key, "failed"));
        if (p.Fleet is { } fleet) { fleetGrants!.Finish(fleet, new(LaunchOutcome.Failed, "policy")); }
    }

    // A record write that fails after the launch decided keeps the record as it was; the gate is settled regardless.
    static void Save(Func<bool> move)
    {
        try { move(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    void Pending(Guid id, string principal, string request, string key, FleetLaunchContext? fleet) { lock (unconfirmed) { unconfirmed[id] = (principal, request, key, fleet); } }

    (string Principal, string Request, string Key, FleetLaunchContext? Fleet)? Take(Guid id, bool unverifiedRegistration = false)
    {
        lock (unconfirmed)
        {
            if (!unconfirmed.TryGetValue(id, out var p) || unverifiedRegistration && p.Fleet is not null) { return null; }
            unconfirmed.Remove(id);
            return p;
        }
    }

    static LaunchResult Told(LaunchRequestRecord r) => r.State switch
    {
        LaunchRequestState.Launched => new(LaunchOutcome.Started) { Session = r.Session },
        LaunchRequestState.Refused => new(r.Code == "busy" ? LaunchOutcome.Busy : LaunchOutcome.Refused, "this request was refused already; it is not launched again"),
        LaunchRequestState.Failed when r.Code == "failed" => new(LaunchOutcome.Failed, "this request failed already; it is not launched again"),
        _ => new(LaunchOutcome.Unconfirmed, "whether this request started is not known; it is not launched again, so check the machine"),
    };

    InvalidOperationException Refused(AgentCredential caller, JsonElement data, string why)
    {
        LocalApi.AuditRefusal(credentials.PrincipalOf(caller), caller.Chain, LaunchProtocol.Launch, data, audit, why, refusals);
        return new InvalidOperationException($"refused: {why}");
    }
}
