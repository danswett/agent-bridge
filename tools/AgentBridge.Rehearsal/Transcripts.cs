using System.Text;
using System.Text.Json;

namespace AgentBridge.Rehearsal;

internal sealed record Match(bool Found, bool Final, string Detail, object? Data = null)
{
    public static readonly Match Waiting = new(false, false, "nothing yet");
    public static Match Yes(string detail, object? data = null) => new(true, true, detail, data);
    // A record that answers the step, differently: waiting longer cannot change it.
    public static Match No(string detail, object? data = null) => new(false, true, detail, data);
}

// The agents' own records of what happened, read after a mark: an answer recorded, a prompt
// taken, a permission decided, a session shut down. Read the way the host reads them (by the
// record's kind and content, from the main agent), and never written.
internal static class Transcripts
{
    public const string Copilot = "copilot", Claude = "claude", Codex = "codex";

    public static (string Kind, string Path)? Find(Homes homes, string sessionId)
    {
        if (!sessionId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') || sessionId.Length is < 8 or > 64) { return null; }
        var copilot = Path.Combine(homes.CopilotHome, "session-state", sessionId, "events.jsonl");
        if (File.Exists(copilot)) { return (Copilot, copilot); }
        var projects = Path.Combine(homes.ClaudeHome, "projects");
        if (Directory.Exists(projects))
        {
            foreach (var dir in Directory.EnumerateDirectories(projects))
            {
                var file = Path.Combine(dir, sessionId + ".jsonl");
                if (File.Exists(file)) { return (Claude, file); }
            }
        }
        var sessions = Path.Combine(homes.CodexHome, "sessions");
        if (Directory.Exists(sessions))
        {
            var rollout = Directory.EnumerateFiles(sessions, $"rollout-*-{sessionId}.jsonl", SearchOption.AllDirectories).FirstOrDefault();
            if (rollout is not null) { return (Codex, rollout); }
        }
        return null;
    }

    // The session whose transcript was written last, for when no host is there to ask.
    public static (string Kind, string Id)? Newest(Homes homes, string agent)
    {
        IEnumerable<(string Id, DateTime At)> candidates = agent switch
        {
            // Only the session's own file: subagents keep theirs further down.
            Copilot => Dirs(Path.Combine(homes.CopilotHome, "session-state")).Select(d => Path.Combine(d, "events.jsonl")).Where(File.Exists)
                .Select(f => (Path.GetFileName(Path.GetDirectoryName(f))!, File.GetLastWriteTimeUtc(f))),
            Claude => Dirs(Path.Combine(homes.ClaudeHome, "projects")).SelectMany(d => Files(d, "*.jsonl", SearchOption.TopDirectoryOnly))
                .Select(f => (Path.GetFileNameWithoutExtension(f), File.GetLastWriteTimeUtc(f))),
            Codex => Files(Path.Combine(homes.CodexHome, "sessions"), "rollout-*.jsonl", SearchOption.AllDirectories)
                .Select(f => (CodexId(Path.GetFileNameWithoutExtension(f)), File.GetLastWriteTimeUtc(f))),
            _ => throw new UsageException("the agent is copilot, claude or codex"),
        };
        var newest = candidates.Where(c => c.Id.Length > 0).OrderByDescending(c => c.At).FirstOrDefault();
        return newest.Id is null ? null : (agent, newest.Id);
    }

    static IEnumerable<string> Dirs(string root) =>
        Directory.Exists(root) ? Directory.EnumerateDirectories(root, "*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }) : [];

    static IEnumerable<string> Files(string root, string pattern, SearchOption depth) =>
        Directory.Exists(root) ? Directory.EnumerateFiles(root, pattern, new EnumerationOptions { RecurseSubdirectories = depth == SearchOption.AllDirectories, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }) : [];

    // rollout-<time>-<uuid>: the id is the uuid at the end.
    static string CodexId(string name) => name.Length >= 36 && Guid.TryParse(name[^36..], out _) ? name[^36..] : "";

    // How many questions the session recorded as answered after a mark, counted by tool call
    // (Copilot records one answer twice: the structured response and the tool's result), and
    // how many prompts of the same words followed, which is where a second typing of a text
    // answer would land once the question had gone.
    public static (int Answers, int Strays) AnswerCount(string kind, string path, long from, string? text)
    {
        var calls = new HashSet<string>(StringComparer.Ordinal);
        var unnamed = 0;
        var strays = 0;
        foreach (var (_, e) in Lines(path, from))
        {
            if (kind == Copilot)
            {
                if (Str(e, "agentId") is { Length: > 0 } || Obj(e, "data") is not { } d) { continue; }
                switch (Str(e, "type"))
                {
                    case "human_response.recorded":
                    case "tool.execution_complete" when Obj(d, "result") is { } r && (Str(r, "content") ?? "").TrimStart().StartsWith("User responded: ", StringComparison.Ordinal):
                        if (Str(d, "toolCallId") is { Length: > 0 } id) { calls.Add(id); } else { unnamed++; }
                        break;
                    case "user.message" when text is not null && Str(d, "source") is not { Length: > 0 } && Str(d, "content") is { } words && Same(words, text):
                        strays++;
                        break;
                }
            }
            else if (kind == Claude && Str(e, "type") == "user" && !Bool(e, "isSidechain") && Obj(e, "toolUseResult") is { } result && Obj(result, "answers") is not null)
            {
                unnamed++;
            }
        }
        return (calls.Count + unnamed, strays);
    }

    // Complete lines from an offset, with where each starts; a line still being written waits.
    public static IEnumerable<(long Offset, JsonElement Entry)> Lines(string path, long from)
    {
        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (from > stream.Length) { from = 0; }
            stream.Seek(from, SeekOrigin.Begin);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }
        var start = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n') { continue; }
            JsonElement? entry = null;
            try
            {
                using var doc = JsonDocument.Parse(bytes.AsMemory(start, i - start));
                if (doc.RootElement.ValueKind == JsonValueKind.Object) { entry = doc.RootElement.Clone(); }
            }
            catch (JsonException) { }
            if (entry is { } e) { yield return (from + start, e); }
            start = i + 1;
        }
    }

