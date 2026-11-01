using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Parsing;

// Work a session started beside its own turn: its sub-agents (docs/design/subagents.md),
// and the background shells it runs, each tracked by id, never by a count. Copilot writes
// subagent.completed again for every agent it still tracks when the session shuts down, so
// a count went negative and hid an agent still running; an id that ends twice is simply
// already gone.
//
// Every sub-agent is an entry: background ones, which outlive their starter's turn and a
// message can wake again, and task (sync) ones, which are part of it. Only background
// agents that are running count in activity.background, as before; the entries are what a
// newer client lists.
//
// Nothing here outlives the CLI process that started it: sub-agents run inside it, and a
// shell it started reports back only to it. So each piece of work remembers when it (last)
// started, and Running drops whatever began before the session's current process did.
// Without that, a CLI that was replaced, or a host reading an old tail, left a session
// "delegating" for good, waiting on work nothing would ever finish.
internal sealed class BackgroundWork
{
    // Enough for any real session; a hostile transcript cannot grow these without bound.
    const int Keep = 256;
    // Task calls seen before the subagent.started they lead to (milliseconds apart).
    const int KeepCalls = 64;
    // A sub-agent's own shells: a busy one runs dozens over its life, a handful at once.
    const int KeepAgentShells = 32;
    // Start times the OS rounds (Linux reports them in clock ticks from boot).
    static readonly TimeSpan Slack = TimeSpan.FromSeconds(1);

    // What subagent.started says about the agent besides its ids and mode.
    public sealed record StartInfo(string? DisplayName = null, string? AgentType = null, string? Model = null, string? Parent = null);

    // The task call that starts an agent carries what a person gave it: its description
    // (the label) and its prompt. Parent: the agent that made the call, if not the main one.
    sealed record TaskCall(string? Description, string? Prompt, string? Parent);

    internal sealed class Agent(string id, long order)
    {
        public readonly string Id = id;
        public readonly long Order = order;
        public string? CallId;
        public string? Parent;
        public bool Background;
        public string? Name;
        public string? Task;
        public string? Model;
        public bool Working;
        public bool AskedForTools;
        public bool Completed;
        // Runs that ended before their agent_idle notification arrived. The parent takes
        // notifications when it next can, which was 90 minutes later in one transcript,
        // so a message may wake the agent first; that late notice belongs to the run
        // before and must not end the one now going.
        public int IdleOwed;
        // When the current (or latest) run started, and when the agent first did.
        public DateTimeOffset? Since;
        public DateTimeOffset? StartedAt;
        public DateTimeOffset? EndedAt;
        // Can no longer run: its own abort or failure, or the session's CLI exiting.
        public bool Ended;
        public SubagentOutcome? Outcome;
        public string? Response;
        public bool ResponseTruncated;
        public string? Reasoning;
        public string? Line;
        public readonly ToolHistory Tools = new();
        public readonly Dictionary<string, DateTimeOffset?> Shells = new(StringComparer.Ordinal);
    }

    readonly Dictionary<string, Agent> _agents = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _agentByCall = new(StringComparer.Ordinal);
    readonly Dictionary<string, TaskCall> _calls = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTimeOffset?> _shells = new(StringComparer.Ordinal);
    long _order;

    // Moves on every change a client sees in the counts or an entry, except an entry's
    // line, so a parser can tell whether an event changed what is shown.
    public int Version { get; private set; }

    // Every id held, for checking that a long session cannot grow it without bound.
    internal int Tracked => _agents.Count + _agentByCall.Count + _calls.Count + _shells.Count;

    public void Clear()
    {
        _agents.Clear();
        _agentByCall.Clear();
        _calls.Clear();
        _shells.Clear();
        Version++;
    }

    // A task tool call, made by the main agent or (stamped) by a sub-agent: the label and
    // prompt for the agent it is about to start.
    public void TaskCalled(string? callId, string? starter, JsonElement? arguments)
    {
        if (callId is null) { return; }
        var args = arguments is { ValueKind: JsonValueKind.Object } a ? a : default;
        if (_calls.Count >= KeepCalls) { _calls.Remove(_calls.Keys.First()); }
        _calls[callId] = new TaskCall(Text.NonBlank(Json.Str(args, "description")), Text.NonBlank(Json.Str(args, "prompt")), starter);
    }

