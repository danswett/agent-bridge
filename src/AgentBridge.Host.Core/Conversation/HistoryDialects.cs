using System.Text;
using System.Text.Json;
using AgentBridge.Host.Core.Parsing;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Conversation;

// What one read of a session's history knows beyond the turn it is building: the delegates it has
// seen start and what each is called, the main agent's task calls, whose tool calls are whose. One
// per request; turns are built oldest first, so a start is known before its report.
internal sealed class HistoryContext(IReadOnlyDictionary<string, string?> listed, IReadOnlyDictionary<string, string> calls, bool remoteApproval)
{
    // The main agent's own delegates, by id, with their names: those this read saw start, and those
    // the session lists now (its subagents with no parent), whose starts may be on an older page.
    readonly Dictionary<string, string?> _main = new(listed, StringComparer.Ordinal);
    readonly Dictionary<string, Run> _runs = new(StringComparer.Ordinal);
    readonly Dictionary<string, string?> _tasks = new(StringComparer.Ordinal);
    // Who made a call (a delegate's own calls), and which delegate a task call started: two maps,
    // since a nested delegate's task call is made by one delegate and starts another. The session
    // knows the second for its own delegates, so an unstamped end names the same delegate whatever
    // page its start is on.
    readonly Dictionary<string, string> _callAgent = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _callDelegate = new(calls, StringComparer.Ordinal);
    readonly Dictionary<string, string> _permissions = new(StringComparer.Ordinal);
    const int Keep = 4096;

    // A delegate's runs as the main agent hears of them within one turn, as BackgroundWork keeps
    // them live: a run ends once, whoever says so first, and a notice for a run already said to have
    // ended is owed to it, never spent on one a message has started since (a Codex wait, then its
    // notice). Within a turn only: a turn must read the same whatever page it is on, and a page
    // starting at it knows nothing of the turns before (found in review of #292).
    sealed class Run
    {
        public bool Working;
        public bool Said;
        public int Owed;
    }

    public void TurnBegins() => _runs.Clear();

    public bool RemoteApproval { get; } = remoteApproval;
    // The session's own thread (Codex), from its first record.
    public string? Thread { get; set; }

    public void TaskCalled(string callId, string? description)
    {
        if (_tasks.Count < Keep) { _tasks[callId] = description; }
    }

    public string? TaskDescription(string? callId) => callId is not null ? _tasks.GetValueOrDefault(callId) : null;

    public void CallBy(string callId, string agent)
    {
        if (_callAgent.Count < Keep) { _callAgent[callId] = agent; }
    }

    public string? AgentOfCall(string? callId) => callId is not null ? _callAgent.GetValueOrDefault(callId) : null;

    public void CallStarted(string callId, string agent)
    {
        if (_callDelegate.Count < Keep) { _callDelegate[callId] = agent; }
    }

    public string? DelegateOfCall(string? callId) => callId is not null ? _callDelegate.GetValueOrDefault(callId) : null;

    public void Permission(string requestId, string questionId)
    {
        if (_permissions.Count < Keep) { _permissions[requestId] = questionId; }
    }

    public string? QuestionOf(string? requestId) => requestId is not null ? _permissions.GetValueOrDefault(requestId) : null;

    public bool IsMain(string agent) => _main.ContainsKey(agent);

    public void Started(TurnBuilder turn, string agent, string? name, SubagentKind kind, DateTimeOffset at)
    {
        if (_main.Count < Keep || _main.ContainsKey(agent)) { _main[agent] = name ?? _main.GetValueOrDefault(agent); }
        if (RunOf(agent) is { } run) { (run.Working, run.Said, run.Owed) = (true, false, 0); }
        turn.Delegate(new DelegateEvent { Agent = agent, Event = DelegateEventKind.Started, Name = name, Kind = kind }, at);
    }

    // A delegate's run began again (woken by a message): its next end is reported again.
    public void Woken(string agent)
    {
        if (_main.ContainsKey(agent) && RunOf(agent) is { Working: false } run) { (run.Working, run.Said) = (true, false); }
    }

