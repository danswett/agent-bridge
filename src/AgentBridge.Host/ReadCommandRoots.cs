using AgentBridge.Host.Core;

namespace AgentBridge.Host;

internal static class ReadCommandRoots
{
    public static SessionSourceOptions Resolve(IReadOnlyList<string> arguments, Func<SessionSourceOptions> currentUser)
    {
        var index = arguments.ToList().IndexOf("--read-root");
        if (index < 0) { return currentUser(); }
        if (arguments[0] is not ("sessions" or "get")
            || arguments.Count(a => a == "--read-root") != 1 || index + 1 >= arguments.Count
            || !Path.IsPathFullyQualified(arguments[index + 1])
            || arguments.Any(a => a is "--copilot-home" or "--claude-home" or "--codex-home" or "--profile"))
        {
            throw new ArgumentException("--read-root requires one absolute directory, sessions or get, and no agent-home or profile options.");
        }
        var root = Path.GetFullPath(arguments[index + 1]);
        if (!Directory.Exists(root)) { throw new ArgumentException("--read-root directory does not exist."); }
        var runtime = Path.Combine(root, ".agent-ha-bridge", "runtime");
        return new()
        {
            CopilotHome = Path.Combine(root, ".copilot"),
            ClaudeHome = Path.Combine(root, ".claude"),
            ClaudeConfigDirExplicit = true,
            CodexHome = Path.Combine(root, ".codex"),
            ClaudeRegistrationRoots = [Path.Combine(runtime, "agent-bridge-claude")],
            CodexRegistrationRoots = [Path.Combine(runtime, "agent-bridge-codex")],
            UsageProbeRoots = [Path.Combine(root, ".agent-bridge")],
        };
    }
}