    // A start seen only in the skimmed part of the transcript: the agent is worth knowing
    // (its later events may be in the parsed window, and a finished one stays listed), but
    // whether it is still working is for those events to say.
    public void KnowMode(string? callId, string? agentId, bool background, StartInfo? info = null, DateTimeOffset? at = null)
    {
        var agent = Entry(callId, agentId, background, info);
        if (agent is not null) { agent.StartedAt ??= at; agent.Since ??= at; }
    }

    public void AgentStarted(string? callId, string? agentId, DateTimeOffset? at, bool background = true, StartInfo? info = null)
    {
        if (Entry(callId, agentId, background, info) is not { } agent) { return; }
        agent.StartedAt ??= at;
        Run(agent, at);
    }

    Agent? Entry(string? callId, string? agentId, bool background, StartInfo? info)
    {
        var key = agentId ?? callId;
        if (key is null) { return null; }
        if (callId is not null)
        {
            // One agent id started under ever new call ids would otherwise grow this for good.
            if (!_agentByCall.ContainsKey(callId) && _agentByCall.Count >= 2 * Keep) { _agentByCall.Remove(_agentByCall.Keys.First()); }
            _agentByCall[callId] = key;
        }
        if (!_agents.TryGetValue(key, out var agent)) { agent = Add(key); }
        var call = callId is not null && _calls.Remove(callId, out var c) ? c : null;
        var name = Text.Title(call?.Description ?? info?.DisplayName ?? info?.AgentType);
        var model = Limits.KeepStart(Text.NonBlank(info?.Model), Limits.SubagentName);
        var changed = agent.Background != background || (name is not null && agent.Name != name)
            || (model is not null && agent.Model != model);
        agent.CallId = callId ?? agent.CallId;
        agent.Background = background;
        agent.Parent = Limits.KeepStart(info?.Parent ?? call?.Parent, Limits.SubagentName) ?? agent.Parent;
        agent.Name = name ?? agent.Name;
        // Kept cut: an entry lives as long as its session, and a transcript line can be huge.
        agent.Task = Limits.KeepStart(call?.Prompt, Limits.SubagentTask) ?? agent.Task;
        agent.Model = model ?? agent.Model;
        if (changed) { Version++; }
        return agent;
    }

    // The agent's own prompt, turn or tool call: it is working, again if it had stopped
    // (a message sent to an idle agent writes no second subagent.started).
    public void AgentActive(string agentId, DateTimeOffset? at)
    {
        if (_agents.TryGetValue(agentId, out var agent) && !agent.Working) { Run(agent, at); }
    }

    // A message stamped with the agent's id: the first is the prompt it was started with,
    // for an agent whose task call was outside the window.
    public void AgentPrompt(string agentId, string? text)
    {
        if (_agents.TryGetValue(agentId, out var agent) && agent.Task is null && Text.NonBlank(text) is { } t) { agent.Task = Limits.KeepStart(t, Limits.SubagentTask); }
    }

    public void AgentModel(string agentId, string? model)
    {
        if (!_agents.TryGetValue(agentId, out var agent) || Limits.KeepStart(Text.NonBlank(model), Limits.SubagentName) is not { } m || agent.Model == m) { return; }
        agent.Model = m;
        Version++;
    }

    // What the agent said and thought. Its line is the latest of either; a line changing
    // moves nothing (subagents.md §5), so busy delegates do not flood upserts.
    public void AgentSaid(string agentId, string? content, string? reasoning, string? model)
    {
        if (!_agents.TryGetValue(agentId, out var agent)) { return; }
        AgentModel(agentId, model);
        if (Text.NonBlank(reasoning) is { } r) { agent.Reasoning = Limits.KeepEnd(r, Limits.Reasoning).Text; agent.Line = LineOf(r); }
        if (Text.NonBlank(content) is { } c)
        {
            (agent.Response, agent.ResponseTruncated) = Limits.KeepEnd(c, Limits.Response);
            agent.Line = LineOf(c);
        }
    }

