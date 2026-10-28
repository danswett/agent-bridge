using System.Text;
using System.Text.Json;

namespace AgentBridge.Host.Delivery.Copilot;

// What a Copilot transcript says about one tool call. Answer is the first record of it
// being answered: human_response.recorded (the structured answer, current builds) is
// preferred over tool.execution_complete (its rendering) when both have been written.
public sealed record CopilotCallState(
    JsonElement? Arguments,
    bool IsMainAgent,
    CopilotAnswerRecord? Answer,
    bool SessionEnded,
    long ScannedTo);

// HumanContent is response.content of human_response.recorded; otherwise the call's
// tool.execution_complete, with its result text or its failure.
public sealed record CopilotAnswerRecord(JsonElement? HumanContent, bool Completed, bool Succeeded, string? ResultText);

// Reads a session's events.jsonl for one tool call, by its id, never by time: the start
// that asked, and whatever answered it. Only reads (FileShare.ReadWrite | Delete, the CLI
// keeps writing), and only parses the few lines that name the call or end the session,
// so a transcript of hundreds of megabytes costs a scan, not a parse.
public static class CopilotTranscript
{
    // Windows of the end of the file searched for the call's start, widening until it is
    // found: a pending question is normally the last thing the session wrote.
    static readonly long[] Windows = [4L << 20, 64L << 20, long.MaxValue];

    // Finds the call from the end of the file, widening the window until its start is seen.
    public static CopilotCallState Find(string path, string toolCallId)
    {
        var length = Jsonl.Length(path);
        CopilotCallState? state = null;
        foreach (var window in Windows)
        {
            var from = window >= length ? 0 : length - window;
            state = Scan(path, toolCallId, from, requireStart: true);
            if (state.Arguments is not null || from == 0) { break; }
        }
        return state!;
    }

    // Reads from a known line boundary (an earlier ScannedTo) for records of the call.
    public static CopilotCallState Since(string path, string toolCallId, long from) => Scan(path, toolCallId, from, requireStart: false);

    static CopilotCallState Scan(string path, string toolCallId, long from, bool requireStart)
    {
        var id = Encoding.UTF8.GetBytes($"\"{toolCallId}\"");
        JsonElement? arguments = null;
        var main = true;
        JsonElement? human = null;
        CopilotAnswerRecord? completed = null;
        var ended = false;
        var started = !requireStart;

        // A window starting inside a line skips that partial line; Since starts on a boundary.
        // A trailing line without its newline is still being written; ScannedTo stays before
        // it so the next scan reads it whole.
        if (Jsonl.Lines(path, from, skipFirst: requireStart, Line) is not { } scannedTo)
        {
            return new CopilotCallState(null, true, null, false, from);
        }
        var answer = human is { } h ? new CopilotAnswerRecord(h, completed is not null, completed?.Succeeded ?? true, completed?.ResultText) : completed;
        return new CopilotCallState(arguments, main, started ? answer : null, started && ended, scannedTo);

        void Line(ReadOnlySpan<byte> bytes, long start)
        {
            var named = bytes.IndexOf(id) >= 0;
            var ending = !named && (bytes.IndexOf("\"type\":\"abort\""u8) >= 0 || bytes.IndexOf("\"type\":\"session.shutdown\""u8) >= 0);
            if (!named && !ending) { return; }
            JsonDocument doc;
            try { doc = JsonDocument.Parse(bytes.ToArray()); }
            catch (JsonException) { return; }
            using (doc)
            {
                var e = doc.RootElement;
                if (e.ValueKind != JsonValueKind.Object) { return; }
                var type = Str(e, "type");
                var data = e.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
                var sub = Str(e, "agentId") is { Length: > 0 };
                if (ending)
                {
                    // The session's own abort or shutdown ends its question; a subagent's does not.
                    if (started && !sub) { ended = true; }
                    return;
                }
                if (data.ValueKind != JsonValueKind.Object || Str(data, "toolCallId") != toolCallId) { return; }
                switch (type)
                {
                    case "tool.execution_start":
                        // A later start with the same id is a replay of the same call: keep the
                        // first, and forget nothing it has already seen answered.
                        if (arguments is null)
                        {
                            arguments = data.TryGetProperty("arguments", out var a) ? a.Clone() : default(JsonElement);
                            main = !sub;
                            started = true;
                        }
                        break;
                    case "human_response.recorded" when started && human is null:
                        human = data.TryGetProperty("response", out var r) && r.ValueKind == JsonValueKind.Object && r.TryGetProperty("content", out var c)
                            ? c.Clone() : JsonSerializer.SerializeToElement<object?>(null);
                        break;
                    case "tool.execution_complete" when started && completed is null:
                        {
                            var ok = !(data.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.False) && !data.TryGetProperty("error", out _);
                            var text = data.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object ? Str(result, "content") : null;
                            completed = new CopilotAnswerRecord(null, true, ok, text);
                            break;
                        }
                }
            }
        }
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