    public static Match Answer(string kind, string path, long from, string? text, IReadOnlyDictionary<string, string> fields) => kind switch
    {
        Copilot => CopilotAnswer(path, from, text, fields),
        Claude => ClaudeAnswer(path, from, text, fields),
        _ => Match.No("Codex records nothing that says how a question or approval was answered; check the session at the machine"),
    };

    static Match CopilotAnswer(string path, long from, string? text, IReadOnlyDictionary<string, string> fields)
    {
        foreach (var (offset, e) in Lines(path, from))
        {
            if (Str(e, "agentId") is { Length: > 0 }) { continue; }
            var type = Str(e, "type");
            var data = Obj(e, "data");
            if (type == "human_response.recorded" && data is { } d)
            {
                var content = Obj(d, "response") is { } r && r.TryGetProperty("content", out var c) ? c : default;
                var (ok, why) = Compare(content, text, fields);
                var evidence = new { record = type, offset, toolCallId = Str(d, "toolCallId"), recorded = Show(content) };
                return ok ? Match.Yes("the session recorded the answer", evidence) : Match.No(why, evidence);
            }
            if (type == "tool.execution_complete" && data is { } done && text is not null && fields.Count == 0 &&
                Obj(done, "result") is { } result && Str(result, "content") is { } rendered && rendered.TrimStart().StartsWith("User responded: ", StringComparison.Ordinal))
            {
                var words = rendered.Trim()["User responded: ".Length..];
                var evidence = new { record = type, offset, toolCallId = Str(done, "toolCallId"), recorded = words };
                return Same(words, text) ? Match.Yes("the session recorded the words", evidence) : Match.No("the session recorded different words", evidence);
            }
        }
        return Match.Waiting;
    }

