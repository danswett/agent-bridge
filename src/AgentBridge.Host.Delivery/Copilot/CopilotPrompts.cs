using System.Text.Json;

namespace AgentBridge.Host.Delivery.Copilot;

// What a transcript says before a reply is typed: whether it could be read at all, whether
// anything is asking the person, and the line boundary later records are read from. Resumed:
// all that is asking was left open by a CLI since replaced, and may or may not still be shown.
public sealed record CopilotInputState(bool Readable, bool Asking, long ScannedTo, bool Resumed = false);

// A prompt the person gave: a main-agent user.message with no other source, the offset its
// line starts at, which identifies it, and the paths of the files Copilot attached to it
// (data.attachments[].path: one for each @<path> it took). At is the event's own timestamp,
// for timings only: a prompt is never matched by time.
public sealed record CopilotPrompt(long Offset, string Content, IReadOnlyList<string>? Attachments = null, DateTimeOffset? At = null);

public sealed record CopilotPromptsSince(IReadOnlyList<CopilotPrompt> Prompts, bool SessionEnded, long ScannedTo);

// Whether a question or a permission prompt holds a session's input, where typed words would
// answer it and Enter accept it. The whole transcript counts: a subagent's question stops
// only that subagent, so other work can write any amount after it, and the session source
// does not report a subagent's question at all. So it is read once from the start, then
// only what has been added since, keeping the open questions in between.
public sealed class CopilotInput(string path)
{
    readonly Lock gate = new();
    // Open ask_user calls (by tool call id, with the agent that asked) and permission
    // requests (by request id), as of scannedTo.
    readonly Dictionary<string, string?> asks = new(StringComparer.Ordinal);
    readonly HashSet<string> permissions = new(StringComparer.Ordinal);
    // Open when a new CLI resumed the session (session.resume) with no end written for them: the
    // killed CLI's. Whether the new one shows them again is not known, and a reply's Enter on a
    // menu still shown would accept it, so they keep a reply out until something settles them: their
    // own completion, an abort or shutdown, a question asked since (shown in their place), or a
    // prompt the person typed at the machine (no source, or "user" for one with an attachment; never a
    // schedule's or an agent's), so nothing held the input. Answers are not held by
    // them: an answer to one of these is refused as stale (CopilotPermissionTranscript). Each with the
    // agent that asked it (null for the session's own, and for a permission request), so a helper's
    // abort or shutdown settles its own (Copilot on #837).
    readonly Dictionary<string, string?> stale = new(StringComparer.Ordinal);
    long scannedTo;

    public string Path => path;

    public CopilotInputState Read()
    {
        lock (gate)
        {
            // The CLI only appends. A shorter file is a different one: start again.
            if (Jsonl.Length(path) < scannedTo)
            {
                asks.Clear();
                permissions.Clear();
                stale.Clear();
                scannedTo = 0;
            }
            if (Jsonl.Lines(path, scannedTo, skipFirst: false, Line) is not { } to)
            {
                return new CopilotInputState(false, true, scannedTo);
            }
            BytesRead += to - scannedTo;
            scannedTo = to;
            var fresh = asks.Count > 0 || permissions.Count > 0;
            return new CopilotInputState(true, fresh || stale.Count > 0, scannedTo, Resumed: !fresh && stale.Count > 0);
        }
    }

    // Every byte read so far, for tests: a reply to a session read ahead reads only what came after.
    internal long BytesRead { get; private set; }

