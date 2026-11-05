using AgentBridge.Host.Core.Parsing;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Conversation;

// One turn as a history reader builds it from its records, in transcript order: a prompt, then
// parts (docs/protocol/v1-history.md, Turn). A dialect says what each record adds; this keeps the
// ids, ties a tool's or a question's end to its start, and applies the per-turn caps.
internal sealed class TurnBuilder(string id, DateTimeOffset at, HistoryPrompt? prompt)
{
    readonly List<HistoryPart> _parts = [];
    readonly Dictionary<string, int> _tools = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _questions = new(StringComparer.Ordinal);
    // The parts that may still be open, by index, so an abort or a delegate's end settles them
    // without visiting every part: rescanning them all, 2,000 responses and 2,000 aborts were four
    // million visits (review of #686). An index is dropped once settled, so each is visited once.
    readonly List<int> _running = [];
    readonly List<int> _unanswered = [];
    readonly Dictionary<string, List<int>> _askedBy = new(StringComparer.Ordinal);
    string _record = id;
    int _index;

    public string Id { get; } = id;
    public DateTimeOffset At { get; set; } = at;
    public HistoryPrompt? Prompt { get; } = prompt;
    public bool Unreadable { get; init; }
    // Something in the turn was not read: a line that was not JSON, or bytes of a long turn skipped.
    public bool Truncated { get; set; }

    // The record the next parts come from; their ids are its id and their place in it.
    public void Record(string recordId)
    {
        _record = recordId;
        _index = 0;
    }

    HistoryPart Add(HistoryPart part)
    {
        var p = part with { Id = TurnIds.Part(_record, _index++) };
        _parts.Add(p);
        return p;
    }

    static HistoryPart Blank(HistoryPartKind kind, DateTimeOffset at) => new() { Id = "", Kind = kind, At = at };

    public void Response(string text, DateTimeOffset at, string? message = null)
    {
        // One message written in pieces (Claude's blocks, several entries sharing a message id) is
        // one response, gathered here and cut to its cap as it grows: copying the whole of it for
        // every piece was quadratic in a record of many small blocks (found in review of #292).
        if (message is not null && _message is not null && _parts.Count - 1 == _messagePart && _lastMessage == message)
        {
            _message.Append("\n\n").Append(text);
            Bound();
            return;
        }
        Flush();
        Add(Blank(HistoryPartKind.Response, at) with { Text = text });
        _lastMessage = message;
        if (message is not null)
        {
            _message = new System.Text.StringBuilder(text);
            _messagePart = _parts.Count - 1;
            Bound();
        }
    }

    string? _lastMessage;
    System.Text.StringBuilder? _message;
    int _messagePart = -1;
    bool _messageCut;

    // Only the response's end is sent (HistoryLimits.Response bytes, so at most that many chars):
    // once twice that is held, the start goes, and each char is copied a bounded number of times.
    void Bound()
    {
        var keep = HistoryLimits.Response;
        if (_message!.Length <= 2 * keep) { return; }
        var from = _message.Length - keep;
        // Never split a surrogate pair.
        if (char.IsLowSurrogate(_message[from])) { from--; }
        _message.Remove(0, from);
        _messageCut = true;
    }

    // The gathered message into its part, cut flag and all.
    void Flush()
    {
        if (_message is null) { return; }
        _parts[_messagePart] = _parts[_messagePart] with { Text = _message.ToString(), Truncated = _messageCut ? true : null };
        (_message, _messagePart, _messageCut) = (null, -1, false);
    }

    public void Reasoning(string text, DateTimeOffset at) => Add(Blank(HistoryPartKind.Reasoning, at) with { Text = text });

    // A reply or reasoning an agent writes in two streams (Codex's events and its response items,
    // and the final reply again at the turn's end): the same text already in the turn is not
    // added twice.
    public void Said(HistoryPartKind kind, string text, DateTimeOffset at)
    {
        if (!_said.Add((kind, text))) { return; }
        Add(Blank(kind, at) with { Text = text });
    }

    readonly HashSet<(HistoryPartKind, string)> _said = [];

    public void ToolStarted(string callId, string name, string? summary, DateTimeOffset at)
    {
        // Seen twice (Codex writes a call in both its streams): the first is kept.
        if (_tools.ContainsKey(callId)) { return; }
        Add(Blank(HistoryPartKind.Tool, at) with { Tool = new ToolCall { Id = callId, Name = name, Summary = summary, Status = ToolStatus.Running, StartedAt = at } });
        _tools[callId] = _parts.Count - 1;
        _running.Add(_parts.Count - 1);
    }

    // The first report of a call's end wins, as in the tool history.
    public void ToolEnded(string? callId, bool succeeded, DateTimeOffset? at)
    {
        if (callId is null || !_tools.TryGetValue(callId, out var i) || _parts[i].Tool is not { Status: ToolStatus.Running } tool) { return; }
        _parts[i] = _parts[i] with { Tool = tool with { Status = succeeded ? ToolStatus.Done : ToolStatus.Failed, EndedAt = at ?? tool.EndedAt } };
    }

    public bool HasTool(string callId) => _tools.ContainsKey(callId);

    public void Asked(Question question, DateTimeOffset at)
    {
        if (_questions.ContainsKey(question.Id)) { return; }
        Add(Blank(HistoryPartKind.Question, at) with { Question = question, Outcome = QuestionOutcome.Unknown });
        _questions[question.Id] = _parts.Count - 1;
        _unanswered.Add(_parts.Count - 1);
        if (question.Agent is { } agent)
        {
            if (!_askedBy.TryGetValue(agent, out var asked)) { _askedBy[agent] = asked = []; }
            asked.Add(_parts.Count - 1);
        }
    }