    // Cut once as it arrives, not on every build: an entry is rebuilt each pass for as long as
    // the session lives, and a reply can be 16 KiB. Some slack before collapsing whitespace
    // keeps a run of it from leaving the line short.
    static string? LineOf(string text) =>
        Limits.KeepEnd(Collapse(Limits.KeepEnd(text, 4 * Limits.SubagentLine).Text), Limits.SubagentLine).Text;

    // The turn's latest reply decides whether it goes on to run tools, as for the session
    // itself (#104): a reply asking for tools, then their results, then a final reply with
    // nothing more to run is a turn that ends its run. Kept from the first reply, the
    // agent read as running past its turn_end until the next agent_idle (#84).
    public void AgentAskedForTools(string agentId, bool asked = true)
    {
        if (_agents.TryGetValue(agentId, out var agent)) { agent.AskedForTools = asked; }
    }

    // As for the session itself: a turn that asked for tools is a round, followed by the
    // next turn reading their results; one that asked for none is the end of its work.
    public void AgentTurnEnded(string agentId, DateTimeOffset? at)
    {
        if (!_agents.TryGetValue(agentId, out var agent)) { return; }
        if (!agent.AskedForTools) { EndRun(agent, at, SubagentOutcome.Done); }
        agent.AskedForTools = false;
    }

    // The agent's own abort or shutdown: its run is over, and nothing else of the session's.
    // An abort failed it even if no run of it was seen going (its start only skimmed, or
    // between runs); a shutdown failed only a run it cut short.
    public void AgentEnded(string agentId, DateTimeOffset? at, bool aborted = true)
    {
        if (!_agents.TryGetValue(agentId, out var agent)) { return; }
        EndRun(agent, at, SubagentOutcome.Failed);
        if (aborted && agent.Outcome != SubagentOutcome.Failed) { agent.Outcome = SubagentOutcome.Failed; agent.EndedAt = at ?? agent.EndedAt; Version++; }
        Finish(agent);
    }

    // subagent.failed, by the task call that started it (stamped with its id since 1.0.10x).
    public void AgentFailed(string? callId, string? agentId, DateTimeOffset? at)
    {
        var key = agentId ?? (callId is not null && _agentByCall.TryGetValue(callId, out var k) ? k : null);
        if (key is null || !_agents.TryGetValue(key, out var agent)) { return; }
        EndRun(agent, at, SubagentOutcome.Failed);
        if (agent.Outcome != SubagentOutcome.Failed) { agent.Outcome = SubagentOutcome.Failed; agent.EndedAt = at ?? agent.EndedAt; Version++; }
        Finish(agent);
    }

    // agent_idle: spent on the oldest run still owed one; only a notice no run is owed
    // ends the current run (one whose end its own events never showed).
    public void AgentIdle(string? agentId, DateTimeOffset? at = null)
    {
        if (agentId is null || !_agents.TryGetValue(agentId, out var agent)) { return; }
        if (agent.IdleOwed > 0) { agent.IdleOwed--; return; }
        if (!agent.Working) { return; }
        Stop(agent, at, SubagentOutcome.Done);
    }

    // Written once, after the agent's first run; written again for every agent as the
    // session shuts down, which session.shutdown itself settles. Only the first ends a run.
    public void AgentCompleted(string? callId, DateTimeOffset? at = null)
    {
        if (callId is null || !_agentByCall.TryGetValue(callId, out var key) || !_agents.TryGetValue(key, out var agent) || agent.Completed) { return; }
        agent.Completed = true;
        EndRun(agent, at, SubagentOutcome.Done);
    }

