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
// background agent or shell it started is still running, as Copilot's own prompt does;
// that work is reported beside the status, as activity.background (BackgroundWork).
//
// Which background agents are working is read from each agent's own turn bookkeeping,
// not from subagent.completed: that is written once, after an agent's first run, so an
// agent woken again by a message never writes another, and its agent_idle notification
// came up to 90 minutes late, or never, in real transcripts. Every one of 125 run ends
// in them was an agent turn_end that asked for no tools, at or before those signals.
// remoteApproval: SessionSourceOptions.CopilotRemoteApproval, the host's permissions.remoteApproval.
internal sealed class CopilotParser(SessionState state, TimeProvider time, bool remoteApproval = true) : TranscriptParser(state, time)
{
    static readonly IReadOnlyList<byte[]> Markers =
    [
        Marker("\"user.message\""),
        Marker("\"session.compaction_complete\""),
        Marker("\"session.start\""),
        Marker("\"session.resume\""),
        Marker("\"session.model_change\""),
        Marker("\"session.shutdown\""),
        Marker("\"subagent.started\""),
        // A shell's start, end or stop is in a tool result's text, and its completion in a
        // notification; agent-ha-bridge reads both from wherever they are.
        Marker("shellId: "),
        Marker("command with id: "),
        Marker("\"shell_completed\""),
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
                if (!IsSubagent(e)) { S.Model ??= Json.Str(data, "selectedModel"); }
                break;
            // The session's own model, from before the window: a session whose background
            // agents filled the window had no message of its own in it, and so showed no model
            // where agent-ha-bridge, which reads every line as it comes, showed one.
            case "session.resume" when !IsSubagent(e):
                S.Model = Json.Str(data, "selectedModel") ?? S.Model;
                break;
            case "session.model_change" when !IsSubagent(e):
                S.Model = Json.Str(data, "newModel") ?? S.Model;
                break;
            case "subagent.started":
                // Only the mode: the agent's events in the parsed window say whether it is
                // still working, and only an agent known to be background is tracked, so a
                // sync one's events never look like one.
                S.Background.KnowMode(Json.Str(data, "toolCallId"), Json.Str(e, "agentId"), Json.Str(data, "executionMode") == "background");
                break;
            // A background shell is started once and reports its end once, however long
            // apart: a CI watch started hours ago in a busy session is far outside the window.
            // So its start and end are taken from the skim as well, as agent-ha-bridge takes
            // them from every line; BackgroundWork.Running still drops any that began before
            // the session's current process.
            case "tool.execution_complete" when !IsSubagent(e):
                {
                    var content = Json.Obj(data, "result") is { } result ? Json.Str(result, "content") : null;
                    var (started, ended) = ShellMarkers.Read(content);
                    if (ended is not null) { S.Background.ShellEnded(ended); }
                    if (started is not null) { S.Background.ShellStarted(started, Json.Time(e, "timestamp")); }
                    break;
                }
            case "system.notification" when Json.Obj(data, "kind") is { } kind && Json.Str(kind, "type") == "shell_completed":
                S.Background.ShellEnded(Json.Str(kind, "shellId"));
                break;
            case "session.shutdown" when !IsSubagent(e):
                S.Background.EndAll();
                break;
        }
    }

    // A shell the skim saw start may have ended in the bytes the tail never read (a transcript
    // past the skim budget); counted, it would read as running for as long as its CLI lives.
    // One started again or still running in the window is counted from there.
    public override void Skipped() => S.Background.ForgetShells();

    protected override bool Apply(JsonElement e, DateTimeOffset? at)
    {
        var before = S.Background.Version;
        Background(e, at);
        return ApplyOwn(e, at) || S.Background.Version != before;
    }

    void Background(JsonElement e, DateTimeOffset? at)
    {
        var data = Json.Obj(e, "data") ?? default;
        var agentId = Json.Str(e, "agentId") is { Length: > 0 } a ? a : null;
        var work = S.Background;
        switch (Json.Str(e, "type"))
        {
            case "subagent.started":
                if (Json.Str(data, "executionMode") == "background") { work.AgentStarted(Json.Str(data, "toolCallId"), agentId, at); }
                break;
            case "subagent.completed":
                work.AgentCompleted(Json.Str(data, "toolCallId"));
                break;
            case "system.notification" when Json.Obj(data, "kind") is { } kind:
                switch (Json.Str(kind, "type"))
                {
                    case "agent_idle": work.AgentIdle(Json.Str(kind, "agentId")); break;
                    case "shell_completed": work.ShellEnded(Json.Str(kind, "shellId")); break;
                }
                break;
            case "session.shutdown" when agentId is null:
                work.EndAll();
                break;
            // A subagent's own abort or shutdown ends its run and nothing else: none were in
            // two months of real transcripts, but a woken agent writes no second
            // subagent.completed, so missing one would leave it working for good.
            case "abort" or "session.shutdown" when agentId is not null:
                work.AgentEnded(agentId);
                break;
            case "user.message" or "assistant.turn_start" or "tool.execution_start" when agentId is not null:
                work.AgentActive(agentId, at);
                break;
            case "assistant.message" when agentId is not null:
                work.AgentActive(agentId, at);
                if (Json.Arr(data, "toolRequests") is { } requests && requests.GetArrayLength() > 0) { work.AgentAskedForTools(agentId); }
                break;
            case "assistant.turn_end" when agentId is not null:
                work.AgentTurnEnded(agentId, at);
                break;
            // A subagent's shells are its own work, covered by its agent; they report their
            // ends to it, not to the session, and counted here they never ended.
            case "tool.execution_complete" when agentId is null:
                {
                    var content = Json.Obj(data, "result") is { } result ? Json.Str(result, "content") : null;
                    var (started, ended) = ShellMarkers.Read(content);
                    if (ended is not null) { work.ShellEnded(ended); }
                    if (started is not null) { work.ShellStarted(started, at); }
                    break;
                }
        }
    }

    bool ApplyOwn(JsonElement e, DateTimeOffset? at)
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
            // What a subagent says and thinks reaches the card, as it does in
            // agent-ha-bridge, so a delegated job (above all a background one, while the
            // session itself reads idle) is not a silent one. Its turns, its prompt and its
            // model stay its own: the status, the person's message and the model shown are
            // the session's.
            case "assistant.message" when sub:
                {
                    if (Text.NonBlank(Json.Str(data, "reasoningText")) is { } reasoning) { S.Reasoning = reasoning; }
                    if (Text.NonBlank(Json.Str(data, "content")) is { } content) { S.Response = content; }
                    S.Activity(at);
                    break;
                }
            // A subagent's abort or shutdown ends its own work, not the session's turn:
            // abandoning on it set the session idle and failed the parent's running tools.
            case "abort" when !sub:
                S.Abandon(at);
                _turnAskedForTools = false;
                break;
            case "session.shutdown" when !sub:
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

    // A permission request is asked of the person like a question, so it is one, of kind
    // approval, carrying in full what it would allow (CopilotPermission). A host with
    // remote approval off, or a prompt of a kind it does not drive, shows it as answered
    // only at the machine.
    void Permission(JsonElement data, DateTimeOffset at)
    {
        if (CopilotPermission.Describe(data) is not { } prompt) { return; }
        var id = prompt.QuestionId;
        _permissionQuestion[prompt.RequestId] = id;
        S.Ask(QuestionMapper.Approval(id, prompt.Message, at, atMachine: !remoteApproval || prompt.AtMachine is not null));
    }

    void Context(JsonElement? context)
    {
        if (context is not { } c) { return; }
        S.Cwd = Json.Str(c, "cwd") ?? S.Cwd;
        S.Repository = Json.Str(c, "repository") ?? S.Repository;
        S.Branch = Json.Str(c, "branch") ?? S.Branch;
    }
}
