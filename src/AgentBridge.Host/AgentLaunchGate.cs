namespace AgentBridge.Host;

public sealed record AgentLaunchPolicyVerdict(bool Allowed, string? Reason, AgentLaunchLimits? Limits);

// B1 checks policy only. The pipe stays closed until scope, content and caps are enforced in B2.
public sealed class AgentLaunchGate(string folder)
{
    public AgentLaunchPolicyVerdict CheckPolicy()
    {
        var (policy, problem) = HostSettings.CurrentAgents(folder);
        if (problem is not null) { return new(false, problem, null); }
        if (!policy.LaunchEnabled) { return new(false, "agents.launch.enabled is off or missing on this machine", null); }
        if (!policy.Enabled) { return new(false, "agents.enabled is off on this machine", null); }
        return new(true, null, policy.Limits.Launch);
    }
}
