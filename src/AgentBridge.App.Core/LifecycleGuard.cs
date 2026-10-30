using AgentBridge.Host.Core.Processes;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// Velopack runs the app's install hooks for whoever starts AgentBridge.exe with their arguments,
// so `AgentBridge.exe --veloapp-uninstall` from an agent session stopped the host and took away
// its logon task, and `--veloapp-updated` started it again: the host's own pipe guard bypassed
// through the app (design §6.3 invariant 13; the 2026-10-29 review of #67). The hooks ask this
// first, as the host's command line does: refused inside a session the host launched, under any
// agent CLI the ancestry shows, or when the ancestry cannot be read in full. A person's install,
// update or uninstall runs under Velopack's Update.exe, which is none of those.
public static class LifecycleGuard
{
    // Why this process may not stop, start or unregister the host, or null when it may.
    public static string? Refusal() =>
        AgentAncestry.Inside(Environment.GetEnvironmentVariable(AgentAncestry.SessionVariable), ProcessChain.Read(Environment.ProcessId), () => Running(new SystemProcessInspector()));

    // Every agent CLI running now, whichever runtime it is in: what the host's KnownAgents.Running
    // gives its own command line.
    public static IReadOnlyList<KnownProcess> Running(IProcessInspector inspector) =>
        [.. new[] { AgentKind.Copilot, AgentKind.Claude, AgentKind.Codex }.SelectMany(k => inspector.GetAgentProcesses(k).Select(p => new KnownProcess(p.Id, p.StartedAt, $"a {k.ToString().ToLowerInvariant()} process")))];
}