    // A delegate's run ended, said by the run's own end (Copilot's subagent.completed or .failed, a
    // Codex wait returning its state): reported unless this run's end was already, and a notice
    // that says it again is owed to it.
    public void Ended(TurnBuilder turn, string agent, SubagentOutcome outcome, DateTimeOffset at)
    {
        if (!_main.ContainsKey(agent) || RunOf(agent) is not { } run) { return; }
        // The same end said again (a second wait on a run already reported) owes nothing more: owed,
        // it would swallow the next run's notice (found in review of #292).
        if (!run.Working && run.Said) { return; }
        Report(turn, agent, run, outcome, at);
        run.Owed++;
    }

    // A notice of a delegate's end (Copilot's agent_idle, Claude's task notification, Codex's
    // subagent notification): spent on a run already said to have ended if one is owed, else it
    // ends the run, once.
    public void Noticed(TurnBuilder turn, string agent, SubagentOutcome outcome, DateTimeOffset at)
    {
        if (!_main.ContainsKey(agent) || RunOf(agent) is not { } run) { return; }
        if (run.Owed > 0) { run.Owed--; return; }
        if (run.Working || !run.Said) { Report(turn, agent, run, outcome, at); }
    }

    void Report(TurnBuilder turn, string agent, Run run, SubagentOutcome outcome, DateTimeOffset at)
    {
        (run.Working, run.Said) = (false, true);
        // The session's name for it first, which is the same whatever page this is.
        var name = listed.TryGetValue(agent, out var known) && known is not null ? known : _main.GetValueOrDefault(agent);
        turn.Delegate(new DelegateEvent { Agent = agent, Event = DelegateEventKind.Reported, Name = name, Outcome = outcome }, at);
    }

    Run? RunOf(string agent)
    {
        if (_runs.TryGetValue(agent, out var run)) { return run; }
        if (_runs.Count >= Keep) { return null; }
        return _runs[agent] = new Run();
    }
}

// One agent's transcript as history reads it (docs/protocol/v1-history.md, Where a host gets each
// part). Which records start a turn is exactly what moves activity.userMessage in that agent's
// parser, by the same predicate, so a turn's id here is the id activity.turn gives it.
internal abstract class HistoryDialect
{
    public static HistoryDialect? For(AgentKind agent) => agent switch
    {
        AgentKind.Copilot => new CopilotHistory(),
        AgentKind.Claude => new ClaudeHistory(),
        AgentKind.Codex => new CodexHistory(),
        _ => null,
    };

    // What a line that may start a turn carries: only those are parsed while paging back.
    public abstract TurnMarks Marks { get; }

    public bool MayStart(ReadOnlySpan<byte> line) => Marks.MayStart(line);

    // Whether a parsed record starts a turn, and with what prompt.
    public abstract HistoryPrompt? Start(JsonElement e);

    // A byte search: could the line add anything to a turn? Lines that cannot are never parsed.
    public abstract bool Wants(ReadOnlySpan<byte> line);

    public abstract void Apply(JsonElement e, DateTimeOffset at, TurnBuilder turn, HistoryContext context);

    // What the transcript's first records say of the session (Codex's own thread).
    public virtual void First(JsonElement e, HistoryContext context) { }

    protected static bool Has(ReadOnlySpan<byte> line, ReadOnlySpan<byte> marker) => line.IndexOf(marker) >= 0;

    public static HistoryPrompt Prompt(string text, UserMessageFrom? from = null, string? label = null)
    {
        var kept = Limits.KeepStart(text, HistoryLimits.Prompt)!;
        return new HistoryPrompt { Text = kept, Truncated = kept.Length < text.Length ? true : null, From = from, Label = label };
    }
}

internal sealed class CopilotHistory : HistoryDialect
{
    public override TurnMarks Marks => TurnMarks.Copilot;

    public override HistoryPrompt? Start(JsonElement e) =>
        CopilotParser.TurnStart(e) is { } p ? Prompt(p.Text, p.From, p.Label) : null;

