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

// For an optional field whose unknown value must read as absent, not as one of its values
// (session.class, #913): its absence is the older host's behaviour, which a client knows how to show.
public sealed class UnknownAsAbsentConverter<T> : JsonConverter<T?> where T : struct, Enum
{
    static readonly Dictionary<string, T> ByName = Enum.GetValues<T>().ToDictionary(v => JsonNamingPolicy.CamelCase.ConvertName(v.ToString()), v => v, StringComparer.Ordinal);

    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && ByName.TryGetValue(reader.GetString()!, out var v)) { return v; }
        // Anything else, an object or a list included, is passed over whole.
        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
    {
        if (value is { } v) { writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(v.ToString())); }
        else { writer.WriteNullValue(); }
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
    // activity.userMessageLabel: a schedule's number, say (#489).
    public const int UserMessageLabel = 64;
    public const int ToolSummary = 200;
    public const int DriverPrincipal = 128;
    public const int QuestionMessage = 8 * 1024;
    public const int History = 50;
    // Free text in an answer (session.answer's text, or a field's other), and the request.
    public const int AnswerText = 4 * 1024;
    public const int AnswerRequest = 16 * 1024;
    // The text of a session.reply.
    public const int ReplyText = 16 * 1024;
    // A sub-agent's entry (docs/design/subagents.md §5): its label, the start of the prompt
    // it was given, and the end of what it last said or thought.
    public const int SubagentName = 120;
    public const int SubagentTask = 512;
    public const int SubagentLine = 300;
    // session.attention.message: the host's own sentence about what went wrong (#800, #841).
    public const int AttentionMessage = 300;
    // Entries a host lists, running ones first; SessionBudget.Fit may cap them further.
    public const int Subagents = 20;

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
    // Sub-agent entries left once everything else about them has been trimmed.
    public const int SubagentsCap = 8;

    // Additive first: everything an older client ignores goes before anything it shows, so a
    // session carrying sub-agent fields (subagents.md) never shows an older reader less than
    // the same session would without them. The sub-agents (finished entries, every line,
    // every task, all but SubagentsCap), the main agent's own reasoning, its own response (to
    // 4 KiB, then gone, still marked truncated), the entries' tools, and the entries, each
    // counted in subagentsMore. Then, as before them: oldest tool history, reasoning, the
    // user's message, the response (to 4 KiB, end kept), the question's message (to 2 KiB),
    // and only then the question's fields, marking the question truncated so a client says
    // to answer elsewhere.
    public static Session Fit(Session s, int maxBytes = MaxSessionBytes)
    {
        bool Fits(Session x) => ProtocolJson.ByteCount(x) <= maxBytes;
        if (Fits(s)) { return s; }

        s = Subagents(s, a => a.Where(e => !e.Finished));
        if (!Fits(s)) { s = Subagents(s, a => a.Select(e => e with { Line = null })); }
        if (!Fits(s)) { s = Subagents(s, a => a.Select(e => e with { Task = null })); }
        if (!Fits(s)) { s = Subagents(s, a => a.Take(SubagentsCap)); }
        if (!Fits(s)) { s = WithOwn(s, o => o with { Reasoning = null }); }
        if (!Fits(s))
        {
            s = WithOwn(s, o =>
            {
                var (own, cut) = Limits.KeepEnd(o.Response, 4 * 1024);
                return o with { Response = own, ResponseTruncated = cut || o.ResponseTruncated == true ? true : null };
            });
        }
        if (!Fits(s)) { s = WithOwn(s, o => o with { Response = null, ResponseTruncated = o.Response is null ? o.ResponseTruncated : true }); }
        if (!Fits(s)) { s = Subagents(s, a => a.Select(e => e with { CurrentTool = null })); }
        if (!Fits(s)) { s = Subagents(s, _ => []); }
        if (Fits(s)) { return s; }

        // What is left of them is a few ids and flags. The older fields are trimmed exactly as
        // they would be without them, then, should those few bytes still overflow, further.
        s = Older(s, x => Fits(WithoutSubagentFields(x)));
        return Older(s, Fits);
    }

    static Session Older(Session s, Func<Session, bool> fits)
    {
        var history = s.Activity.History?.ToList();
        while (history is { Count: > 0 } && !fits(s))
        {
            history.RemoveAt(0);
            s = s with { Activity = s.Activity with { History = history.Count == 0 ? null : [.. history] } };
        }
        if (!fits(s)) { s = s with { Activity = s.Activity with { Reasoning = null } }; }
        // Who wrote it goes with it: a label on no message says nothing.
        if (!fits(s)) { s = s with { Activity = s.Activity with { UserMessage = null, UserMessageFrom = null, UserMessageLabel = null } }; }
        if (!fits(s))
        {
            var (r, cut) = Limits.KeepEnd(s.Activity.Response, 4 * 1024);
            s = s with { Activity = s.Activity with { Response = r, ResponseTruncated = cut || s.Activity.ResponseTruncated == true ? true : null } };
        }
        if (!fits(s) && s.Question is { } q1)
        {
            // The escaped text goes with the message it describes: the same marks, those still in it,
            // and the digest of what is now sent. Cut, it is truncated, which is never confirmed.
            var kept = Limits.KeepStart(q1.Message, 2 * 1024)!;
            s = s with { Question = q1 with { Message = kept, Truncated = true, Escaped = q1.Escaped is { } e ? EscapedApproval.Cut(e, kept) : null } };
        }
        if (!fits(s) && s.Question is { } q2)
        {
            s = s with { Question = q2 with { Fields = [], Truncated = true } };
        }
        if (!fits(s))
        {
            s = s with { Activity = s.Activity with { Response = null, ResponseTruncated = true, CurrentTool = null } };
        }
        return s;
    }

    // The session as a client that predates sub-agents reads it.
    internal static Session WithoutSubagentFields(Session s) => s with
    {
        Subagents = null,
        SubagentsMore = null,
        WaitingOn = null,
        Activity = s.Activity with { Source = null, Own = null },
        Question = s.Question is { } q ? q with { Agent = null } : null,
    };

    // Trims the list, counting whatever it drops in subagentsMore.
    static Session Subagents(Session s, Func<IEnumerable<Subagent>, IEnumerable<Subagent>> trim)
    {
        if (s.Subagents is not { Count: > 0 } list) { return s; }
        var kept = trim(list).ToList();
        var more = (s.SubagentsMore ?? 0) + list.Count - kept.Count;
        return s with { Subagents = kept.Count == 0 ? null : kept, SubagentsMore = more > 0 ? more : null };
    }

    static Session WithOwn(Session s, Func<OwnActivity, OwnActivity> change) =>
        s.Activity.Own is { } own ? s with { Activity = s.Activity with { Own = change(own) } } : s;

    // session.get { id, agent }: one sub-agent in full, within the same budget. Its oldest
    // tools go first, then its reasoning, its response to 4 KiB, then everything but the entry.
    public static Subagent Fit(Subagent a, int maxBytes = MaxSessionBytes)
    {
        bool Fits(Subagent x) => ProtocolJson.ByteCount(x) <= maxBytes;
        if (Fits(a)) { return a; }
        var history = a.History?.ToList();
        while (history is { Count: > 0 } && !Fits(a))
        {
            history.RemoveAt(0);
            a = a with { History = history.Count == 0 ? null : [.. history] };
        }
        if (!Fits(a)) { a = a with { Reasoning = null }; }
        if (!Fits(a))
        {
            var (r, cut) = Limits.KeepEnd(a.Response, 4 * 1024);
            a = a with { Response = r, ResponseTruncated = cut || a.ResponseTruncated == true ? true : null };
        }
        if (!Fits(a)) { a = a with { Response = null, ResponseTruncated = a.Response is null ? a.ResponseTruncated : true, Line = null, Task = null }; }
        return a;
    }
}

