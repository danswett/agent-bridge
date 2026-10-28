using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Parsing;

// Copilot CLI's events.jsonl: one {type, data, id, timestamp, parentId} per line.
//
// Status: a prompt or assistant.turn_start means working; assistant.turn_end means idle
// unless that turn asked for tools, because Copilot ends a turn after every round of
// tool calls and starts the next one to read their results (reporting idle there made
// every tool round flash idle). A pending ask_user or permission request means waiting.
// abort and session.shutdown end the turn wherever it was.
//
// Subagents write into the same file, stamped with a top-level agentId the session's own
// events never carry. Their turns, prompts, replies and model are not the session's, so
// they are ignored for status and text; their tool calls are real work and do appear in
// the tool history. A session whose own turn has ended reads idle even while a
// background agent it started is still running, as Copilot's own prompt does: such an
// agent can sit idle awaiting messages without ever writing subagent.completed, so
// counting it as work left sessions reading working for good.
internal sealed class CopilotParser(SessionState state, TimeProvider time) : TranscriptParser(state, time)
{
    static readonly IReadOnlyList<byte[]> Markers =
    [
        Marker("\"type\":\"user.message\""),
        Marker("\"type\":\"session.compaction_complete\""),
        Marker("\"type\":\"session.start\""),
    ];

    bool _turnAskedForTools;
    readonly Dictionary<string, string> _permissionQuestion = new(StringComparer.Ordinal);

    protected override IReadOnlyList<byte[]> SkimMarkers => Markers;

    public override void Reset()
    {
        base.Reset();
        _turnAskedForTools = false;
        _permissionQuestion.Clear();
    }

    static bool IsSubagent(JsonElement e) => Json.Str(e, "agentId") is { Length: > 0 };

    protected override void Count(JsonElement e)
    {
        var data = Json.Obj(e, "data") ?? default;
        switch (Json.Str(e, "type"))
        {
            case "user.message" when !IsSubagent(e):
                if (Text.NonBlank(Json.Str(data, "content")) is { } text)
                {
                    S.FirstUserMessage ??= text;
                    S.UserMessage = text;
                    S.Prompts++;
                }
                break;
            case "session.compaction_complete" when Json.Bool(data, "success") == true:
                S.Compactions++;
                break;
            case "session.start":
                S.StartedAt ??= Json.Time(e, "timestamp");
                break;
        }
    }

