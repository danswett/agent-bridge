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
internal sealed class CodexParser(SessionState state, TimeProvider time) : TranscriptParser(state, time)
{
    static readonly IReadOnlyList<byte[]> Markers =
    [
        Marker("\"task_started\""),
        Marker("\"user_message\""),
        Marker("\"UserMessage\""),
        Marker("\"compacted\""),
        Marker("\"session_meta\""),
    ];

    protected override IReadOnlyList<byte[]> SkimMarkers => Markers;

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
            case "event_msg":
                switch (Json.Str(payload, "type"))
                {
                    case "task_started":
                        S.Prompts++;
                        break;
                    case "user_message":
                        S.UserMessage = Text.NonBlank(Json.Str(payload, "message")) ?? S.UserMessage;
                        S.FirstUserMessage ??= S.UserMessage;
                        break;
                    case "item_completed" when Json.Obj(payload, "item") is { } item && Json.Str(item, "type") == "UserMessage":
                        S.UserMessage = ContentText(item) ?? S.UserMessage;
                        S.FirstUserMessage ??= S.UserMessage;
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
                S.Prompts++;
                S.Reasoning = null;
                S.SetStatus(SessionStatus.Working);
                S.Activity(at);
                break;
            case "task_complete":
                S.Response = Text.NonBlank(Json.Str(p, "last_agent_message")) ?? S.Response;
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
                S.Response = text;
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
                    var command = ToolSummary.From(p) ?? "a command";
                    Approve(id, $"Run `{command}`?", Json.Str(p, "reason"), at);
                    break;
                }
            case "apply_patch_approval_request":
                {
                    var id = Json.Str(p, "call_id");
                    if (id is null) { break; }
                    Approve(id, $"Apply changes to {Files(p) ?? "files"}?", Json.Str(p, "reason"), at);
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
                S.Response = text;
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
            default:
                return false;
        }
        return true;
    }

    bool Item(JsonElement p, DateTimeOffset? at)
    {
        switch (Json.Str(p, "type"))
        {
            case "message" when Json.Str(p, "role") == "assistant":
                {
                    var text = OutputText(p);
                    if (text is not null) { S.Response = text; S.Activity(at); }
                    break;
                }
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

    void UserMessage(string text, DateTimeOffset? at)
    {
        // task_started counted the turn; a user message only says what it is.
        S.UserMessage = text;
        S.FirstUserMessage ??= text;
        S.Pending.Clear();
        S.SetStatus(SessionStatus.Working);
        S.Activity(at);
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

    void Approve(string id, string message, string? reason, DateTimeOffset? at)
    {
        // Answered only at the machine: what agent-ha-bridge answers is the approval its
        // hook recorded (the marker, which SessionTracker prefers), since only that has a
        // generation to check before typing. A request seen only in the rollout has none.
        S.Ask(QuestionMapper.Approval(id, string.IsNullOrWhiteSpace(reason) ? message : $"{message}\n\n{reason}", Now(at), atMachine: true));
    }

    static string? Files(JsonElement p) =>
        Json.Obj(p, "changes") is { } changes ? ToolSummary.Cut(string.Join(", ", changes.EnumerateObject().Select(c => Path.GetFileName(c.Name)))) : null;

    static string? ContentText(JsonElement item)
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

    static string? OutputText(JsonElement p)
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
