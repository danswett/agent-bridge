using System.Text.RegularExpressions;

namespace AgentBridge.Host.Core.Parsing;

// Work a session started that outlives its own turn: background agents and shells, each
// tracked by id, never by a count. Copilot writes subagent.completed again for every agent
// it still tracks when the session shuts down, so a count went negative and hid an agent
// still running; an id that ends twice is simply already gone.
//
// Nothing here outlives the CLI process that started it: background agents run inside
// it, and a shell it started reports back only to it. So each piece of work remembers
// when it (last) started, and Running drops whatever began before the session's current
// process did. Without that, a CLI that was replaced, or a host reading an old tail,
// left a session "delegating" for good, waiting on work nothing would ever finish.
internal sealed class BackgroundWork
{
    // Enough for any real session; a hostile transcript cannot grow these without bound.
    const int Keep = 256;
    // Start times the OS rounds (Linux reports them in clock ticks from boot).
    static readonly TimeSpan Slack = TimeSpan.FromSeconds(1);

    sealed class Agent
    {
        public bool Working;
        public bool AskedForTools;
        public bool Completed;
        // Runs that ended before their agent_idle notification arrived. The parent takes
        // notifications when it next can, which was 90 minutes later in one transcript,
        // so a message may wake the agent first; that late notice belongs to the run
        // before and must not end the one now going.
        public int IdleOwed;
        public DateTimeOffset? Since;
    }

    readonly Dictionary<string, Agent> _agents = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _agentByCall = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTimeOffset?> _shells = new(StringComparer.Ordinal);

    // Moves on every change, so a parser can tell whether an event changed what is shown.
    public int Version { get; private set; }

    // Every id held, for checking that a long session cannot grow it without bound.
    internal int Tracked => _agents.Count + _agentByCall.Count + _shells.Count;

    public void Clear()
    {
        _agents.Clear();
        _agentByCall.Clear();
        _shells.Clear();
        Version++;
    }

    // A start seen only in the skimmed part of the transcript: a background agent's mode is
    // a fact worth keeping (its later events may be in the parsed window), but whether it is
    // still working is for those events to say. A sync agent is never kept: only agents
    // known to be background are tracked, so its events change nothing here, and keeping
    // its id grew without bound in a session that ran many.
    public void KnowMode(string? callId, string? agentId, bool background)
    {
        var key = agentId ?? callId;
        if (key is null || !background) { return; }
        if (callId is not null) { _agentByCall[callId] = key; }
        if (!_agents.ContainsKey(key)) { Add(key, new Agent()); }
    }

    public void AgentStarted(string? callId, string? agentId, DateTimeOffset? at)
    {
        var key = agentId ?? callId;
        if (key is null) { return; }
        KnowMode(callId, agentId, background: true);
        Set(_agents[key], working: true, at);
        _agents[key].AskedForTools = false;
    }

    // The agent's own prompt, turn or tool call: it is working, again if it had stopped
    // (a message sent to an idle agent writes no second subagent.started).
    public void AgentActive(string agentId, DateTimeOffset? at)
    {
        if (_agents.TryGetValue(agentId, out var agent)) { Set(agent, working: true, at); }
    }

    public void AgentAskedForTools(string agentId)
    {
        if (_agents.TryGetValue(agentId, out var agent)) { agent.AskedForTools = true; }
    }

    // As for the session itself: a turn that asked for tools is a round, followed by the
    // next turn reading their results; one that asked for none is the end of its work.
    public void AgentTurnEnded(string agentId, DateTimeOffset? at)
    {
        if (!_agents.TryGetValue(agentId, out var agent)) { return; }
        if (!agent.AskedForTools) { EndRun(agent); }
        agent.AskedForTools = false;
    }

    // The agent's own abort or shutdown: its run is over, and nothing else of the session's.
    public void AgentEnded(string agentId)
    {
        if (_agents.TryGetValue(agentId, out var agent)) { EndRun(agent); agent.AskedForTools = false; }
    }

    // agent_idle: spent on the oldest run still owed one; only a notice no run is owed
    // ends the current run (one whose end its own events never showed).
    public void AgentIdle(string? agentId)
    {
        if (agentId is null || !_agents.TryGetValue(agentId, out var agent)) { return; }
        if (agent.IdleOwed > 0) { agent.IdleOwed--; return; }
        Set(agent, working: false, null);
    }

    // Written once, after the agent's first run; written again for every agent as the
    // session shuts down, which session.shutdown itself settles. Only the first ends a run.
    public void AgentCompleted(string? callId)
    {
        if (callId is null || !_agentByCall.TryGetValue(callId, out var key) || !_agents.TryGetValue(key, out var agent) || agent.Completed) { return; }
        agent.Completed = true;
        EndRun(agent);
    }

    void EndRun(Agent agent)
    {
        if (!agent.Working) { return; }
        Set(agent, working: false, null);
        agent.IdleOwed++;
    }

