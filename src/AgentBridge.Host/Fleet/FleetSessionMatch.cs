using AgentBridge.Protocol;

namespace AgentBridge.Host.Fleet;

internal static class FleetSessionMatch
{
    public static bool Active(string state) => state is "idle" or "working" or "waiting" or "agents" or "shell";
    public static bool IsId(string? id) => FleetSessionId.IsCanonical(id)
        || id is { Length: 16 } && Hex(id)
        || id is { Length: 29 } && id.StartsWith("agent_bridge_", StringComparison.Ordinal) && Hex(id[13..]);

    static bool Hex(string id) => id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool MatchesId(string? node, string? sessionId, string id) =>
        IsId(id) && (FleetSessionId.IsCanonical(id) ? FleetSessionId.ForNode(sessionId, node) == id
            : node == (id.Length == 16 ? "agent_bridge_" + id : id));

    public static bool Matches(FleetSession session, string id) =>
        IsId(id) && Active(session.State)
        && !(session.Evidence?.Copy().TryGetValue("stale", out var stale) == true && stale.ValueKind == System.Text.Json.JsonValueKind.True)
        && MatchesId(session.Node, session.SessionId, id);
}
