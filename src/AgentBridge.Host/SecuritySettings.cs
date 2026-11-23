using System.Text.Json;

namespace AgentBridge.Host;

// config.json's security section (docs/host-config.md):
//
//   roster   how this host uses the account's signed device roster (docs/protocol/devices.md):
//            "off" (the default) reads the relay's copy, verifies it for itself and reports what it
//            found, but enforces nothing and refuses no connection. "observe" also feeds that copy
//            to the retained pinned reader, read-only (HostRosterObserve), so a host with a pin can
//            supply the fleet gate its verified roster. "enforce" also feeds it and gates every channel op on it (HostRosterGate,
//            ChannelHub.RosterGate): a device the roster does not approve is told to approve this machine. It is never the
//            default and is only set by `config set security.roster enforce` at the machine (refused inside an agent session).
//
// A value this build does not have is read as off, and said so.
public sealed record SecuritySettings
{
    public const string Off = "off";
    public const string Observe = "observe";
    public const string Enforce = "enforce";

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
        if (s.TryGetProperty("roster", out var roster) && roster.ValueKind == JsonValueKind.String && roster.GetString() is Observe or Enforce)
        {
            return (new SecuritySettings { Roster = roster.GetString()! }, problems);
        }
        if (s.TryGetProperty("roster", out roster) && roster.ValueKind != JsonValueKind.Null
            && !(roster.ValueKind == JsonValueKind.String && roster.GetString() == Off))
        {
            problems.Add("security.roster is not \"off\", \"observe\" or \"enforce\", the modes this host has, so it is off");
        }
        return (Default, problems);
    }
}
