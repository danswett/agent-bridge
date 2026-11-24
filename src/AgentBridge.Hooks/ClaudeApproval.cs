using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentBridge.Hooks;

// The Claude Code permission prompt answered from a device (#1003, docs/design/claude-remote-approval.md), hook side. The hook's PermissionRequest
// event names no request id, so one is derived from the session, the tool and its input. Each prompt also gets a fresh one-time NONCE the hook keeps
// in its own pending record; a decision must carry the PROOF for this prompt (a hash of the nonce with the request id, session, tool and full input),
// be dated no earlier than the prompt began, and is CONSUMED (claimed by a move) the moment it is read, so one answer settles one prompt and can never
// be reused for the next identical one. Only a person's current decision is taken; anything else, and no decision by the deadline, prints nothing, so
// Claude shows its own prompt at the terminal. Nothing is allowed by default.
//
// NOT AUTHENTICATED: the decision file is a file in the user's profile, and any process of the same user can read the pending record and so forge a
// matching decision. The nonce stops replay and cross-prompt reuse, not forgery. The feature stays off until the host answers through a channel an
// agent cannot write (the design doc says so); this contract must not be turned on before then.
public static class ClaudeApproval
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaxFuture = TimeSpan.FromSeconds(5);
    public const int PreviewMax = 300;
    // There is no authenticated channel for a device's answer yet (#3847: an unauthenticated local file never drives an answer), so NO code path here
    // can produce an allow: the hook can print a deny or nothing. This stays false until that channel exists and is approved; Enabled is the one check.
    public const bool AuthenticatedChannel = false;
    public static bool Enabled(bool settingOn) => settingOn && AuthenticatedChannel;
    // A prompt whose command would be shown cut is never answerable: the id binds the full input, but a person would see only a prefix.
    public static bool Answerable(int commandLength) => commandLength <= PreviewMax;
    public const int MaxDecisionBytes = 4096;
    // What the hook waits for a decision, and the timeout Claude is given for it: the wait is the shorter, so the hook ends itself with no output.
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(100);
    public static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(120);

    public static string DecisionPath(string runtimeRoot, string sessionId) =>
        Path.Combine(runtimeRoot, SpoolContract.ClaudeFolder, SpoolContract.SafeKey(sessionId) + ".decision.json");

    public static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    static (string Session, string Tool, string Input)? Parts(string eventJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(eventJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("session_id", out var session) || session.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("tool_name", out var tool) || tool.ValueKind != JsonValueKind.String) { return null; }
            return (session.GetString()!, tool.GetString()!, root.TryGetProperty("tool_input", out var i) ? i.GetRawText() : "");
        }
        catch (JsonException) { return null; }
    }

    static string Hash(string text, int length) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..length].ToLowerInvariant();

    // 16 hex digits of a hash over the session, tool and input: stable for one prompt, and holding none of it.
    public static string? RequestId(string eventJson) => Parts(eventJson) is var (s, t, i) ? Hash($"{s}\n{t}\n{i}", 16) : null;

    // The proof a decision must carry for this prompt: 64 hex digits over the nonce, request id, session, tool and the full input.
    public static string? Proof(string nonce, string eventJson) =>
        Parts(eventJson) is var (s, t, i) && RequestId(eventJson) is { } request ? Hash($"{nonce}\n{request}\n{s}\n{t}\n{i}", 64) : null;

    // Claude's decision for a verdict: a deny, or nothing (no output, the prompt stays at the terminal). Never an allow: see AuthenticatedChannel.
    public static string Reply(string? verdict) => verdict switch
    {
        "deny" => "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"deny\",\"message\":\"Denied from a Slipgates device.\"}}}\n",
        _ => "",
    };

    // The decision file's verdict, or the one word why it was refused (never the file's text). `since` is when this prompt began.
    public static (string? Verdict, string? Reason) Verdict(string decisionJson, string requestId, string proof, DateTimeOffset now, DateTimeOffset since)
    {
        if (decisionJson.Length > MaxDecisionBytes) { return (null, "size"); }
        try
        {
            using var doc = JsonDocument.Parse(decisionJson);
            var root = doc.RootElement;
            string? Text(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (Text("request") != requestId) { return (null, "request"); }
            if (Text("proof") != proof) { return (null, "proof"); }
            if (Text("principal") != "person") { return (null, "principal"); }
            if (!DateTimeOffset.TryParse(Text("at"), null, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)) { return (null, "unreadable"); }
            if (at < since) { return (null, "early"); }
            if (now - at > MaxAge) { return (null, "stale"); }
            if (at - now > MaxFuture) { return (null, "future"); }
            return Text("verdict") switch { "deny" => ("deny", null), "allow" => (null, "unavailable"), _ => (null, "verdict") };
        }
        catch (JsonException) { return (null, "unreadable"); }
    }

    // Polls for a usable decision until the deadline (measured by `elapsed`, a monotonic clock, so a stepped wall clock cannot stretch it); a refused one
    // is logged once by its reason and ignored, and a read that fails (a file held open mid-write) is tried again. `read` must consume what it returns.
    public static async Task<string?> AwaitAsync(Func<string?> read, string requestId, string proof, DateTimeOffset since, Func<DateTimeOffset> now,
        Func<TimeSpan> elapsed, Func<TimeSpan, Task> delay, TimeSpan wait, TimeSpan poll, Action<string> log)
    {
        var told = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            string? text = null;
            try { text = read(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { if (told.Add("read")) { log("claude approval decision unreadable: read"); } }
            if (text is { Length: > 0 })
            {
                var (verdict, reason) = Verdict(text, requestId, proof, now(), since);
                if (verdict is not null) { return verdict; }
                if (told.Add(reason!)) { log($"claude approval decision refused: {reason}"); }
            }
            if (elapsed() >= wait) { return null; }
            await delay(poll);
        }
    }
}