    public override bool Wants(ReadOnlySpan<byte> line) =>
        Has(line, "\"assistant.message\""u8) || Has(line, "\"tool.execution_start\""u8) || Has(line, "\"tool.execution_complete\""u8)
        || Has(line, "\"permission.requested\""u8) || Has(line, "\"permission.completed\""u8) || Has(line, "\"human_response.recorded\""u8)
        || Has(line, "\"subagent.started\""u8) || Has(line, "\"subagent.completed\""u8) || Has(line, "\"subagent.failed\""u8)
        || Has(line, "\"agent_idle\""u8) || Has(line, "\"abort\""u8) || Has(line, "\"session.shutdown\""u8) || Has(line, "\"user.message\""u8);

    public override void Apply(JsonElement e, DateTimeOffset at, TurnBuilder turn, HistoryContext context)
    {
        var data = Json.Obj(e, "data") ?? default;
        var agent = Json.Str(e, "agentId") is { Length: > 0 } a ? a : null;
        switch (Json.Str(e, "type"))
        {
            case "assistant.message" when agent is null:
                if (Text.NonBlank(Json.Str(data, "reasoningText")) is { } reasoning) { turn.Reasoning(reasoning, at); }
                if (Text.NonBlank(Json.Str(data, "content")) is { } content) { turn.Response(content, at); }
                break;
            // A delegate's first message is its prompt; a later one wakes it for another run.
            case "user.message" when agent is not null:
                context.Woken(agent);
                break;
            case "tool.execution_start":
                {
                    var id = Json.Str(data, "toolCallId");
                    var name = Json.Str(data, "toolName");
                    if (id is null || name is null) { break; }
                    var arguments = Json.Get(data, "arguments");
                    if (name == "task") { context.TaskCalled(id, arguments is { ValueKind: JsonValueKind.Object } args ? Text.NonBlank(Json.Str(args, "description")) : null); }
                    if (agent is not null)
                    {
                        // A delegate's call is its own work, not the main agent's; remembered for
                        // a permission prompt that names only the call, and for a task call's
                        // delegate being that delegate's. What it asks is still asked of the
                        // person, as live (status rule 2): the question is kept, with whose it is.
                        context.CallBy(id, agent);
                        if (name == "ask_user" && arguments is { } asked) { turn.Asked(QuestionMapper.FromCopilotAskUser(id, asked, at) with { Agent = agent }, at); }
                        break;
                    }
                    turn.ToolStarted(id, name, ToolSummary.From(arguments), at);
                    if (name == "ask_user" && arguments is { } ask) { turn.Asked(QuestionMapper.FromCopilotAskUser(id, ask, at), at); }
                    break;
                }
            // Anyone's: a delegate's call ending answers its question as the main agent's does.
            case "tool.execution_complete":
                {
                    var id = Json.Str(data, "toolCallId");
                    var failed = Json.Bool(data, "success") == false || Json.Get(data, "error") is not null;
                    turn.ToolEnded(id, !failed, at);
                    if (id is not null && turn.Pending(id))
                    {
                        var result = Json.Obj(data, "result") is { } r ? Json.Str(r, "content") : null;
                        turn.Resolved(id, failed ? QuestionOutcome.Cancelled : QuestionOutcome.Answered, at, result);
                    }
                    break;
                }
            case "human_response.recorded":
                turn.Resolved(Json.Str(data, "toolCallId"), QuestionOutcome.Answered, at, Json.Obj(data, "response") is { } response ? AnswerText(Json.Get(response, "content")) : null);
                break;
            case "permission.requested":
                {
                    if (CopilotPermission.Describe(data) is not { } prompt) { break; }
                    var by = agent ?? context.AgentOfCall(prompt.ToolCallId);
                    context.Permission(prompt.RequestId, prompt.QuestionId);
                    turn.Asked(QuestionMapper.Approval(prompt.QuestionId, prompt.Message, at, atMachine: !context.RemoteApproval || prompt.AtMachine is not null) with { Agent = by }, at);
                    break;
                }
            case "permission.completed":
                {
                    var kind = Json.Obj(data, "result") is { } result ? Json.Str(result, "kind") : null;
                    var outcome = kind is null ? QuestionOutcome.Unknown
                        : kind.StartsWith("approved", StringComparison.Ordinal) ? QuestionOutcome.Approved
                        : kind.StartsWith("denied", StringComparison.Ordinal) ? QuestionOutcome.Denied
                        : QuestionOutcome.Unknown;
                    turn.Resolved(context.QuestionOf(Json.Str(data, "requestId")), outcome, at);
                    break;
                }
            // Only the main agent's own delegates: a nested one's start names its parent, or its
            // task call was made by a delegate (the parent then is only that call's stamp).
            case "subagent.started" when agent is not null && Json.Str(data, "parentId") is null or "":
                {
                    var call = Json.Str(data, "toolCallId");
                    if (call is not null)
                    {
                        var nested = context.AgentOfCall(call) is not null;
                        context.CallStarted(call, agent);
                        if (nested) { break; }
                    }
                    var name = DelegateName.Clean(context.TaskDescription(call)) ?? DelegateName.Clean(Json.Str(data, "agentDescription"))
                        ?? DelegateName.Clean(Json.Str(data, "agentDisplayName"))
                        ?? DelegateName.Clean(Json.Str(data, "agentType") ?? Json.Str(data, "agentName"));
                    context.Started(turn, agent, name, Json.Str(data, "executionMode") == "background" ? SubagentKind.Background : SubagentKind.Task, at);
                    break;
                }
            case "subagent.completed":
                if ((agent ?? context.DelegateOfCall(Json.Str(data, "toolCallId"))) is { } done) { context.Ended(turn, done, SubagentOutcome.Done, at); }
                break;
            case "subagent.failed":
                if ((agent ?? context.DelegateOfCall(Json.Str(data, "toolCallId"))) is { } lost)
                {
                    context.Ended(turn, lost, SubagentOutcome.Failed, at);
                    turn.Withdrawn(lost, at);
                }
                break;
            case "system.notification" when Json.Obj(data, "kind") is { } kind && Json.Str(kind, "type") == "agent_idle" && Json.Str(kind, "agentId") is { Length: > 0 } idle:
                context.Noticed(turn, idle, SubagentOutcome.Done, at);
                break;
            case "abort" or "session.shutdown" when agent is null:
                turn.Abandoned(at);
                break;
            // A delegate's own abort or shutdown: what it was asking is asked of no one, as live.
            case "abort" or "session.shutdown":
                turn.Withdrawn(agent!, at);
                break;
        }
    }

