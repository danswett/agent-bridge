using System.Text;
using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Parsing;

// A Codex rollout: ~/.codex/sessions/<yyyy>/<MM>/<dd>/rollout-<time>-<id>.jsonl, one
// {timestamp, type, payload} per line. Two streams describe each turn: `event_msg`
// (what the UI saw) and `response_item` (the durable model record), and Codex has
// written both an older flat event set (user_message, agent_message, agent_reasoning,
// exec_command_begin/end, exec_approval_request) and a newer item_completed one
// (UserMessage, AgentMessage, Reasoning with summary_text, CommandExecution). Both are
// read; a tool call seen in both streams is one call, by its call id.
//
// Status: task_started is working, task_complete and turn_aborted are idle, and a
// pending approval request is waiting until that call starts, the turn ends or the user
// speaks. Reasoning is encrypted unless the user asked for summaries, so its absence is
// normal. Response-stream user messages are skipped: they include the environment
// context and instructions Codex injects, not what the user typed.
//
// Sub-agents (docs/design/subagents.md §3), as Codex CLI 0.166.0 (multi_agent v1) records
// them in the session's own rollout (tests/fixtures/codex): an item_completed of type
// CollabAgentToolCall for each multi-agent tool the session calls, naming the threads it
// acted on (receiver_thread_ids, receiver_agents with each one's agent_nickname) and how each
// stood then (agents_states: "pending_init", "running", "interrupted", "shutdown",
// "not_found", {"completed": text} or {"errored": text}); spawn_agent's carries the prompt
// and model as well. A sub-agent that stops tells the session in a user message,
// <subagent_notification>{"agent_path": its thread id, "status": …}</subagent_notification>,
// marked as one by the message's content_item_kinds ("multi_agent.subagent_notification").
// A sub-agent is a thread of its own with a rollout of its own (never a session, CodexRollout),
// so what it says reaches this one only through these, never as its response. It stays open
// for more input until closed, so a finished one is idle, not ended. Its own sub-agents would
// be in its own rollout, which is not read: they are neither listed nor counted.
// Sign-in and turn-ending errors (session.attention): TODO(#800), once a real redacted sample of
// Codex's is captured (AgentErrors). None was on VM1, and no string is guessed meanwhile.
internal sealed class CodexParser(SessionState state, TimeProvider time) : TranscriptParser(state, time)
{
    static readonly IReadOnlyList<byte[]> Markers =
    [
        Marker("\"task_started\""),
        Marker("\"user_message\""),
        Marker("\"UserMessage\""),
        Marker("\"compacted\""),
        Marker("\"session_meta\""),
        // A sub-agent started or stopped long before the window is still listed as it is now.
        Marker("\"CollabAgentToolCall\""),
        Marker("\"multi_agent.subagent_notification\""),
    ];

    const string NotificationStart = "<subagent_notification>";
    const string NotificationEnd = "</subagent_notification>";
    const string NotificationKind = "multi_agent.subagent_notification";

    protected override IReadOnlyList<byte[]> SkimMarkers => Markers;

    protected override Conversation.TurnMarks Turns => Conversation.TurnMarks.Codex;

    // The session's own thread: a sub-agent another sub-agent started is not the main agent's.
    string? _thread;

    public override void Reset()
    {
        base.Reset();
        _thread = null;
    }

    // A sub-agent the skim saw start may have stopped in bytes never read: listed with no
    // outcome until the window says more, never running for as long as the CLI lives.
    public override void Skipped() => S.Background.ForgetRuns();

