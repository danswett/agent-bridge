using System.Text.RegularExpressions;

namespace AgentBridge.Host.Fleet;

internal static class FleetSessionMatch
{
    public static string Normalize(string? name)
    {
        var text = Regex.Replace(name?.Trim() ?? "", @"\s+", " ");
        if (text.StartsWith("Copilot: ", StringComparison.Ordinal)) { text = text[9..]; }
        else if (text.StartsWith("Claude: ", StringComparison.Ordinal)) { text = text[8..]; }
        if (text.EndsWith(" Status", StringComparison.Ordinal)) { text = text[..^7]; }
        return text.Trim();
    }

    public static bool Active(string state) => state is "idle" or "working" or "waiting" or "agents" or "shell";
    public static bool Matches(FleetSession session, string name) =>
        Active(session.State) && Normalize(session.Name) == name
        && !(session.Evidence?.Copy().TryGetValue("stale", out var stale) == true && stale.ValueKind == System.Text.Json.JsonValueKind.True);
}