[UnknownAs(Unknown)]
public enum AgentKind { Copilot, Claude, Codex, Unknown }

[UnknownAs(Idle)]
// Ending: the host is ending the session (session.stop), until its process has gone.
public enum SessionStatus { Working, Waiting, Idle, Ending }

// Who drives the session's current turn: the person, or an agent acting through the host (the
// purple edge). Absent on the wire means Person, and a host sends it only as Agent, so an older
// client, which ignores it, shows every session as the person's: the safe way round.
[UnknownAs(Person)]
public enum DriverKind { Person, Agent }

// Who wrote activity.userMessage (#489). A value a client does not know is Other, never the
// person's: a prompt is shown as the person's own only when nothing says it was not.
[UnknownAs(Other)]
public enum UserMessageFrom { Person, Schedule, Agent, Other }

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

// session.attention.kind (#800). A kind a client does not know is shown as an error: something
// is wrong, and the agent's own message says what.
[UnknownAs(Error)]
public enum AttentionKind { Error, SignInNeeded, NotResponding }

// What sort of error ended the turn, from the agent's structured fields, never its wording.
[UnknownAs(Other)]
public enum ErrorClass { Other, Auth, Rate, Quota, Model, Network }

// Where the agent can be signed in again. Machine is sent only when the host knows nothing else
// is possible. A value a client does not know is Unknown, which claims nothing (#800 ruling): no
// sign-in from the phone is offered on it, and nor is it said to be the machine's only.
[UnknownAs(Unknown)]
public enum SignInWhere { Phone, Machine, Unknown }

