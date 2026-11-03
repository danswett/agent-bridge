using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Parsing;

// Claude Code's ~/.claude/projects/<slug>/<id>.jsonl. Each line has type, message,
// timestamp, isSidechain and isMeta; message.content is a string or an array of text,
// thinking, tool_use and tool_result blocks.
//
// Claude writes no turn-start record, and the old bridge took idle only from its Stop
// hook. Without the hook, idle is inferred here, in this order:
//   1. A `system` entry with subtype `turn_duration` (or `stop_hook_summary`): Claude
//      2.1.2xx and later write these as a turn ends. Authoritative.
//   2. An `assistant` entry whose message has stop_reason `end_turn` or `stop_sequence`,
//      carries no tool_use, and leaves no tool call running: the model finished speaking
//      and asked for nothing. Older builds write no turn_duration, so this is what they
//      get; an assistant entry still streaming has stop_reason null and stays working.
//   3. "[Request interrupted by user…]" from the user ends the turn.
// The registration's HookStatus, when agent-ha-bridge's hooks are installed and newer
// than the transcript, is applied on top by the tracker.
//
// Sidechain entries are a Task subagent's and meta entries housekeeping; neither is the
// session's own activity. Slash-command echoes (<command-name>, <local-command-stdout>)
// are written as user entries but are not prompts and start no turn.
//
// Delegates (docs/design/subagents.md §3), as Claude Code 2.1.316 records them in the
// session's own transcript (tests/fixtures/claude):
//   - An Agent call whose tool_result carries a toolUseResult with status "async_launched"
//     started a background sub-agent: its agentId, description (the name), prompt (the task)
//     and resolvedModel (its model). 2.1.316 launches every Agent call that way.
//   - A Bash call with run_in_background: its toolUseResult.backgroundTaskId is a command
//     running in the background.
//   - A <task-notification> names either by its <task-id> once it stops, with a <status>. It
//     is queued (a queue-operation "enqueue") the moment it stops, for every agent at every
//     depth, and delivered later as a user entry to whoever started it: the main agent's is a
//     turn of the main agent's, not a prompt of the person's, and holds the sub-agent's
//     result; a sub-agent's is in that agent's own transcript, and this one has only the
//     queue's "remove". An agent notifies only once none of its own background agents is
//     live, and notifies again each time a message (SendMessage, as its launch says) has
//     woken it and it stops.
// A nested agent's start and parent are written only beside it (ClaudeSubagents, Nested).
// What any sub-agent says is in its own transcript, never in the session's response.
internal sealed class ClaudeParser(SessionState state, TimeProvider time) : TranscriptParser(state, time)
{
    static readonly IReadOnlyList<byte[]> Markers =
    [
        Marker("\"user\""),
        Marker("\"compact_boundary\""),
        Marker("\"ai-title\""),
        Marker("\"summary\""),
        // A delegate's or a background command's end, and a message waking a delegate: an
        // agent started long before the window is still listed as it is now.
        Marker("\"queue-operation\""),
        Marker("\"SendMessage\""),
    ];

    const string NoticeStart = "<task-notification>";
    const int KeepNotices = 64;

    protected override IReadOnlyList<byte[]> SkimMarkers => Markers;

    // The message whose text the response holds, so its later blocks extend it.
    string? _responseMessage;
    // Notices read as they were queued: the same notice delivered later as a user entry is not
    // a second end, and by then a message may have woken the agent again.
    readonly HashSet<string> _queued = new(StringComparer.Ordinal);
    readonly Queue<string> _queuedOrder = new();
    // Ends of agents not known yet: a nested agent is known only from its own record, which
    // can be read after the session's transcript says it stopped.
    readonly Dictionary<string, (SubagentOutcome Outcome, DateTimeOffset? At)> _unclaimed = new(StringComparer.Ordinal);
    readonly Queue<string> _unclaimedOrder = new();

    public override void Reset()
    {
        base.Reset();
        _responseMessage = null;
        _queued.Clear();
        _queuedOrder.Clear();
        _unclaimed.Clear();
        _unclaimedOrder.Clear();
    }

