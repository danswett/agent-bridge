using AgentBridge.Local;
using AgentBridge.Transport;

namespace AgentBridge.Host.Mcp;

// Where the MCP server finds the host (docs/protocol/local-pipe.md, "The MCP server"), as mcp/src/pipe.mjs does: AGENT_BRIDGE_PIPE
// (a pipe name or path) wins; otherwise the host's own derivation from AGENT_BRIDGE_ROOT (default: the product's root) and
// AGENT_BRIDGE_PROFILE (default "default"). A profile that is not a profile name finds no host.
public static class McpEndpoint
{
    const string WindowsPipePrefix = @"\\.\pipe\";

    public static LocalEndpoint? Resolve(Func<string, string?> env, bool? windows = null)
    {
        var onWindows = windows ?? OperatingSystem.IsWindows();
        if (Clean(env("AGENT_BRIDGE_PIPE")) is { } explicitPipe)
        {
            var address = onWindows ? WindowsPipeName(explicitPipe) : UnixSocketPath(explicitPipe);
            return address is null ? null : new LocalEndpoint(string.Empty, address);
        }
        var profile = Clean(env("AGENT_BRIDGE_PROFILE")) ?? "default";
        if (!ProfileFolders.IsValidProfile(profile)) { return null; }
        var root = Clean(env("AGENT_BRIDGE_ROOT")) ?? ProfileFolders.DefaultRoot;
        return LocalEndpoint.For(Path.Combine(root, profile));
    }

    // One pipe name in the local namespace, or \\.\pipe\ plus one. Nothing else: "..\UNC\host\pipe\x" normalises to \\.\UNC\host\pipe\x, a pipe on
    // another machine, so no separator, colon or ".." is accepted.
    static string? WindowsPipeName(string value)
    {
        var name = value.StartsWith(WindowsPipePrefix, StringComparison.OrdinalIgnoreCase) ? value[WindowsPipePrefix.Length..] : value;
        return PipeName.IsMatch(name) && !name.Contains("..", StringComparison.Ordinal) ? name : null;
    }

    static readonly System.Text.RegularExpressions.Regex PipeName = new(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // An absolute socket path to a file, with no ".." segment. Who owns it is checked on connect (LocalPeerOwner).
    static string? UnixSocketPath(string value) =>
        value.StartsWith('/') && !value.EndsWith('/') && !value.Split('/').Contains("..") ? value : null;

    static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