    // A form's answer as Copilot recorded it: text as it is, a value per field one per line.
    static string? AnswerText(JsonElement? content)
    {
        switch (content)
        {
            case { ValueKind: JsonValueKind.String } s:
                return s.GetString();
            case { ValueKind: JsonValueKind.Object } o:
                {
                    var sb = new StringBuilder();
                    foreach (var p in o.EnumerateObject())
                    {
                        if (sb.Length > 0) { sb.Append('\n'); }
                        sb.Append(p.Name).Append(": ").Append(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText());
                        if (sb.Length > HistoryLimits.Answer) { break; }
                    }
                    return sb.ToString();
                }
            default:
                return null;
        }
    }
}

internal sealed class ClaudeHistory : HistoryDialect
{
    // A tool result is never a prompt, and is most of the user entries.
    public override TurnMarks Marks => TurnMarks.Claude;

    public override HistoryPrompt? Start(JsonElement e) => ClaudeParser.TurnStart(e) is { } text ? Prompt(text) : null;

    public override bool Wants(ReadOnlySpan<byte> line) => Has(line, "\"assistant\""u8) || Has(line, "\"user\""u8);

    public override void Apply(JsonElement e, DateTimeOffset at, TurnBuilder turn, HistoryContext context)
    {
        if (Json.Bool(e, "isSidechain") == true || Json.Bool(e, "isMeta") == true) { return; }
        var message = Json.Obj(e, "message");
        switch (Json.Str(e, "type"))
        {
            case "user":
                {
                    if (message is { } m && Json.Get(m, "content") is { ValueKind: JsonValueKind.String } s && s.GetString() is { } said)
                    {
                        if (ClaudeParser.IsNotice(e, said))
                        {
                            if (ClaudeParser.Tag(said, "task-id") is { } task)
                            {
                                context.Noticed(turn, task, ClaudeParser.Tag(said, "status") == "completed" ? SubagentOutcome.Done : SubagentOutcome.Failed, at);
                            }
                            break;
                        }
                        if (ClaudeParser.IsInterrupt(said)) { turn.Abandoned(at); }
                        break;
                    }
                    foreach (var block in Blocks(message).Where(b => Json.Str(b, "type") == "tool_result"))
                    {
                        var id = Json.Str(block, "tool_use_id");
                        var error = Json.Bool(block, "is_error") == true;
                        turn.ToolEnded(id, !error, at);
                        if (id is not null && turn.Pending(id)) { turn.Resolved(id, error ? QuestionOutcome.Cancelled : QuestionOutcome.Answered, at, ResultText(block)); }
                    }
                    if (Json.Obj(e, "toolUseResult") is { } result && Json.Str(result, "status") == "async_launched" && Text.NonBlank(Json.Str(result, "agentId")) is { } agent)
                    {
                        context.Started(turn, agent, DelegateName.Clean(Json.Str(result, "description")), SubagentKind.Background, at);
                    }
                    if (ClaudeParser.Prompt(e) is { } text && ClaudeParser.IsInterrupt(text)) { turn.Abandoned(at); }
                    break;
                }
            case "assistant":
                {
                    var key = message is { } msg ? Json.Str(msg, "id") : null;
                    foreach (var block in Blocks(message))
                    {
                        switch (Json.Str(block, "type"))
                        {
                            case "thinking" when Text.NonBlank(Json.Str(block, "thinking")) is { } thinking:
                                turn.Reasoning(thinking, at);
                                break;
                            case "text" when Text.NonBlank(Json.Str(block, "text")) is { } text:
                                turn.Response(text, at, key);
                                break;
                            case "tool_use":
                                {
                                    var id = Json.Str(block, "id");
                                    var name = Json.Str(block, "name");
                                    if (id is null || name is null) { break; }
                                    var input = Json.Get(block, "input");
                                    turn.ToolStarted(id, name, ToolSummary.From(input), at);
                                    if (name == "AskUserQuestion" && input is { } i) { turn.Asked(QuestionMapper.FromClaudeAskUserQuestion(id, i, at), at); }
                                    if (name == "SendMessage" && input is { ValueKind: JsonValueKind.Object } sent && Json.Str(sent, "to") is { } to) { context.Woken(to); }
                                    break;
                                }
                        }
                    }
                    break;
                }
        }
    }

