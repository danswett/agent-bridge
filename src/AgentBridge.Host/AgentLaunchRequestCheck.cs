using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

public static class AgentLaunchRequestCheck
{
    // Keep the raw fields: deserializing first would silently discard agentScope and extraArgs.
    // The caller supplies the selected launcher's configured extra arguments, not request data.
    public static string? Check(JsonElement request, IReadOnlyList<string> extraArgs, bool fleet = false)
    {
        if (request.ValueKind != JsonValueKind.Object) { return "The agent launch request must be an object."; }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in request.EnumerateObject())
        {
            if (!names.Add(field.Name)) { return "The agent launch request contains a duplicate field."; }
            switch (field.Name)
            {
                case "resume": return "An agent launch cannot set resume.";
                case "transfer": return "An agent launch cannot set transfer.";
                case "attachments": return "An agent launch cannot set attachments.";
                case "extraArgs": return "An agent launch cannot set extraArgs.";
                case "agentScope": return "An agent launch cannot set agentScope.";
                case "profile": if (!fleet) { return "An agent launch cannot set profile."; } break;
                case "request": case "agent": case "workspace": case "fleetName":
                case "model": case "effort": case "context":
                case "permissions": case "role": case "prompt":
                    break;
                default: return "The agent launch request contains an unsupported field.";
            }
        }
        if (!request.TryGetProperty("permissions", out var permissions)
            || permissions.ValueKind != JsonValueKind.String || permissions.GetString() != LaunchProtocol.Ask
                && !(fleet && permissions.GetString() == LaunchProtocol.AllowAll))
        {
            return "An agent launch requires explicit permissions 'ask'.";
        }
        if (extraArgs.Count != 0) { return "Agent launches cannot use configured extra arguments."; }
        try
        {
            var launch = request.Deserialize<LaunchRequest>(ProtocolJson.Options)!;
            return LaunchProtocol.Check(launch);
        }
        catch (JsonException)
        {
            return "The agent launch request has an invalid field shape or a missing required field.";
        }
    }
}
