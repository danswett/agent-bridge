using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

public enum CallerKind { App, Agent, Other }

// Who a local connection is, for what it may do: the Agent Bridge app, something running
// inside an agent session, or anything else of the same user. Description says which, for a
// person and the log ("inside the copilot session 1a2b3c4d (pid 4120)").
public sealed record Caller(CallerKind Kind, string Description);

// A process the host knows to be an agent's: a live session's own process, one it launched, or
// any agent CLI running now. Which says what it is.
public sealed record KnownProcess(int Id, DateTimeOffset? StartedAt, string Which);

public interface ICallerGuard
{
    Caller Classify(IProtocolChannel channel);
}

// Design §6.3 invariant 13: no session launched by the bridge, nor any agent process, may stop
// or restart its host, sign it in or change its settings through the local pipe. On 2026-10-15
// an agent ran `agent-ha-bridge restart`; the new daemon then took that agent for an
// unaccounted process, held discovery, and every resumable session vanished from the phone.
//
// A connection is an agent's when its process, or any process it descends from, is one the host
// knows to be an agent's (KnownProcess, matched by id and start time so a reused id does not
// count) or is an agent CLI by name. It is the app when, failing that, its own executable is
// AppExecutable beside this host, which is how the app ships (docs/app-windows.md).
// Anything else is Other: a person's script, say, which may still read and answer as before, but
// not manage the host.
//
// This is a safety rail, not a security boundary. Every process here runs as the same user, who
// can read the device key and config.json, write config.json directly, end the host with Task
// Manager, copy any executable to the app's name, or start one detached from the agent so that it
// descends from nothing. What it stops is an agent following an instruction, or making a
// mistake, through the obvious door: the agent-ha-bridge incident, not an attacker.
public sealed class CallerGuard(Func<IReadOnlyList<KnownProcess>> known, string? appImage, IReadOnlyList<string>? agentNames = null) : ICallerGuard
{
    // Executables that are an agent CLI or its launcher whatever runs them. Node and Bun are
    // recognised by the script they run, among the known processes.
    public static readonly IReadOnlyList<string> AgentNames = ["copilot", "claude", "codex", "agency"];

    // The Windows app's executable as it ships: AgentBridge.App.csproj's AssemblyName, which
    // build/pack-app.ps1 packs beside the host. Not the project's name.
    public const string AppExecutable = "AgentBridge.exe";

    readonly IReadOnlyList<string> names = agentNames ?? AgentNames;

    public Caller Classify(IProtocolChannel channel) => Classify((channel as LocalChannel)?.Peer?.Chain ?? []);

    public Caller Classify(IReadOnlyList<PeerProcess> chain)
    {
        // Not knowing who it is counts against it, as for PeerPolicy: it could be an agent.
        if (chain.Count == 0) { return new Caller(CallerKind.Other, "a process the host could not trace"); }
        if (FindAgent(chain, known(), names) is { } found) { return Inside(chain, found.At, found.Which); }
        var self = chain[0];
        if (appImage is not null && self.Image is { } image && string.Equals(Path.GetFullPath(image), Path.GetFullPath(appImage), StringComparison.OrdinalIgnoreCase))
        {
            return new Caller(CallerKind.App, "the Agent Bridge app");
        }
        return new Caller(CallerKind.Other, $"{(FileName(self.Image) is { Length: > 0 } file ? file : "a process")} (pid {self.Id}), which is not the Agent Bridge app beside this host");
    }

    // The nearest process in the chain that is an agent's, and what it is; null when none is.
    public static (PeerProcess At, string Which)? FindAgent(IReadOnlyList<PeerProcess> chain, IReadOnlyList<KnownProcess> agents, IReadOnlyList<string>? agentNames = null)
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

    // The last part of a path in either convention: a Windows image path read on any platform.
    static string? FileName(string? path) => path?[(path.LastIndexOfAny(['\\', '/']) + 1)..];

    static Caller Inside(IReadOnlyList<PeerProcess> chain, PeerProcess at, string which) =>
        new(CallerKind.Agent, at == chain[0] ? $"{which} (pid {at.Id})" : $"pid {chain[0].Id}, inside {which} (pid {at.Id})");

    // The same process: its id, and its start time when both sides know it. The chain was read
    // a moment ago and every id in it was live then, so an id alone settles it when either side
    // cannot say when its process started.
    static bool Same(KnownProcess a, PeerProcess p) =>
        a.Id == p.Id && (a.StartedAt is not { } x || p.StartedAt is not { } y || (x - y).Duration() < TimeSpan.FromSeconds(1));
}

public static class KnownAgents
{
    // Every process the host knows to be an agent's: its live sessions' own processes first, so
    // a match names the session; then what its launches started; then every agent CLI running,
    // whichever runtime it is in.
    public static IReadOnlyList<KnownProcess> Of(ISessionSource sessions, ISessionLauncher? launcher, IProcessInspector inspector)
    {
        var known = new List<KnownProcess>();
        foreach (var s in sessions.Snapshot())
        {
            if (sessions.ProcessOf(s.Id) is { } p) { known.Add(new KnownProcess(p.Id, p.StartedAt, $"the {Name(s.Agent)} session {s.Id[..Math.Min(8, s.Id.Length)]}")); }
        }
        foreach (var l in launcher?.LaunchedProcesses() ?? []) { known.Add(new KnownProcess(l.ProcessId, l.StartedAt, "a session this host launched")); }
        known.AddRange(Running(inspector));
        return known;
    }

    public static IReadOnlyList<KnownProcess> Running(IProcessInspector inspector) =>
        [.. new[] { AgentKind.Copilot, AgentKind.Claude, AgentKind.Codex }.SelectMany(k => inspector.GetAgentProcesses(k).Select(p => new KnownProcess(p.Id, p.StartedAt, $"a {Name(k)} process")))];

    // Where a process with this chain runs inside an agent session, said for a person, or null
    // when it does not. A launched session's variable settles it even where the chain was broken
    // (a process started detached, whose parent has gone).
    public static string? Inside(string? sessionVariable, IReadOnlyList<PeerProcess> chain, Func<IReadOnlyList<KnownProcess>> agents)
    {
        if (!string.IsNullOrWhiteSpace(sessionVariable)) { return $"inside a session the Bridge Host launched ({Launchers.SessionVariable} is set)"; }
        return chain.Count > 0 && CallerGuard.FindAgent(chain, agents()) is { } found ? $"inside {found.Which} (pid {found.At.Id})" : null;
    }

    static string Name(AgentKind kind) => kind.ToString().ToLowerInvariant();
}

// Every connection is the same caller. Tests use it; nothing else should.
public sealed class FixedCallerGuard(Caller caller) : ICallerGuard
{
    public Caller Classify(IProtocolChannel channel) => caller;
}