    void Line(ReadOnlySpan<byte> bytes, long start)
    {
        var ask = bytes.IndexOf("\"type\":\"tool.execution_start\""u8) >= 0 && bytes.IndexOf("\"ask_user\""u8) >= 0;
        var permission = bytes.IndexOf("\"type\":\"permission."u8) >= 0;
        var ending = bytes.IndexOf("\"type\":\"abort\""u8) >= 0 || bytes.IndexOf("\"type\":\"session.shutdown\""u8) >= 0 || bytes.IndexOf("\"type\":\"session.resume\""u8) >= 0;
        var answer = (asks.Count > 0 || stale.Count > 0) && (bytes.IndexOf("\"type\":\"tool.execution_complete\""u8) >= 0 || bytes.IndexOf("\"type\":\"human_response.recorded\""u8) >= 0);
        var typed = stale.Count > 0 && bytes.IndexOf("\"type\":\"user.message\""u8) >= 0;
        if (!ask && !permission && !ending && !answer && !typed) { return; }
        if (CopilotPrompts.Parse(bytes) is not { } e) { return; }
        using (e.Doc)
        {
            var data = e.Data;
            switch (e.Type)
            {
                case "tool.execution_start" when CopilotPrompts.Str(data, "toolName") == "ask_user" && CopilotPrompts.Str(data, "toolCallId") is { } id:
                    stale.Clear();
                    asks[id] = e.AgentId;
                    break;
                case "tool.execution_complete" or "human_response.recorded" when CopilotPrompts.Str(data, "toolCallId") is { } id:
                    asks.Remove(id);
                    stale.Remove(id);
                    break;
                case "permission.requested" when CopilotPrompts.Str(data, "requestId") is { } id:
                    stale.Clear();
                    permissions.Add(id);
                    break;
                case "permission.completed" when CopilotPrompts.Str(data, "requestId") is { } id:
                    permissions.Remove(id);
                    stale.Remove(id);
                    break;
                case "user.message" when e.AgentId is null && CopilotPrompts.Str(data, "source") is null or "" or "user":
                    stale.Clear();
                    break;
                case "session.resume" when e.AgentId is null:
                    // A new CLI: what the killed one asked may still be on its screen (stale).
                    foreach (var (id, agent) in asks) { stale[id] = agent; }
                    foreach (var id in permissions) { stale[id] = null; }
                    asks.Clear();
                    permissions.Clear();
                    break;
                case "abort" or "session.shutdown" when e.AgentId is null:
                    // The session's own abort or shutdown ends everything it was asking.
                    asks.Clear();
                    permissions.Clear();
                    stale.Clear();
                    break;
                case "abort" or "session.shutdown":
                    // A subagent's ends only its own questions, the killed CLI's left stale too.
                    foreach (var id in asks.Where(a => a.Value == e.AgentId).Select(a => a.Key).ToList()) { asks.Remove(id); }
                    foreach (var id in stale.Where(a => a.Value == e.AgentId).Select(a => a.Key).ToList()) { stale.Remove(id); }
                    break;
            }
        }
    }
}

// Reads a session's events.jsonl for a reply: before typing, whether anything is asking
// (CopilotInput); after, the prompts recorded since. By identity, never by time.
public static class CopilotPrompts
{
    // The whole transcript read once, for a caller with no CopilotInput kept.
    public static CopilotInputState Before(string path) => new CopilotInput(path).Read();

    // The person's prompts recorded after `from` (a line boundary from Before), in order.
    public static CopilotPromptsSince Since(string path, long from)
    {
        var prompts = new List<CopilotPrompt>();
        var ended = false;
        var scannedTo = Jsonl.Lines(path, from, skipFirst: false, Line) ?? from;
        return new CopilotPromptsSince(prompts, ended, scannedTo);

        void Line(ReadOnlySpan<byte> bytes, long start)
        {
            var user = bytes.IndexOf("\"type\":\"user.message\""u8) >= 0;
            if (!user && bytes.IndexOf("\"type\":\"session.shutdown\""u8) < 0) { return; }
            if (Parse(bytes) is not { } e) { return; }
            using (e.Doc)
            {
                // A subagent's messages are its own; one with a source (a scheduled prompt,
                // memory, another agent) was not typed by anybody. "user" is the person's own, as
                // Copilot marks a prompt with an attachment (CopilotParser.Source).
                if (e.AgentId is not null) { return; }
                if (e.Type == "session.shutdown") { ended = true; return; }
                if (e.Type != "user.message" || e.Data.ValueKind != JsonValueKind.Object) { return; }
                if (Str(e.Data, "source") is { Length: > 0 } and not "user") { return; }
                if (Str(e.Data, "content") is { } content) { prompts.Add(new CopilotPrompt(start, content, AttachedPaths(e.Data), Jsonl.Timestamp(e.Doc.RootElement))); }
            }
        }
    }

    static List<string> AttachedPaths(JsonElement data)
    {
        var paths = new List<string>();
        if (data.TryGetProperty("attachments", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in list.EnumerateArray())
            {
                if (Str(a, "path") is { Length: > 0 } path) { paths.Add(path); }
            }
        }
        return paths;
    }

    internal sealed record Event(JsonDocument Doc, string? Type, JsonElement Data, string? AgentId);

    internal static Event? Parse(ReadOnlySpan<byte> bytes)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(bytes.ToArray()); }
        catch (JsonException) { return null; }
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { doc.Dispose(); return null; }
        var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
        return new Event(doc, Str(root, "type"), data, Str(root, "agentId") is { Length: > 0 } agent ? agent : null);
    }

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