// The way back from signInNeeded. Pending: signed in (a sign-in finished, or a still-valid
// credential was found), waiting for the session to show it: attention stays signInNeeded until
// the model is seen answering after the latest failure, and a sign-in alone never clears it
// (#800 ruling). One a client does not know is Failed: a sign-in is then offered where it can
// be, rather than waited on.
[UnknownAs(Failed)]
public enum RecoveryState { Checking, SigningIn, Pending, Failed }

// session.class (#913): which notification tier the session is in now, decided by the host so
// every client follows one policy instead of its own. A class a client does not know reads as
// absent (UnknownAsAbsentConverter), so it keeps today's rules: read as NeedsYou, a newer host
// could interrupt people with something an older client can't explain (Creative, #913 q10).
public enum NotificationClass { NeedsYou, Done, Fyi }

// Dev: built from a working copy rather than released (docs/protocol/v1-usage.md). Sessions:
// how many were live when hello was answered. ReadOnly: why this machine takes no answers,
// replies, stops or launches from other devices now (agent-ha-bridge running for the user,
// design G5), for a client to say so rather than only leave the controls out. Instance: this
// run of the host, new each time it starts, so a client can tell a reconnect to the same process
// from a restart.
// Restore: what went when the host last stopped (#780, RestoreProtocol), for RestoreProtocol.ShownFor
// after it started; absent when nothing did, or from a host that does not say.
public sealed record MachineInfo([property: Strict(Strictness.Identity)] string Id, string Name, string Os, string HostVersion, bool? Dev = null, int? Sessions = null, string? ReadOnly = null, string? Instance = null,
    RestoreNotice? Restore = null);

public sealed record HelloResult([property: Strict(Strictness.Identity)] int Protocol, MachineInfo Machine, IReadOnlyList<string> Capabilities);

