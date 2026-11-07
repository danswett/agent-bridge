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
//     queue's "remove" (reason "delivered_to_agent"). The delivery is marked as Claude's
//     (origin.kind "task-notification", turnOrigin "task_notification"); the queue is not, and a
//     prompt the person types while the agent works is queued too, as "human" when delivered.
//     So only a marked delivery, or a remove delivering a notice queued earlier, ends anything.
//     An agent notifies only once none of its own background agents is
//     live, and notifies again each time a message (SendMessage, as its launch says) has
//     woken it and it stops.
// A nested agent's start and parent are written only beside it (ClaudeSubagents, Nested).
// What any sub-agent says is in its own transcript, never in the session's response.
// Sign-in and turn-ending errors (session.attention): TODO(#800), once a real redacted sample of
// Claude Code's is captured (AgentErrors). None was on VM1, and no string is guessed meanwhile.
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

    protected override IReadOnlyList<byte[]> SkimMarkers => Markers;

    protected override Conversation.TurnMarks Turns => Conversation.TurnMarks.Claude;

    // The message whose text the response holds, so its later blocks extend it.
    string? _responseMessage;
    // Notices queued and not yet delivered, by task id, oldest first, each by its content's hash.
    // The queue carries no mark of who wrote to it, and a prompt the person types while the agent
    // works is queued the same way, so a queued notice is not an end until its delivery is (see
    // Delivered); the queue says only when that end came.
    readonly Dictionary<string, List<(DateTimeOffset? At, string Hash)>> _queued = new(StringComparer.Ordinal);
    const int MaxQueuedPerAgent = 8;
    // When a message last woke each agent: a delivered notice is of a run before it when it
    // was queued before that.
    readonly Dictionary<string, DateTimeOffset?> _wokenAt = new(StringComparer.Ordinal);
    // Ends of agents not known yet: a nested agent is known only from its own record, which
    // can be read after the session's transcript says it stopped. The latest is kept. As many as
    // there can be records (ClaudeSubagents); past that, a record with no end kept is not known
    // to be running.
    readonly Dictionary<string, (SubagentOutcome Outcome, DateTimeOffset? At)> _unclaimed = new(StringComparer.Ordinal);
    bool _unclaimedDropped;

    public override void Reset()
    {
        base.Reset();
        _responseMessage = null;
        _queued.Clear();
        _wokenAt.Clear();
        _unclaimed.Clear();
        _unclaimedDropped = false;
    }

    // A command or delegate the skim saw start may have ended in bytes never read; counted, it
    // would read as running for as long as its CLI lives. One still running is said again in the
    // window, a delegate by its notice; until then it is listed with no outcome.
    public override void Skipped()
    {
        S.Background.ForgetShells();
        S.Background.ForgetRuns();
        // A queued notice may have been delivered, and an agent woken and stopped again, in the
        // bytes never read. An end not claimed yet keeps its outcome.
        _queued.Clear();
        _wokenAt.Clear();
    }

    protected override void Count(JsonElement e)
    {
        if (Json.Bool(e, "isSidechain") == true) { return; }
        Delegation(e, Json.Time(e, "timestamp"));
        switch (Json.Str(e, "type"))
        {
            case "user" when Json.Bool(e, "isMeta") != true && Json.Bool(e, "isCompactSummary") != true:
                S.StartedAt ??= Json.Time(e, "timestamp");
                if (Prompt(e) is not { } text || IsInterrupt(text)) { break; }
                // Which turn the last is, a prompt's or a notice's, holds past the skim: the
                // window may begin inside it.
                if (IsNotice(e, text)) { S.UncountedTurn = true; break; }
                S.FirstUserMessage ??= text;
                S.UserMessage = text;
                TurnStarts();
                S.Prompt(Json.Time(e, "timestamp"));

                S.UncountedTurn = false;
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
            case "queue-operation" when Json.Str(e, "content") is { } queued && IsNoticeText(queued):
                switch (Json.Str(e, "operation"))
                {
                    case "enqueue": Queue(queued, at); break;
                    // Delivered to the sub-agent that started it, not to the main agent.
                    case "remove" when Json.Str(e, "reason") == "delivered_to_agent": Delivered(queued, at, confirmed: false); break;
                }
                break;
            case "user":
                {
                    var message = Json.Obj(e, "message");
                    if (message is { } m && Json.Get(m, "content") is { ValueKind: JsonValueKind.String } content && Json.Text(content) is { } text && IsNotice(e, text))
                    {
                        Delivered(text, at, confirmed: true);
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
                        && Json.Obj(block, "input") is { } input && Json.Str(input, "to") is { } to)
                    {
                        work.AgentActive(to, at);
                        // Also for a nested agent whose record is not read yet.
                        if (_wokenAt.ContainsKey(to) || _wokenAt.Count < ClaudeSubagents.MaxRecords) { _wokenAt[to] = at; }
                    }
                }
                break;
        }
    }

    // A notice queued: when the agent or command it names stopped, should its delivery say so.
    void Queue(string text, DateTimeOffset? at)
    {
        if (Tag(text, "task-id") is not { } id) { return; }
        if (!_queued.TryGetValue(id, out var queued))
        {
            if (_queued.Count >= ClaudeSubagents.MaxRecords) { _queued.Remove(_queued.MinBy(p => p.Value[0].At ?? DateTimeOffset.MinValue).Key); }
            _queued[id] = queued = [];
        }
        if (queued.Count >= MaxQueuedPerAgent) { queued.RemoveAt(0); }
        queued.Add((at, Hash(text)));
    }

    // A delegate or a background command stopped: "completed" is done, any other status failed.
    // Only Claude's own delivery says so: a user entry it marks as a task notification (confirmed),
    // or the queue's "remove" delivering one it queued to the sub-agent that started the agent
    // (Claude delivers nothing the person typed to a sub-agent). The end is when it was queued,
    // when that was read; a run a message started after then is still going.
    void Delivered(string text, DateTimeOffset? at, bool confirmed)
    {
        if (Tag(text, "task-id") is not { } id) { return; }
        var outcome = Tag(text, "status") == "completed" ? SubagentOutcome.Done : SubagentOutcome.Failed;
        var queuedAt = Dequeue(id, Hash(text), out var wasQueued);
        if (!confirmed && !wasQueued) { return; }
        var endedAt = wasQueued ? queuedAt ?? at : at;
        var work = S.Background;
        if (work.Knows(id))
        {
            if (WokenSince(id, endedAt) is { } woken) { work.AgentActive(id, woken); return; }
            if (work.AgentIdle(id, endedAt, outcome)) { work.DescendantsStopped(id, endedAt, a => WokenSince(a, endedAt) is not null); }
            return;
        }
        if (work.ShellEnded(id)) { return; }
        if (!_unclaimed.ContainsKey(id) && _unclaimed.Count >= ClaudeSubagents.MaxRecords)
        {
            _unclaimed.Remove(_unclaimed.MinBy(p => p.Value.At ?? DateTimeOffset.MinValue).Key);
            _unclaimedDropped = true;
        }
        _unclaimed[id] = (outcome, endedAt);
    }

    DateTimeOffset? Dequeue(string id, string hash, out bool found)
    {
        found = false;
        if (!_queued.TryGetValue(id, out var queued)) { return null; }
        var i = queued.FindIndex(q => q.Hash == hash);
        if (i < 0) { return null; }
        var at = queued[i].At;
        queued.RemoveAt(i);
        if (queued.Count == 0) { _queued.Remove(id); }
        found = true;
        return at;
    }

    DateTimeOffset? WokenSince(string id, DateTimeOffset? endedAt) =>
        _wokenAt.TryGetValue(id, out var woken) && woken is { } w && endedAt is { } e && w > e ? w : null;

    // Hashed in pieces, so a notice holding a long result is never copied whole.
    static string Hash(string text)
    {
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        Span<byte> bytes = stackalloc byte[4096 * 3];
        var chars = text.AsSpan();
        while (!chars.IsEmpty)
        {
            var take = Math.Min(chars.Length, 4096);
            // Never split a surrogate pair across pieces.
            if (take < chars.Length && char.IsHighSurrogate(chars[take - 1])) { take--; }
            var n = System.Text.Encoding.UTF8.GetBytes(chars[..take], bytes);
            sha.AppendData(bytes[..n]);
            chars = chars[take..];
        }
        return Convert.ToHexString(sha.GetHashAndReset());
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
            work.AgentPrompt(record.Id, record.Prompt);
            return false;
        }
        // Listed only under a delegate that is: one whose start was in bytes never read says
        // nothing of whether this one still runs. Records are read parents first.
        if (!work.Knows(parent)) { return false; }
        var before = work.Version;
        work.AgentStarted(record.ToolUseId, record.Id, record.StartedAt, record.Background, info);
        work.AgentPrompt(record.Id, record.Prompt);
        if (_unclaimed.Remove(record.Id, out var end))
        {
            work.AgentIdle(record.Id, end.At, end.Outcome);
            if (WokenSince(record.Id, end.At) is { } woken) { work.AgentActive(record.Id, woken); }
        }
        else if (_unclaimedDropped) { work.ForgetRun(record.Id); }
        // Started before the agent above it began its current run: in an earlier one, which
        // was notified only once this one had stopped, though its own end was not read.
        else if (record.StartedAt is { } started && work.RunSince(parent) is { } since && started < since) { work.ForgetRun(record.Id); }
        else if (work.Stopped(parent, out var parentEnded, out var parentOutcome))
        {
            // One whose own end was never read (bytes the tail skipped) says nothing of this one's.
            if (parentOutcome is not null)
            {
                work.AgentIdle(record.Id, parentEnded ?? record.StartedAt);
                // A message woke it after the agent above it ended.
                if (WokenSince(record.Id, parentEnded) is { } woken) { work.AgentActive(record.Id, woken); }
            }
            else { work.ForgetRun(record.Id); }
        }
        return work.Version != before;
    }

    static bool IsNoticeText(string text) => text.StartsWith(NoticeStart, StringComparison.Ordinal);

    // A notice Claude delivered, marked as such (origin.kind "task-notification", turnOrigin
    // "task_notification", as 2.1.316 writes). The person can type the same text: Claude marks
    // that "human", and it is their prompt like any other.
    internal static bool IsNotice(JsonElement e, string text) => IsNoticeText(text)
        && (Json.Obj(e, "origin") is { } origin && Json.Str(origin, "kind") == "task-notification" || Json.Str(e, "turnOrigin") == "task_notification");

    internal static string? Tag(string text, string name)
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
        if (IsNotice(e, text))
        {
            // A turn of the main agent's, about a delegate or a command that stopped: neither
            // the person's message nor a prompt counted, and the text in it is the delegate's.
            S.UncountedTurn = true;
            S.ForgetReasoning();
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
                    S.ResponseTurn = S.TurnId;
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

    internal static bool IsInterrupt(string text) => text.StartsWith("[Request interrupted by user", StringComparison.Ordinal);

    // Whether a record starts a turn (docs/protocol/v1-history.md), and its prompt: exactly the
    // entries Count and User take as the person's prompt, so history's turn ids are activity.turn's.
    internal static string? TurnStart(JsonElement e)
    {
        if (Json.Str(e, "type") != "user" || Json.Bool(e, "isSidechain") == true || Json.Bool(e, "isMeta") == true || Json.Bool(e, "isCompactSummary") == true) { return null; }
        if (Prompt(e) is not { } text || IsInterrupt(text) || IsNotice(e, text)) { return null; }
        return text;
    }

    // The text of a prompt, or null for an entry that is not one: tool results, and the
    // echoes of slash commands, which Claude writes as user entries.
    internal static string? Prompt(JsonElement e)
    {
        var message = Json.Obj(e, "message");
        string? text;
        if (message is { } m && Json.Get(m, "content") is { ValueKind: JsonValueKind.String } s) { text = Json.Text(s); }
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