    // The task call that started a task (sync) agent has returned: the agent is done, and
    // how the call went is how it went. A background agent's call returns at once, saying
    // only that it started.
    public void TaskCallDone(string? callId, bool succeeded, DateTimeOffset? at)
    {
        if (callId is null || !_agentByCall.TryGetValue(callId, out var key) || !_agents.TryGetValue(key, out var agent) || agent.Background) { return; }
        var outcome = succeeded ? SubagentOutcome.Done : SubagentOutcome.Failed;
        EndRun(agent, at, outcome);
        if (!succeeded && agent.Outcome != outcome) { agent.Outcome = outcome; Version++; }
        Finish(agent);
    }

    // The main agent's turn was interrupted: a task agent is part of that turn, so it went
    // with it. Background agents carry on.
    public void MainAborted(DateTimeOffset? at)
    {
        foreach (var agent in _agents.Values.Where(a => !a.Background && a.Working).ToList())
        {
            EndRun(agent, at, SubagentOutcome.Failed);
            Finish(agent);
        }
    }

    public void AgentToolStarted(string agentId, string id, string name, string? summary, DateTimeOffset at)
    {
        if (_agents.TryGetValue(agentId, out var agent)) { agent.Tools.Start(id, name, summary, at); }
    }

    public void AgentToolEnded(string agentId, string? id, bool succeeded, DateTimeOffset? at)
    {
        if (_agents.TryGetValue(agentId, out var agent)) { agent.Tools.End(id, succeeded, at); }
    }

    // A sub-agent's shells are its own work, counted on its entry (agent-ha-bridge#261):
    // their ends come back in its own tool results, never to the session.
    public void AgentShell(string agentId, string? started, string? ended, DateTimeOffset? at)
    {
        if (!_agents.TryGetValue(agentId, out var agent)) { return; }
        if (ended is not null && agent.Shells.Remove(ended)) { Version++; }
        if (started is null) { return; }
        if (!agent.Shells.ContainsKey(started))
        {
            if (agent.Shells.Count >= KeepAgentShells) { agent.Shells.Remove(agent.Shells.OrderBy(p => p.Value ?? DateTimeOffset.MinValue).First().Key); }
            Version++;
        }
        agent.Shells[started] = at;
    }

    void Run(Agent agent, DateTimeOffset? at)
    {
        agent.Working = true;
        agent.Since = at;
        agent.Ended = false;
        agent.Outcome = null;
        agent.EndedAt = null;
        agent.AskedForTools = false;
        Version++;
    }

    void EndRun(Agent agent, DateTimeOffset? at, SubagentOutcome outcome)
    {
        if (!agent.Working) { return; }
        Stop(agent, at, outcome);
        agent.IdleOwed++;
    }

    void Stop(Agent agent, DateTimeOffset? at, SubagentOutcome outcome)
    {
        agent.Working = false;
        agent.Outcome = outcome;
        agent.EndedAt = at ?? agent.EndedAt;
        agent.AskedForTools = false;
        Version++;
    }

