using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentBridge.App.Core;

public enum FeedbackEvent
{
    Connecting,
    Connected,
    NotRunning,
    Refused,
    SessionsUpsert,
    SessionsRemove,
    SessionsSynced,
}

public enum FeedbackLog { HostConnectionError }

public sealed class FeedbackDiagnostics(TimeProvider? time = null)
{
    const int Limit = 100;
    readonly TimeProvider time = time ?? TimeProvider.System;
    readonly Queue<(DateTimeOffset At, FeedbackEvent Name)> events = new();
    readonly Queue<(DateTimeOffset At, FeedbackLog Code)> logs = new();
    readonly object gate = new();

    public void Record(FeedbackEvent name)
    {
        if (!Enum.IsDefined(name)) { throw new ArgumentOutOfRangeException(nameof(name)); }
        lock (gate)
        {
            events.Enqueue((time.GetUtcNow(), name));
            if (events.Count > Limit) { events.Dequeue(); }
        }
    }

    public void Record(FeedbackLog code)
    {
        if (!Enum.IsDefined(code)) { throw new ArgumentOutOfRangeException(nameof(code)); }
        lock (gate)
        {
            logs.Enqueue((time.GetUtcNow(), code));
            if (logs.Count > Limit) { logs.Dequeue(); }
        }
    }

    public FeedbackDraft CreateDraft(string? appVersion, string? hostVersion)
    {
        lock (gate)
        {
            return new FeedbackDraft(time.GetUtcNow(), appVersion, hostVersion, events.ToArray(), logs.ToArray());
        }
    }
}

public sealed class FeedbackDraft
{
    static readonly Regex Version = new(@"^[0-9]{1,6}(\.[0-9]{1,6}){0,3}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    readonly DateTimeOffset createdAt;
    readonly string appVersion;
    readonly string? hostVersion;
    readonly (DateTimeOffset At, FeedbackEvent Name)[] events;
    readonly (DateTimeOffset At, FeedbackLog Code)[] logs;
    readonly string bundleId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal FeedbackDraft(DateTimeOffset createdAt, string? appVersion, string? hostVersion,
        (DateTimeOffset At, FeedbackEvent Name)[] events, (DateTimeOffset At, FeedbackLog Code)[] logs)
    {
        this.createdAt = createdAt;
        this.appVersion = SafeVersion(appVersion) ?? "Unknown";
        this.hostVersion = SafeVersion(hostVersion);
        this.events = events;
        this.logs = logs;
    }

    static string? SafeVersion(string? value) => value is not null && Version.IsMatch(value) ? value : null;

    public string Preview(string note)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (note.Length > 1000) { throw new ArgumentOutOfRangeException(nameof(note)); }
        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("schema", "slipgates-feedback.v1");
        writer.WriteString("bundleId", bundleId);
        writer.WriteString("createdAt", createdAt.ToUniversalTime().ToString("O"));
        writer.WriteString("platform", "Windows");
        writer.WriteStartObject("versions");
        writer.WriteString("app", appVersion);
        writer.WriteNumber("protocol", 1);
        writer.WriteString("browser", "Not applicable");
        writer.WriteStartArray("hosts");
        if (hostVersion is not null) { writer.WriteStringValue(hostVersion); }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteStartArray("events");
        foreach (var (at, name) in events)
        {
            writer.WriteStartObject();
            writer.WriteString("at", at.ToUniversalTime().ToString("O"));
            writer.WriteString("name", EventName(name));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("logs");
        foreach (var (at, code) in logs)
        {
            writer.WriteStartObject();
            writer.WriteString("at", at.ToUniversalTime().ToString("O"));
            writer.WriteString("code", LogCode(code));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        if (!string.IsNullOrWhiteSpace(note)) { writer.WriteString("note", note); }
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    static string EventName(FeedbackEvent name) => name switch
    {
        FeedbackEvent.Connecting => "connection.connecting",
        FeedbackEvent.Connected => "connection.connected",
        FeedbackEvent.NotRunning => "connection.resting",
        FeedbackEvent.Refused => "connection.stopped",
        FeedbackEvent.SessionsUpsert => "host.sessions.upsert",
        FeedbackEvent.SessionsRemove => "host.sessions.remove",
        FeedbackEvent.SessionsSynced => "host.sessions.synced",
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    static string LogCode(FeedbackLog code) => code switch
    {
        FeedbackLog.HostConnectionError => "transport-error",
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };
}