    protected override void Count(JsonElement e)
    {
        var payload = Json.Obj(e, "payload") ?? default;
        switch (Json.Str(e, "type"))
        {
            case "session_meta":
                Meta(payload, Json.Time(e, "timestamp"));
                break;
            case "compacted":
                S.Compactions++;
                break;
            case "response_item" when Json.Str(payload, "type") == "message" && Json.Str(payload, "role") == "user":
                Notification(payload, Json.Time(e, "timestamp"));
                break;
            case "event_msg":
                switch (Json.Str(payload, "type"))
                {
                    case "task_started":
                        S.Prompt(Json.Time(e, "timestamp"));
                        break;
                    case "user_message" when Text.NonBlank(Json.Str(payload, "message")) is { } said:
                        Skimmed(said, Json.Time(e, "timestamp"));
                        break;
                    case "item_completed" when Json.Obj(payload, "item") is { } item && Json.Str(item, "type") == "UserMessage" && ContentText(item) is { } said:
                        Skimmed(said, Json.Time(e, "timestamp"));
                        break;
                    case "item_completed" when Json.Obj(payload, "item") is { } item && Json.Str(item, "type") == "CollabAgentToolCall":
                        Collab(item, Json.Time(e, "timestamp"));
                        break;
                }
                break;
        }
    }

    protected override bool Apply(JsonElement e, DateTimeOffset? at)
    {
        var payload = Json.Obj(e, "payload") ?? default;
        switch (Json.Str(e, "type"))
        {
            case "session_meta":
                Meta(payload, at);
                return true;
            case "turn_context":
                {
                    // Written every turn; it changes what a client shows only when the
                    // model or folder actually changed.
                    var (model, cwd) = (S.Model, S.Cwd);
                    S.Model = Text.NonBlank(Json.Str(payload, "model")) ?? S.Model;
                    S.Cwd = Json.Str(payload, "cwd") ?? S.Cwd;
                    return model != S.Model || cwd != S.Cwd;
                }
            case "compacted":
                S.Compactions++;
                return true;
            case "event_msg":
                return Event(payload, at);
            case "response_item":
                return Item(payload, at);
            default:
                return false;
        }
    }

    void Meta(JsonElement payload, DateTimeOffset? at)
    {
        _thread ??= Text.NonBlank(Json.Str(payload, "id"));
        S.StartedAt ??= Json.Time(payload, "timestamp") ?? at;
        S.Cwd ??= Json.Str(payload, "cwd");
        if (Json.Obj(payload, "git") is { } git)
        {
            S.Branch ??= Json.Str(git, "branch");
            S.Repository ??= RepositoryName(Json.Str(git, "repository_url"));
        }
    }

    bool Event(JsonElement p, DateTimeOffset? at)
    {
        switch (Json.Str(p, "type"))
        {
            case "task_started":
                S.Prompt(at);
                S.Reasoning = null;
                S.SetStatus(SessionStatus.Working);
                S.Activity(at);
                break;
            case "task_complete":
                if (Text.NonBlank(Json.Str(p, "last_agent_message")) is { } last) { Replied(last); }
                S.Pending.Clear();
                S.SetStatus(SessionStatus.Idle);
                S.Activity(at);
                break;
            case "turn_aborted":
                S.Abandon(at);
                S.Activity(at);
                break;
            case "user_message" when Text.NonBlank(Json.Str(p, "message")) is { } text:
                UserMessage(text, at);
                break;
            case "agent_message" when Text.NonBlank(Json.Str(p, "message")) is { } text:
                Replied(text);
                S.Activity(at);
                break;
            case "agent_reasoning" when Text.NonBlank(Json.Str(p, "text")) is { } text:
                S.Reasoning = text;
                break;
            case "exec_command_begin":
                Start(Json.Str(p, "call_id"), "shell", ToolSummary.From(p), at);
                break;
            case "exec_command_end":
                End(Json.Str(p, "call_id"), Json.Int(p, "exit_code") is null or 0, at);
                break;
            case "patch_apply_begin":
                Start(Json.Str(p, "call_id"), "apply_patch", Files(p), at);
                break;
            case "patch_apply_end":
                End(Json.Str(p, "call_id"), Json.Bool(p, "success") != false, at);
                break;
            case "mcp_tool_call_begin":
                {
                    var invocation = Json.Obj(p, "invocation") ?? default;
                    var name = string.Join('.', new[] { Json.Str(invocation, "server"), Json.Str(invocation, "tool") }.Where(x => x is not null));
                    Start(Json.Str(p, "call_id"), name.Length == 0 ? "mcp" : name, ToolSummary.From(Json.Get(invocation, "arguments")), at);
                    break;
                }
            case "mcp_tool_call_end":
                End(Json.Str(p, "call_id"), Json.Obj(p, "result") is not { } r || Json.Get(r, "Err") is null, at);
                break;
            case "exec_approval_request":
                {
                    var id = Json.Str(p, "call_id");
                    if (id is null) { break; }
                    Approve(id, "Allow **Codex** to run a command?", Json.Str(p, "reason"), ToolSummary.From(p), at);
                    break;
                }
            case "apply_patch_approval_request":
                {
                    var id = Json.Str(p, "call_id");
                    if (id is null) { break; }
                    Approve(id, "Allow **Codex** to change files?", Json.Str(p, "reason"), Files(p), at);
                    break;
                }
            case "item_completed" when Json.Obj(p, "item") is { } item:
                return Completed(item, at);
            default:
                // token_count and the like.
                return false;
        }
        return true;
    }

