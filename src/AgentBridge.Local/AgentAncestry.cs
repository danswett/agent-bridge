namespace AgentBridge.Local;

// A process known to be an agent's: a live session's own process, one a launch started, or any
// agent CLI running now. Which says what it is, for a person.
public sealed record KnownProcess(int Id, DateTimeOffset? StartedAt, string Which);

// Whether a process runs inside an agent session, from its ancestry: what the host's pipe guard
// (CallerGuard), its own command line and the app's install hooks all ask before doing what only
// a person may do (design §6.3 invariant 13).
public static class AgentAncestry
{
    // Set in the environment of every session the host launches (Launchers.SessionVariable), so
    // anything inside one is known for it even where the chain is broken (a process started
    // detached, whose parent has gone).
    public const string SessionVariable = "AGENT_BRIDGE_SESSION";

    // Executables that are an agent CLI or its launcher whatever runs them. Node and Bun are
    // recognised by the script they run, among the known processes.
    public static readonly IReadOnlyList<string> AgentNames = ["copilot", "claude", "codex", "agency"];

    // The nearest process in the chain that is an agent's, and what it is; null when none is.
    public static (PeerProcess At, string Which)? Find(IReadOnlyList<PeerProcess> chain, IReadOnlyList<KnownProcess> agents, IReadOnlyList<string>? agentNames = null)
    {
        agentNames ??= AgentNames;
        foreach (var p in chain)
        {
            if (agents.FirstOrDefault(a => Same(a, p)) is { } agent) { return (p, agent.Which); }
            if (Path.GetFileNameWithoutExtension(FileName(p.Image)) is { Length: > 0 } name && agentNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return (p, $"a {name.ToLowerInvariant()} process");
            }
        }
        return null;
    }

    // Why this process may not do what only a person may do, said for a person ("this is
    // running {it}"), or null when it may. Not knowing is a reason: an ancestry that could not be
    // read in full may have an agent above what was read.
    public static string? Inside(string? sessionVariable, ProcessAncestry ancestry, Func<IReadOnlyList<KnownProcess>> agents, IReadOnlyList<string>? agentNames = null)
    {
        if (!string.IsNullOrWhiteSpace(sessionVariable)) { return $"inside a session the Bridge Host launched ({SessionVariable} is set)"; }
        if (ancestry.Processes.Count > 0 && Find(ancestry.Processes, agents(), agentNames) is { } found) { return $"inside {found.Which} (pid {found.At.Id})"; }
        if (!ancestry.Complete) { return "in a process tree that could not be traced in full, so it cannot be told from one inside an agent session"; }
        return null;
    }

    // The last part of a path in either convention: a Windows image path read on any platform.
    public static string? FileName(string? path) => path?[(path.LastIndexOfAny(['\\', '/']) + 1)..];

    // The same process: its id, and its start time when both sides know it. The chain was read
    // a moment ago and every id in it was live then, so an id alone settles it when either side
    // cannot say when its process started.
    static bool Same(KnownProcess a, PeerProcess p) =>
        a.Id == p.Id && (a.StartedAt is not { } x || p.StartedAt is not { } y || (x - y).Duration() < TimeSpan.FromSeconds(1));
}
