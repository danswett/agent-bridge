// Bridge Protocol v1, notifications (Web Push). The contract is the "Notifications" part of
// docs/protocol/v1-sessions.md: a client registers its browser's push subscription with a
// host over the end-to-end channel, and the host sends it notices, encrypted to that
// browser (RFC 8291), which the relay forwards without being able to read them.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBridge.Protocol;

// push.subscribe. Endpoint and keys are exactly what the browser's PushSubscription.toJSON()
// gives; App is where a tap opens the client; Device names this device in push.list.
public sealed record PushSubscribeRequest
{
    public required string Endpoint { get; init; }
    public required PushKeys Keys { get; init; }
    public required string App { get; init; }
    public string? Device { get; init; }
}

public sealed record PushKeys(string P256dh, string Auth);

public sealed record PushSubscribeResult(bool Subscribed, int Count);

// push.unsubscribe: the calling device's subscription, and the one with Endpoint if given
// (whoever holds an endpoint can already send to it, so it may also remove it).
public sealed record PushUnsubscribeRequest(string? Endpoint = null);

public sealed record PushUnsubscribeResult(bool Removed);

// push.list: the devices this host notifies. Endpoints are never sent back: one is a
// capability to notify that device, and push.list must not hand it to another.
public sealed record PushListResult(IReadOnlyList<PushDevice> Devices);

public sealed record PushDevice(string Device, string Name, string Service, DateTimeOffset Since, bool Current);

[UnknownAs(Question)]
public enum NoticeKind { Question, Finished, Resolved }

// The plaintext of one push, before RFC 8291 encryption. It is a Declarative Web Push
// message (web_push: 8030), which Safari 18.4+ shows without running the service worker,
// and which the service worker shows itself everywhere else from the same fields.
public sealed record PushMessage
{
    [JsonPropertyName("web_push")]
    public int WebPush { get; init; } = 8030;
    public required PushNotification Notification { get; init; }
}

public sealed record PushNotification
{
    public required string Title { get; init; }
    public required string Body { get; init; }
    // Where a tap goes: the client's app URL with ?host=&session=[&question=].
    public required string Navigate { get; init; }
    // One per machine and session (PushNotice.Tag): every notice about a session replaces
    // the one before it on the device.
    public required string Tag { get; init; }
    public string Lang { get; init; } = "en";
    // Resolved notices replace a question quietly.
    public bool? Silent { get; init; }
    public required PushNoticeData Data { get; init; }
}

// What a client reads to act on a notice; Title and Body above carry the text.
public sealed record PushNoticeData
{
    public int V { get; init; } = 1;
    public required NoticeKind Kind { get; init; }
    public required string Machine { get; init; }
    public required string Session { get; init; }
    public string? Question { get; init; }
    public required DateTimeOffset At { get; init; }
}

public static class PushNotice
{
    // UTF-8 bytes of the JSON as encrypted. RFC 8291's one 4096-byte record leaves 3993
    // bytes of plaintext (WebPushCrypto.MaxPlaintext), and Apple caps a payload at 4 KiB;
    // this keeps well inside both.
    public const int MaxBytes = 3072;
    public const int TitleBytes = 120;
    public const int BodyBytes = 1024;
    // A finished turn's preview: the first part of the response, as agent-ha-bridge's
    // response notification sent the first 880 characters.
    public const int PreviewChars = 880;

    // Every notice about one session on one machine shares a tag (the notification it
    // replaces on the device) and a topic (the undelivered push it replaces at the push
    // service), so the device only ever shows the latest state of a session.
    public static string Tag(string machine, string session) => $"{machine}:{session}";

    // 24 bytes of SHA-256, which base64url-encodes to 32 characters: RFC 8030's maximum,
    // and a length Apple accepts (spike S5 finding 6).
    public static string Topic(string machine, string session)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"agent-bridge-push|{machine}|{session}"));
        return Convert.ToBase64String(hash, 0, 24).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Navigate(string app, string machine, string session, string? question)
    {
        var q = $"host={Uri.EscapeDataString(machine)}&session={Uri.EscapeDataString(session)}" +
            (question is null ? "" : $"&question={Uri.EscapeDataString(question)}");
        var hash = app.IndexOf('#');
        if (hash >= 0) { app = app[..hash]; }
        return app + (app.Contains('?') ? "&" : "?") + q;
    }

    // Whitespace runs collapse to one space: a lock screen shows a few lines, and newlines
    // and indentation would spend them on nothing.
    public static string Flatten(string? text) =>
        string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // The message as sent, made to fit MaxBytes: the body is cut first (from its end, with
    // an ellipsis), then the title. Emoji are escaped as 12 bytes of JSON, so the limits on
    // the text alone cannot promise the size; this measures what is actually sent.
    public static PushMessage Fit(PushMessage m, int maxBytes = MaxBytes)
    {
        var n = m.Notification;
        n = n with { Title = Cut(n.Title, TitleBytes), Body = Cut(n.Body, BodyBytes) };
        m = m with { Notification = n };
        var bodyBudget = BodyBytes;
        while (ProtocolJson.ByteCount(m) > maxBytes && bodyBudget > 0)
        {
            bodyBudget = Math.Max(0, bodyBudget - 128);
            m = m with { Notification = m.Notification with { Body = Cut(n.Body, bodyBudget) } };
        }
        var titleBudget = TitleBytes;
        while (ProtocolJson.ByteCount(m) > maxBytes && titleBudget > 16)
        {
            titleBudget -= 16;
            m = m with { Notification = m.Notification with { Title = Cut(n.Title, titleBudget) } };
        }
        if (ProtocolJson.ByteCount(m) > maxBytes) { throw new ArgumentException("a push notice cannot be made to fit"); }
        return m;
    }

    public static byte[] Serialize(PushMessage m) => JsonSerializer.SerializeToUtf8Bytes(m, ProtocolJson.Options);

    static string Cut(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) { return text; }
        if (maxBytes < 4) { return ""; }
        return Limits.KeepStart(text, maxBytes - 3)!.TrimEnd() + "…";
    }
}
