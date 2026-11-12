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
        return policy with { Allowed = result.Allowed, Reason = result.Reason,
            RetryAfterSeconds = result.RetryAfterSeconds, Duplicate = result.Duplicate };
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
