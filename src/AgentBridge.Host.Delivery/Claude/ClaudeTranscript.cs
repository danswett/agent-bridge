using System.Text;
using System.Text.Json;
using AgentBridge.Host.Delivery.Copilot;

namespace AgentBridge.Host.Delivery.Claude;

// What a Claude Code transcript (~/.claude/projects/<slug>/<id>.jsonl) says about one
// AskUserQuestion, by its tool_use id, never by time: the assistant entry that asked, and
// the user entry whose tool_result answers it.
public sealed record ClaudeCallState(
    JsonElement? Input,
    bool IsMainAgent,
    ClaudeAnswerRecord? Answer,
    bool Interrupted,
    long ScannedTo);

// The tool_result for the call. Answers is the entry's toolUseResult.answers, question text
// to the answer Claude recorded, when the build writes it; Text is the result's own text,
// '... "question"="answer" ...', which every build does.
public sealed record ClaudeAnswerRecord(bool IsError, string Text, IReadOnlyDictionary<string, string>? Answers);

public static class ClaudeTranscript
{
    static readonly long[] Windows = [4L << 20, 64L << 20, long.MaxValue];

    public static ClaudeCallState Find(string path, string toolUseId)
    {
        var length = Jsonl.Length(path);
        ClaudeCallState? state = null;
        foreach (var window in Windows)
        {
            var from = window >= length ? 0 : length - window;
            state = Scan(path, toolUseId, from, requireStart: true);
            if (state.Input is not null || from == 0) { break; }
        }
        return state!;
    }

    public static ClaudeCallState Since(string path, string toolUseId, long from) => Scan(path, toolUseId, from, requireStart: false);

    static ClaudeCallState Scan(string path, string toolUseId, long from, bool requireStart)
    {
        var id = Encoding.UTF8.GetBytes($"\"{toolUseId}\"");
        JsonElement? input = null;
        var main = true;
        ClaudeAnswerRecord? answer = null;
        var interrupted = false;
        var started = !requireStart;

        if (Jsonl.Lines(path, from, skipFirst: requireStart, Line) is not { } scannedTo)
        {
            return new ClaudeCallState(null, true, null, false, from);
        }
        return new ClaudeCallState(input, main, started ? answer : null, started && interrupted, scannedTo);

        void Line(ReadOnlySpan<byte> bytes, long start)
        {
            var named = bytes.IndexOf(id) >= 0;
            var interrupt = !named && bytes.IndexOf("[Request interrupted by user"u8) >= 0;
            if (!named && !interrupt) { return; }
            JsonDocument doc;
            try { doc = JsonDocument.Parse(bytes.ToArray()); }
            catch (JsonException) { return; }
            using (doc)
            {
                var e = doc.RootElement;
                if (e.ValueKind != JsonValueKind.Object) { return; }
                var sidechain = e.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True;
                var type = Str(e, "type");
                if (interrupt)
                {
                    // The person pressed Esc in the session: its turn, and the prompt, are over.
                    if (started && !sidechain && type == "user") { interrupted = true; }
                    return;
                }
                foreach (var block in Blocks(e))
                {
                    switch (Str(block, "type"))
                    {
                        case "tool_use" when type == "assistant" && input is null && Str(block, "id") == toolUseId && Str(block, "name") == "AskUserQuestion":
                            input = block.TryGetProperty("input", out var i) ? i.Clone() : default(JsonElement);
                            main = !sidechain;
                            started = true;
                            break;
                        case "tool_result" when type == "user" && started && answer is null && Str(block, "tool_use_id") == toolUseId:
                            answer = new ClaudeAnswerRecord(
                                block.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True,
                                ResultText(block),
                                RecordedAnswers(e));
                            break;
                    }
                }
            }
        }
    }

    static IEnumerable<JsonElement> Blocks(JsonElement entry)
    {
        if (!entry.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object) { yield break; }
        if (!m.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) { yield break; }
        foreach (var b in content.EnumerateArray())
        {
            if (b.ValueKind == JsonValueKind.Object) { yield return b; }
        }
    }

    static string ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var c)) { return ""; }
        if (c.ValueKind == JsonValueKind.String) { return c.GetString() ?? ""; }
        if (c.ValueKind != JsonValueKind.Array) { return ""; }
        return string.Join(" ", c.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(x => Str(x, "text")).Where(t => t is not null));
    }

    static Dictionary<string, string>? RecordedAnswers(JsonElement entry)
    {
        if (!entry.TryGetProperty("toolUseResult", out var r) || r.ValueKind != JsonValueKind.Object) { return null; }
        if (!r.TryGetProperty("answers", out var a) || a.ValueKind != JsonValueKind.Object) { return null; }
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in a.EnumerateObject())
        {
            answers[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
        }
        return answers;
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
