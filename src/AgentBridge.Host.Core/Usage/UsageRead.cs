using System.Globalization;
using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Usage;

// What every collector shares: reading an agent's own state files without disturbing them,
// and building windows and records in the one shape clients draw.
internal static class UsageRead
{
    // Copilot writes its state as JSON behind a // banner, so comments are skipped. Opened
    // for reading only and shared every way, so the agent writing it never finds it locked.
    public static JsonDocument? Json(string? path, long maxBytes)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { return null; }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maxBytes) { return null; }
            return JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static JsonElement? Property(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v : null;

    public static string? String(JsonElement e, string name) =>
        Property(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    public static double? Number(JsonElement e, string name) => Property(e, name) switch
    {
        { ValueKind: JsonValueKind.Number } v => v.GetDouble(),
        { ValueKind: JsonValueKind.String } v when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    public static DateTimeOffset? Instant(JsonElement e, string name) =>
        String(e, name) is { Length: > 0 } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at.ToUniversalTime() : null;

    public static string Label(AgentKind agent) => agent switch
    {
        AgentKind.Copilot => "GitHub Copilot",
        AgentKind.Claude => "Claude Code",
        AgentKind.Codex => "Codex",
        _ => agent.ToString(),
    };

    // Percent is how much is gone, never negative, to a tenth.
    public static UsageWindow Window(string key, string label, double percentUsed, DateTimeOffset? resetsAt = null, DateTimeOffset? startsAt = null,
        double? used = null, double? limit = null, string? unit = null) => new()
        {
            Key = key,
            Label = label,
            Percent = Math.Round(Math.Max(0, percentUsed), 1),
            Used = used,
            Limit = limit,
            Unit = unit,
            ResetsAt = resetsAt,
            // A start is only worth sending with an end after it: the pace is the share of
            // the span between them that has gone.
            StartsAt = startsAt is { } s && resetsAt is { } r && r > s ? s : null,
        };

    public static AgentUsage Failure(AgentKind agent, string error, DateTimeOffset at, string? account = null, string? accountKey = null, string? plan = null) => new()
    {
        Agent = agent,
        Label = Label(agent),
        Account = account,
        AccountKey = accountKey,
        Plan = plan,
        Source = UsageSource.None,
        Error = error,
        MeasuredAt = at,
    };
}