public sealed record Session
{
    [Strict(Strictness.Identity)]
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
    // Agent while an agent drives the current turn: it launched the session, or answered or
    // replied to it, and nobody has taken it back since with an answer, a reply or input at the
    // machine. Null (absent) for the person.
    public DriverKind? Driver { get; init; }
    // With Driver Agent, which agent, when the host knows (within Limits.DriverPrincipal):
    // "homeassistant:<user id>" for a press by one of homeAssistant.agentUserIds.
    public string? DriverPrincipal { get; init; }
    // The sub-agents this session started, at any depth (docs/design/subagents.md): running
    // ones first, then the most recently finished. Absent when there are none. Older clients
    // ignore it and keep reading activity.background's counts.
    public IReadOnlyList<Subagent>? Subagents { get; init; }
    // Entries left out of Subagents: by the host's list limit or by SessionBudget.Fit.
    public int? SubagentsMore { get; init; }
    // Status precedence rule 2: the sub-agent whose question is pending (Question.Agent),
    // when the session itself has none.
    public string? WaitingOn { get; init; }
    // What is wrong with the session that its status cannot say (#800): it needs its agent
    // signed in again, its turn ended on an error, or it stopped answering. Status stays
    // truthful beside it (idle after an error), so an older client, which ignores this,
    // reads the session as it always did. Absent when nothing is wrong.
    public Attention? Attention { get; init; }
    // The notification tier the session is in (#913): NeedsYou interrupts, Done waits, Fyi never
    // notifies. The host sets it from the fields above, the parent session carrying its
    // delegates' questions. Absent from an older host, and a client then keeps its own rules.
    [JsonConverter(typeof(UnknownAsAbsentConverter<NotificationClass>))]
    public NotificationClass? Class { get; init; }
    // When the session entered its present tier (#913), the episode's start, which the 15-minute
    // reminder counts from: kept while Class is unchanged, so a further question never restarts it;
    // on a change of Class, the earliest of question.askedAt and attention.since for NeedsYou, else
    // statusSince. It moves only with Class, so never moves updatedAt on its own.
    public DateTimeOffset? ClassSince { get; init; }
    // Restored by the host after a reboot or a crash (#780, RestoreProtocol). Absent otherwise,
    // and from a host that does not restore.
    public SessionRestored? Restored { get; init; }

    // The list and event form: everything but the tool history.
    public Session WithoutHistory() => Activity.History is null ? this : this with { Activity = Activity with { History = null } };
}

// session.attention (#800, v1-sessions.md "Attention"). Which fields a kind uses is the spec's;
// a reader takes whatever is there. Nothing in it is a credential, a sign-in code or a link:
// message is a sentence the host composes from kind, errorClass, statusCode and provider alone,
// never the agent's words (#841), shown as text only.
public sealed record Attention
{
    public required AttentionKind Kind { get; init; }
    // When it began: the error's time, or for NotResponding the last event's.
    public required DateTimeOffset Since { get; init; }
    public AgentKind? Agent { get; init; }
    // SignInNeeded: whose sign-in it is ("github", "anthropic", "openai").
    public string? Provider { get; init; }
    // SignInNeeded: the account, when the host knows it from metadata, never from a token.
    public string? Account { get; init; }
    // Host-composed from the structured fields, never agent text; within Limits.AttentionMessage.
    public string? Message { get; init; }
    public ErrorClass? ErrorClass { get; init; }
    // Error: when the agent said it can be tried again (a rate limit, a quota).
    public DateTimeOffset? RetryAt { get; init; }
    public SignInWhere? SignIn { get; init; }
    public RecoveryState? Recovery { get; init; }
    // NotResponding: the transcript's last event.
    public DateTimeOffset? LastEventAt { get; init; }
}

