using AgentBridge.Protocol;

namespace AgentBridge.Host;

public sealed record AgentLaunchPolicyVerdict(bool Allowed, string? Reason, AgentLaunchLimits? Limits,
    int RetryAfterSeconds = 0, bool Duplicate = false);

// The pipe stays closed until the caller/content checks and launch-service wiring land.
public sealed class AgentLaunchGate(string folder, Func<DateTimeOffset>? clock = null)
{
    readonly AgentLimits limits = new(clock);

    public AgentLaunchPolicyVerdict CheckLaunch(string origin, string request, string requestSha256)
    {
        var policy = CheckPolicy();
        if (!policy.Allowed || policy.Limits is null) { return policy; }
        var result = limits.CheckLaunch(origin, request, requestSha256, policy.Limits);
        if (result.Reason == AgentLimitVerdict.RateLimited && !new AgentAuditLog(folder).Append(
            new("local", request, origin, [], LaunchProtocol.Launch, "local", "refused",
                $"rate-limited; retry after {result.RetryAfterSeconds}s; limits {policy.Limits.LaunchesPerOriginPerHour} per agent and {policy.Limits.LaunchesPerMachinePerHour} per machine per hour")))
        { return policy with { Allowed = false, Reason = "auditUnwritable: the launch refusal could not be recorded" }; }
        return policy with { Allowed = result.Allowed, Reason = result.Reason,
            RetryAfterSeconds = result.RetryAfterSeconds, Duplicate = result.Duplicate };
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
        return new(true, null, policy.Limits.Launch);
    }
}
