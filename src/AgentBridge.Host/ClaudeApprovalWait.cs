using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// The reply to approval.wait. Verdict is "allow" or "deny" (a person's answer) or "none": no answer, so the hook shows Claude's own prompt at the machine and
// never allows. Reason, only with "none", is one word: bad-event, caller, busy, truncated or timeout.
public sealed record ApprovalWaitReply(string Verdict, string? Reason = null);

// #1003 PR B (docs/design/claude-approval-channel.md): the pipe op approval.wait. The hook sends the raw PermissionRequest event ({ "event": "<JSON text>" }, at most
// 16 KiB); the HOST reads the session, tool and input from it, derives the request id (the hook's formula) and builds the text shown (after escaping, as the app shows
// it), which the broker digests, so a person's answer is bound to what the host showed. The caller check is agent.hello's (a live session credential, classified an agent),
// not a new process walk, and the event must name THAT session. Off: LocalApi is handed this only when the host is; nothing hands it today.
public sealed class ClaudeApprovalWait
{
    public const string Op = "approval.wait";
    public const int MaxEvent = 16 * 1024;
    const string Hook = "PermissionRequest";
    readonly ClaudeApprovalBroker broker;

    internal ClaudeApprovalWait(ClaudeApprovalBroker broker) => this.broker = broker;

    static ApprovalWaitReply None(string reason) => new("none", reason);

    public async Task<object?> HandleAsync(AgentCredentials credentials, ICallerGuard callers, IProtocolChannel channel, JsonElement data)
    {
        if (credentials.Of(channel) is not { Kind: "session" } caller || !credentials.IsLive(caller))
        {
            throw new InvalidOperationException("refused: approval.wait is for an agent connection that has passed agent.hello");
        }
        if (callers.Classify(channel, fresh: true).Kind != CallerKind.Agent)
        {
            throw new InvalidOperationException("refused: approval.wait is for the agent session's own hook");
        }
        // Only the event is read: a session, a principal or an id in the body is ignored.
        // Text that is not well-formed (a lone surrogate) cannot be read as a string or hashed: it is a bad event, never a host fault.
        string? json;
        try
        {
            json = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("event", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
        }
        catch (InvalidOperationException) { json = null; }
        var parts = json is { Length: > 0 and <= MaxEvent } ? Read(json) : null;
        if (parts is not { } p) { return None("bad-event"); }
        var (session, tool, input, command) = p;
        if (!string.Equals(session, caller.Session, StringComparison.OrdinalIgnoreCase)) { return None("caller"); }
        var shown = ShownText.Reveal($"{tool}: {command ?? input}".Replace("\r\n", "\n"), rightToLeft: true);
        // Cut when shown, so it can never be answered: no wait for an answer that cannot come.
        if (shown.Length > ClaudeApprovalBroker.PreviewMax) { return None("truncated"); }
        Task<string?>? verdict;
        try { verdict = broker.Register(caller.Session, RequestId(session, tool, input), shown); }
        catch (ArgumentException) { return None("bad-event"); }
        // Null: a prompt for this request is already waiting, or too many are held.
        if (verdict is null) { return None("busy"); }
        // The hook going away ends its wait with no verdict, so nobody answers a prompt nobody is waiting for.
        if (await Task.WhenAny(verdict, channel.Closed) != verdict)
        {
            broker.Withdraw(caller.Session);
            return None("timeout");
        }
        return await verdict is { } answer ? new ApprovalWaitReply(answer) : None("timeout");
    }

    // The session, tool and raw input of a PermissionRequest event, and the command when the input is an object holding one; null for anything else.
    static (string Session, string Tool, string Input, string? Command)? Read(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (root.ValueKind != JsonValueKind.Object || Text("hook_event_name") != Hook || Text("session_id") is not { Length: > 0 } session
                || Text("tool_name") is not { Length: > 0 } tool) { return null; }
            var input = root.TryGetProperty("tool_input", out var i) ? i.GetRawText() : "";
            var command = root.TryGetProperty("tool_input", out var t) && t.ValueKind == JsonValueKind.Object
                && t.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            return (session, tool, input, command);
        }
        // GetString throws InvalidOperationException on a lone surrogate escape.
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    // 16 hex digits of a hash over the session, tool and input: the hook's own formula (ClaudeApproval.RequestId), so a prompt is named alike on both sides.
    internal static string RequestId(string session, string tool, string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{session}\n{tool}\n{input}")))[..16].ToLowerInvariant();
}