    // A command or delegate the skim saw start may have ended in bytes never read; counted, it
    // would read as running for as long as its CLI lives. One still running is said again in the
    // window, a delegate by its notice; until then it is listed with no outcome.
    public override void Skipped()
    {
        S.Background.ForgetShells();
        S.Background.ForgetRuns();
    }

    protected override void Count(JsonElement e)
    {
        if (Json.Bool(e, "isSidechain") == true) { return; }
        Delegation(e, Json.Time(e, "timestamp"));
        switch (Json.Str(e, "type"))
        {
            case "user" when Json.Bool(e, "isMeta") != true && Json.Bool(e, "isCompactSummary") != true:
                S.StartedAt ??= Json.Time(e, "timestamp");
                if (Prompt(e) is { } text && !IsInterrupt(text) && !IsNotice(text))
                {
                    S.FirstUserMessage ??= text;
                    S.UserMessage = text;
                    S.Prompts++;
                }
                break;
            case "system" when Json.Str(e, "subtype") == "compact_boundary":
                S.Compactions++;
                break;
            case "ai-title":
                S.AgentTitle = Text.NonBlank(Json.Str(e, "aiTitle")) ?? S.AgentTitle;
                break;
            case "summary":
                S.AgentTitle ??= Text.NonBlank(Json.Str(e, "summary"));
                break;
        }
    }

    protected override bool Apply(JsonElement e, DateTimeOffset? at)
    {
        var type = Json.Str(e, "type");
        if (type is "ai-title" or "summary") { Count(e); return true; }
        if (Json.Bool(e, "isSidechain") == true || Json.Bool(e, "isMeta") == true) { return false; }
        var before = S.Background.Version;
        Delegation(e, at);
        var delegates = S.Background.Version != before;
        if (type is "user" or "assistant")
        {
            S.Cwd = Json.Str(e, "cwd") ?? S.Cwd;
            S.Branch = Text.NonBlank(Json.Str(e, "gitBranch")) ?? S.Branch;
            S.StartedAt ??= at;
        }
        switch (type)
        {
            case "user":
                User(e, at);
                return true;
            case "assistant":
                Assistant(e, at);
                return true;
            case "system":
                switch (Json.Str(e, "subtype"))
                {
                    case "turn_duration" or "stop_hook_summary":
                        S.SetStatus(SessionStatus.Idle);
                        return true;
                    case "compact_boundary":
                        S.Compactions++;
                        return true;
                }
                return delegates;
            default:
                // attachment, file-history-snapshot, last-prompt, mode, cost-state and
                // the like: bookkeeping no client shows. A queued notice can end a delegate.
                return delegates;
        }
    }

    // What an entry of the session's own says about its delegates and background commands.
    void Delegation(JsonElement e, DateTimeOffset? at)
    {
        if (Json.Bool(e, "isMeta") == true) { return; }
        var work = S.Background;
        switch (Json.Str(e, "type"))
        {
            case "queue-operation" when Json.Str(e, "operation") == "enqueue" && Json.Str(e, "content") is { } queued && IsNotice(queued):
                Notice(queued, at, queued: true);
                break;
            case "user":
                {
                    var message = Json.Obj(e, "message");
                    if (message is { } m && Json.Get(m, "content") is { ValueKind: JsonValueKind.String } content && content.GetString() is { } text && IsNotice(text))
                    {
                        Notice(text, at, queued: false);
                        break;
                    }
                    if (Json.Obj(e, "toolUseResult") is not { } result) { break; }
                    var call = Blocks(message).Where(b => Json.Str(b, "type") == "tool_result").Select(b => Json.Str(b, "tool_use_id")).FirstOrDefault();
                    if (Json.Str(result, "status") == "async_launched" && Text.NonBlank(Json.Str(result, "agentId")) is { } agent)
                    {
                        work.AgentStarted(call, agent, at, background: true, new BackgroundWork.StartInfo(DisplayName: Json.Str(result, "description"), Model: Json.Str(result, "resolvedModel")));
                        work.AgentPrompt(agent, Json.Str(result, "prompt"));
                    }
                    else if (Text.NonBlank(Json.Str(result, "backgroundTaskId")) is { } shell)
                    {
                        work.ShellStarted(shell, at);
                    }
                    break;
                }
            case "assistant":
                foreach (var block in Blocks(Json.Obj(e, "message")))
                {
                    if (Json.Str(block, "type") == "tool_use" && Json.Str(block, "name") == "SendMessage"
                        && Json.Obj(block, "input") is { } input && Json.Str(input, "to") is { } to && work.Knows(to))
                    {
                        work.AgentActive(to, at);
                    }
                }
                break;
        }
    }

