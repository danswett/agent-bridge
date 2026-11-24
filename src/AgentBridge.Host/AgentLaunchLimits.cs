using System.Text.Json;

namespace AgentBridge.Host;

public sealed record AgentLaunchLimits
{
    public static readonly AgentLaunchLimits Default = new();
    public int LaunchesPerOriginPerHour { get; init; } = 3;
    public int LaunchesPerMachinePerHour { get; init; } = 10;
    public int LaunchAttemptsPerOriginPerHour { get; init; } = 20;
    public int LiveAgentLaunched { get; init; } = 6;
    public int AgentLaunchedIdleHours { get; init; } = 2;
    // The fleet route (the dispatcher's workers, agents.fleetLaunch): owner-set in config.json only, raisable to a hard ceiling, never unlimited (#2770).
    public const int FleetCeiling = 60;
    public int FleetPerHour { get; init; } = 8;
    public int LiveFleetWorkers { get; init; } = 6;

    internal static AgentLaunchLimits Read(JsonElement? limits, bool required, List<string> problems)
    {
        var d = Default;
        return new()
        {
            LaunchesPerOriginPerHour = ReadLimit("launchesPerOriginPerHour", d.LaunchesPerOriginPerHour),
            LaunchesPerMachinePerHour = ReadLimit("launchesPerMachinePerHour", d.LaunchesPerMachinePerHour),
            LaunchAttemptsPerOriginPerHour = ReadLimit("launchAttemptsPerOriginPerHour", d.LaunchAttemptsPerOriginPerHour, optional: true),
            LiveAgentLaunched = ReadLimit("liveAgentLaunched", d.LiveAgentLaunched),
            AgentLaunchedIdleHours = ReadLimit("agentLaunchedIdleHours", d.AgentLaunchedIdleHours),
            FleetPerHour = ReadFleet("fleetPerHour", d.FleetPerHour),
            LiveFleetWorkers = ReadFleet("liveFleetWorkers", d.LiveFleetWorkers),
        };

        // A whole number from 1: above the hard ceiling it is the ceiling (said), and anything else is the default, so never unlimited.
        int ReadFleet(string key, int fallback)
        {
            if (limits is not { } section || !section.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) { return fallback; }
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n >= 1)
            {
                if (n <= FleetCeiling) { return n; }
                problems.Add($"agents.limits.{key} is above {FleetCeiling}, so it is {FleetCeiling}");
                return FleetCeiling;
            }
            problems.Add($"agents.limits.{key} must be a whole number from 1 to {FleetCeiling}, so it is {fallback}");
            return fallback;
        }

        int ReadLimit(string key, int ceiling, bool optional = false)
        {
            if (limits is { } section && section.TryGetProperty(key, out var value))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n >= 1 && n <= ceiling) { return n; }
                problems.Add($"agents.limits.{key} must be a whole number from 1 to {ceiling}; agent launch is refused");
            }
            else if (required && !optional) { problems.Add($"agents.limits.{key} is missing; agent launch is refused"); }
            return ceiling;
        }
    }
}