    // How a question ended. Only the first outcome counts: an approval's tool call starting after
    // it was approved says nothing more.
    public void Resolved(string? questionId, QuestionOutcome outcome, DateTimeOffset? at, string? answer = null)
    {
        if (questionId is null || !_questions.TryGetValue(questionId, out var i) || _parts[i].Outcome != QuestionOutcome.Unknown) { return; }
        var said = outcome == QuestionOutcome.Answered ? Text.NonBlank(answer) : null;
        var kept = Limits.KeepStart(said, HistoryLimits.Answer);
        _parts[i] = _parts[i] with
        {
            Outcome = outcome,
            AnsweredAt = at,
            Answer = kept,
            // A cut answer says so, as a cut text does: the turn is then truncated too.
            Truncated = kept?.Length < said?.Length ? true : _parts[i].Truncated,
        };
    }

    public bool Pending(string questionId) => _questions.TryGetValue(questionId, out var i) && _parts[i].Outcome == QuestionOutcome.Unknown;

    // A delegate stopped for good (its abort, shutdown or failure): what it was asking is asked of
    // no one, as the live question goes with it.
    public void Withdrawn(string agent, DateTimeOffset at)
    {
        if (!_askedBy.Remove(agent, out var asked)) { return; }
        foreach (var i in asked)
        {
            if (_parts[i] is { Outcome: QuestionOutcome.Unknown } p) { _parts[i] = p with { Outcome = QuestionOutcome.Cancelled, AnsweredAt = at }; }
        }
    }

    public void Delegate(DelegateEvent e, DateTimeOffset at) => Add(Blank(HistoryPartKind.Delegate, at) with { Delegate = e });

    // The main agent's turn failed (a session.error that ends it, SessionState.FailTurn, #857): its
    // own tools and questions end with it; a delegate's question is still the person's, as live.
    public void Failed(DateTimeOffset at)
    {
        foreach (var i in _running)
        {
            if (_parts[i].Tool is { Status: ToolStatus.Running } tool) { _parts[i] = _parts[i] with { Tool = tool with { Status = ToolStatus.Failed, EndedAt = at } }; }
        }
        _running.Clear();
        _unanswered.RemoveAll(i =>
        {
            if (_parts[i] is not { Outcome: QuestionOutcome.Unknown } p) { return true; }
            if (p.Question?.Agent is not null) { return false; }
            _parts[i] = p with { Outcome = QuestionOutcome.Cancelled, AnsweredAt = at };
            return true;
        });
    }

    // The turn was interrupted or the agent shut down: nothing is running and nothing is asked any more.
    public void Abandoned(DateTimeOffset? at)
    {
        foreach (var i in _running)
        {
            if (_parts[i].Tool is { Status: ToolStatus.Running } tool) { _parts[i] = _parts[i] with { Tool = tool with { Status = ToolStatus.Failed, EndedAt = at } }; }
        }
        foreach (var i in _unanswered)
        {
            if (_parts[i] is { Outcome: QuestionOutcome.Unknown } p) { _parts[i] = p with { Outcome = QuestionOutcome.Cancelled, AnsweredAt = at }; }
        }
        _running.Clear();
        _unanswered.Clear();
        _askedBy.Clear();
    }

    // The turn as sent. latest: no prompt follows it, so what is running may still be, and what
    // is asked may still be answered; in an earlier turn neither can.
    public HistoryTurn Build(bool latest)
    {
        if (Unreadable) { return new HistoryTurn { Id = Id, At = At, Unreadable = true }; }
        Flush();
        if (!latest) { Abandoned(null); }
        var parts = new List<HistoryPart>(_parts.Count);
        foreach (var p in _parts)
        {
            parts.Add(p.Kind switch
            {
                HistoryPartKind.Response => Cut(p, Limits.KeepEnd(p.Text, HistoryLimits.Response)),
                HistoryPartKind.Reasoning => Cut(p, Limits.KeepEnd(p.Text, HistoryLimits.Reasoning)),
                _ => p,
            });
        }
        var more = 0;
        more += Cap(parts, HistoryPartKind.Tool, HistoryLimits.ToolsPerTurn);
        more += Cap(parts, HistoryPartKind.Delegate, HistoryLimits.DelegatesPerTurn);
        more += Cap(parts, HistoryPartKind.Question, HistoryLimits.QuestionsPerTurn);
        more += Cap(parts, HistoryPartKind.Response, HistoryLimits.TextsPerTurn);
        more += Cap(parts, HistoryPartKind.Reasoning, HistoryLimits.TextsPerTurn);
        // A question cut to fit (QuestionMapper) is as much a cut as a cut part (found in review of #292).
        var truncated = Truncated || more > 0 || parts.Any(p => p.Truncated == true || p.Question?.Truncated == true) || Prompt?.Truncated == true;
        var turn = new HistoryTurn
        {
            Id = Id,
            At = At,
            Prompt = Prompt,
            Parts = parts,
            PartsMore = more > 0 ? more : null,
            Truncated = truncated ? true : null,
            Unreadable = Unreadable ? true : null,
        };
        return HistoryBudget.Fit(turn);
    }

    static HistoryPart Cut(HistoryPart p, (string? Text, bool Truncated) cut) =>
        p with { Text = cut.Text, Truncated = cut.Truncated || p.Truncated == true ? true : null };

    // Keeps the latest `cap` parts of a kind, and says how many it left out.
    static int Cap(List<HistoryPart> parts, HistoryPartKind kind, int cap)
    {
        var over = parts.Count(p => p.Kind == kind) - cap;
        if (over <= 0) { return 0; }
        var left = over;
        parts.RemoveAll(p => p.Kind == kind && left-- > 0);
        return over;
    }
}