    static string? ResultText(JsonElement block) => Json.Get(block, "content") switch
    {
        { ValueKind: JsonValueKind.String } s => s.GetString(),
        { ValueKind: JsonValueKind.Array } a => string.Join('\n', a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object && Json.Str(x, "type") == "text").Select(x => Json.Str(x, "text"))),
        _ => null,
    };

    static IEnumerable<JsonElement> Blocks(JsonElement? message)
    {
        if (message is not { } m || Json.Arr(m, "content") is not { } content) { yield break; }
        foreach (var b in content.EnumerateArray())
        {
            if (b.ValueKind == JsonValueKind.Object) { yield return b; }
        }
    }
}

internal sealed class CodexHistory : HistoryDialect
{
    public override TurnMarks Marks => TurnMarks.Codex;

    public override HistoryPrompt? Start(JsonElement e) => CodexParser.TurnStart(e) is { } text ? Prompt(text) : null;

    public override bool Wants(ReadOnlySpan<byte> line) => Has(line, "\"event_msg\""u8) || Has(line, "\"response_item\""u8);

    public override void First(JsonElement e, HistoryContext context)
    {
        if (Json.Str(e, "type") == "session_meta" && Json.Obj(e, "payload") is { } p) { context.Thread ??= Text.NonBlank(Json.Str(p, "id")); }
    }

