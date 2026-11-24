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
            var address = onWindows && explicitPipe.StartsWith(WindowsPipePrefix, StringComparison.OrdinalIgnoreCase) ? explicitPipe[WindowsPipePrefix.Length..] : explicitPipe;
            return new LocalEndpoint(string.Empty, address);
        }
        var profile = Clean(env("AGENT_BRIDGE_PROFILE")) ?? "default";
        if (!ProfileFolders.IsValidProfile(profile)) { return null; }
        var root = Clean(env("AGENT_BRIDGE_ROOT")) ?? ProfileFolders.DefaultRoot;
        return LocalEndpoint.For(Path.Combine(root, profile));
    }

    static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
