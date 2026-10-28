// Bridge Protocol v1, sessions (read-only slice). The contract is docs/protocol/v1-sessions.md;
// these records mirror it and serialize to exactly that JSON with ProtocolJson.Options.
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBridge.Protocol;

public static class ProtocolJson
{
    public const int Version = 1;

    // No single message may exceed this much JSON, as sent (UTF-8 bytes), so it always
    // fits one Noise transport message (65,535 bytes) with room for framing.
    public const int MaxMessageBytes = 48 * 1024;

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Text in the Basic Multilingual Plane is sent as UTF-8, not \u escapes: the default
        // encoder doubles the size of CJK text, which broke the byte limits in review.
        // Nothing here is ever embedded in HTML, which is the only thing the stricter
        // encoder protects. Even this encoder escapes characters outside the BMP (emoji)
        // as a surrogate pair, \uXXXX\uXXXX: 12 bytes for 4 of text. So the per-field
        // limits, which count the text, do not bound the JSON; SessionBudget.Fit does.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new TolerantEnumConverterFactory(), new UtcTimeConverter() },
    };

    public static int ByteCount<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options).Length;
}

// Every time on the wire is UTC with millisecond precision and a Z, whatever offset the
// value carries, so clients in any language compare and sort them as strings.
public sealed class UtcTimeConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
}

// The value a reader uses for an enum string it does not know, sent by a newer host.
[AttributeUsage(AttributeTargets.Enum)]
public sealed class UnknownAsAttribute(object value) : Attribute
{
    public object Value { get; } = value;
}

// camelCase enum strings. Reading an unknown string gives the enum's [UnknownAs] value
// instead of failing the whole message: the contract promises clients tolerate them.
public sealed class TolerantEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(TolerantEnumConverter<>).MakeGenericType(typeToConvert))!;

    sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        static readonly Dictionary<string, T> ByName = Enum.GetValues<T>().ToDictionary(v => JsonNamingPolicy.CamelCase.ConvertName(v.ToString()), v => v, StringComparer.Ordinal);
        static readonly T Fallback = typeof(T).GetCustomAttribute<UnknownAsAttribute>() is { } a ? (T)a.Value
            : throw new InvalidOperationException($"{typeof(T).Name} needs [UnknownAs]");

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String && ByName.TryGetValue(reader.GetString()!, out var v) ? v : Fallback;

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
    }
}

// Limits are UTF-8 bytes of the text. Its JSON form can be larger (escaped quotes and
// control characters, and 12 bytes for each emoji), which SessionBudget.Fit accounts for.
public static class Limits
{
    public const int Title = 240;
    public const int Response = 16 * 1024;
    public const int Reasoning = 4 * 1024;
    public const int UserMessage = 2 * 1024;
    public const int ToolSummary = 200;
    public const int QuestionMessage = 8 * 1024;
    public const int History = 50;
    // Free text in an answer (session.answer's text, or a field's other), and the request.
    public const int AnswerText = 4 * 1024;
    public const int AnswerRequest = 16 * 1024;
    // The text of a session.reply.
    public const int ReplyText = 16 * 1024;

    // Keeps the end of the text, as clients show the most recent part. Never splits a
    // character (UTF-8 sequence or surrogate pair).
    public static (string? Text, bool Truncated) KeepEnd(string? text, int maxBytes)
    {
        if (text is null || Encoding.UTF8.GetByteCount(text) <= maxBytes) { return (text, false); }
        int start = text.Length, bytes = 0;
        while (start > 0)
        {
            var take = start >= 2 && char.IsSurrogatePair(text[start - 2], text[start - 1]) ? 2 : 1;
            var n = Encoding.UTF8.GetByteCount(text.AsSpan(start - take, take));
            if (bytes + n > maxBytes) { break; }
            bytes += n;
            start -= take;
        }
        return (text[start..], true);
    }

    // Keeps the start, for titles and one-line summaries.
    public static string? KeepStart(string? text, int maxBytes)
    {
        if (text is null || Encoding.UTF8.GetByteCount(text) <= maxBytes) { return text; }
        int end = 0, bytes = 0;
        while (end < text.Length)
        {
            var take = end + 1 < text.Length && char.IsSurrogatePair(text[end], text[end + 1]) ? 2 : 1;
            var n = Encoding.UTF8.GetByteCount(text.AsSpan(end, take));
            if (bytes + n > maxBytes) { break; }
            bytes += n;
            end += take;
        }
        return text[..end];
    }
}

// Every Session as sent - in a list page, an event or session.get - fits this budget, so a
// page of at least one session always fits a message. Field limits alone cannot promise
// it (question options, escaping, history), so hosts finish with Fit.
public static class SessionBudget
{
    public const int MaxSessionBytes = 40 * 1024;