    public override void Apply(JsonElement e, DateTimeOffset at, TurnBuilder turn, HistoryContext context)
    {
        var p = Json.Obj(e, "payload") ?? default;
        switch (Json.Str(e, "type"))
        {
            case "event_msg":
                Event(p, at, turn, context);
                break;
            case "response_item":
                switch (Json.Str(p, "type"))
                {
                    case "message" when Json.Str(p, "role") == "user":
                        Notification(p, at, turn, context);
                        break;
                    // The same reply and reasoning as the event stream's, in rollouts that write
                    // both: said once (TurnBuilder.Said).
                    case "message" when Json.Str(p, "role") == "assistant" && CodexParser.OutputText(p) is { } text:
                        turn.Said(HistoryPartKind.Response, text, at);
                        break;
                    case "reasoning" when Json.Arr(p, "summary") is { } summary:
                        foreach (var s in summary.EnumerateArray())
                        {
                            if (Text.NonBlank(s.ValueKind == JsonValueKind.String ? s.GetString() : Json.Str(s, "text")) is { } said) { turn.Said(HistoryPartKind.Reasoning, said, at); }
                        }
                        break;
                    case "web_search_call" when (Json.Str(p, "id") ?? Json.Str(p, "call_id")) is { } search:
                        Started(turn, search, "web_search", ToolSummary.From(Json.Obj(p, "action")), at);
                        turn.ToolEnded(search, true, at);
                        break;
                    case "function_call":
                        Started(turn, Json.Str(p, "call_id"), Json.Str(p, "name") ?? "function", ToolSummary.From(Json.Get(p, "arguments")), at);
                        break;
                    case "custom_tool_call":
                        Started(turn, Json.Str(p, "call_id"), Json.Str(p, "name") ?? "tool", ToolSummary.From(Json.Get(p, "input")), at);
                        break;
                    case "local_shell_call":
                        Started(turn, Json.Str(p, "call_id") ?? Json.Str(p, "id"), "shell", ToolSummary.From(Json.Obj(p, "action")), at);
                        break;
                    case "function_call_output" or "custom_tool_call_output":
                        turn.ToolEnded(Json.Str(p, "call_id"), true, at);
                        break;
                }
                break;
        }
    }

    static void Event(JsonElement p, DateTimeOffset at, TurnBuilder turn, HistoryContext context)
    {
        switch (Json.Str(p, "type"))
        {
            case "agent_message" when Text.NonBlank(Json.Str(p, "message")) is { } text:
                turn.Said(HistoryPartKind.Response, text, at);
                break;
            case "agent_reasoning" when Text.NonBlank(Json.Str(p, "text")) is { } text:
                turn.Said(HistoryPartKind.Reasoning, text, at);
                break;
            // The turn's final reply, which the live parser takes over any said before it.
            case "task_complete" when Text.NonBlank(Json.Str(p, "last_agent_message")) is { } text:
                turn.Said(HistoryPartKind.Response, text, at);
                break;
            case "turn_aborted":
                turn.Abandoned(at);
                break;
            case "exec_command_begin":
                Started(turn, Json.Str(p, "call_id"), "shell", ToolSummary.From(p), at);
                break;
            case "exec_command_end":
                turn.ToolEnded(Json.Str(p, "call_id"), Json.Int(p, "exit_code") is null or 0, at);
                break;
            case "patch_apply_begin":
                Started(turn, Json.Str(p, "call_id"), "apply_patch", CodexParser.Files(p), at);
                break;
            case "patch_apply_end":
                turn.ToolEnded(Json.Str(p, "call_id"), Json.Bool(p, "success") != false, at);
                break;
            case "mcp_tool_call_begin":
                {
                    var invocation = Json.Obj(p, "invocation") ?? default;
                    var name = string.Join('.', new[] { Json.Str(invocation, "server"), Json.Str(invocation, "tool") }.Where(x => x is not null));
                    Started(turn, Json.Str(p, "call_id"), name.Length == 0 ? "mcp" : name, ToolSummary.From(Json.Get(invocation, "arguments")), at);
                    break;
                }
            case "mcp_tool_call_end":
                turn.ToolEnded(Json.Str(p, "call_id"), Json.Obj(p, "result") is not { } r || Json.Get(r, "Err") is null, at);
                break;
            case "exec_approval_request" when Json.Str(p, "call_id") is { } id:
                turn.Asked(CodexParser.ApprovalQuestion(id, "Allow **Codex** to run a command?", Json.Str(p, "reason"), ToolSummary.From(p), at), at);
                break;
            case "apply_patch_approval_request" when Json.Str(p, "call_id") is { } id:
                turn.Asked(CodexParser.ApprovalQuestion(id, "Allow **Codex** to change files?", Json.Str(p, "reason"), CodexParser.Files(p), at), at);
                break;
            case "item_completed" when Json.Obj(p, "item") is { } item:
                switch (Json.Str(item, "type"))
                {
                    case "AgentMessage" when CodexParser.ContentText(item) is { } text:
                        turn.Said(HistoryPartKind.Response, text, at);
                        break;
                    case "Reasoning" when Json.Arr(item, "summary_text") is { } summary:
                        foreach (var s in summary.EnumerateArray())
                        {
                            if (s.ValueKind == JsonValueKind.String && Text.NonBlank(s.GetString()) is { } said) { turn.Said(HistoryPartKind.Reasoning, said, at); }
                        }
                        break;
                    case "CommandExecution":
                        {
                            var id = Json.Str(item, "id");
                            Started(turn, id, "shell", ToolSummary.From(item), at);
                            if (Json.Str(item, "status") is { } status and not "in_progress") { turn.ToolEnded(id, status == "completed" && Json.Int(item, "exit_code") is null or 0, at); }
                            break;
                        }
                    case "CollabAgentToolCall":
                        Collab(item, at, turn, context);
                        break;
                }
                break;
        }
    }