public sealed record Activity
{
    public string? Response { get; init; }
    public bool? ResponseTruncated { get; init; }
    public string? Reasoning { get; init; }
    public ToolCall? CurrentTool { get; init; }
    public IReadOnlyList<ToolCall>? History { get; init; }
    public string? UserMessage { get; init; }
    // Who wrote UserMessage when it was not the person (#489): a schedule, an agent, or something
    // else that is not someone typing. Absent means the person. Strict: read as absent, a prompt an
    // agent or a schedule sent would be shown as the person's own words.
    [Strict(Strictness.Security)]
    public UserMessageFrom? UserMessageFrom { get; init; }
    // A short plain-text label for it, at most Limits.UserMessageLabel bytes: a schedule's number.
    public string? UserMessageLabel { get; init; }
    public int? TurnCount { get; init; }
    public int? Compactions { get; init; }
    // Work the session started that outlives its own turn, still running. Absent when there
    // is none; status says nothing about it, so an idle session with this is not finished.
    public Background? Background { get; init; }
    // Which sub-agent wrote the top-level response and reasoning, each on its own: they stay
    // last-writer-wins for older clients, and a main agent's reply can sit beside a
    // delegate's reasoning. Absent while both are the main agent's own. Strict: read as absent,
    // a delegate's text would be shown as the main agent's.
    [Strict(Strictness.Security)]
    public ActivitySource? Source { get; init; }
    // The main agent's own text for each field Source attributes to a sub-agent, so a newer
    // client never loses the main agent's answer. Sent only with Source.
    public OwnActivity? Own { get; init; }
}

// The sub-agent ids that wrote the top-level text; a field left out is the main agent's.
public sealed record ActivitySource
{
    [Strict(Strictness.Security)]
    public string? Response { get; init; }
    [Strict(Strictness.Security)]
    public string? Reasoning { get; init; }
}

// The main agent's latest response and reasoning, for the fields Source names a sub-agent
// for. A field absent here, with Source naming one for it, means the main agent has none.
public sealed record OwnActivity
{
    public string? Response { get; init; }
    public bool? ResponseTruncated { get; init; }
    public string? Reasoning { get; init; }
    // When the main agent last wrote either.
    public DateTimeOffset? At { get; init; }
}

[UnknownAs(Task)]
public enum SubagentKind { Background, Task }

// Idle: a background sub-agent between runs, which a message can wake. Ended: it can no
// longer run (its own abort, a failure, the session's CLI exiting, or a task agent done).
[UnknownAs(Idle)]
public enum SubagentStatus { Working, Idle, Waiting, Ended }

// How a finished run went, for the done and failed glyphs. A host that did not see the run
// end (its start was all it read) leaves it out.
[UnknownAs(Done)]
public enum SubagentOutcome { Done, Failed }

