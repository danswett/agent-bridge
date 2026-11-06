using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Parsing;

// What a transcript has said so far, accumulated event by event. Text is kept whole here
// and cut to the contract's limits only when a Session is built.
internal sealed class SessionState
{
    public string? FirstUserMessage;
    // A title the agent generated itself (Claude's ai-title), better than the first message.
    public string? AgentTitle;
    public string? UserMessage;
    // Who wrote UserMessage when it was not the person, and its label (activity.userMessageFrom,
    // #489). Only Copilot's transcripts say so; null is the person.
    public UserMessageFrom? UserMessageFrom;
    public string? UserMessageLabel;
    // The record UserMessage came from (Copilot's event id, Codex's time), where the parser knows
    // it: the same words sent twice are two prompts, and a driver tells them apart by this.
    public string? UserMessageKey;
    // The latest text, from whichever agent wrote it last, as older clients have it.
    public string? Response;
    public string? Reasoning;
    // The sub-agent that wrote Response or Reasoning, null for the main agent's own.
    public string? ResponseBy;
    public string? ReasoningBy;
    // The main agent's own latest text, and when it wrote it: activity.own.
    public string? OwnResponse;
    // OwnResponse was cut as the skim took it: kept bounded, since only the skim holds it.
    public bool OwnResponseCut;
    public string? OwnReasoning;
    public DateTimeOffset? OwnAt;
    public string? Model;
    public string? Cwd;
    public string? Branch;
    public string? Repository;
    public DateTimeOffset? StartedAt;
    public DateTimeOffset? UpdatedAt;
    // The newest entry that is the session's own work (a prompt, a reply, a tool result),
    // so a hook's status can be told apart from the transcript tail it came after.
    public DateTimeOffset? LastActivityAt;
    // The newest event of any kind, however little it changes (a model call, a hook, a delegate's
    // tool): a session still writing these is busy, not stalled (the Not responding watchdog).
    public DateTimeOffset? LastEventAt;
    // Prompts the user sent; turnCount is these less the one still in progress.
    public int Prompts;
    // When the newest of them was sent, by its own record: a restore tells a prompt after it by this.
    public DateTimeOffset? LastPromptAt;
    // The turn in progress is not a prompt's (Claude's turn for a delegate's notice), so no
    // prompt is still in progress.
    public bool UncountedTurn;
    public int Compactions;
    public bool Seen;

    public SessionStatus Status = SessionStatus.Idle;
    public DateTimeOffset? StatusSince;

    public readonly ToolHistory Tools = new();
    // Work started in the background that outlives the turn (Copilot's agents and shells).
    public readonly BackgroundWork Background = new();
    // Questions the agent is waiting on, oldest first; the newest is the one shown.
    public readonly List<Question> Pending = [];
    // A restored session's boundary (#780): a question asked before it is the interrupted run's,
    // which went with it, so it neither shows nor makes the session waiting. Set by the session
    // source; not the transcript's, so a reset keeps it.
    public DateTimeOffset? AskedFrom;
    // What is asking now: Pending, less what the interrupted run left.
    public List<Question> Asking => AskedFrom is { } from ? Pending.Where(q => q.AskedAt >= from).ToList() : Pending;
    // session.attention (#800): what went wrong that the status cannot say. An error lasts until
    // the next turn, a sign-in until the model is seen answering (CopilotParser.Answered).
    public Attention? Attention;

    public SessionStatus Effective => (AskedFrom is { } from ? Pending.Any(q => q.AskedAt >= from) : Pending.Count > 0) ? SessionStatus.Waiting : Status;
    public DateTimeOffset? EffectiveSince;

    public void Reset()
    {
        FirstUserMessage = null;
        AgentTitle = null; UserMessage = null; UserMessageFrom = null; UserMessageLabel = null; UserMessageKey = null; Response = null; Reasoning = null; Model = null;
        ResponseBy = null; ReasoningBy = null; OwnResponse = null; OwnResponseCut = false; OwnReasoning = null; OwnAt = null;
        Cwd = null; Branch = null; Repository = null; StartedAt = null; UpdatedAt = null; LastActivityAt = null; LastEventAt = null;
        Prompts = 0; LastPromptAt = null; Compactions = 0; Seen = false; UncountedTurn = false;
        Status = SessionStatus.Idle; StatusSince = null; EffectiveSince = null;
        Tools.Clear();
        Background.Clear();
        Pending.Clear();
        Attention = null;
    }

    // Called after every event, with its time, so StatusSince is when the change happened
    // in the transcript rather than when the host noticed it.
    public void Observe(DateTimeOffset? at, SessionStatus before, bool visible)
    {
        Seen = true;
        if (at is { } heard && (LastEventAt is null || heard > LastEventAt)) { LastEventAt = heard; }
        if (!visible) { return; }
        if (at is { } t && (UpdatedAt is null || t > UpdatedAt)) { UpdatedAt = t; }
        if (Effective != before || EffectiveSince is null) { EffectiveSince = at ?? EffectiveSince; }
    }

    public void SetStatus(SessionStatus status) => Status = status;

    public void Activity(DateTimeOffset? at)
    {
        if (at is { } t && (LastActivityAt is null || t > LastActivityAt)) { LastActivityAt = t; }
    }

