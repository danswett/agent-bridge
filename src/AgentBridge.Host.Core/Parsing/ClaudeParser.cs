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
internal sealed class ClaudeParser(SessionState state, TimeProvider time) : TranscriptParser(state, time)
{
    static readonly IReadOnlyList<byte[]> Markers =
    [
        Marker("\"user\""),
        Marker("\"compact_boundary\""),
        Marker("\"ai-title\""),
        Marker("\"summary\""),
    ];

    protected override IReadOnlyList<byte[]> SkimMarkers => Markers;

    // The message whose text the response holds, so its later blocks extend it.
    string? _responseMessage;

    public override void Reset()
    {
        base.Reset();
        _responseMessage = null;
    }

    protected override void Count(JsonElement e)
    {
        if (Json.Bool(e, "isSidechain") == true) { return; }
        switch (Json.Str(e, "type"))
        {
            case "user" when Json.Bool(e, "isMeta") != true && Json.Bool(e, "isCompactSummary") != true:
                S.StartedAt ??= Json.Time(e, "timestamp");
                if (Prompt(e) is { } text && !IsInterrupt(text))
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
                return false;
            default:
                // attachment, file-history-snapshot, last-prompt, mode, cost-state and
                // the like: bookkeeping no client shows.
                return false;
        }
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
