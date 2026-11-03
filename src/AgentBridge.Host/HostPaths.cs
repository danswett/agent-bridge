using AgentBridge.Hooks;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// Explicit roots keep development hosts out of an installed bridge's profile and
// registration spool. They do not change any host-management authorization checks.
internal sealed record HostPaths(string DataRoot, string Profile, string? RegistrationRoot)
{
    public string ProfileFolder => Path.Combine(DataRoot, Profile);

    public static HostPaths Resolve(string? dataRoot, string? profile, string? registrationRoot, bool macOS)
    {
        profile ??= "default";
        if (!Identity.IsValidProfile(profile)) { throw new ArgumentException("Invalid profile name; use letters, digits, '-', '_' and '.'."); }
        var root = Absolute(dataRoot ?? Identity.DefaultRoot);
        var registrations = registrationRoot is not null ? Absolute(registrationRoot)
            : macOS ? Path.Combine(root, profile, "registrations") : null;
        return new(root, profile, registrations);
    }

    public IReadOnlyList<string> Registrations(bool claude, IReadOnlyList<string> defaults) => RegistrationRoot is null
        ? defaults : [Path.Combine(RegistrationRoot, claude ? SpoolContract.ClaudeFolder : SpoolContract.CodexFolder)];

    static string Absolute(string path)
    {
        if (!Path.IsPathFullyQualified(path)) { throw new ArgumentException("Host data and registration roots must be absolute paths."); }
        return Path.GetFullPath(path);
    }
}
