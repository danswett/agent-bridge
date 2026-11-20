using System.Text.Json;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

public static partial class LocalApi
{
    static async Task<LaunchResult> LaunchAgentAsync(SessionService sessions, AgentCredentials credentials,
        AgentCredential caller, IProtocolChannel channel, JsonElement data, AgentAuditLog? audit,
        RefusalAudits refusals, Action<string>? log)
    {
        var principal = credentials.PrincipalOf(caller);
        bool callerAudited = false;
        LaunchResult Denied(string reason, bool recorded = false)
        {
            if (!recorded && !callerAudited) { AuditRefusal(credentials, caller, LaunchProtocol.Launch, data, audit, reason, refusals); }
            log?.Invoke($"local pipe: refused agent launch: {reason}");
            return new(LaunchOutcome.Refused, reason);
        }
        if (audit is null || !audit.Bootstrap())
        {
            const string reason = "auditUnwritable: the launch audit is unavailable; nothing was launched";
            log?.Invoke($"local pipe: refused agent launch: {reason}");
            return new(LaunchOutcome.Refused, reason);
        }
        if (data.ValueKind != JsonValueKind.Object || System.Text.Encoding.UTF8.GetByteCount(data.GetRawText()) > LaunchProtocol.MaxRequest)
        { return Denied("the agent launch request must be an object within its size limit"); }
        if (AgentLaunchRequestCheck.Check(data, []) is { } invalid) { return Denied(invalid); }
        var request = data.Deserialize<LaunchRequest>(ProtocolJson.Options)!;
        if (!Guid.TryParseExact(request.Request, "D", out _)) { return Denied("request must be a UUID"); }
        if (request.Agent is null) { return Denied("an agent launch requires an explicit agent"); }
        if (sessions.AgentLauncher is not { } launcher) { return Denied("the agent launch service is unavailable"); }
        var options = await launcher.OptionsAsync();
        var gate = credentials.LaunchGate;
        var callerCheck = new AgentLaunchCallerCheck(audit, refusals);
        string? lastRefusal = null;
        string? Check()
        {
            if (!credentials.IsLive(caller)) { return "the credential is no longer live"; }
            if (sessions.ReadOnly?.Invoke() is { } readOnly) { return readOnly; }
            var policy = gate.CheckPolicy();
            if (!policy.Allowed) { return policy.Reason; }
            var dispatch = launcher.CaptureDispatch(request.Agent, principal);
            if (dispatch?.AuthenticatedPrincipal != principal) { return "authenticated launch dispatch facts are unavailable"; }
            if (AgentLaunchRequestCheck.Check(data, dispatch.ExtraArguments) is { } problem) { return problem; }
            if (policy.Grant is not { } grant || !grant.Agents.Contains(request.Agent, StringComparer.Ordinal))
            { return "the agent is not approved for local agent launches"; }
            var approved = options.Workspaces.Where(w => w.Isolate == true
                && (grant.Workspaces.Contains("approved", StringComparer.Ordinal) || grant.Workspaces.Contains(w.Label, StringComparer.Ordinal)))
                .Select(w => w.Label).ToArray();
            var verdict = callerCheck.Check(caller, principal, request.Request, request.Workspace, approved);
            callerAudited = !verdict.Allowed;
            return verdict.Reason;
        }
        if (Check() is { } denied) { return Denied(denied); }
        var admission = gate.CheckLaunch(principal, request.Request, LaunchService.RequestBinding(request));
        if (!admission.Allowed)
        {
            return Denied(admission.Duplicate ? "duplicate: the launch was already admitted; no second session was started"
                : admission.Reason ?? "agent launch admission was refused",
                recorded: admission.Reason is "rate-limited" || admission.Reason?.StartsWith("live-limit:", StringComparison.Ordinal) == true
                    || admission.Reason?.StartsWith("auditUnwritable:", StringComparison.Ordinal) == true);
        }
        var result = await GateLaunchAudit.LaunchAsync(launcher, request, principal, audit, new("local", "agent", principal, null),
            refusal: () => lastRefusal = Check());
        if (result.Outcome is LaunchOutcome.Refused or LaunchOutcome.Busy) { gate.LaunchFinished(principal, request.Request); }
        if (result.Outcome == LaunchOutcome.Started && result.Session is { } session) { gate.LaunchStarted(principal, request.Request, session); }
        if (result.Outcome == LaunchOutcome.Refused)
        { return Denied(lastRefusal ?? "the launch could not be completed; check details at the machine"); }
        return result with
        {
            Trust = null,
            Reason = result.Reason is null ? null : "check the launch at the machine",
            Launched = result.Launched is { } launched ? launched with { Cwd = null, Resume = null, Transfer = null, Profile = null } : null,
        };
    }
}