    public void ShellStarted(string id, DateTimeOffset? at)
    {
        if (_shells.TryGetValue(id, out var since))
        {
            // Said again ("still running"), or an id reused (the agent names shells, and a
            // new CLI may name one like a dead one's): the latest start is what counts.
            if (at is { } t && (since is null || t > since)) { _shells[id] = at; Version++; }
            return;
        }
        if (_shells.Count >= Keep) { _shells.Remove(_shells.OrderBy(p => p.Value ?? DateTimeOffset.MinValue).First().Key); }
        _shells[id] = at;
        Version++;
    }

    public void ShellEnded(string? id)
    {
        if (id is not null && _shells.Remove(id)) { Version++; }
    }

    // The session's CLI shut down: everything it was running went with it.
    public void EndAll()
    {
        var any = _shells.Count > 0 || _agents.Values.Any(a => a.Working);
        _shells.Clear();
        foreach (var a in _agents.Values) { a.Working = false; a.AskedForTools = false; a.IdleOwed = 0; }
        if (any) { Version++; }
    }

    // What is still running and could still report back to the session's current process.
    public (int Agents, int Shells) Running(DateTimeOffset? processStartedAt)
    {
        bool Live(DateTimeOffset? since) => since is null || processStartedAt is not { } start || since >= start - Slack;
        return (_agents.Values.Count(a => a.Working && Live(a.Since)), _shells.Values.Count(Live));
    }

    void Set(Agent agent, bool working, DateTimeOffset? at)
    {
        if (agent.Working == working) { return; }
        agent.Working = working;
        if (working) { agent.Since = at; }
        Version++;
    }

    void Add(string key, Agent agent)
    {
        if (_agents.Count >= Keep)
        {
            // Forget an agent that has finished; failing that, the one working longest.
            var drop = _agents.FirstOrDefault(p => !p.Value.Working).Key
                ?? _agents.OrderBy(p => p.Value.Since ?? DateTimeOffset.MinValue).First().Key;
            _agents.Remove(drop);
            foreach (var call in _agentByCall.Where(p => p.Value == drop).Select(p => p.Key).ToList()) { _agentByCall.Remove(call); }
            Version++;
        }
        _agents[key] = agent;
    }
}

// The markers Copilot's shell tools put in a tool result. Measured on real transcripts, a
// start, a move to the background and a stop are the whole result, and "still running"
// and "completed" its last line; the same text anywhere else is a command's own output
// quoting it (a transcript printed back, an issue body), which must neither invent a
// shell nor finish a real one.
internal static partial class ShellMarkers
{
    // Long enough for any marker; a result longer than this cannot be one whole.
    const int MaxMarker = 1024;

    public static (string? Started, string? Ended) Read(string? content)
    {
        if (string.IsNullOrEmpty(content)) { return (null, null); }
        var whole = content.Trim();
        if (whole.Length <= MaxMarker && whole.StartsWith("<command ", StringComparison.Ordinal))
        {
            if (StartedWhole().Match(whole) is { Success: true } a) { return (a.Groups[1].Value.Trim(), null); }
            if (Moved().Match(whole) is { Success: true } m) { return (m.Groups[1].Value, null); }
            if (Stopped().Match(whole) is { Success: true } s) { return (null, s.Groups[1].Value); }
        }
        var newline = whole.LastIndexOf('\n');
        var last = (newline < 0 ? whole : whole[(newline + 1)..]).TrimEnd('\r');
        if (last.Length > MaxMarker || !last.StartsWith('<')) { return (null, null); }
        if (StillRunning().Match(last) is { Success: true } r) { return (r.Groups[1].Value, null); }
        if (Completed().Match(last) is { Success: true } c) { return (null, c.Groups[1].Value); }
        return (null, null);
    }

    // Not "started in detached background": a detached shell is a service meant to
    // outlive the session, so nothing is waiting on it.
    [GeneratedRegex(@"\A<command started in background with shellId: ([^>\r\n]+)>\z", RegexOptions.CultureInvariant)]
    private static partial Regex StartedWhole();

    [GeneratedRegex(@"\A<command with shellId: (\S+?) moved to background by the user[^\r\n]*>\z", RegexOptions.CultureInvariant)]
    private static partial Regex Moved();

    // A stopped shell never completes and gets no notification: without this it stayed
    // outstanding for the rest of the session.
    [GeneratedRegex(@"\A<command with id: (\S+?) stopped>\z", RegexOptions.CultureInvariant)]
    private static partial Regex Stopped();

    // A sync command that outran its wait carries on in the background, and says so only
    // here; most background shells in real sessions arrived this way or were moved.
    [GeneratedRegex(@"\A<command with shellId: (\S+?) is (?:still running|running in the background)", RegexOptions.CultureInvariant)]
    private static partial Regex StillRunning();

    [GeneratedRegex(@"\A<shellId: (\S+?) completed with exit code", RegexOptions.CultureInvariant)]
    private static partial Regex Completed();
}