    // A call starting means the approval it waited for was given.
    static void Started(TurnBuilder turn, string? id, string name, string? summary, DateTimeOffset at)
    {
        if (id is null) { return; }
        turn.ToolStarted(id, name, summary, at);
        turn.Resolved(id, QuestionOutcome.Approved, at);
    }

    // One multi-agent call, as CodexParser.Collab reads it live: the delegates it started, woke or
    // closed, and how each stood as it returned, which is the main agent hearing how a run ended.
    static void Collab(JsonElement item, DateTimeOffset at, TurnBuilder turn, HistoryContext context)
    {
        // Only the session's own: one a sub-agent called is that sub-agent's.
        var sender = Text.NonBlank(Json.Str(item, "sender_thread_id"));
        if (sender is not null && context.Thread is not null && sender != context.Thread) { return; }
        var names = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (Json.Arr(item, "receiver_agents") is { } agents)
        {
            foreach (var a in agents.EnumerateArray())
            {
                if (a.ValueKind == JsonValueKind.Object && Text.NonBlank(Json.Str(a, "thread_id")) is { } id)
                {
                    names[id] = DelegateName.Clean(Json.Str(a, "agent_nickname")) ?? DelegateName.Clean(Json.Str(a, "agent_role"));
                }
            }
        }
        if (Json.Arr(item, "receiver_thread_ids") is { } ids)
        {
            foreach (var x in ids.EnumerateArray())
            {
                if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } id) { names.TryAdd(id, null); }
            }
        }
        // A call that failed did what it says to no one; the states it reports still hold.
        switch (Json.Str(item, "status") == "failed" ? null : Json.Str(item, "tool"))
        {
            case "spawn_agent":
                foreach (var (id, name) in names) { context.Started(turn, id, name, SubagentKind.Background, at); }
                break;
            // Input wakes a delegate for another run; its state as the call returned says
            // nothing of how that run will go.
            case "send_input" or "resume_agent" or "send_message" or "followup_task":
                foreach (var id in names.Keys) { context.Woken(id); }
                return;
            case "close_agent":
                return;
        }
        if (Json.Obj(item, "agents_states") is { } states)
        {
            foreach (var s in states.EnumerateObject())
            {
                if (CodexParser.StopOf(s.Value) is { } outcome) { context.Ended(turn, s.Name, outcome, at); }
            }
        }
    }

    static void Notification(JsonElement p, DateTimeOffset at, TurnBuilder turn, HistoryContext context)
    {
        if (CodexParser.ReadNotification(p) is { } n) { context.Noticed(turn, n.Agent, n.Outcome, at); }
    }
}
