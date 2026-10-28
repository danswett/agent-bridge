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
    public string? Response;
    public string? Reasoning;
    public string? Model;
    public string? Cwd;
    public string? Branch;
    public string? Repository;
    public DateTimeOffset? StartedAt;
    public DateTimeOffset? UpdatedAt;
    // The newest entry that is the session's own work (a prompt, a reply, a tool result),
    // so a hook's status can be told apart from the transcript tail it came after.
    public DateTimeOffset? LastActivityAt;
    // Prompts the user sent; turnCount is these less the one still in progress.
    public int Prompts;
    public int Compactions;
    public bool Seen;

    public SessionStatus Status = SessionStatus.Idle;
    public DateTimeOffset? StatusSince;

    public readonly ToolHistory Tools = new();
    // Questions the agent is waiting on, oldest first; the newest is the one shown.
    public readonly List<Question> Pending = [];

    public SessionStatus Effective => Pending.Count > 0 ? SessionStatus.Waiting : Status;
    public DateTimeOffset? EffectiveSince;

    public void Reset()
    {
        FirstUserMessage = null;
        AgentTitle = null; UserMessage = null; Response = null; Reasoning = null; Model = null;
        Cwd = null; Branch = null; Repository = null; StartedAt = null; UpdatedAt = null; LastActivityAt = null;
        Prompts = 0; Compactions = 0; Seen = false;
        Status = SessionStatus.Idle; StatusSince = null; EffectiveSince = null;
        Tools.Clear();
        Pending.Clear();
    }

    // Called after every event, with its time, so StatusSince is when the change happened
    // in the transcript rather than when the host noticed it.
    public void Observe(DateTimeOffset? at, SessionStatus before, bool visible)
    {
        Seen = true;
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

    // The turn ended without finishing (the user interrupted, or the agent shut down):
    // nothing is running any more and nothing is still being asked.
    public void Abandon(DateTimeOffset? at)
    {
        Tools.FailRunning(at);
        Pending.Clear();
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
    }

    public int Count => _entries.Count;

    public void Clear() { _entries.Clear(); _byId.Clear(); }

    public void Start(string id, string name, string? summary, DateTimeOffset at)
    {
        if (_byId.TryGetValue(id, out var known))
        {
            // Seen twice (Codex writes a call in both its streams): keep the first, and
            // fill in what it lacked.
            known.Summary ??= summary;
            return;
        }
        var entry = new Entry { Id = id, Name = name, Summary = summary, Status = ToolStatus.Running, StartedAt = at };
        _entries.Add(entry);
        _byId[id] = entry;
        while (_entries.Count > Keep)
        {
            _byId.Remove(_entries[0].Id);
            _entries.RemoveAt(0);
        }
    }

    public bool End(string? id, bool succeeded, DateTimeOffset? at)
    {
        if (id is null || !_byId.TryGetValue(id, out var entry)) { return false; }
        entry.Status = succeeded ? ToolStatus.Done : ToolStatus.Failed;
        entry.EndedAt = at ?? entry.EndedAt;
        return true;
    }

    public bool IsRunning(string id) => _byId.TryGetValue(id, out var e) && e.Status == ToolStatus.Running;

    public bool AnyRunning => _entries.Any(e => e.Status == ToolStatus.Running);

    public void FailRunning(DateTimeOffset? at)
    {
        foreach (var e in _entries.Where(e => e.Status == ToolStatus.Running))
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
