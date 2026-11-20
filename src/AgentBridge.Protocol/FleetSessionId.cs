namespace AgentBridge.Protocol;

public static class FleetSessionId
{
    public static bool IsCanonical(string? value) =>
        Guid.TryParseExact(value, "D", out var id) && id.ToString("D") == value;

    public static string? ForNode(string? value, string? node) =>
        value is not null && IsCanonical(value) && node == "agent_bridge_" + value.Replace("-", "")[..16] ? value : null;
}