    bool Completed(JsonElement item, DateTimeOffset? at)
    {
        switch (Json.Str(item, "type"))
        {
            case "UserMessage" when ContentText(item) is { } text:
                UserMessage(text, at);
                break;
            case "AgentMessage" when ContentText(item) is { } text:
                Replied(text);
                S.Activity(at);
                break;
            case "Reasoning":
                if (Json.Arr(item, "summary_text") is { } summary)
                {
                    var text = Text.NonBlank(string.Join('\n', summary.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())));
                    if (text is not null) { S.Reasoning = text; }
                }
                break;
            case "CommandExecution":
                {
                    var id = Json.Str(item, "id");
                    Start(id, "shell", ToolSummary.From(item), at);
                    var status = Json.Str(item, "status");
                    if (status is not null and not "in_progress")
                    {
                        End(id, status == "completed" && Json.Int(item, "exit_code") is null or 0, at);
                    }
                    break;
                }
            case "CollabAgentToolCall":
                {
                    var before = S.Background.Version;
                    Collab(item, at);
                    return S.Background.Version != before;
                }
            default:
                return false;
        }
        return true;
    }

    // One multi-agent tool call of the session's: the threads it started, woke, closed or waited
    // on, and how each stood when it returned.
    void Collab(JsonElement item, DateTimeOffset? at)
    {
        var work = S.Background;
        var sender = Text.NonBlank(Json.Str(item, "sender_thread_id"));
        var parent = sender is null || _thread is null || string.Equals(sender, _thread, StringComparison.Ordinal) ? null : sender;
        var nicknames = new Dictionary<string, (string? Nickname, string? Role)>(StringComparer.Ordinal);
        if (Json.Arr(item, "receiver_agents") is { } agents)
        {
            foreach (var a in agents.EnumerateArray())
            {
                if (a.ValueKind == JsonValueKind.Object && Text.NonBlank(Json.Str(a, "thread_id")) is { } id) { nicknames[id] = (Json.Str(a, "agent_nickname"), Json.Str(a, "agent_role")); }
            }
        }
        var receivers = new List<string>();
        if (Json.Arr(item, "receiver_thread_ids") is { } ids)
        {
            receivers.AddRange(ids.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).OfType<string>().Where(x => x.Length > 0));
        }
        receivers.AddRange(nicknames.Keys);
        receivers = [.. receivers.Distinct(StringComparer.Ordinal)];
        // A call that failed did what it says to no one; the states it reports still hold.
        switch (Json.Str(item, "status") == "failed" ? null : Json.Str(item, "tool"))
        {
            case "spawn_agent":
                foreach (var id in receivers)
                {
                    var (nickname, role) = nicknames.GetValueOrDefault(id);
                    work.AgentStarted(Json.Str(item, "id"), id, at, background: true, new BackgroundWork.StartInfo(DisplayName: nickname, AgentType: role, Model: Json.Str(item, "model"), Parent: parent));
                    work.AgentPrompt(id, Json.Str(item, "prompt"));
                }
                break;
            // Input for a sub-agent wakes it; what its state was as the call returned says
            // nothing of how that run will go.
            case "send_input" or "resume_agent" or "send_message" or "followup_task":
                foreach (var id in receivers) { work.AgentActive(id, at); }
                return;
            // Closed, it takes no more input.
            case "close_agent":
                foreach (var id in receivers) { work.AgentClosed(id, at); }
                return;
        }
        if (Json.Obj(item, "agents_states") is { } states)
        {
            foreach (var p in states.EnumerateObject()) { State(p.Name, p.Value, at, notice: false); }
        }
    }

    // A <subagent_notification> user message: a sub-agent stopped, and how. Only one Codex marks
    // as one: the person can type the same text.
    bool Notification(JsonElement p, DateTimeOffset? at)
    {
        if (Json.Obj(p, "internal_chat_message_metadata_passthrough") is not { } metadata || Json.Arr(metadata, "content_item_kinds") is not { } kinds
            || !kinds.EnumerateArray().Any(k => k.ValueKind == JsonValueKind.String && k.GetString() == NotificationKind)) { return false; }
        if (ContentText(p) is not { } text || !text.StartsWith(NotificationStart, StringComparison.Ordinal)) { return false; }
        // Only what is between the tags is the notice's JSON.
        // The last closing tag: a result can quote one.
        var close = text.LastIndexOf(NotificationEnd, StringComparison.Ordinal);
        if (close < 0) { return false; }
        var start = text.IndexOf('{', NotificationStart.Length);
        if (start < 0 || start > close) { return false; }
        var before = S.Background.Version;
        try
        {
            // Exactly one JSON value from its first brace, whatever braces its strings hold.
            var reader = new Utf8JsonReader(Json.Repaired(System.Text.Encoding.UTF8.GetBytes(text, start, close - start)).Span);
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && Text.NonBlank(Json.Str(doc.RootElement, "agent_path")) is { } agent
                && Json.Get(doc.RootElement, "status") is { } status)
            {
                State(agent, status, at, notice: true);
            }
        }
        catch (Exception x) when (x is JsonException or InvalidOperationException) { return false; }
        return S.Background.Version != before;
    }

    // A sub-agent's status as Codex reports it: running, stopped (completed is done; errored or
    // interrupted failed), or gone for good (shut down, or no longer known). A stop is said twice
    // when the session waited for it, by the wait and then by the notice: the notice is then
    // spent on the run the wait ended, never on one a message has started since.
    void State(string agent, JsonElement state, DateTimeOffset? at, bool notice)
    {
        var work = S.Background;
        if (!work.Knows(agent)) { return; }
        switch (state.ValueKind)
        {
            case JsonValueKind.String:
                switch (state.GetString())
                {
                    case "pending_init" or "running":
                        work.AgentActive(agent, at);
                        break;
                    case "interrupted":
                        Stopped(null, SubagentOutcome.Failed);
                        break;
                    case "shutdown" or "not_found":
                        work.AgentClosed(agent, at);
                        break;
                }
                break;
            case JsonValueKind.Object:
                if (Json.Get(state, "completed") is { } completed)
                {
                    Stopped(completed.ValueKind == JsonValueKind.String ? completed.GetString() : null, SubagentOutcome.Done);
                }
                else if (Json.Get(state, "errored") is { } errored)
                {
                    Stopped(errored.ValueKind == JsonValueKind.String ? errored.GetString() : null, SubagentOutcome.Failed);
                }
                break;
        }

        void Stopped(string? text, SubagentOutcome outcome)
        {
            if (!notice) { work.AgentRunEnded(agent, at, outcome); }
            else if (!work.AgentIdle(agent, at, outcome)) { return; }
            if (text is not null) { work.AgentSaid(agent, text, null, null); }
        }
    }

    bool Item(JsonElement p, DateTimeOffset? at)
    {
        switch (Json.Str(p, "type"))
        {
            case "message" when Json.Str(p, "role") == "assistant":
                {
                    var text = OutputText(p);
                    if (text is not null) { Replied(text); S.Activity(at); }
                    break;
                }
            // Otherwise what Codex injects, never shown: only a sub-agent's notice is read.
            case "message" when Json.Str(p, "role") == "user":
                return Notification(p, at);
            case "reasoning" when Json.Arr(p, "summary") is { } summary:
                {
                    var text = Text.NonBlank(string.Join('\n', summary.EnumerateArray().Select(x =>
                        x.ValueKind == JsonValueKind.String ? x.GetString() : Json.Str(x, "text"))));
                    if (text is not null) { S.Reasoning = text; }
                    break;
                }
            case "function_call":
                Start(Json.Str(p, "call_id"), Json.Str(p, "name") ?? "function", ToolSummary.From(Json.Get(p, "arguments")), at);
                break;
            case "custom_tool_call":
                Start(Json.Str(p, "call_id"), Json.Str(p, "name") ?? "tool", ToolSummary.From(Json.Get(p, "input")), at);
                break;
            case "local_shell_call":
                Start(Json.Str(p, "call_id") ?? Json.Str(p, "id"), "shell", ToolSummary.From(Json.Obj(p, "action")), at);
                break;
            case "web_search_call":
                {
                    var id = Json.Str(p, "id") ?? Json.Str(p, "call_id");
                    Start(id, "web_search", ToolSummary.From(Json.Obj(p, "action")), at);
                    End(id, true, at);
                    break;
                }
            case "function_call_output" or "custom_tool_call_output":
                End(Json.Str(p, "call_id"), true, at);
                break;
            default:
                return false;
        }
        return true;
    }

    // Which prompt it is, by its record's time (the driver's PromptKey, #489): the same whether the
    // record is read in the window or skimmed before it, so a re-read keeps an agent's prompt the
    // agent's.
    static string? Key(DateTimeOffset? at) => at?.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);

    void Skimmed(string text, DateTimeOffset? at)
    {
        S.UserMessage = text;
        S.FirstUserMessage ??= text;
        S.UserMessageKey = Key(at);
        TurnStarts();
    }

    // Whether a record starts a turn (docs/protocol/v1-history.md), and its prompt: the records
    // Count and Event take as the user's message, so history's turn ids are activity.turn's.
    internal static string? TurnStart(JsonElement e)
    {
        if (Json.Str(e, "type") != "event_msg" || Json.Obj(e, "payload") is not { } p) { return null; }
        return Json.Str(p, "type") switch
        {
            "user_message" => Text.NonBlank(Json.Str(p, "message")),
            "item_completed" when Json.Obj(p, "item") is { } item && Json.Str(item, "type") == "UserMessage" => ContentText(item),
            _ => null,
        };
    }

    // Shared with history, so an earlier turn's approval reads as the live one did. Codex's reason is
    // its own words, literal; the command or files are code (#209).
    internal static Question ApprovalQuestion(string id, string ask, string? reason, string? subject, DateTimeOffset at)
    {
        var parts = new[] { ask, CopilotPermission.Literal(reason), subject is { Length: > 0 } s ? CopilotPermission.Fence(s) : null };
        return QuestionMapper.Approval(id, string.Join("\n\n", parts.Where(x => !string.IsNullOrWhiteSpace(x))), at, atMachine: true);
    }

    // A <subagent_notification> user message, as history reads it: which sub-agent stopped, and how.
    // A status that is not a stop (running, shut down) is none.
    internal static (string Agent, SubagentOutcome Outcome)? ReadNotification(JsonElement p)
    {
        if (Json.Obj(p, "internal_chat_message_metadata_passthrough") is not { } metadata || Json.Arr(metadata, "content_item_kinds") is not { } kinds
            || !kinds.EnumerateArray().Any(k => k.ValueKind == JsonValueKind.String && k.GetString() == NotificationKind)) { return null; }
        if (ContentText(p) is not { } text || !text.StartsWith(NotificationStart, StringComparison.Ordinal)) { return null; }
        var close = text.LastIndexOf(NotificationEnd, StringComparison.Ordinal);
        var start = text.IndexOf('{', NotificationStart.Length);
        if (close < 0 || start < 0 || start > close) { return null; }
        try
        {
            var reader = new Utf8JsonReader(Json.Repaired(Encoding.UTF8.GetBytes(text, start, close - start)).Span);
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || Text.NonBlank(Json.Str(doc.RootElement, "agent_path")) is not { } agent
                || Json.Get(doc.RootElement, "status") is not { } status || StopOf(status) is not { } outcome) { return null; }
            return (agent, outcome);
        }
        catch (Exception x) when (x is JsonException or InvalidOperationException) { return null; }
    }

    // A sub-agent's status as a notice or a wait reports it, as history reads it: how its run
    // stopped, or null for one that is not a stop (running, shut down).
    internal static SubagentOutcome? StopOf(JsonElement status) => status.ValueKind switch
    {
        JsonValueKind.String when status.GetString() == "interrupted" => SubagentOutcome.Failed,
        JsonValueKind.Object when Json.Get(status, "completed") is not null => SubagentOutcome.Done,
        JsonValueKind.Object when Json.Get(status, "errored") is not null => SubagentOutcome.Failed,
        _ => null,
    };

    void UserMessage(string text, DateTimeOffset? at)
    {
        // task_started counted the turn; a user message only says what it is.
        Skimmed(text, at);
        S.Pending.Clear();
        S.SetStatus(SessionStatus.Working);
        S.Activity(at);
    }

    // The main agent's reply, and the turn it is of (activity.responseTurn).
    void Replied(string text)
    {
        S.Response = text;
        S.ResponseTurn = S.TurnId;
    }

    void Start(string? id, string name, string? summary, DateTimeOffset? at)
    {
        if (id is null) { return; }
        S.Tools.Start(id, name, summary, Now(at));
        // The call started, so whatever approval it waited for was given.
        S.Answer(id);
        S.SetStatus(SessionStatus.Working);
        S.Activity(at);
    }

    void End(string? id, bool succeeded, DateTimeOffset? at)
    {
        S.Tools.End(id, succeeded, at);
        S.Answer(id);
        S.Activity(at);
    }

    void Approve(string id, string ask, string? reason, string? subject, DateTimeOffset? at)
    {
        // Answered only at the machine: what agent-ha-bridge answers is the approval its
        // hook recorded (the marker, which SessionTracker prefers), since only that has a
        // generation to check before typing. A request seen only in the rollout has none.
        S.Ask(ApprovalQuestion(id, ask, reason, subject, Now(at)));
    }

    internal static string? Files(JsonElement p) =>
        Json.Obj(p, "changes") is { } changes ? ToolSummary.Cut(string.Join(", ", changes.EnumerateObject().Select(c => Path.GetFileName(c.Name)))) : null;

    internal static string? ContentText(JsonElement item)
    {
        if (Json.Arr(item, "content") is not { } content) { return Text.NonBlank(Json.Str(item, "text")); }
        var sb = new StringBuilder();
        foreach (var c in content.EnumerateArray())
        {
            var text = c.ValueKind == JsonValueKind.String ? c.GetString() : Json.Str(c, "text");
            if (string.IsNullOrEmpty(text)) { continue; }
            if (sb.Length > 0) { sb.Append('\n'); }
            sb.Append(text);
        }
        return Text.NonBlank(sb.ToString());
    }

    internal static string? OutputText(JsonElement p)
    {
        if (Json.Arr(p, "content") is not { } content) { return null; }
        var sb = new StringBuilder();
        foreach (var c in content.EnumerateArray())
        {
            var type = Json.Str(c, "type");
            if (type is not (null or "output_text") || Json.Str(c, "text") is not { } text) { continue; }
            if (sb.Length > 0) { sb.Append('\n'); }
            sb.Append(text);
        }
        return Text.NonBlank(sb.ToString());
    }

    // owner/name from https://github.com/owner/name(.git) or git@github.com:owner/name.git.
    internal static string? RepositoryName(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) { return null; }
        var path = url.Trim();
        var colon = path.IndexOf(':');
        if (path.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out var uri)) { return null; }
            path = uri.AbsolutePath;
        }
        else if (colon > 0) { path = path[(colon + 1)..]; }
        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) { path = path[..^4]; }
        var parts = path.Split('/');
        return parts.Length >= 2 ? $"{parts[^2]}/{parts[^1]}" : null;
    }
}
