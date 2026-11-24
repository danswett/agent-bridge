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
    // For tests and diagnostics: the folder profiles live in, instead of the product's own. The only
    // way to move it. LOCALAPPDATA is not one: on Windows the default below comes from the shell
    // folder API, which ignores that variable, so a test that set only it still wrote the machine's
    // real config.json (#4520). It must be an absolute path. A relative one is refused, never read as
    // the default, since falling back to the real folder is the harm this exists to prevent.
    public const string RootVariable = "AGENT_BRIDGE_PROFILE_ROOT";

    // The one line the host's command line and the launcher say when it is relative (exit 2).
    public const string RootRefusal = "AGENT_BRIDGE_PROFILE_ROOT must be an absolute path; nothing was read or written";

    public static string DefaultRoot => ResolveRoot(Environment.GetEnvironmentVariable(RootVariable));

    // RootRefusal when this process's variable is set and is not an absolute path, else null. Read
    // first by every entry point, before it opens anything, so it can refuse cleanly.
    public static string? RootProblem => RefusalFor(Environment.GetEnvironmentVariable(RootVariable));

    public static string? RefusalFor(string? configured) =>
        string.IsNullOrWhiteSpace(configured) || Path.IsPathFullyQualified(configured.Trim()) ? null : RootRefusal;

    // The root for a value of RootVariable: the product's own when it is unset or empty.
    public static string ResolveRoot(string? configured)
    {
        if (RefusalFor(configured) is { } refusal) { throw new InvalidOperationException(refusal); }
        return string.IsNullOrWhiteSpace(configured) ? ProductRoot : Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured.Trim()));
    }

    static string ProductRoot => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentBridge")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agent-bridge");

    // A profile is a folder name, never a path: "../x" must not reach outside the root.
    public static bool IsValidProfile(string? profile) =>
        profile is { Length: > 0 and <= 64 } && profile is not ("." or "..")
        && profile.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
