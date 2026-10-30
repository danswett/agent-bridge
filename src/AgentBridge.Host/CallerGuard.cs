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

public interface ICallerGuard
{
    // fresh asks again now; otherwise a classification of this connection a few seconds old
    // will do. Managing the host always asks afresh; the session ops, of which an attachment
    // upload sends thousands in a row, do not.
    Caller Classify(IProtocolChannel channel, bool fresh = false);
}

// Design §6.3 invariant 13: no session launched by the bridge, nor any agent process, may stop
// or restart its host, sign it in or change its settings through the local pipe. On 2026-10-15
// an agent ran `agent-ha-bridge restart`; the new daemon then took that agent for an
// unaccounted process, held discovery, and every resumable session vanished from the phone.
//
// A connection is an agent's when its process, or any process it descends from, is one the host
// knows to be an agent's (KnownProcess, matched by id and start time so a reused id does not
// count) or is an agent CLI by name (AgentAncestry). It is the app when, failing that, its
// ancestry was read in full and its own executable is AppExecutable beside this host, which is
// how the app ships (docs/app-windows.md). Anything else is Other: a person's script, say, which
// may still read and answer as before, but not manage the host. So is one whose ancestry could
// not be read in full: an agent may sit above what was read (fails closed).
//
// This is a safety rail, not a security boundary. Every process here runs as the same user, who
// can read the device key and config.json, write config.json directly, end the host with Task
// Manager, copy any executable to the app's name, or start one detached from the agent so that it
// descends from nothing. What it stops is an agent following an instruction, or making a
// mistake, through the obvious door: the agent-ha-bridge incident, not an attacker.
public sealed class CallerGuard(Func<IReadOnlyList<KnownProcess>> known, string? appImage, IReadOnlyList<string>? agentNames = null, TimeProvider? time = null,
    Func<IProtocolChannel, ProcessAncestry>? ancestryOf = null) : ICallerGuard
{
    // The Windows app's executable as it ships: AgentBridge.App.csproj's AssemblyName, which
    // build/pack-app.ps1 packs beside the host. Not the project's name.
    public const string AppExecutable = "AgentBridge.exe";

    // How long a connection's classification stands for the session ops. Each one lists every
    // agent process (nine scans), and a 100 MiB attachment is some nine thousand chunks.
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(5);

    readonly IReadOnlyList<string> names = agentNames ?? AgentAncestry.AgentNames;
    readonly TimeProvider clock = time ?? TimeProvider.System;
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<IProtocolChannel, Seen> seen = new();

    sealed record Seen(Caller Caller, DateTimeOffset At);

    public Caller Classify(IProtocolChannel channel, bool fresh = false)
    {
        var now = clock.GetUtcNow();
        if (!fresh && seen.TryGetValue(channel, out var last) && now - last.At < MaxAge) { return last.Caller; }
        var caller = Classify(ancestryOf?.Invoke(channel) ?? (channel as LocalChannel)?.Peer?.Ancestry ?? ProcessAncestry.Unknown);
        seen.AddOrUpdate(channel, new Seen(caller, now));
        return caller;
    }

    public Caller Classify(IReadOnlyList<PeerProcess> chain) => Classify(new ProcessAncestry(chain, chain.Count > 0));

    public Caller Classify(ProcessAncestry ancestry)
    {
        var chain = ancestry.Processes;
        // Not knowing who it is counts against it, as for PeerPolicy: it could be an agent.
        if (chain.Count == 0) { return new Caller(CallerKind.Other, "a process the host could not trace"); }
        if (AgentAncestry.Find(chain, known(), names) is { } found) { return Inside(chain, found.At, found.Which); }
        var self = chain[0];
        if (!ancestry.Complete)
        {
            return new Caller(CallerKind.Other, $"{(AgentAncestry.FileName(self.Image) is { Length: > 0 } partial ? partial : "a process")} (pid {self.Id}), whose ancestry could not be read in full");
        }
        if (appImage is not null && self.Image is { } image && string.Equals(Path.GetFullPath(image), Path.GetFullPath(appImage), StringComparison.OrdinalIgnoreCase))
        {
            return new Caller(CallerKind.App, "the Agent Bridge app");
        }
        return new Caller(CallerKind.Other, $"{(AgentAncestry.FileName(self.Image) is { Length: > 0 } file ? file : "a process")} (pid {self.Id}), which is not the Agent Bridge app beside this host");
    }

    static Caller Inside(IReadOnlyList<PeerProcess> chain, PeerProcess at, string which) =>
        new(CallerKind.Agent, at == chain[0] ? $"{which} (pid {at.Id})" : $"pid {chain[0].Id}, inside {which} (pid {at.Id})");
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


    static string Name(AgentKind kind) => kind.ToString().ToLowerInvariant();
}

// Every connection is the same caller. Tests use it; nothing else should.
public sealed class FixedCallerGuard(Caller caller) : ICallerGuard
{
    public Caller Classify(IProtocolChannel channel, bool fresh = false) => caller;
}