    // Trims in the order that loses least for a reader: oldest tool history, reasoning,
    // the user's message, the response (to 4 KiB, end kept), the question's message (to
    // 2 KiB), and only then the question's fields, marking the question truncated so a
    // client says to answer elsewhere.
    public static Session Fit(Session s, int maxBytes = MaxSessionBytes)
    {
        bool Fits(Session x) => ProtocolJson.ByteCount(x) <= maxBytes;
        if (Fits(s)) { return s; }

        var history = s.Activity.History?.ToList();
        while (history is { Count: > 0 } && !Fits(s))
        {
            history.RemoveAt(0);
            s = s with { Activity = s.Activity with { History = history.Count == 0 ? null : [.. history] } };
        }
        if (!Fits(s)) { s = s with { Activity = s.Activity with { Reasoning = null } }; }
        if (!Fits(s)) { s = s with { Activity = s.Activity with { UserMessage = null } }; }
        if (!Fits(s))
        {
            var (r, cut) = Limits.KeepEnd(s.Activity.Response, 4 * 1024);
            s = s with { Activity = s.Activity with { Response = r, ResponseTruncated = cut || s.Activity.ResponseTruncated == true ? true : null } };
        }
        if (!Fits(s) && s.Question is { } q1)
        {
            s = s with { Question = q1 with { Message = Limits.KeepStart(q1.Message, 2 * 1024)!, Truncated = true } };
        }
        if (!Fits(s) && s.Question is { } q2)
        {
            s = s with { Question = q2 with { Fields = [], Truncated = true } };
        }
        if (!Fits(s)) { s = s with { Activity = s.Activity with { Response = null, ResponseTruncated = true, CurrentTool = null } }; }
        return s;
    }
}

[UnknownAs(Unknown)]
public enum AgentKind { Copilot, Claude, Codex, Unknown }

[UnknownAs(Idle)]
// Ending: the host is ending the session (session.stop), until its process has gone.
public enum SessionStatus { Working, Waiting, Idle, Ending }

[UnknownAs(Done)]
public enum ToolStatus { Running, Done, Failed }

[UnknownAs(Form)]
public enum QuestionKind { Form, Approval }

[UnknownAs(Text)]
public enum FieldType { Choice, Multi, Boolean, Text, Number }

// An outcome a client does not know reads as Unconfirmed: never as Delivered, and never as
// a reason to answer again.
[UnknownAs(Unconfirmed)]
public enum AnswerOutcome { Delivered, Unconfirmed, Stale, Busy, Refused }

// Dev: built from a working copy rather than released (docs/protocol/v1-usage.md). Sessions:
// how many were live when hello was answered.
public sealed record MachineInfo(string Id, string Name, string Os, string HostVersion, bool? Dev = null, int? Sessions = null);

public sealed record HelloResult(int Protocol, MachineInfo Machine, IReadOnlyList<string> Capabilities);

public sealed record Session
{
    public required string Id { get; init; }
    public required AgentKind Agent { get; init; }
    public string? Title { get; init; }
    public string? Cwd { get; init; }
    public string? Repository { get; init; }
    public string? Branch { get; init; }
    public string? Model { get; init; }
    public required SessionStatus Status { get; init; }
    public required DateTimeOffset StatusSince { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public int? ProcessId { get; init; }
    public required Activity Activity { get; init; }
    public Question? Question { get; init; }

    // The list and event form: everything but the tool history.
    public Session WithoutHistory() => Activity.History is null ? this : this with { Activity = Activity with { History = null } };
}

public sealed record Activity
{
    public string? Response { get; init; }
    public bool? ResponseTruncated { get; init; }
    public string? Reasoning { get; init; }
    public ToolCall? CurrentTool { get; init; }
    public IReadOnlyList<ToolCall>? History { get; init; }
    public string? UserMessage { get; init; }
    public int? TurnCount { get; init; }
    public int? Compactions { get; init; }
    // Work the session started that outlives its own turn, still running. Absent when there
    // is none; status says nothing about it, so an idle session with this is not finished.
    public Background? Background { get; init; }
}

// Counts, never ids: a client shows that the session is waiting and on what kind of work.
public sealed record Background
{
    // Background agents (Copilot task agents started in background mode) still working.
    public int Agents { get; init; }
    // Background shell commands (async, or sync ones moved to the background) still running.
    public int Shells { get; init; }

