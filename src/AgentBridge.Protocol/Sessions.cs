// Bridge Protocol v1, sessions (read-only slice). The contract is docs/protocol/v1-sessions.md;
// these records mirror it and serialize to exactly that JSON with ProtocolJson.Options.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBridge.Protocol;

public static class ProtocolJson
{
    public const int Version = 1;

    // No single message may exceed this much JSON (docs: "Rules for every message").
    public const int MaxMessageBytes = 48 * 1024;

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new UtcTimeConverter() },
    };
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

public static class Limits
{
    public const int Title = 120;
    public const int Response = 16_000;
    public const int Reasoning = 4_000;
    public const int UserMessage = 2_000;
    public const int ToolSummary = 200;
    public const int QuestionMessage = 8_000;
    public const int History = 50;

    // Keeps the end of the text, as clients show the most recent part. Never splits a
    // surrogate pair.
    public static (string? Text, bool Truncated) KeepEnd(string? text, int max)
    {
        if (text is null || text.Length <= max) { return (text, false); }
        var start = text.Length - max;
        if (char.IsLowSurrogate(text[start])) { start++; }
        return (text[start..], true);
    }

    // Keeps the start, for titles and one-line summaries.
    public static string? KeepStart(string? text, int max)
    {
        if (text is null || text.Length <= max) { return text; }
        var end = max;
        if (char.IsHighSurrogate(text[end - 1])) { end--; }
        return text[..end];
    }
}

public enum AgentKind { Copilot, Claude, Codex }

public enum SessionStatus { Working, Waiting, Idle }

public enum ToolStatus { Running, Done, Failed }

public enum QuestionKind { Form, Approval }

public enum FieldType { Choice, Multi, Boolean, Text, Number }

public sealed record MachineInfo(string Id, string Name, string Os, string HostVersion);

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

public sealed record SessionList(IReadOnlyList<Session> Sessions, long Revision);

public sealed record SessionRemoved(string Id);

public static class Ops
{
    public const string Hello = "hello";
    public const string SessionsList = "sessions.list";
    public const string SessionGet = "session.get";
    public const string SessionsSubscribe = "sessions.subscribe";
}

public static class Events
{
    public const string SessionUpsert = "session.upsert";
    public const string SessionRemove = "session.remove";
}

public static class Capabilities
{
    public const string SessionsRead = "sessions.read";
}
