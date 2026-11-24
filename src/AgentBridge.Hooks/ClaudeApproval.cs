using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentBridge.Hooks;

// The Claude Code permission prompt answered from a device (#1003, docs/design/claude-remote-approval.md), hook side. The hook's PermissionRequest
// event names no request id, so one is derived from the session, the tool and its input; the host writes a decision file for it
// (<runtime>\agent-bridge-claude\<session>.decision.json: request, verdict allow|deny, principal, at), and the hook prints Claude's decision for it.
// Only a person's current decision for this very request is taken; anything else, and no decision by the deadline, prints nothing, so Claude shows its
// own prompt at the terminal. Nothing is allowed by default.
public static class ClaudeApproval
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaxFuture = TimeSpan.FromSeconds(5);

    public static string DecisionPath(string runtimeRoot, string sessionId) =>
        Path.Combine(runtimeRoot, SpoolContract.ClaudeFolder, SpoolContract.SafeKey(sessionId) + ".decision.json");

    // 16 hex digits of a hash over the session, tool and input: stable for one prompt, and holding none of it.
    public static string? RequestId(string eventJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(eventJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("session_id", out var session) || session.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("tool_name", out var tool) || tool.ValueKind != JsonValueKind.String) { return null; }
            var input = root.TryGetProperty("tool_input", out var i) ? i.GetRawText() : "";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{session.GetString()}\n{tool.GetString()}\n{input}")))[..16].ToLowerInvariant();
        }
        catch (JsonException) { return null; }
    }

    // Claude's decision for a verdict. Anything but allow or deny is nothing: no output, and the prompt stays at the terminal.
    public static string Reply(string? verdict) => verdict switch
    {
        "allow" => "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"allow\"}}}\n",
        "deny" => "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"deny\",\"message\":\"Denied from a Slipgates device.\"}}}\n",
        _ => "",
    };

    // The decision file's verdict, or the one word why it was refused (never the file's text).
    public static (string? Verdict, string? Reason) Verdict(string decisionJson, string requestId, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(decisionJson);
            var root = doc.RootElement;
            string? Text(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (Text("request") != requestId) { return (null, "request"); }
            if (Text("principal") != "person") { return (null, "principal"); }
            if (!DateTimeOffset.TryParse(Text("at"), null, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)) { return (null, "unreadable"); }
            if (now - at > MaxAge) { return (null, "stale"); }
            if (at - now > MaxFuture) { return (null, "future"); }
            return Text("verdict") is "allow" or "deny" ? (Text("verdict"), null) : (null, "verdict");
        }
        catch (JsonException) { return (null, "unreadable"); }
    }

    // Polls for a usable decision until the deadline; a refused one is logged once by its reason and ignored. Null at the deadline.
    public static async Task<string?> AwaitAsync(Func<string?> read, string requestId, Func<DateTimeOffset> now, Func<TimeSpan, Task> delay,
        TimeSpan wait, TimeSpan poll, Action<string> log)
    {
        var deadline = now() + wait;
        var told = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            if (read() is { Length: > 0 } text)
            {
                var (verdict, reason) = Verdict(text, requestId, now());
                if (verdict is not null) { return verdict; }
                if (told.Add(reason!)) { log($"claude approval decision refused: {reason}"); }
            }
            if (now() >= deadline) { return null; }
            await delay(poll);
        }
    }
}
