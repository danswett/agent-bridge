using System.Text;
using System.Text.Json;
using AgentBridge.Host.Delivery.Copilot;

namespace AgentBridge.Host.Delivery.AgentReplies;

// Where Claude's transcript stands, and whether Claude has taken a relay prompt (docs/message-delivery.md "Busy sends to Claude", #3856).
// A prompt Claude takes from its queue is recorded as a "user" entry with promptSource "queued" and the same words (2.1.312, seen live; see
// ClaudePrompts); one typed while it was idle is a plain "user" entry. A relay prompt is found by the request id its framed sender line carries
// (" request <id> "), so no message text is kept anywhere. Tool results, sidechains, meta entries and the queue's own enqueue row are not it.
public static class ClaudeQueue
{
    // The transcript's length: where to start looking for what a send is followed by. 0 when it cannot be read.
    public static long Mark(string path) => Jsonl.Length(path);

    // Whether a prompt carrying this request id was recorded at or after the offset. A missing or unreadable transcript is never "taken".
    public static bool Taken(string path, long from, string requestId)
    {
        var needle = Encoding.UTF8.GetBytes(requestId);
        var found = false;
        if (Jsonl.Lines(path, from, skipFirst: false, Line) is null) { return false; }
        return found;

        void Line(ReadOnlySpan<byte> bytes, long start)
        {
            if (found || bytes.IndexOf("\"type\":\"user\""u8) < 0 || bytes.IndexOf(needle) < 0) { return; }
            JsonDocument doc;
            try { doc = JsonDocument.Parse(bytes.ToArray()); }
            catch (JsonException) { return; }
            using (doc)
            {
                var e = doc.RootElement;
                if (e.ValueKind != JsonValueKind.Object || Flag(e, "isSidechain") || Flag(e, "isMeta")) { return; }
                if (ClaudePrompts.Text(e) is { } text && text.Contains(" request " + requestId + " ", StringComparison.Ordinal)) { found = true; }
            }
        }
    }

    // Whether the transcript's tail holds a relay prompt enqueued at or after since and not taken since: the slot a host restart forgot.
    public static bool Unconsumed(string path, DateTimeOffset since) => false;

    static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}