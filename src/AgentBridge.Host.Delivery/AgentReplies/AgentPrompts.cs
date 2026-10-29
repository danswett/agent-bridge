using System.Text.Json;
using AgentBridge.Host.Delivery.Copilot;

namespace AgentBridge.Host.Delivery.AgentReplies;

// A prompt from the person that an agent recorded, by the offset of the line recording it,
// which is what identifies it: two replies of the same words are two prompts. Files are the
// paths the agent took in as attachments of it.
public sealed record AgentPrompt(long Offset, string Text, IReadOnlyList<string> Files);

public sealed record AgentPromptsSince(bool Readable, IReadOnlyList<AgentPrompt> Prompts, long ScannedTo);

// What a Claude Code transcript (~/.claude/projects/<slug>/<id>.jsonl) records of prompts typed
// into it, as 2.1.312 writes them (seen live):
//   * typed while idle: a "user" entry whose message.content is the line as typed, then one
//     "attachment" entry per @<path> it read, attachment.filename being the path;
//   * typed while it works: a "queue-operation" "enqueue" whose content is the line, and, once
//     the turn ends and it is taken, a "user" entry with promptSource "queued" and then its
//     attachments.
// The queued "user" entry repeats a prompt already recorded by its enqueue, so it is never a
// prompt of its own; its attachments are the enqueued prompt's. Tool results, meta entries
// and subagents' entries are not the person's.
public static class ClaudePrompts
{
    public static AgentPromptsSince Since(string path, long from)
    {
        var prompts = new List<AgentPrompt>();
        // The prompt the next attachments belong to, and the files each has collected. Enqueued
        // prompts wait here, by their words, for the queued "user" entry that takes them: one
        // enqueued before the scan began has no entry, so its entry takes nothing.
        List<string>? files = null;
        var queued = new List<(string Words, List<string> Files)>();
        if (Jsonl.Lines(path, from, skipFirst: false, Line) is not { } to) { return new AgentPromptsSince(false, [], from); }
        return new AgentPromptsSince(true, prompts, to);

        void Line(ReadOnlySpan<byte> bytes, long start)
        {
            var user = bytes.IndexOf("\"type\":\"user\""u8) >= 0;
            var queue = bytes.IndexOf("\"type\":\"queue-operation\""u8) >= 0;
            var attachment = bytes.IndexOf("\"type\":\"attachment\""u8) >= 0;
            if (!user && !queue && !attachment) { return; }
            JsonDocument doc;
            try { doc = JsonDocument.Parse(bytes.ToArray()); }
            catch (JsonException) { return; }
            using (doc)
            {
                var e = doc.RootElement;
                if (e.ValueKind != JsonValueKind.Object) { return; }
                if (e.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True) { return; }
                switch (Str(e, "type"))
                {
                    case "queue-operation" when Str(e, "operation") == "enqueue" && Str(e, "content") is { } content:
                        var mine = new List<string>();
                        queued.Add((InjectableText.Flatten(content), mine));
                        prompts.Add(new AgentPrompt(start, content, mine));
                        files = null;
                        break;
                    case "user":
                        if (e.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) { return; }
                        if (Text(e) is not { } text) { return; }
                        if (Str(e, "promptSource") == "queued")
                        {
                            var words = InjectableText.Flatten(text);
                            var at = queued.FindIndex(q => q.Words == words);
                            files = at < 0 ? null : queued[at].Files;
                            if (at >= 0) { queued.RemoveAt(at); }
                            return;
                        }
                        files = [];
                        prompts.Add(new AgentPrompt(start, text, files));
                        break;
                    case "attachment" when files is not null
                        && e.TryGetProperty("attachment", out var a) && a.ValueKind == JsonValueKind.Object
                        && Str(a, "filename") is { } name:
                        files.Add(name);
                        break;
                }
            }
        }
    }

    // The words of a person's prompt: content as a string, or its text blocks. An entry
    // carrying a tool_result is a tool's answer, not the person's.
    static string? Text(JsonElement entry)
    {
        if (!entry.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object) { return null; }
        if (!m.TryGetProperty("content", out var c)) { return null; }
        if (c.ValueKind == JsonValueKind.String) { return c.GetString(); }
        if (c.ValueKind != JsonValueKind.Array) { return null; }
        var parts = new List<string>();
        foreach (var b in c.EnumerateArray())
        {
            if (b.ValueKind != JsonValueKind.Object) { continue; }
            switch (Str(b, "type"))
            {
                case "tool_result": return null;
                case "text" when Str(b, "text") is { } t: parts.Add(t); break;
            }
        }
        return parts.Count > 0 ? string.Join(' ', parts) : null;
    }

    internal static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

// What a Codex rollout records of prompts typed into it. 0.165 writes each, idle or mid-turn
// alike, as an event_msg item_completed whose item is a UserMessage: "text" content with the
// line as typed, and a "local_image" for each image path pasted into the box, whose place in
// the text is held by an "[Image #n]" placeholder (seen live). Earlier builds wrote an
// event_msg user_message (message, local_images), which is read too; a rollout holding both
// for one prompt is read by the newer record alone.
public static class CodexPrompts
{
    public static AgentPromptsSince Since(string path, long from)
    {
        var items = new List<AgentPrompt>();
        var legacy = new List<AgentPrompt>();
        if (Jsonl.Lines(path, from, skipFirst: false, Line) is not { } to) { return new AgentPromptsSince(false, [], from); }
        return new AgentPromptsSince(true, items.Count > 0 ? items : legacy, to);

        void Line(ReadOnlySpan<byte> bytes, long start)
        {
            var item = bytes.IndexOf("\"UserMessage\""u8) >= 0;
            var old = bytes.IndexOf("\"user_message\""u8) >= 0;
            if (!item && !old) { return; }
            JsonDocument doc;
            try { doc = JsonDocument.Parse(bytes.ToArray()); }
            catch (JsonException) { return; }
            using (doc)
            {
                var e = doc.RootElement;
                if (e.ValueKind != JsonValueKind.Object || ClaudePrompts.Str(e, "type") != "event_msg") { return; }
                if (!e.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object) { return; }
                switch (ClaudePrompts.Str(p, "type"))
                {
                    case "item_completed" when p.TryGetProperty("item", out var i) && i.ValueKind == JsonValueKind.Object
                        && ClaudePrompts.Str(i, "type") == "UserMessage" && i.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array:
                        var text = new List<string>();
                        var images = new List<string>();
                        foreach (var c in content.EnumerateArray())
                        {
                            if (c.ValueKind != JsonValueKind.Object) { continue; }
                            switch (ClaudePrompts.Str(c, "type"))
                            {
                                case "text" when ClaudePrompts.Str(c, "text") is { } t: text.Add(t); break;
                                case "local_image" when ClaudePrompts.Str(c, "path") is { } path: images.Add(path); break;
                            }
                        }
                        items.Add(new AgentPrompt(start, string.Join(' ', text), images));
                        break;
                    case "user_message" when ClaudePrompts.Str(p, "message") is { } message:
                        var local = p.TryGetProperty("local_images", out var li) && li.ValueKind == JsonValueKind.Array
                            ? li.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                            : [];
                        legacy.Add(new AgentPrompt(start, message, local));
                        break;
                }
            }
        }
    }

    // The words of a recorded prompt, without the placeholders standing for its images.
    public static string Words(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\[Image #\d+\]", " ");
}
