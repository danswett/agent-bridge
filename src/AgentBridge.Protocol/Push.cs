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

// SignIn (#800): one per machine, agent and account while sessions need sign-in; never per session.
// Restored: one per machine, when its host started after sessions went with a reboot or a crash
// (#780). An older client reads it as a question: shown and alerting, with its own title and body,
// opening the machine (no session or question is named), so nothing invites an answer.
// Group (#913 rule 3): two or more questions of this machine's sessions within 30 s, as one notice
// that opens Needs you. It names no session or question, so an older client, which reads it as a
// question, shows its words and opens the machine, with nothing to answer.
[UnknownAs(Question)]
public enum NoticeKind { Question, Finished, Resolved, SignIn, Restored, Group }

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
    // Resolved notices replace a question quietly, and finished (Done) ones never alert (#913):
    // silent everywhere, older clients and Home Assistant included, not only where a client
    // reads data.class.
    public bool? Silent { get; init; }
    public required PushNoticeData Data { get; init; }
}

// What a client reads to act on a notice; Title and Body above carry the text.
public sealed record PushNoticeData
{
    public int V { get; init; } = 1;
    public required NoticeKind Kind { get; init; }
    public required string Machine { get; init; }
    // Absent for SignIn, which is about a machine, never a session (#800 6646515428): an older
    // client then opens the machine rather than a session view.
    public string? Session { get; init; }
    public string? Question { get; init; }
    // SignIn: whose sign-in it is, and how many sessions it stops. Never the account.
    // Group: how many sessions it stands for.
    public AgentKind? Agent { get; init; }
    public int? Sessions { get; init; }
    // The notice's tier (#913), as session.class: NeedsYou for a question, a sign-in and a crash's
    // restored notice, Done for finished (never NeedsYou). Absent on resolved, which withdraws a
    // notice rather than being one, on a reboot's restored, and from an older host. An unknown one
    // reads as absent, as session.class does.
    [JsonConverter(typeof(UnknownAsAbsentConverter<NotificationClass>))]
    public NotificationClass? Class { get; init; }
    // #913 rule 5: true on the one reminder of a question still unanswered 15 minutes after it was
    // notified, which is otherwise the question's own notice again. Absent on everything else.
    public bool? Reminder { get; init; }
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

    // A sign-in notice's own key in place of a session id, for its tag and topic: one per machine,
    // agent and account. The account goes in only as the first 8 hex of its SHA-256, so neither
    // the tag nor anything derived from it names it.
    public static string SignInKey(AgentKind agent, string? account) =>
        $"signin:{agent.ToString().ToLowerInvariant()}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account ?? "")))[..8].ToLowerInvariant()}";

    // Opens the machine, never a session or a question (#800 6646515428): an older client, which
    // reads the kind as a question, then shows no question view or answer controls.
    public static string SignInNavigate(string app, string machine, AgentKind agent) =>
        Link(app, $"host={Uri.EscapeDataString(machine)}&signin={agent.ToString().ToLowerInvariant()}");

    // A grouped notice (#913 rule 3) opens Needs you: the machine, as NavigateMachine opens it, with
    // needsyou=1. An older client opens the machine.
    public static string NavigateNeedsYou(string app, string machine) => NavigateMachine(app, machine) + "&needsyou=1";

    public static string Navigate(string app, string machine, string session, string? question) =>
        Link(app, $"host={Uri.EscapeDataString(machine)}&session={Uri.EscapeDataString(session)}" +
            (question is null ? "" : $"&question={Uri.EscapeDataString(question)}"));

    static string Link(string app, string q)
    {
        var hash = app.IndexOf('#');
        if (hash >= 0) { app = app[..hash]; }
        return app + (app.Contains('?') ? "&" : "?") + q;
    }

    // To a machine and no session (the restore notice, #780). The subscribed URL may already route
    // somewhere (installed from a session's page): its host, session and question are replaced, never
    // appended to, which left a second host and a session or question the notice never meant.
    // Anything else in the query stays.
    public static string NavigateMachine(string app, string machine)
    {
        var hash = app.IndexOf('#');
        if (hash >= 0) { app = app[..hash]; }
        var mark = app.IndexOf('?');
        var kept = new List<string>();
        if (mark >= 0)
        {
            foreach (var part in app[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                var key = Uri.UnescapeDataString(eq >= 0 ? part[..eq] : part);
                if (key is not ("host" or "session" or "question")) { kept.Add(part); }
            }
            app = app[..mark];
        }
        kept.Add($"host={Uri.EscapeDataString(machine)}");
        return app + "?" + string.Join('&', kept);
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