    protected override bool Apply(JsonElement e, DateTimeOffset? at)
    {
        var type = Json.Str(e, "type");
        var data = Json.Obj(e, "data") ?? default;
        var sub = IsSubagent(e);
        switch (type)
        {
            case "session.start":
                S.StartedAt ??= at;
                S.Model ??= Json.Str(data, "selectedModel");
                Context(Json.Obj(data, "context"));
                break;
            case "session.resume":
                S.Model = Json.Str(data, "selectedModel") ?? S.Model;
                Context(Json.Obj(data, "context"));
                break;
            case "session.context_changed":
                Context(data);
                break;
            case "session.model_change" when !sub:
                S.Model = Json.Str(data, "newModel") ?? S.Model;
                break;
            case "session.compaction_complete" when Json.Bool(data, "success") == true:
                // Only a compaction that finished: Copilot writes a start for every
                // attempt, and one real transcript held 225 starts against 6 compactions.
                S.Compactions++;
                break;
            case "user.message" when !sub:
                {
                    var text = Text.NonBlank(Json.Str(data, "content"));
                    if (text is null)
                    {
                        S.SetStatus(SessionStatus.Working);
                        S.Activity(at);
                    }
                    else
                    {
                        // Every prompt counts, whatever its delivery: interactive prompts are
                        // "steering" even when no turn is running, and those typed while idle
                        // or sent by the bridge are "idle" and carry parentAgentTaskId.
                        UserPrompt(text, at);
                    }
                    _turnAskedForTools = false;
                    break;
                }
            case "assistant.turn_start" when !sub:
                S.SetStatus(SessionStatus.Working);
                _turnAskedForTools = false;
                break;
            case "assistant.message" when !sub:
                {
                    S.Model = Text.NonBlank(Json.Str(data, "model")) ?? S.Model;
                    if (Text.NonBlank(Json.Str(data, "reasoningText")) is { } reasoning) { S.Reasoning = reasoning; }
                    if (Text.NonBlank(Json.Str(data, "content")) is { } content) { S.Response = content; }
                    if (Json.Arr(data, "toolRequests") is { } requests && requests.GetArrayLength() > 0) { _turnAskedForTools = true; }
                    S.SetStatus(SessionStatus.Working);
                    S.Activity(at);
                    break;
                }
            case "assistant.turn_end" when !sub:
                S.SetStatus(_turnAskedForTools ? SessionStatus.Working : SessionStatus.Idle);
                _turnAskedForTools = false;
                break;
            case "abort":
                S.Abandon(at);
                _turnAskedForTools = false;
                break;
            case "session.shutdown":
                S.Abandon(at);
                break;
            case "tool.execution_start":
                {
                    var id = Json.Str(data, "toolCallId");
                    var name = Json.Str(data, "toolName");
                    if (id is null || name is null) { break; }
                    var arguments = Json.Get(data, "arguments");
                    S.Tools.Start(id, name, ToolSummary.From(arguments), Now(at));
                    if (!sub && name == "ask_user" && arguments is { } args)
                    {
                        S.Ask(QuestionMapper.FromCopilotAskUser(id, args, Now(at)));
                    }
                    if (!sub) { S.SetStatus(SessionStatus.Working); }
                    S.Activity(at);
                    break;
                }
            case "tool.execution_complete":
                {
                    var id = Json.Str(data, "toolCallId");
                    var failed = Json.Bool(data, "success") == false || Json.Get(data, "error") is not null;
                    S.Tools.End(id, !failed, at);
                    S.Answer(id);
                    S.Activity(at);
                    break;
                }
            case "human_response.recorded":
                S.Answer(Json.Str(data, "toolCallId"));
                S.Activity(at);
                break;
            case "permission.requested":
                Permission(data, Now(at));
                break;
            case "permission.completed":
                {
                    var request = Json.Str(data, "requestId");
                    if (request is not null && _permissionQuestion.Remove(request, out var question)) { S.Answer(question); }
                    break;
                }
            default:
                // hook.*, session.usage_record, model.*, system.notification and the
                // subagent's own turn bookkeeping change nothing a client shows.
                return false;
        }
        return true;
    }

    // A permission request is asked of the person like a question, so it is one, of
    // kind approval, keyed by the tool call it gates when there is one.
    void Permission(JsonElement data, DateTimeOffset at)
    {
        var request = Json.Str(data, "requestId");
        var ask = Json.Obj(data, "permissionRequest") ?? Json.Obj(data, "promptRequest");
        if (request is null || ask is not { } p) { return; }
        var id = Json.Str(p, "toolCallId") ?? request;
        var what = Json.Str(p, "toolTitle") ?? Json.Str(p, "toolName") ?? Json.Str(p, "kind") ?? "a tool";
        var detail = ToolSummary.From(Json.Get(p, "args")) ?? Text.OneLine(Json.Str(p, "fullCommandText") ?? Json.Str(p, "intention"));
        var message = detail is null ? $"Allow **{what}**?" : $"Allow **{what}**?\n\n`{detail}`";
        _permissionQuestion[request] = id;
        S.Ask(QuestionMapper.Approval(id, message, at));
    }

    void Context(JsonElement? context)
    {
        if (context is not { } c) { return; }
        S.Cwd = Json.Str(c, "cwd") ?? S.Cwd;
        S.Repository = Json.Str(c, "repository") ?? S.Repository;
        S.Branch = Json.Str(c, "branch") ?? S.Branch;
    }
}