    // A delegate or a background command stopped: "completed" is done, any other status failed.
    void Notice(string text, DateTimeOffset? at, bool queued)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        if (queued) { Keep(_queued, _queuedOrder, key); }
        else if (_queued.Contains(key)) { return; }
        if (Tag(text, "task-id") is not { } id) { return; }
        var outcome = Tag(text, "status") == "completed" ? SubagentOutcome.Done : SubagentOutcome.Failed;
        var work = S.Background;
        if (work.Knows(id))
        {
            work.AgentIdle(id, at, outcome);
            work.DescendantsStopped(id, at);
            return;
        }
        work.ShellEnded(id);
        if (!_unclaimed.ContainsKey(id))
        {
            if (_unclaimed.Count >= KeepNotices) { _unclaimed.Remove(_unclaimedOrder.Dequeue()); }
            _unclaimedOrder.Enqueue(id);
        }
        _unclaimed[id] = (outcome, at);
    }

    static void Keep(HashSet<string> set, Queue<string> order, string key)
    {
        if (!set.Add(key)) { return; }
        order.Enqueue(key);
        if (order.Count > KeepNotices) { set.Remove(order.Dequeue()); }
    }

    // A sub-agent another one started, known from its own record: listed as working unless the
    // session's transcript already said it stopped, or the agent above it has stopped since (it
    // notifies only once none under it is live). Says whether that changed what is listed.
    public bool Nested(ClaudeSubagents.Record record)
    {
        var work = S.Background;
        if (record.Parent is not { } parent) { return false; }
        var info = new BackgroundWork.StartInfo(DisplayName: record.Description, AgentType: record.AgentType, Parent: parent);
        // Known already: only a start its record could not say at first (its own transcript
        // not written yet) is filled in.
        if (work.Knows(record.Id))
        {
            if (record.StartedAt is not null) { work.KnowMode(record.ToolUseId, record.Id, record.Background, info, record.StartedAt); }
            return false;
        }
        // Listed only under a delegate that is: one whose start was in bytes never read says
        // nothing of whether this one still runs. Records are read parents first.
        if (!work.Knows(parent)) { return false; }
        var before = work.Version;
        work.AgentStarted(record.ToolUseId, record.Id, record.StartedAt, record.Background, info);
        work.AgentPrompt(record.Id, record.Prompt);
        if (_unclaimed.Remove(record.Id, out var end)) { work.AgentIdle(record.Id, end.At, end.Outcome); }
        else if (work.Stopped(parent, out var parentEnded, out var parentOutcome))
        {
            // One whose own end was never read (bytes the tail skipped) says nothing of this one's.
            if (parentOutcome is not null) { work.AgentIdle(record.Id, parentEnded ?? record.StartedAt); }
            else { work.ForgetRun(record.Id); }
        }
        return work.Version != before;
    }

    static bool IsNotice(string text) => text.StartsWith(NoticeStart, StringComparison.Ordinal);

    static string? Tag(string text, string name)
    {
        var open = $"<{name}>";
        var start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) { return null; }
        start += open.Length;
        var end = text.IndexOf($"</{name}>", start, StringComparison.Ordinal);
        return end < 0 ? null : Text.NonBlank(text[start..end].Trim());
    }

    void User(JsonElement e, DateTimeOffset? at)
    {
        if (Json.Bool(e, "isCompactSummary") == true) { return; }
        var message = Json.Obj(e, "message");
        var results = Blocks(message).Where(b => Json.Str(b, "type") == "tool_result").ToList();
        if (results.Count > 0)
        {
            foreach (var r in results)
            {
                var id = Json.Str(r, "tool_use_id");
                S.Tools.End(id, Json.Bool(r, "is_error") != true, at);
                S.Answer(id);
            }
            S.SetStatus(SessionStatus.Working);
            S.Activity(at);
            return;
        }
        if (Prompt(e) is not { } text) { return; }
        if (IsNotice(text))
        {
            // A turn of the main agent's, about a delegate or a command that stopped: neither
            // the person's message nor a prompt counted, and the text in it is the delegate's.
            S.SetStatus(SessionStatus.Working);
            S.Activity(at);
            return;
        }
        if (IsInterrupt(text))
        {
            S.Abandon(at);
            S.Activity(at);
            return;
        }
        UserPrompt(text, at);
    }

    void Assistant(JsonElement e, DateTimeOffset? at)
    {
        if (Json.Obj(e, "message") is not { } message) { return; }
        // "<synthetic>" marks a message Claude wrote itself (an API error, say); it says
        // nothing about which model the session runs.
        if (Text.NonBlank(Json.Str(message, "model")) is { } model && !model.StartsWith('<')) { S.Model = model; }
        var askedForTools = false;
        // Without an id, the entry itself is the message.
        var key = Json.Str(message, "id") ?? Guid.NewGuid().ToString("N");
        foreach (var block in Blocks(message))
        {
            switch (Json.Str(block, "type"))
            {
                case "thinking" when Text.NonBlank(Json.Str(block, "thinking")) is { } thinking:
                    S.Reasoning = thinking;
                    break;
                case "text" when Text.NonBlank(Json.Str(block, "text")) is { } text:
                    // One reply can hold several text blocks, in one entry or (as Claude
                    // streams them) in several entries sharing the message id: they are
                    // one response, not each other's replacements.
                    S.Response = key == _responseMessage && S.Response is { } sofar ? sofar + "\n\n" + text : text;
                    _responseMessage = key;
                    break;
                case "tool_use":
                    {
                        var id = Json.Str(block, "id");
                        var name = Json.Str(block, "name");
                        if (id is null || name is null) { break; }
                        askedForTools = true;
                        var input = Json.Get(block, "input");
                        S.Tools.Start(id, name, ToolSummary.From(input), Now(at));
                        if (name == "AskUserQuestion" && input is { } i)
                        {
                            S.Ask(QuestionMapper.FromClaudeAskUserQuestion(id, i, Now(at)));
                        }
                        break;
                    }
            }
        }
        var stop = Json.Str(message, "stop_reason");
        var finished = stop is "end_turn" or "stop_sequence" && !askedForTools && !S.Tools.AnyRunning;
        S.SetStatus(finished ? SessionStatus.Idle : SessionStatus.Working);
        S.Activity(at);
    }

    static bool IsInterrupt(string text) => text.StartsWith("[Request interrupted by user", StringComparison.Ordinal);

    // The text of a prompt, or null for an entry that is not one: tool results, and the
    // echoes of slash commands, which Claude writes as user entries.
    static string? Prompt(JsonElement e)
    {
        var message = Json.Obj(e, "message");
        string? text;
        if (message is { } m && Json.Get(m, "content") is { ValueKind: JsonValueKind.String } s) { text = s.GetString(); }
        else
        {
            var blocks = Blocks(message).ToList();
            if (blocks.Any(b => Json.Str(b, "type") == "tool_result")) { return null; }
            var sb = new StringBuilder();
            foreach (var b in blocks.Where(b => Json.Str(b, "type") == "text"))
            {
                if (sb.Length > 0) { sb.Append('\n'); }
                sb.Append(Json.Str(b, "text"));
            }
            text = sb.ToString();
        }
        text = Text.NonBlank(text);
        if (text is null) { return null; }
        if (text.StartsWith("<command-", StringComparison.Ordinal) || text.StartsWith("<local-command-", StringComparison.Ordinal)) { return null; }
        return text;
    }

    static IEnumerable<JsonElement> Blocks(JsonElement? message)
    {
        if (message is not { } m || Json.Arr(m, "content") is not { } content) { yield break; }
        foreach (var b in content.EnumerateArray())
        {
            if (b.ValueKind == JsonValueKind.Object) { yield return b; }
        }
    }
}