    public void Ask(Question question)
    {
        Pending.RemoveAll(q => q.Id == question.Id);
        Pending.Add(question);
    }

    public bool Answer(string? id) => id is not null && Pending.RemoveAll(q => q.Id == id) > 0;

    // What the main agent wrote: the latest text, and its own.
    public void MainSaid(string? content, string? reasoning, DateTimeOffset? at)
    {
        if (reasoning is not null) { Reasoning = OwnReasoning = reasoning; ReasoningBy = null; OwnAt = at ?? OwnAt; }
        if (content is not null) { Response = OwnResponse = content; OwnResponseCut = false; ResponseBy = null; OwnAt = at ?? OwnAt; }
    }

    // What a sub-agent wrote: the latest text still, as on agent-ha-bridge's card, now
    // attributed, so a newer client can show the main agent's own beside it.
    public void SubagentSaid(string agentId, string? content, string? reasoning)
    {
        if (reasoning is not null) { Reasoning = reasoning; ReasoningBy = agentId; }
        if (content is not null) { Response = content; ResponseBy = agentId; }
    }

    // A new prompt starts a new turn: last turn's reasoning would otherwise sit under its status.
    public void ForgetReasoning()
    {
        Reasoning = null;
        ReasoningBy = null;
        OwnReasoning = null;
    }

    public void Prompt(DateTimeOffset? at)
    {
        Prompts++;
        if (at is { } t && !(LastPromptAt >= t)) { LastPromptAt = t; }
    }

    // The turn ended without finishing (the user interrupted, or the agent shut down):
    // nothing is running any more and nothing is still being asked.
    public void Abandon(DateTimeOffset? at)
    {
        Tools.FailRunning(at);
        Pending.Clear();
        Status = SessionStatus.Idle;
    }

    // The main agent's turn failed (#800, a session.error): its own tools and questions end with
    // it. A background delegate may still be running, and its permission prompt is still the
    // person's to answer.
    public void FailTurn(DateTimeOffset? at)
    {
        Tools.FailRunning(at, mainOnly: true);
        Pending.RemoveAll(q => q.Agent is null);
        Status = SessionStatus.Idle;
    }
}

// The latest tool calls, oldest first. Keeps a little more than the contract shows so
// a tool finishing late still finds its entry.
internal sealed class ToolHistory
{
    const int Keep = Limits.History * 2;
    readonly List<Entry> _entries = [];
    readonly Dictionary<string, Entry> _byId = new(StringComparer.Ordinal);

    internal sealed class Entry
    {
        public required string Id;
        public required string Name;
        public string? Summary;
        public ToolStatus Status;
        public DateTimeOffset StartedAt;
        public DateTimeOffset? EndedAt;
        // The sub-agent that made the call, null for the main agent's: a permission prompt
        // names only the call it gates.
        public string? Agent;
    }

    public int Count => _entries.Count;

    public void Clear() { _entries.Clear(); _byId.Clear(); }

    public void Start(string id, string name, string? summary, DateTimeOffset at, string? agent = null)
    {
        if (_byId.TryGetValue(id, out var known))
        {
            // Seen twice (Codex writes a call in both its streams): keep the first, and
            // fill in what it lacked.
            known.Summary ??= summary;
            return;
        }
        var entry = new Entry { Id = id, Name = name, Summary = summary, Status = ToolStatus.Running, StartedAt = at, Agent = agent };
        _entries.Add(entry);
        _byId[id] = entry;
        while (_entries.Count > Keep)
        {
            _byId.Remove(_entries[0].Id);
            _entries.RemoveAt(0);
        }
    }

    // The first report of a call's end wins. Codex reports one call in both its streams,
    // and the response stream's output says nothing about success: letting it overwrite
    // a failure the event stream recorded showed failed commands as done.
    public bool End(string? id, bool succeeded, DateTimeOffset? at)
    {
        if (id is null || !_byId.TryGetValue(id, out var entry) || entry.Status != ToolStatus.Running) { return false; }
        entry.Status = succeeded ? ToolStatus.Done : ToolStatus.Failed;
        entry.EndedAt = at ?? entry.EndedAt;
        return true;
    }

    public bool IsRunning(string id) => _byId.TryGetValue(id, out var e) && e.Status == ToolStatus.Running;

    public bool AnyRunning => _entries.Any(e => e.Status == ToolStatus.Running);

    // mainOnly: the main agent's tools, leaving a live delegate's running.
    public void FailRunning(DateTimeOffset? at, bool mainOnly = false)
    {
        foreach (var e in _entries.Where(e => e.Status == ToolStatus.Running && (!mainOnly || e.Agent is null)))
        {
            e.Status = ToolStatus.Failed;
            e.EndedAt = at;
        }
    }

    public ToolCall? Current()
    {
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i].Status == ToolStatus.Running) { return ToRecord(_entries[i]); }
        }
        return null;
    }

    public Entry? Find(string id) => _byId.GetValueOrDefault(id);

    public IReadOnlyList<ToolCall> Latest(int count) => [.. _entries.Skip(Math.Max(0, _entries.Count - count)).Select(ToRecord)];

    static ToolCall ToRecord(Entry e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Summary = e.Summary,
        Status = e.Status,
        StartedAt = e.StartedAt,
        EndedAt = e.EndedAt,
    };
}