    // It can never run again, and nothing it left running will report back.
    void Finish(Agent agent)
    {
        if (agent.Ended) { return; }
        agent.Ended = true;
        agent.Shells.Clear();
        agent.Tools.FailRunning(agent.EndedAt);
        Version++;
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

    public void ForgetShells()
    {
        var any = _shells.Count > 0;
        _shells.Clear();
        foreach (var a in _agents.Values.Where(a => a.Shells.Count > 0)) { a.Shells.Clear(); any = true; }
        if (any) { Version++; }
    }

    // The session's CLI shut down: everything it was running went with it. An agent cut off
    // mid-run failed; one between runs had finished its work.
    public void EndAll(DateTimeOffset? at = null)
    {
        var any = _shells.Count > 0;
        _shells.Clear();
        foreach (var a in _agents.Values)
        {
            if (a.Working) { Stop(a, at, SubagentOutcome.Failed); any = true; }
            a.IdleOwed = 0;
            if (!a.Ended) { Finish(a); any = true; }
        }
        if (any) { Version++; }
    }

    bool Live(DateTimeOffset? since, DateTimeOffset? processStartedAt) =>
        since is null || processStartedAt is not { } start || since >= start - Slack;

    // What is still running and could still report back to the session's current process.
    public (int Agents, int Shells) Running(DateTimeOffset? processStartedAt) =>
        (_agents.Values.Count(a => a.Background && a.Working && Live(a.Since, processStartedAt)), _shells.Values.Count(s => Live(s, processStartedAt)));

    // The entries as listed: running first (newest start first), then the most recently
    // finished, at most Limits.Subagents, with the number left out. Waiting: the agents with
    // a question pending.
    public (IReadOnlyList<Subagent>? List, int? More) Entries(DateTimeOffset? processStartedAt, IReadOnlySet<string> waiting)
    {
        if (_agents.Count == 0) { return (null, null); }
        var all = _agents.Values.Select(a => (Agent: a, Entry: ToRecord(a, processStartedAt, waiting)))
            .OrderBy(p => p.Entry.Finished)
            .ThenByDescending(p => p.Entry.Finished ? p.Entry.EndedAt ?? p.Agent.StartedAt : p.Agent.Since ?? p.Agent.StartedAt)
            .ThenByDescending(p => p.Agent.Order)
            .Select(p => p.Entry)
            .ToList();
        var listed = all.Take(Limits.Subagents).ToList();
        return (listed, all.Count > listed.Count ? all.Count - listed.Count : null);
    }

    // session.get { id, agent }: the entry with its own words and tools.
    public Subagent? Detail(string agentId, DateTimeOffset? processStartedAt, IReadOnlySet<string> waiting)
    {
        if (!_agents.TryGetValue(agentId, out var a)) { return null; }
        return ToRecord(a, processStartedAt, waiting) with
        {
            Response = a.Response,
            ResponseTruncated = a.ResponseTruncated ? true : null,
            Reasoning = a.Reasoning,
            History = a.Tools.Count > 0 ? a.Tools.Latest(Limits.History) : null,
        };
    }

    Subagent ToRecord(Agent a, DateTimeOffset? processStartedAt, IReadOnlySet<string> waiting)
    {
        // A run its CLI did not outlive: nothing will say how it ended.
        var stale = a.Working && !Live(a.Since, processStartedAt);
        var status = stale ? SubagentStatus.Ended
            : waiting.Contains(a.Id) ? SubagentStatus.Waiting
            : a.Working ? SubagentStatus.Working
            : a.Ended || !a.Background ? SubagentStatus.Ended
            : SubagentStatus.Idle;
        var running = status is SubagentStatus.Working or SubagentStatus.Waiting;
        return new Subagent
        {
            Id = a.Id,
            Parent = a.Parent,
            Kind = a.Background ? SubagentKind.Background : SubagentKind.Task,
            Name = Limits.KeepStart(a.Name, Limits.SubagentName),
            Task = Limits.KeepStart(a.Task, Limits.SubagentTask),
            Model = a.Model,
            Status = status,
            Outcome = running || stale ? null : a.Outcome,
            StartedAt = a.StartedAt,
            EndedAt = running || stale ? null : a.EndedAt,
            Line = a.Line,
            CurrentTool = running ? a.Tools.Current() : null,
            Shells = running ? a.Shells.Values.Count(s => Live(s, processStartedAt)) : 0,
        };
    }

    static string? Collapse(string? text) =>
        text is null ? null : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    Agent Add(string key)
    {
        if (_agents.Count >= Keep)
        {
            // Forget an agent that has finished; failing that, the one working longest.
            var drop = _agents.Values.Where(a => !a.Working).OrderBy(a => a.Order).FirstOrDefault()?.Id
                ?? _agents.Values.OrderBy(a => a.Since ?? DateTimeOffset.MinValue).First().Id;
            _agents.Remove(drop);
            foreach (var call in _agentByCall.Where(p => p.Value == drop).Select(p => p.Key).ToList()) { _agentByCall.Remove(call); }
            Version++;
        }
        var agent = new Agent(key, _order++);
        _agents[key] = agent;
        return agent;
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
