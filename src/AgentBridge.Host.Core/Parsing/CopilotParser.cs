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
// events never carry. Their turns, prompts and model are not the session's, so they are
// ignored for its status; their replies reach the session's text as the latest writer's,
// attributed (activity.source), with the main agent's own kept beside them; their tool
// calls are real work and do appear in the tool history. Each is an entry of its own
// (the session's subagents, BackgroundWork). A session whose own turn has ended reads idle
// even while a background agent or shell it started is still running, as Copilot's own
// prompt does; that work is reported beside the status, as activity.background.
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
        Marker("\"session.error\""),
        // A turn starting clears a turn's error (NewTurn), on a cold read too.
        Marker("\"assistant.turn_start\""),
        Marker("\"subagent.started\""),
        // What a delegate was given, by whom, and its model since: a cold read whose window
        // its start falls before still lists it with its task, nesting and current model.
        Marker("\"task\""),
        Marker("\"subagent.configured\""),
        // A shell's start, end or stop is in a tool result's text, and its completion in a
        // notification; agent-ha-bridge reads both from wherever they are.
        Marker("shellId: "),
        Marker("command with id: "),
        Marker("\"shell_completed\""),
    ];

    bool _turnAskedForTools;
    // The host's sentence for the error that ended this turn, which the CLI's notice of it is
    // shown as (MainText); none once the next turn starts.
    string? _turnError;
    readonly Dictionary<string, string> _permissionQuestion = new(StringComparer.Ordinal);
    static readonly byte[] AssistantMessage = Marker("\"assistant.message\"");
    static readonly byte[] ToolStart = Marker("\"tool.execution_start\"");
    static readonly byte[] Stamped = Marker("\"agentId\"");

    protected override IReadOnlyList<byte[]> SkimMarkers => Markers;

    // The main agent's replies too, for activity.own: a session whose delegates filled the
    // parsed window had no reply of the main agent's in it. A delegate's carries an agentId,
    // and is not wanted; a main reply quoting that word is merely missed, except while the
    // session needs sign-in, when that reply (a read_agent request names an agentId) is what
    // clears it: Count reads the stamp itself. And a delegate's
    // tool calls, whose permission prompts can fall in the window when the calls do not.
    protected override bool SkimWants(ReadOnlySpan<byte> line) =>
        base.SkimWants(line) || (line.IndexOf(AssistantMessage) >= 0 && (line.IndexOf(Stamped) < 0 || S.Attention is { Kind: AttentionKind.SignInNeeded }))
        || (line.IndexOf(ToolStart) >= 0 && line.IndexOf(Stamped) >= 0);

    public override void Reset()
    {
        base.Reset();
        _turnAskedForTools = false;
        _turnError = null;
        _permissionQuestion.Clear();
    }

    static bool IsSubagent(JsonElement e) => Json.Str(e, "agentId") is { Length: > 0 };

    // Who wrote a main-agent user.message, from its data.source (activity.userMessageFrom, #489).
    // Copilot writes none for a prompt typed in its window, and "user" for one typed with an
    // attachment; "schedule-<n>" for a scheduled prompt and "agent-<id>" for one an agent sent.
    // "memory", "system" (a shell's completion notice) and "skill-<name>" are context it gave the
    // agent rather than a prompt anybody sent: they leave userMessage as it was. Anything else is
    // not the person's, but not one of those either.
    internal static (UserMessageFrom? From, string? Label, bool Injected) Source(string? source) => source switch
    {
        null or "" or "user" => (null, null, false),
        "memory" or "system" => (null, null, true),
        _ when source.StartsWith("skill-", StringComparison.Ordinal) => (null, null, true),
        _ when source.StartsWith("schedule-", StringComparison.Ordinal) => (UserMessageFrom.Schedule, ScheduleNumber(source["schedule-".Length..]), false),
        _ when source.StartsWith("agent-", StringComparison.Ordinal) => (UserMessageFrom.Agent, null, false),
        _ => (UserMessageFrom.Other, null, false),
    };

    // A shell's completion notice Copilot delivered while no turn ran: it starts a turn of its own.
    static bool Notice(JsonElement data) => Json.Str(data, "source") == "system" && Json.Str(data, "delivery") != "steering";

    // The schedule's number as its label, and only a number: the label is shown as it is.
    static string? ScheduleNumber(string n) =>
        n.Length is > 0 and <= Limits.UserMessageLabel && n.All(char.IsAsciiDigit) ? n : null;

    protected override void Count(JsonElement e)
    {
        var data = Json.Obj(e, "data") ?? default;
        switch (Json.Str(e, "type"))
        {
            // A sign-in failure and what clears it are kept past the skim too (Failed, NewTurn,
            // Answered): a signed-out session whose delegates wrote on past the parsed window
            // read as fine again after the host restarted.
            case "session.error" when !IsSubagent(e):
                if (AgentErrors.Copilot(data, Json.Time(e, "timestamp") ?? Time.GetUtcNow()) is { } failure)
                {
                    _turnError = failure.Message;
                    Failed(failure);
                }
                break;
            case "assistant.turn_start" when !IsSubagent(e):
                NewTurn();
                break;
            case "user.message" when !IsSubagent(e):
                if (Text.NonBlank(Json.Str(data, "content")) is { } text)
                {
                    var (from, label, injected) = Source(Json.Str(data, "source"));
                    if (injected)
                    {
                        // A notice delivered while no turn ran (not "steering") starts a turn no
                        // prompt's: kept past the skim, since the window may begin inside it, as
                        // Claude's notices are. A prompt after it starts a counted turn again.
                        // Memory and a skill's context come within a turn, and start none.
                        if (Notice(data)) { S.UncountedTurn = true; }
                        break;
                    }
                    NewTurn();
                    Prompted(text, from, label, Json.Str(e, "id"));
                    S.Prompts++;
                    S.UncountedTurn = false;
                    S.OwnReasoning = null;
                }
                break;
            // Only the main agent's own text, never the legacy fields: those are the window's
            // to say, as they always were. Cut as taken: nothing else holds these strings.
            case "assistant.message" when !IsSubagent(e):
                {
                    Answered(data, Json.Time(e, "timestamp"));
                    var reasoning = Text.NonBlank(Json.Str(data, "reasoningText"));
                    var content = MainText(data);
                    if (reasoning is not null) { S.OwnReasoning = Limits.KeepEnd(reasoning, Limits.Reasoning).Text; }
                    if (content is not null) { (S.OwnResponse, S.OwnResponseCut) = Limits.KeepEnd(content, Limits.Response); }
                    if (reasoning is not null || content is not null) { S.OwnAt = Json.Time(e, "timestamp"); }
                    break;
                }
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
            // A delegate's own, likewise: never the session's (#73).
            case "session.model_change":
                S.Background.AgentModel(Json.Str(e, "agentId")!, Json.Str(data, "newModel"));
                break;
            case "subagent.configured" when IsSubagent(e):
                S.Background.AgentModel(Json.Str(e, "agentId")!, Json.Str(data, "model"));
                break;
            // The label and prompt of the agent it starts next, and who started it; not work.
            case "tool.execution_start" when Json.Str(data, "toolName") == "task":
                S.Background.TaskCalled(Json.Str(data, "toolCallId"), Json.Str(e, "agentId") is { Length: > 0 } starter ? starter : null, Json.Get(data, "arguments"));
                break;
            // Whose a delegate's call is, for a permission prompt the window has for it.
            case "tool.execution_start" when IsSubagent(e):
                S.Background.RememberCall(Json.Str(data, "toolCallId"), Json.Str(e, "agentId")!);
                break;
            // A delegate's first message is the prompt it was started with.
            case "user.message":
                S.Background.AgentPrompt(Json.Str(e, "agentId")!, Json.Str(data, "content"));
                break;
            case "subagent.started":
                // What the agent is, not whether it is still working: its events in the parsed
                // window say that, and one with none there is listed as finished.
                S.Background.KnowMode(Json.Str(data, "toolCallId"), Json.Str(e, "agentId"), Json.Str(data, "executionMode") == "background", Info(data), Json.Time(e, "timestamp"));
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
            // A delegate's own shells likewise, counted on its entry.
            case "tool.execution_complete":
                {
                    var content = Json.Obj(data, "result") is { } result ? Json.Str(result, "content") : null;
                    var (started, ended) = ShellMarkers.Read(content);
                    if (started is not null || ended is not null) { S.Background.AgentShell(Json.Str(e, "agentId")!, started, ended, Json.Time(e, "timestamp")); }
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
    // One started again or still running in the window is counted from there; a delegate's
    // likewise. The model may have changed there too, so it is left for the window (or the
    // session's registration) to say rather than kept stale, a delegate's likewise, and so is
    // the main agent's own text.
    public override void Skipped()
    {
        S.Background.ForgetShells();
        S.Background.ForgetModels();
        S.Model = null;
        S.OwnResponse = null;
        S.OwnResponseCut = false;
        S.OwnReasoning = null;
        S.OwnAt = null;
        // Turns may have started and failed unread: a notice in the tail names no error of the
        // skim's (#846), only the generic sentence unless its own error is read there.
        _turnError = null;
        // A sign-in failure the skim read is kept: bytes never read are no evidence the model
        // answered, as a retry loop can write plenty without one (#800). The tail clears it only
        // with a main reply after it, and a newer failure there moves its since.
    }

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
                work.AgentStarted(Json.Str(data, "toolCallId"), agentId, at, Json.Str(data, "executionMode") == "background", Info(data));
                break;
            case "subagent.completed":
                work.AgentCompleted(Json.Str(data, "toolCallId"), at);
                break;
            case "subagent.failed":
                work.AgentFailed(Json.Str(data, "toolCallId"), agentId, at);
                break;
            // The sub-agent's own model, which is its entry's and never the session's (#73).
            case "subagent.configured" when agentId is not null:
                work.AgentModel(agentId, Json.Str(data, "model"));
                break;
            case "session.model_change" when agentId is not null:
                work.AgentModel(agentId, Json.Str(data, "newModel"));
                break;
            case "system.notification" when Json.Obj(data, "kind") is { } kind:
                switch (Json.Str(kind, "type"))
                {
                    case "agent_idle": work.AgentIdle(Json.Str(kind, "agentId"), at); break;
                    case "shell_completed": work.ShellEnded(Json.Str(kind, "shellId")); break;
                }
                break;
            case "session.shutdown" when agentId is null:
                work.EndAll(at);
                break;
            case "abort" when agentId is null:
                work.MainAborted(at);
                break;
            // A subagent's own abort or shutdown ends its run and nothing else: none were in
            // two months of real transcripts, but a woken agent writes no second
            // subagent.completed, so missing one would leave it working for good.
            case "abort" or "session.shutdown" when agentId is not null:
                work.AgentEnded(agentId, at, aborted: Json.Str(e, "type") == "abort");
                break;
            case "user.message" when agentId is not null:
                work.AgentActive(agentId, at);
                work.AgentPrompt(agentId, Json.Str(data, "content"));
                break;
            case "assistant.turn_start" when agentId is not null:
                work.AgentActive(agentId, at);
                break;
            case "tool.execution_start":
                {
                    var id = Json.Str(data, "toolCallId");
                    var name = Json.Str(data, "toolName");
                    if (name == "task") { work.TaskCalled(id, agentId, Json.Get(data, "arguments")); }
                    if (agentId is null) { break; }
                    work.AgentActive(agentId, at);
                    if (id is not null && name is not null) { work.AgentToolStarted(agentId, id, name, ToolSummary.From(Json.Get(data, "arguments")), Now(at)); }
                    break;
                }
            case "assistant.message" when agentId is not null:
                work.AgentActive(agentId, at);
                work.AgentAskedForTools(agentId, Json.Arr(data, "toolRequests") is { } requests && requests.GetArrayLength() > 0);
                work.AgentSaid(agentId, Json.Str(data, "content"), Json.Str(data, "reasoningText"), Json.Str(data, "model"));
                break;
            case "assistant.turn_end" when agentId is not null:
                work.AgentTurnEnded(agentId, at);
                break;
            case "tool.execution_complete":
                {
                    var id = Json.Str(data, "toolCallId");
                    var succeeded = Json.Bool(data, "success") != false && Json.Get(data, "error") is null;
                    var content = Json.Obj(data, "result") is { } result ? Json.Str(result, "content") : null;
                    var (started, ended) = ShellMarkers.Read(content);
                    if (agentId is null)
                    {
                        if (ended is not null) { work.ShellEnded(ended); }
                        if (started is not null) { work.ShellStarted(started, at); }
                    }
                    else
                    {
                        // A subagent's shells are its own work, counted on its entry; they
                        // report their ends to it, not to the session, and counted on the
                        // session they never ended.
                        work.AgentShell(agentId, started, ended, at);
                        work.AgentToolEnded(agentId, id, succeeded, at);
                    }
                    work.TaskCallDone(id, succeeded, at);
                    break;
                }
        }
    }

    static BackgroundWork.StartInfo Info(JsonElement data) => new(
        Json.Str(data, "agentDisplayName"), Json.Str(data, "agentType") ?? Json.Str(data, "agentName"), Json.Str(data, "model"),
        Json.Str(data, "parentId") is { Length: > 0 } parent ? parent : null);

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
                        var (from, label, injected) = Source(Json.Str(data, "source"));
                        // Memory, a skill's context or a notice is no prompt: a turn it starts
                        // writes its own turn_start.
                        if (!injected) { NewTurn(); }
                        UserPrompt(text, at, from: from, label: label, injected: injected, key: Json.Str(e, "id"), notice: injected && Notice(data));
                    }
                    _turnAskedForTools = false;
                    break;
                }
            case "assistant.turn_start" when !sub:
                S.SetStatus(SessionStatus.Working);
                NewTurn();
                _turnAskedForTools = false;
                break;
            case "assistant.message" when !sub:
                {
                    Answered(data, at);
                    S.Model = Text.NonBlank(Json.Str(data, "model")) ?? S.Model;
                    S.MainSaid(MainText(data), Text.NonBlank(Json.Str(data, "reasoningText")), at);
                    // The CLI's notice of the error that ended the turn is written after it, and
                    // starts nothing: read as a reply, it set the session working again for good.
                    if (TurnErrorNotice(data)) { break; }
                    // The turn's latest reply decides whether it goes on to run tools. Copilot answers an
                    // agent_idle notification inside the turn it starts: a read_agent request, its
                    // result, then the reply with nothing more to run. Kept from the first, the session
                    // read working until its next prompt, idle for hours (#40, DSWETT-DEV-VM1).
                    _turnAskedForTools = Json.Arr(data, "toolRequests") is { } requests && requests.GetArrayLength() > 0;
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
            // the session's. Who wrote it goes beside it (activity.source), and the main
            // agent's own text stays in activity.own.
            case "assistant.message" when sub:
                {
                    S.SubagentSaid(Json.Str(e, "agentId")!, Text.NonBlank(Json.Str(data, "content")), Text.NonBlank(Json.Str(data, "reasoningText")));
                    S.Activity(at);
                    break;
                }
            // A subagent's abort or shutdown ends its own work, not the session's turn:
            // abandoning on it set the session idle and failed the parent's running tools.
            // Whatever it, or a task agent it was waiting on, was asking goes with it.
            case "abort" when !sub:
                S.Abandon(at);
                _turnAskedForTools = false;
                break;
            case "abort" or "session.shutdown" when sub:
                {
                    var agent = Json.Str(e, "agentId");
                    if (S.Pending.RemoveAll(q => q.Agent is { } a && (a == agent || S.Background.HasEnded(a))) == 0) { return false; }
                    break;
                }
            // A sub-agent that failed asks nothing any more either; its failure is written
            // stamped since CLI 1.0.10x, and by its task call before.
            case "subagent.failed":
                {
                    var agent = Json.Str(e, "agentId") is { Length: > 0 } a ? a : S.Background.AgentOf(Json.Str(data, "toolCallId"));
                    if (agent is null || S.Pending.RemoveAll(q => q.Agent is { } p && (p == agent || S.Background.HasEnded(p))) == 0) { return false; }
                    break;
                }
            case "session.shutdown" when !sub:
                S.Abandon(at);
                break;
            // An error that ends the turn (AgentErrors.Copilot): the turn is over, so the session
            // is idle, with the error or the sign-in it needs beside it. A delegate's is its own.
            case "session.error" when !sub:
                {
                    if (AgentErrors.Copilot(data, Now(at)) is not { } attention) { return false; }
                    _turnError = attention.Message;
                    S.FailTurn(at);
                    Failed(attention);
                    _turnAskedForTools = false;
                    break;
                }
            case "tool.execution_start":
                {
                    var id = Json.Str(data, "toolCallId");
                    var name = Json.Str(data, "toolName");
                    if (id is null || name is null) { break; }
                    var arguments = Json.Get(data, "arguments");
                    var agent = sub ? Json.Str(e, "agentId") : null;
                    S.Tools.Start(id, name, ToolSummary.From(arguments), Now(at), agent);
                    // A sub-agent's ask_user is its question (status rule 2). Copilot gives its
                    // sub-agents no ask_user: none of 33,966 sub-agent tool calls in two months
                    // of transcripts was one. Read the same way should a later one.
                    if (name == "ask_user" && arguments is { } args)
                    {
                        S.Ask(QuestionMapper.FromCopilotAskUser(id, args, Now(at)) with { Agent = agent });
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
                    // A task call returning ends its agent, failed or not, and whatever that
                    // agent was still asking is asked of no one: left pending, the session and
                    // the failed delegate read waiting.
                    S.Pending.RemoveAll(q => q.Agent is { } a && S.Background.HasEnded(a));
                    S.Activity(at);
                    break;
                }
            case "human_response.recorded":
                S.Answer(Json.Str(data, "toolCallId"));
                S.Activity(at);
                break;
            case "permission.requested":
                Permission(data, Now(at), Json.Str(e, "agentId"));
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

    // A turn starting leaves the last one's error behind it, as the CLI's own session list clears
    // its error on a prompt. Never a sign-in: VM1's Quality session (#800) signed out at 16:45:05,
    // ended a turn at 16:50:55 and failed sign-in again 51 ms later, and Migration's turn_end at
    // 16:47:25 proved nothing either. A prompt, a turn starting or ending, a hook and anything the
    // bridge typed are all written whether the agent can reach its model or not.
    void NewTurn()
    {
        _turnError = null;
        if (S.Attention is { Kind: not AttentionKind.SignInNeeded }) { S.Attention = null; }
    }

    // Only the model answering proves a sign-in good again: text of its own or a tool it asked
    // for, written after the latest sign-in failure (a later failure sets it again). Not an empty
    // message, and not the CLI's own "Turn error" notice, which it writes as an assistant.message
    // carrying no model's words (emitDetachedTurnError, cli-main.js 1.0.105).
    void Answered(JsonElement data, DateTimeOffset? at)
    {
        // Another error is the next turn's to clear (NewTurn): the CLI's turn-error notice
        // follows one as an assistant.message, and it would never have been seen.
        if (S.Attention is not { Kind: AttentionKind.SignInNeeded } attention) { return; }
        var content = Text.NonBlank(Json.Str(data, "content"));
        if ((Asked(data) || (content is not null && !TurnErrorNotice(data))) && at is { } t && t > attention.Since) { S.Attention = null; }
    }

    // The main agent's text as shown; a model's reply is the model's. The CLI's turn-error notice
    // carries the error's own words, which can hold a credential, a path or an address, and reach
    // every client, HA and a finished push: it is shown as the host's sentence for the error that
    // ended the turn (AgentErrors.Template), as attention.message is, and none of its words
    // (#846). Redacting them was a blocklist that missed a new shape every round (#840, #841).
    string? MainText(JsonElement data) =>
        TurnErrorNotice(data)
            ? TurnErrorNoticeText + " " + (_turnError ?? AgentErrors.Template(AttentionKind.Error, ErrorClass.Other, null, AgentKind.Copilot))
            : Text.NonBlank(Json.Str(data, "content"));

    static bool Asked(JsonElement data) => Json.Arr(data, "toolRequests") is { } requests && requests.GetArrayLength() > 0;

    // emitDetachedTurnError (cli-main.js 1.0.105): "⚠️ Turn error: …" with no tool requested.
    static bool TurnErrorNotice(JsonElement data) =>
        !Asked(data) && Text.NonBlank(Json.Str(data, "content")) is { } content && content.StartsWith(TurnErrorNoticeText, StringComparison.Ordinal);

    // Another error while it needs sign-in (a slash command failing, say) is no proof it can
    // reach its model, and the next prompt would clear it.
    void Failed(Attention attention)
    {
        if (attention.Kind == AttentionKind.SignInNeeded || S.Attention is not { Kind: AttentionKind.SignInNeeded }) { S.Attention = attention; }
    }

    const string TurnErrorNoticeText = "\u26A0\uFE0F Turn error:";

    // A permission request is asked of the person like a question, so it is one, of kind
    // approval, carrying in full what it would allow (CopilotPermission). A host with
    // remote approval off, or a prompt of a kind it does not drive, shows it as answered
    // only at the machine. A sub-agent's is written unstamped, naming only the tool call it
    // gates, so the call's own stamp says whose it is. Its prompt is the CLI's one
    // permission dialog whoever asked, so it is answered exactly as the main agent's is.
    void Permission(JsonElement data, DateTimeOffset at, string? stamped)
    {
        if (CopilotPermission.Describe(data) is not { } prompt) { return; }
        var id = prompt.QuestionId;
        _permissionQuestion[prompt.RequestId] = id;
        var agent = stamped ?? (prompt.ToolCallId is { } call ? S.Tools.Find(call)?.Agent ?? S.Background.CallAgent(call) : null);
        // An agent asking is working, though a cold read may have seen nothing else of its run.
        if (agent is not null) { S.Background.AgentActive(agent, at); }
        S.Ask(QuestionMapper.Approval(id, prompt.Message, at, atMachine: !remoteApproval || prompt.AtMachine is not null) with { Agent = agent });
    }

    void Context(JsonElement? context)
    {
        if (context is not { } c) { return; }
        S.Cwd = Json.Str(c, "cwd") ?? S.Cwd;
        S.Repository = Json.Str(c, "repository") ?? S.Repository;
        S.Branch = Json.Str(c, "branch") ?? S.Branch;
    }
}