    // The field as sent: null when nothing is running, so it is absent rather than zeros.
    public static Background? Of(int agents, int shells) =>
        agents <= 0 && shells <= 0 ? null : new Background { Agents = Math.Max(0, agents), Shells = Math.Max(0, shells) };
}

public sealed record ToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Summary { get; init; }
    public required ToolStatus Status { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
}

public sealed record Question
{
    public required string Id { get; init; }
    public required QuestionKind Kind { get; init; }
    public required string Message { get; init; }
    public IReadOnlyList<Field> Fields { get; init; } = [];
    public required DateTimeOffset AskedAt { get; init; }
    // Set when the host had to cut the message or drop the fields to fit the size budget;
    // a client then says to answer at the machine instead.
    public bool? Truncated { get; init; }
    // Set when no host can answer this prompt from another device (a Claude permission
    // prompt, which agent-ha-bridge only ever mirrored; a Copilot permission prompt of a kind
    // the host does not drive, or on a host with remote approval off): a client shows it with
    // no controls and says it is waiting at the machine.
    public bool? AnswerAtMachine { get; init; }
}

public sealed record Field
{
    public required string Name { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public required FieldType Type { get; init; }
    public IReadOnlyList<Option>? Options { get; init; }
    public bool? AllowOther { get; init; }
    public JsonElement? Default { get; init; }
    public bool? Required { get; init; }
}

public sealed record Option(string Value, string? Label = null);

// One page of sessions.list. Next is absent on the last page.
public sealed record SessionList(IReadOnlyList<Session> Sessions, long Revision, string? Next = null);

public sealed record ListRequest(string? Cursor = null);

// sessions.subscribe's result; the sessions themselves follow as session.upsert events,
// then sessions.synced.
public sealed record SubscribeResult(long Revision, int Count);

public sealed record SessionsSynced(long Revision);

public sealed record SessionRemoved(string Id);

// session.answer. Fields holds one value per field name, shaped by the field's type (see
// the contract); Text answers a question without fields. Request is the client's UUID for
// this answer, which makes a resend return the first outcome instead of typing again.
public sealed record AnswerRequest
{
    public required string Id { get; init; }
    public required string Question { get; init; }
    public required string Request { get; init; }
    public IReadOnlyDictionary<string, JsonElement>? Fields { get; init; }
    public string? Text { get; init; }
}

public sealed record AnswerResult(AnswerOutcome Outcome, string? Reason = null);

// session.reply: a prompt for the session, typed into its own input as if at the machine.
// Request is the client's UUID for this reply, as for an answer. Attachments name files the
// same device sent first (Attachments.cs); with any, the text may be blank.
public sealed record ReplyRequest
{
    public required string Id { get; init; }
    public required string Request { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<AttachmentRef>? Attachments { get; init; }
}

// The same outcomes as an answer, read the same way.
public sealed record ReplyResult(AnswerOutcome Outcome, string? Reason = null);

public static class Ops
{
    public const string Hello = "hello";
    public const string SessionsList = "sessions.list";
    public const string SessionGet = "session.get";
    public const string SessionsSubscribe = "sessions.subscribe";
    public const string SessionAnswer = "session.answer";
    public const string SessionReply = "session.reply";
    public const string SessionStop = "session.stop";
    public const string PushSubscribe = "push.subscribe";
    public const string PushUnsubscribe = "push.unsubscribe";
    public const string PushList = "push.list";
    public const string AttachmentStart = "attachment.start";
    public const string AttachmentChunk = "attachment.chunk";
    public const string AttachmentDiscard = "attachment.discard";
}

public static class Events
{
    public const string SessionUpsert = "session.upsert";
    public const string SessionRemove = "session.remove";
    public const string SessionsSynced = "sessions.synced";
}

// Splits sessions into list pages that each fit a message. Every session must already
// be within SessionBudget, so a page always holds at least one.
public static class Paging
{
    public static IEnumerable<IReadOnlyList<Session>> Pages(IEnumerable<Session> sessions, int maxBytes = ProtocolJson.MaxMessageBytes)
    {
        // Room for the envelope: {"re":n,"ok":true,"result":{"sessions":[...],"revision":n,"next":"..."}}
        const int Envelope = 256;
        var page = new List<Session>();
        var used = Envelope;
        foreach (var s in sessions)
        {
            var n = ProtocolJson.ByteCount(s) + 1;
            if (page.Count > 0 && used + n > maxBytes)
            {
                yield return page;
                page = [];
                used = Envelope;
            }
            page.Add(s);
            used += n;
        }
        if (page.Count > 0) { yield return page; }
    }
}

public static class Capabilities
{
    public const string SessionsRead = "sessions.read";
    public const string SessionsAnswer = "sessions.answer";
    public const string SessionsReply = "sessions.reply";
    public const string SessionsStop = "sessions.stop";
    public const string Push = "push";
    public const string SessionsAttach = "sessions.attach";
    // Copilot permission prompts are approved or denied through session.answer on this host.
    public const string SessionsApprove = "sessions.approve";
}
