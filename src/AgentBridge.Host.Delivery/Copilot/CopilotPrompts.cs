using System.Text.Json;

namespace AgentBridge.Host.Delivery.Copilot;

// What a transcript says before a reply is typed: whether it could be read at all, whether
// anything is asking the person, and the line boundary later records are read from.
public sealed record CopilotInputState(bool Readable, bool Asking, long ScannedTo);

// A prompt the person gave: a main-agent user.message with no other source, and the offset
// its line starts at, which identifies it.
public sealed record CopilotPrompt(long Offset, string Content);

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
                scannedTo = 0;
            }
            if (Jsonl.Lines(path, scannedTo, skipFirst: false, Line) is not { } to)
            {
                return new CopilotInputState(false, true, scannedTo);
            }
            scannedTo = to;
            return new CopilotInputState(true, asks.Count > 0 || permissions.Count > 0, scannedTo);
        }
    }

    void Line(ReadOnlySpan<byte> bytes, long start)
    {
        var ask = bytes.IndexOf("\"type\":\"tool.execution_start\""u8) >= 0 && bytes.IndexOf("\"ask_user\""u8) >= 0;
        var permission = bytes.IndexOf("\"type\":\"permission."u8) >= 0;
        var ending = bytes.IndexOf("\"type\":\"abort\""u8) >= 0 || bytes.IndexOf("\"type\":\"session.shutdown\""u8) >= 0;
        var answer = asks.Count > 0 && (bytes.IndexOf("\"type\":\"tool.execution_complete\""u8) >= 0 || bytes.IndexOf("\"type\":\"human_response.recorded\""u8) >= 0);
        if (!ask && !permission && !ending && !answer) { return; }
        if (CopilotPrompts.Parse(bytes) is not { } e) { return; }
        using (e.Doc)
        {
            var data = e.Data;
            switch (e.Type)
            {
                case "tool.execution_start" when CopilotPrompts.Str(data, "toolName") == "ask_user" && CopilotPrompts.Str(data, "toolCallId") is { } id:
                    asks[id] = e.AgentId;
                    break;
                case "tool.execution_complete" or "human_response.recorded" when CopilotPrompts.Str(data, "toolCallId") is { } id:
                    asks.Remove(id);
                    break;
                case "permission.requested" when CopilotPrompts.Str(data, "requestId") is { } id:
                    permissions.Add(id);
                    break;
                case "permission.completed" when CopilotPrompts.Str(data, "requestId") is { } id:
                    permissions.Remove(id);
                    break;
                case "abort" or "session.shutdown" when e.AgentId is null:
                    // The session's own abort or shutdown ends everything it was asking.
                    asks.Clear();
                    permissions.Clear();
                    break;
                case "abort" or "session.shutdown":
                    // A subagent's ends only its own questions.
                    foreach (var id in asks.Where(a => a.Value == e.AgentId).Select(a => a.Key).ToList()) { asks.Remove(id); }
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
                // memory, another agent) was not typed by anybody.
                if (e.AgentId is not null) { return; }
                if (e.Type == "session.shutdown") { ended = true; return; }
                if (e.Type != "user.message" || e.Data.ValueKind != JsonValueKind.Object) { return; }
                if (Str(e.Data, "source") is { Length: > 0 }) { return; }
                if (Str(e.Data, "content") is { } content) { prompts.Add(new CopilotPrompt(start, content)); }
            }
        }
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
