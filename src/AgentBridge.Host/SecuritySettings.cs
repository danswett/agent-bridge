using System.Text.Json;

namespace AgentBridge.Host;

// config.json's security section (docs/host-config.md):
//
//   roster   how this host uses the account's signed device roster (docs/protocol/devices.md):
//            "off" is the only mode this build has. The host reads the relay's copy and verifies
//            it for itself, and reports what it found, but enforces nothing and refuses no
//            connection. "observe" and "enforce" come with the later slices of the roster work.
//
// A value this build does not have is read as off, and said so.
public sealed record SecuritySettings
{
    public const string Off = "off";

    public static readonly SecuritySettings Default = new();

    public string Roster { get; init; } = Off;

    public static (SecuritySettings Settings, IReadOnlyList<string> Problems) Read(JsonElement? section)
    {
        var problems = new List<string>();
        if (section is not { } s || s.ValueKind == JsonValueKind.Null) { return (Default, problems); }
        if (s.ValueKind != JsonValueKind.Object)
        {
            problems.Add("security is not an object, so security.roster is off");
            return (Default, problems);
        }
        if (s.TryGetProperty("roster", out var roster) && roster.ValueKind != JsonValueKind.Null
            && !(roster.ValueKind == JsonValueKind.String && roster.GetString() == Off))
        {
            problems.Add("security.roster is not \"off\", the only mode this host has, so it is off");
        }
        return (Default, problems);
    }
}
