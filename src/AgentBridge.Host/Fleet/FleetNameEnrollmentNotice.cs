using System.Text.Json;

namespace AgentBridge.Host.Fleet;

internal static class FleetNameEnrollmentNotice
{
    const string PresentWarning = "Another live session uses this label; lookup may refuse or select a newer session.";
    const string UnknownWarning = "Could not determine whether another live session uses this label; lookup may refuse or select a newer session.";

    public static void WriteDisclosure(TextWriter output) =>
        output.WriteLine("The relay sees this label. Choose a public-safe label, such as a role name. Do not include private task details, account names, email addresses, repository names or paths.");

    public static void WriteWarning(JsonElement result, TextWriter output)
    {
        var status = result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("duplicateStatus", out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        var warning = status switch
        {
            "present" => PresentWarning,
            "absent" => null,
            _ => UnknownWarning,
        };
        if (warning is not null) { output.WriteLine(warning); }
    }
}
