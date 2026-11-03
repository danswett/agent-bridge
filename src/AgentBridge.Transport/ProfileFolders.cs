namespace AgentBridge.Transport;

// Where a profile's folder is, and which names are profiles: the one rule, compiled into the
// host (through Identity) and into agent-bridge-hostw, which finds the profile's log and the
// app's stop marker by it and must never look in a different folder from the host's (#106).
#if AGENT_BRIDGE_HOSTW
internal
#else
public
#endif
static class ProfileFolders
{
    public static string DefaultRoot => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentBridge")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agent-bridge");

    // A profile is a folder name, never a path: "../x" must not reach outside the root.
    public static bool IsValidProfile(string? profile) =>
        profile is { Length: > 0 and <= 64 } && profile is not ("." or "..")
        && profile.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