    static Match ClaudeAnswer(string path, long from, string? text, IReadOnlyDictionary<string, string> fields)
    {
        foreach (var (offset, e) in Lines(path, from))
        {
            if (Str(e, "type") != "user" || Bool(e, "isSidechain")) { continue; }
            if (Obj(e, "toolUseResult") is not { } result || Obj(result, "answers") is not { } answers) { continue; }
            var recorded = answers.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText());
            var evidence = new { record = "toolUseResult.answers", offset, recorded };
            foreach (var (name, value) in fields)
            {
                if (!recorded.TryGetValue(name, out var got) || !Same(got, value)) { return Match.No($"Claude recorded a different answer to \"{name}\"", evidence); }
            }
            if (text is not null && !recorded.Values.Any(v => Same(v, text))) { return Match.No("Claude recorded different words", evidence); }
            return Match.Yes("Claude recorded the answer", evidence);
        }
        return Match.Waiting;
    }

    // A prompt the person sent: Copilot's user.message with no source, Claude's user entry,
    // Codex's user_message event. With attachments, Copilot's content starts with an @path
    // mention per file, so the words are its end and the files its attachments.
    public static Match Reply(string kind, string path, long from, string text, int attachments, bool steering)
    {
        foreach (var (offset, e) in Lines(path, from))
        {
            switch (kind)
            {
                case Copilot:
                    {
                        if (Str(e, "type") != "user.message" || Str(e, "agentId") is { Length: > 0 } || Obj(e, "data") is not { } d) { continue; }
                        if (Str(d, "source") is { Length: > 0 }) { continue; }
                        var content = Str(d, "content") ?? "";
                        var files = d.TryGetProperty("attachments", out var a) && a.ValueKind == JsonValueKind.Array ? a.GetArrayLength() : 0;
                        var delivery = Str(d, "delivery");
                        var matches = attachments > 0 ? Flatten(content).EndsWith(Flatten(text), StringComparison.Ordinal) : Same(content, text);
                        if (!matches) { continue; }
                        var evidence = new { record = "user.message", offset, delivery, attachments = files, length = content.Length };
                        if (files < attachments) { return Match.No($"Copilot took the prompt with {files} of {attachments} attachment(s)", evidence); }
                        // Necessary, not sufficient: Copilot records interactive prompts as steering even with no
                        // turn running, so the checklist pairs this with a working status just before.
                        if (steering && delivery is not ("steering" or "queued")) { return Match.No($"Copilot took the prompt as \"{delivery ?? "(none)"}\", not as steering or queued", evidence); }
                        return Match.Yes($"Copilot took the prompt ({delivery ?? "no delivery recorded"})", evidence);
                    }
                case Claude:
                    {
                        if (Str(e, "type") != "user" || Bool(e, "isSidechain") || Bool(e, "isMeta") || Obj(e, "message") is not { } m) { continue; }
                        var words = m.TryGetProperty("content", out var c) ? c.ValueKind switch
                        {
                            JsonValueKind.String => c.GetString(),
                            JsonValueKind.Array => string.Join(" ", c.EnumerateArray().Where(b => Str(b, "type") == "text").Select(b => Str(b, "text"))),
                            _ => null,
                        } : null;
                        if (words is not null && Same(words, text)) { return Match.Yes("Claude took the prompt", new { record = "user", offset }); }
                        continue;
                    }
                default:
                    {
                        // Older Codex records the prompt as user_message; newer as an
                        // item_completed UserMessage whose content holds the text, as the host's
                        // CodexParser reads both.
                        if (Str(e, "type") != "event_msg" || Obj(e, "payload") is not { } p) { continue; }
                        var words = Str(p, "type") switch
                        {
                            "user_message" => Str(p, "message"),
                            "item_completed" when Obj(p, "item") is { } item && Str(item, "type") == "UserMessage" => ItemText(item),
                            _ => null,
                        };
                        if (words is not null && Same(words, text)) { return Match.Yes("Codex took the prompt", new { record = $"event_msg {Str(p, "type")}", offset }); }
                        continue;
                    }
            }
        }
        return Match.Waiting;
    }

    // Copilot's permission.completed: approved; for a deny, denied-interactively-by-user with no
    // feedback, or cancelled followed by the session's own abort (Escape on "No (Esc to stop)").
    public static Match Permission(string path, long from, string decision)
    {
        string? cancelledAt = null;
        foreach (var (offset, e) in Lines(path, from))
        {
            var type = Str(e, "type");
            if (cancelledAt is not null && type == "abort" && Str(e, "agentId") is not { Length: > 0 })
            {
                var evidence = new { record = "permission.completed cancelled, then abort", offset, requestId = cancelledAt };
                return decision == "denied" ? Match.Yes("Copilot recorded the deny (cancelled, then the turn stopped)", evidence) : Match.No("Copilot recorded a cancel, not an approval", evidence);
            }
            if (type != "permission.completed" || Obj(e, "data") is not { } d) { continue; }
            var result = Obj(d, "result");
            var kind = result is { } r ? Str(r, "kind") ?? "" : "";
            var feedback = result is { } rr && Str(rr, "feedback") is { Length: > 0 };
            var data = new { record = "permission.completed", offset, requestId = Str(d, "requestId"), kind, feedback };
            if (kind == "cancelled") { cancelledAt = Str(d, "requestId") ?? ""; continue; }
            var approved = kind == "approved";
            var denied = kind == "denied-interactively-by-user" && !feedback;
            if (decision == "approved" && approved) { return Match.Yes("Copilot recorded the request approved", data); }
            if (decision == "denied" && denied) { return Match.Yes("Copilot recorded the request denied", data); }
            return Match.No($"Copilot recorded \"{kind}\"{(feedback ? " with a message" : "")}", data);
        }
        return Match.Waiting;
    }

    public static Match Shutdown(string kind, string path, long from)
    {
        if (kind != Copilot) { return Match.No($"{kind} writes no shutdown record; use `sessions --id <id> --expect-gone`"); }
        foreach (var (offset, e) in Lines(path, from))
        {
            if (Str(e, "type") == "session.shutdown" && Str(e, "agentId") is not { Length: > 0 }) { return Match.Yes("Copilot recorded its shutdown", new { record = "session.shutdown", offset }); }
        }
        return Match.Waiting;
    }

    static (bool, string) Compare(JsonElement content, string? text, IReadOnlyDictionary<string, string> fields)
    {
        if (text is not null)
        {
            var recorded = content.ValueKind == JsonValueKind.String ? content.GetString()
                : content.ValueKind == JsonValueKind.Object && content.EnumerateObject().ToList() is [{ Value.ValueKind: JsonValueKind.String } only] ? only.Value.GetString()
                : null;
            if (recorded is null || !Same(recorded, text)) { return (false, "the session recorded different words"); }
        }
        foreach (var (name, value) in fields)
        {
            if (content.ValueKind != JsonValueKind.Object || !content.TryGetProperty(name, out var v)) { return (false, $"the session recorded no \"{name}\""); }
            if (!SameValue(v, value)) { return (false, $"the session recorded a different answer for \"{name}\""); }
        }
        return (true, "");
    }

    // A field written on the command line: words, a number or true/false, or a list as a,b,c
    // compared as a set.
    static bool SameValue(JsonElement v, string expected) => v.ValueKind switch
    {
        JsonValueKind.String => Same(v.GetString()!, expected),
        JsonValueKind.Array => v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? Flatten(x.GetString()!) : x.GetRawText()).Order(StringComparer.Ordinal)
            .SequenceEqual(expected.Split(',').Select(s => Flatten(s)).Where(s => s.Length > 0).Order(StringComparer.Ordinal)),
        _ => v.GetRawText() == expected.Trim(),
    };

    internal static bool Same(string recorded, string typed) => Flatten(recorded) == Flatten(typed);

    // As the host compares typed words: whitespace runs collapsed, trimmed.
    internal static string Flatten(string s)
    {
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    static string? ItemText(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) { return Str(item, "text"); }
        var parts = content.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() : Str(c, "text")).Where(t => !string.IsNullOrEmpty(t));
        return string.Join("\n", parts);
    }

    static string? Show(JsonElement e) => e.ValueKind == JsonValueKind.Undefined ? null : e.GetRawText() is { Length: > 400 } raw ? raw[..400] + "…" : e.GetRawText();
    static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static JsonElement? Obj(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;
    static bool Bool(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
