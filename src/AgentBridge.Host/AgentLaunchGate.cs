namespace AgentBridge.Host;

public sealed record AgentLaunchPolicyVerdict(bool Allowed, string? Reason, AgentLaunchLimits? Limits,
    int RetryAfterSeconds = 0, bool Duplicate = false);

// B1 checks policy only. The pipe stays closed until scope, content and caps are enforced in B2.
public sealed class AgentLaunchGate(string folder, Func<DateTimeOffset>? clock = null)
{
    public AgentLaunchPolicyVerdict CheckLaunch(string origin, string request, string requestSha256)
    {
        _ = clock;
        return CheckPolicy();
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