// One sub-agent (docs/design/subagents.md §2). Lists carry the entry; session.get { id,
// agent } adds its own response, reasoning and tool history.
public sealed record Subagent
{
    [Strict(Strictness.Identity)]
    public required string Id { get; init; }
    public string? Parent { get; init; }
    public required SubagentKind Kind { get; init; }
    public string? Name { get; init; }
    public string? Task { get; init; }
    public string? Model { get; init; }
    public required SubagentStatus Status { get; init; }
    public SubagentOutcome? Outcome { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string? Line { get; init; }
    public ToolCall? CurrentTool { get; init; }
    public int Shells { get; init; }
    // session.get { id, agent } only.
    public string? Response { get; init; }
    public bool? ResponseTruncated { get; init; }
    public string? Reasoning { get; init; }
    public IReadOnlyList<ToolCall>? History { get; init; }

    // Not running: idle between runs, or ended. The first entries the budget lets go.
    [JsonIgnore]
    public bool Finished => Status is SubagentStatus.Idle or SubagentStatus.Ended;
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
    [Strict(Strictness.Identity)]
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Summary { get; init; }
    public required ToolStatus Status { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
}

// Everything a person decides on is [Strict] (Security): the question this id answers, what it
// asks, how and with which choices, whether a device can answer it, and who asks. A question
// any of that cannot be read from is dropped, never shown with part of it guessed or left out.
public sealed record Question
{
    [Strict(Strictness.Security)]
    public required string Id { get; init; }
    [Strict(Strictness.Security)]
    public required QuestionKind Kind { get; init; }
    [Strict(Strictness.Security)]
    public required string Message { get; init; }
    [Strict(Strictness.Security)]
    public IReadOnlyList<Field> Fields { get; init; } = [];
    public required DateTimeOffset AskedAt { get; init; }
    // Set when the host had to cut the message or drop the fields to fit the size budget;
    // a client then says to answer at the machine instead.
    [Strict(Strictness.Security)]
    public bool? Truncated { get; init; }
    // Set when no host can answer this prompt from another device (a Claude permission
    // prompt, which agent-ha-bridge only ever mirrored; a Copilot permission prompt of a kind
    // the host does not drive, or on a host with remote approval off): a client shows it with
    // no controls and says it is waiting at the machine.
    [Strict(Strictness.Security)]
    public bool? AnswerAtMachine { get; init; }
    // The sub-agent that asked, when it is not the session's own question (status
    // precedence rule 2). Absent for the main agent's.
    [Strict(Strictness.Security)]
    public string? Agent { get; init; }
    // Set by the host on an approval it showed one or more characters of as escapes (#220): Approve
    // then takes the escaped-command confirmation, Deny does not (#803, EscapedApproval).
    [Strict(Strictness.Security)]
    public EscapedText? Escaped { get; init; }
}

// What the host escaped in an approval's message, and how Approve is given for it (#803).
//   Shown: SHA-256, lower-case hex, of the message's UTF-8 exactly as sent. A confirmation names it,
//     so one given for what a person read approves nothing that reads otherwise.
//   Marks: the host's own escapes in the message, each [start, length] in UTF-16 code units, in
//     order, one mark for each escape and nothing else (EscapedApproval.IsEscape). Text an agent wrote itself (six characters "\u202E")
//     is never in one, which is how a client tells the two apart.
//   Approve: "confirm" (Approve needs the confirmation, from an approved person's device) or
//     "machine" (this machine takes Approve for it only at the machine; Deny still goes) or
//     "tooMany" (more escapes than Marks can carry, EscapedApproval.MaxMarks: Approve only at the
//     machine whatever its policy; Deny still goes).
public sealed record EscapedText
{
    [Strict(Strictness.Security)]
    public required string Shown { get; init; }
    [Strict(Strictness.Security)]
    public required IReadOnlyList<IReadOnlyList<int>> Marks { get; init; }
    [Strict(Strictness.Security)]
    public required string Approve { get; init; }
}

public sealed record Field
{
    [Strict(Strictness.Identity)]
    public required string Name { get; init; }
    [Strict(Strictness.Security)]
    public string? Title { get; init; }
    [Strict(Strictness.Security)]
    public string? Description { get; init; }
    [Strict(Strictness.Security)]
    public required FieldType Type { get; init; }
    [Strict(Strictness.Security)]
    public IReadOnlyList<Option>? Options { get; init; }
    [Strict(Strictness.Security)]
    public bool? AllowOther { get; init; }
    public JsonElement? Default { get; init; }
    public bool? Required { get; init; }
    // What the agent's schema allows (#766). Not strict: a client that cannot read them checks
    // less before sending, and the host checks them again before it types.
    public FieldConstraints? Constraints { get; init; }
}

public sealed record Option([property: Strict(Strictness.Security)] string Value, [property: Strict(Strictness.Security)] string? Label = null);

// One page of sessions.list. Next is absent on the last page. DiscoveryHeld is present while
// the host's discovery is incomplete (DiscoveryHeld below).
public sealed record SessionList(IReadOnlyList<Session> Sessions, long Revision, string? Next = null, [property: Strict(Strictness.Security)] DiscoveryHeld? DiscoveryHeld = null);

// Why the host's last discovery pass could not account for everything, so it is holding
// whatever it would decide from a session's absence: removing sessions of those agents,
// offering or starting resumes of them, and reclaiming worktrees. Agents are the ones held;
// sessions of the others are removed and resumed as usual. Reason is for a person, in words
// ("an unrecognised copilot process (pid 4120)"), within MaxReasonBytes.
// Strict where it is held, and its agents whole: read as absent or as fewer agents, a hold the
// host still has would read as lifted, and a client would take sessions of a held agent as gone.
public sealed record DiscoveryHeld([property: Strict(Strictness.Identity)] string Reason, [property: Strict(Strictness.Security)] IReadOnlyList<AgentKind> Agents)
{
    public const int MaxReasonBytes = 512;
}

// discovery.changed: the hold began, changed or ended (DiscoveryHeld absent).
public sealed record DiscoveryChanged([property: Strict(Strictness.Security)] DiscoveryHeld? DiscoveryHeld = null);

public sealed record ListRequest(string? Cursor = null);

// sessions.subscribe's request. Patch: live changes as session.patch. Since: what the client
// held of an earlier channel to this run of the host, to be sent only what changed since. Both
// are taken by a host that offers sessions.patch and ignored by any other.
public sealed record SubscribeRequest
{
    public bool? Patch { get; init; }
    public SubscribeSince? Since { get; init; }
}

// The run of the host (hello's machine.instance) and the revision the client's whole list is at.
public sealed record SubscribeSince
{
    public required string Instance { get; init; }
    public required long Revision { get; init; }
}

// sessions.focus (Capabilities.SessionsFocus): whether the client is on screen now, and the session
// it shows, if any. Live changes to the others then come together at most every 12 s, and a
// visible device is not notified of a finished turn it can see (#610).
public sealed record FocusRequest
{
    public required bool Visible { get; init; }
    public string? Open { get; init; }
}

// sessions.subscribe's result; the sessions themselves follow as session.upsert events,
// then sessions.synced. Resumed: only the sessions changed since the request's Since follow.
public sealed record SubscribeResult(long Revision, int Count, bool? Resumed = null);

// DiscoveryHeld: as on sessions.list, as of the end of the snapshot. Live: in a resumed
// snapshot, every live session's id, the ones not sent again included. Strict and whole: read
// with an id left out, a session still running would be dropped from the client's list.
public sealed record SessionsSynced(long Revision, [property: Strict(Strictness.Security)] DiscoveryHeld? DiscoveryHeld = null, [property: Strict(Strictness.Identity)] IReadOnlyList<string>? Live = null)
{
    // The most of Live a host sends; a host with more sessions sends the whole snapshot instead.
    public const int MaxLiveBytes = 32 * 1024;
}

// Revision: to a client that asked for patches, as on a session.patch.
public sealed record SessionRemoved([property: Strict(Strictness.Identity)] string Id, long? Revision = null);

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
    // True from a client that saw sessions.progress: it hears DeliveryProgress frames before the
    // result, and the result carries Timings. Absent, nothing about the exchange changes.
    public bool? Progress { get; init; }
    // With Approve on a question that has Escaped: the command the person's device signed when they
    // pressed the confirmation (EscapedApproval). Only a host's relay channel acts on one.
    public SignedCommand? Confirm { get; init; }
}

public sealed record AnswerResult(AnswerOutcome Outcome, string? Reason = null)
{
    // Only for a request that asked for progress.
    public DeliveryTimings? Timings { get; init; }
}

// How far a delivery had got, in milliseconds after the host received the request. Recorded is
// the agent's own timestamp for the prompt or answer, where its transcript has one. A stage
// absent was not reached, or is not known.
public sealed record DeliveryTimings
{
    public long? Typed { get; init; }
    public long? Recorded { get; init; }
    public long? Confirmed { get; init; }
}

// The progress of a session.reply or session.answer that asked for it, sent as
// {"re": id, "progress": {...}} before the response (Channel framing in v1-sessions.md). Stage
// is a string, not an enum: a client ignores one it doesn't know rather than reading it as
// another.
public sealed record DeliveryProgress(string Stage, bool? Working = null)
{
    // The host has the request, before any check.
    public const string Received = "received";
    // Every key has gone: the outcome will be delivered or unconfirmed. Working: the session was
    // working, so the agent takes it at its next step (queued).
    public const string Typed = "typed";
}

// session.reply: a prompt for the session, typed into its own input as if at the machine.
// Request is the client's UUID for this reply, as for an answer. Attachments name files the
// same device sent first (Attachments.cs); with any, the text may be blank.
public sealed record ReplyRequest
{
    public required string Id { get; init; }
    public required string Request { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<AttachmentRef>? Attachments { get; init; }
    // As on AnswerRequest.
    public bool? Progress { get; init; }
}

// The same outcomes as an answer, read the same way.
public sealed record ReplyResult(AnswerOutcome Outcome, string? Reason = null)
{
    // Only for a request that asked for progress.
    public DeliveryTimings? Timings { get; init; }
}

public static class Ops
{
    public const string Hello = "hello";
    public const string SessionsList = "sessions.list";
    public const string SessionGet = "session.get";
    public const string SessionsSubscribe = "sessions.subscribe";
    public const string SessionsFocus = "sessions.focus";
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
    public const string SessionPatch = "session.patch";
    public const string SessionRemove = "session.remove";
    public const string SessionsSynced = "sessions.synced";
    public const string DiscoveryChanged = "discovery.changed";
    // That what hello says has changed, sent with {} to a channel whose hello asked for changes,
    // when its readOnly or capabilities differ from what that channel was last told
    // (Capabilities.HelloChanged). The client asks hello again.
    public const string HelloChanged = "hello.changed";
}

// Splits sessions into list pages that each fit a message. Every session must already
// be within SessionBudget, so a page always holds at least one.
public static class Paging
{
    public static IEnumerable<IReadOnlyList<Session>> Pages(IEnumerable<Session> sessions, int maxBytes = ProtocolJson.MaxMessageBytes)
    {
        // Room for the envelope: {"re":n,"ok":true,"result":{"sessions":[...],"revision":n,"next":"..."}}
        // and a discoveryHeld, whose reason is bounded and whose agents are at most three, though
        // a reason of emoji is escaped to three times its size.
        const int Envelope = 256 + 3 * DiscoveryHeld.MaxReasonBytes + 128;
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
    // sessions.subscribe takes patch (live changes as session.patch) and since (resuming without
    // a whole snapshot): docs/protocol/v1-sessions.md, Patches and Resuming.
    public const string SessionsPatch = "sessions.patch";
    // hello takes { "changes": true }: the host then sends hello.changed on that channel whenever
    // what hello would say to it changes, and a client needs to ask hello again only rarely
    // (docs/protocol/v1-sessions.md, hello). Apps asked every 10 s, ~36 hellos a minute (#640).
    public const string HelloChanged = "hello.changed";
    // sessions.focus: a client says whether it is on screen and which session it shows; the host
    // sends the open session's changes at once and the rest together, at most every 12 s
    // (docs/protocol/v1-sessions.md, sessions.focus). Each radio wake-up costs a phone (#610).
    public const string SessionsFocus = "sessions.focus";
    public const string SessionsAnswer = "sessions.answer";
    public const string SessionsReply = "sessions.reply";
    public const string SessionsStop = "sessions.stop";
    public const string Push = "push";
    public const string SessionsAttach = "sessions.attach";
    // attachment.start takes a blob: files up to AttachmentLimits.BlobBytes through the relay's
    // blob store (docs/protocol/blobs.md). Offered with sessions.attach, on a channel through a
    // relay that has a blob store.
    public const string SessionsAttachBlob = "sessions.attach.blob";
    // attachment.start in chunks takes files up to AttachmentLimits.DirectBytes, not only
    // AttachmentLimits.Bytes: offered with sessions.attach on a direct channel (the local pipe),
    // where the bytes cost the relay nothing.
    public const string SessionsAttachDirect = "sessions.attach.direct";
    // Copilot permission prompts are approved or denied through session.answer on this host.
    public const string SessionsApprove = "sessions.approve";
    // session.reply and session.answer take progress: true, and report DeliveryProgress before
    // their result. Offered with either.
    public const string SessionsProgress = "sessions.progress";
}
