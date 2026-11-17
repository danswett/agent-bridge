using System.Text.Json;

namespace AgentBridge.Protocol;

public static class FleetNamePresentation
{
    public const string Disclosure = "The relay sees this label. Choose a public-safe label, such as a role name. "
        + "Do not include private task details, account names, email addresses, repository names or paths. Blank clears the label.";

    public static string Saved(JsonElement reply)
    {
        var status = reply.ValueKind == JsonValueKind.Object
            && reply.TryGetProperty("duplicateStatus", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
        return status switch
        {
            "present" => "Fleet label saved. Duplicate status: present. Another live session uses this label; lookup may refuse or select a newer session.",
            "absent" => "Fleet label saved. Duplicate status: absent. No duplicate was found; this is not proof of uniqueness.",
            _ => "Fleet label saved. Duplicate status: unknown. Other live labels could not be checked.",
        };
    }
}
