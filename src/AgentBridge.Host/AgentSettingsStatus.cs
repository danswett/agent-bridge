using System.Text.Json.Serialization;

namespace AgentBridge.Host;

public static class AgentSettingsStatus
{
    public const string Op = "host.agentSettings.status";

    public sealed record Entry(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] object? Value,
        object Default, int? Min = null, int? Max = null, bool[]? Allowed = null);

    public sealed record Result(string State, string? Reason, IReadOnlyDictionary<string, Entry> Settings);

    public static Result Read(string folder)
    {
        var (settings, fileProblem) = HostSettings.Load(folder);
        var policy = settings.Agents;
        var reason = fileProblem ?? (settings.AgentsProblems.Count == 0 ? null : string.Join("; ", settings.AgentsProblems));
        var state = fileProblem is not null ? "unreadable" : reason is not null ? "refused" : "ok";
        var defaults = AgentPolicy.Off;
        var limits = policy.Limits;
        var d = defaults.Limits;
        var entries = new Dictionary<string, Entry>
        {
            ["agents.launch.enabled"] = new(reason is null ? policy.LaunchEnabled : null, defaults.LaunchEnabled, Allowed: [false, true]),
            ["agents.limits.maxDepth"] = Limit(limits.MaxDepth, d.MaxDepth),
            ["agents.limits.conversationMessages"] = Limit(limits.ConversationMessages, d.ConversationMessages),
            ["agents.limits.conversationMinutes"] = Limit(limits.ConversationMinutes, d.ConversationMinutes),
            ["agents.limits.perPairPerMinute"] = Limit(limits.PerPairPerMinute, d.PerPairPerMinute),
            ["agents.limits.perTargetPerMinute"] = Limit(limits.PerTargetPerMinute, d.PerTargetPerMinute),
            ["agents.limits.launchesPerOriginPerHour"] = Limit(limits.Launch.LaunchesPerOriginPerHour, d.Launch.LaunchesPerOriginPerHour),
            ["agents.limits.launchesPerMachinePerHour"] = Limit(limits.Launch.LaunchesPerMachinePerHour, d.Launch.LaunchesPerMachinePerHour),
            ["agents.limits.launchAttemptsPerOriginPerHour"] = Limit(limits.Launch.LaunchAttemptsPerOriginPerHour, d.Launch.LaunchAttemptsPerOriginPerHour),
            ["agents.limits.liveAgentLaunched"] = Limit(limits.Launch.LiveAgentLaunched, d.Launch.LiveAgentLaunched),
            ["agents.limits.agentLaunchedIdleHours"] = Limit(limits.Launch.AgentLaunchedIdleHours, d.Launch.AgentLaunchedIdleHours),
        };
        return new(state, reason, entries);

        Entry Limit(int value, int ceiling) => new(reason is null ? value : null, ceiling, 1, ceiling);
    }
}
