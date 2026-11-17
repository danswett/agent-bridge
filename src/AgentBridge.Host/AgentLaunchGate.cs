using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

public sealed record AgentLaunchPolicyVerdict(bool Allowed, string? Reason, AgentLaunchLimits? Limits,
    int RetryAfterSeconds = 0, bool Duplicate = false, AgentLaunchGrant? Grant = null);

// Shared policy and reservations for credentialed local launches.
public sealed class AgentLaunchGate(string folder, Func<DateTimeOffset>? clock = null, TimeProvider? auditTime = null)
{
    readonly AgentLaunchLedger limits = new(folder, clock);
    readonly AgentAuditLog audit = new(folder);
    readonly LocalApi.RefusalAudits refusals = new(auditTime ?? TimeProvider.System);

    public AgentLaunchPolicyVerdict CheckLaunch(string origin, string request, string requestSha256)
    {
        var policy = CheckPolicy();
        if (!policy.Allowed || policy.Limits is null) { return policy; }
        AgentLimitVerdict result;
        try { result = limits.Use(l => l.ReserveLaunch(origin, request, requestSha256, policy.Limits)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { return policy with { Allowed = false, Reason = "capRebuilding: needs-person; the durable launch admission ledger is unavailable" }; }
        var reason = result.Reason == AgentLimitVerdict.LiveLimited
            ? $"live-limit: this machine has reached its limit of {policy.Limits.LiveAgentLaunched} live agent-started sessions; wait for one to end"
            : result.Reason;
        if (result.Reason is AgentLimitVerdict.RateLimited or AgentLimitVerdict.LiveLimited)
        {
            try
            {
                LocalApi.AuditRefusal(origin, [], LaunchProtocol.Launch, JsonSerializer.SerializeToElement(new { request }), audit,
                    result.Reason == AgentLimitVerdict.LiveLimited ? reason!
                    : $"rate-limited; retry after {result.RetryAfterSeconds}s; hourly caps: launches {policy.Limits.LaunchesPerOriginPerHour} per agent and {policy.Limits.LaunchesPerMachinePerHour} per machine; attempts {policy.Limits.LaunchAttemptsPerOriginPerHour} per agent", refusals);
            }
            catch (InvalidOperationException)
            { return policy with { Allowed = false, Reason = "auditUnwritable: the launch refusal could not be recorded" }; }
        }
        return policy with { Allowed = result.Allowed, Reason = reason,
            RetryAfterSeconds = result.RetryAfterSeconds, Duplicate = result.Duplicate };
    }

    // Host lifecycle API for the ingress/idle-loop composition; never exposed as agent operations.
    public void LaunchStarted(string origin, string request, string session) =>
        limits.Use(l => { l.LaunchStarted(origin, request, session); return true; });
    public bool LaunchFinished(string origin, string request) => limits.Use(l => l.LaunchFinished(origin, request));
    internal bool LaunchRefused(string origin, string request) => limits.Use(l => l.LaunchRefused(origin, request));
    public bool SessionEnded(string session) => limits.Use(l => l.SessionEnded(session));
    public bool ObserveSession(string session, DateTimeOffset? idleSince, bool personTyped) =>
        limits.Use(l => l.ObserveSession(session, idleSince, personTyped));

    public IReadOnlyList<string> IdleSessions()
    {
        var policy = CheckPolicy();
        if (!policy.Allowed || policy.Limits is null)
        { throw new InvalidOperationException($"Idle-end policy is unavailable: {policy.Reason}"); }
        return limits.Use(l => l.IdleSessions(policy.Limits));
    }

    internal object ProjectOptions(LaunchOptions offered)
    {
        var (policy, problem) = HostSettings.CurrentAgents(folder);
        if (problem is not null) { throw new InvalidOperationException("refused: agent launch options policy is unreadable"); }
        var grant = policy.Enabled ? policy.InboundLocal.Launch : null;
        var agents = offered.Agents.Where(a => grant?.Agents.Contains(a.Id, StringComparer.Ordinal) == true)
                .Select(a => a with { Profiles = null, Attachments = null,
                    Defaults = a.Defaults is { } defaults ? defaults with { Profile = null } : null }).ToArray();
        var workspaces = offered.Workspaces.Where(w => w.Isolate == true && grant is not null
                && (grant.Workspaces.Contains("approved", StringComparer.Ordinal) || grant.Workspaces.Contains(w.Label, StringComparer.Ordinal)))
                .Select(w => new { w.Label }).ToArray();
        return new
        {
            agents, workspaces,
            defaults = new LaunchDefaults
            {
                Agent = agents.FirstOrDefault(a => a.Id == offered.Defaults.Agent)?.Id ?? agents.FirstOrDefault()?.Id,
                Workspace = workspaces.FirstOrDefault(w => w.Label == offered.Defaults.Workspace)?.Label ?? workspaces.FirstOrDefault()?.Label,
                Permissions = LaunchProtocol.Ask,
            },
            busy = offered.Busy is null ? null : "a launch on this machine is waiting for a person at the machine; try again in a few minutes",
        };
    }

    public AgentLaunchPolicyVerdict CheckPolicy()
    {
        var (policy, problem) = HostSettings.CurrentAgents(folder);
        if (problem is not null) { return new(false, problem, null); }
        if (!policy.LaunchEnabled) { return new(false, "agents.launch.enabled is off or missing on this machine", null); }
        if (!policy.Enabled) { return new(false, "agents.enabled is off on this machine", null); }
        return new(true, null, policy.Limits.Launch, Grant: policy.InboundLocal.Launch);
    }
}
