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
    internal AgentLaunchLimits? Temporary { get; init; }
    internal DateTimeOffset RaiseStartedAt { get; init; }
    internal DateTimeOffset RaiseExpiresAt { get; init; }

    internal AgentLaunchLimits At(DateTimeOffset now) =>
        Temporary is { } raised && now >= RaiseStartedAt && now < RaiseExpiresAt ? raised : this;

    internal static AgentLaunchLimits Read(JsonElement? limits, bool required, List<string> problems)
    {
        var d = Default;
        var baseline = new AgentLaunchLimits
        {
            LaunchesPerOriginPerHour = ReadLimit("launchesPerOriginPerHour", d.LaunchesPerOriginPerHour),
            LaunchesPerMachinePerHour = ReadLimit("launchesPerMachinePerHour", d.LaunchesPerMachinePerHour),
            LaunchAttemptsPerOriginPerHour = ReadLimit("launchAttemptsPerOriginPerHour", d.LaunchAttemptsPerOriginPerHour, optional: true),
            LiveAgentLaunched = ReadLimit("liveAgentLaunched", d.LiveAgentLaunched),
            AgentLaunchedIdleHours = ReadLimit("agentLaunchedIdleHours", d.AgentLaunchedIdleHours),
        };
        if (limits is not { } configSection || !configSection.TryGetProperty("temporaryRaise", out var temporary)) { return baseline; }
        const string path = "agents.limits.temporaryRaise";
        if (temporary.ValueKind != JsonValueKind.Object
            || !Stamp("startedAt", out var started) || !Stamp("expiresAt", out var expires)
            || expires <= started || expires - started > TimeSpan.FromHours(4)
            || !temporary.TryGetProperty("limits", out var overrides) || overrides.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"{path} needs startedAt, expiresAt (at most 4 hours later), and a limits object; agent launch is refused");
            return baseline;
        }
        foreach (var p in temporary.EnumerateObject())
        {
            if (p.Name is not ("startedAt" or "expiresAt" or "limits"))
            { problems.Add($"{path}.{p.Name} is not a setting; agent launch is refused"); }
        }
        var raised = baseline;
        foreach (var p in overrides.EnumerateObject())
        {
            var ceiling = p.Name switch
            {
                "launchesPerOriginPerHour" => d.LaunchesPerOriginPerHour,
                "launchesPerMachinePerHour" => d.LaunchesPerMachinePerHour,
                "launchAttemptsPerOriginPerHour" => d.LaunchAttemptsPerOriginPerHour,
                "liveAgentLaunched" => d.LiveAgentLaunched,
                "agentLaunchedIdleHours" => d.AgentLaunchedIdleHours,
                _ => 0,
            };
            if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out var n) || n < 1 || n > ceiling)
            {
                problems.Add($"{path}.limits.{p.Name} must name a launch limit with a whole number from 1 to {ceiling}; agent launch is refused");
                continue;
            }
            raised = p.Name switch
            {
                "launchesPerOriginPerHour" => raised with { LaunchesPerOriginPerHour = n },
                "launchesPerMachinePerHour" => raised with { LaunchesPerMachinePerHour = n },
                "launchAttemptsPerOriginPerHour" => raised with { LaunchAttemptsPerOriginPerHour = n },
                "liveAgentLaunched" => raised with { LiveAgentLaunched = n },
                "agentLaunchedIdleHours" => raised with { AgentLaunchedIdleHours = n },
                _ => raised,
            };
        }
        return baseline with { Temporary = raised, RaiseStartedAt = started, RaiseExpiresAt = expires };

        bool Stamp(string key, out DateTimeOffset value)
        {
            value = default;
            return temporary.TryGetProperty(key, out var stamp) && stamp.ValueKind == JsonValueKind.String
                && stamp.GetString() is { } text && text.Contains('T')
                && (text.EndsWith('Z') || (text.Length >= 6 && text[^6] is '+' or '-'))
                && stamp.TryGetDateTimeOffset(out value);
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
