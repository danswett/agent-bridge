using System.Security.Cryptography;
using System.Text.Json;

namespace AgentBridge.App.Core.Feedback;

// Recovery never falls through to the free-text feedback collector or its deny-list redaction.
static class RecoveryFeedback
{
    public static FeedbackGatherResult Build(PreviousRun run, DateTimeOffset createdAt)
    {
        var id = RandomNumberGenerator.GetBytes(16);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            w.WriteStartObject();
            w.WriteString("schema", FeedbackLimits.Schema);
            w.WriteString("bundleId", Convert.ToBase64String(id).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
            w.WriteString("createdAt", createdAt.ToUniversalTime());
            w.WriteStartObject("recovery");
            w.WriteString("Version", VersionOf(run.Version));
            w.WriteString("Stage", run.Crash is not null ? "crash" : run.Hang is not null ? "hang" : run.Unclean ? "unclean-exit" : "clean-exit");
            w.WriteString("StartedAt", run.StartedAt?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            w.WriteString("HangAt", run.Hang?.At.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            if (run.Hang is { SecondsStalled: var seconds } && double.IsFinite(seconds) && seconds >= 0) { w.WriteNumber("SecondsStalled", seconds); }
            else { w.WriteNull("SecondsStalled"); }
            w.WriteString("ExceptionType", ExceptionType(run.Crash));
            w.WriteNull("HResult"); // Legacy markers contain no typed HResult; never infer one.
            w.WriteEndObject();
            w.WriteEndObject();
        }
        if (buffer.Length > FeedbackLimits.MaxBundleBytes) { return new FeedbackGatherResult.Refused(FeedbackRefusalReason.BundleTooLarge); }
        return new FeedbackGatherResult.Ready(new FeedbackBundle(buffer.ToArray(), id, createdAt,
            [new("recovery", "Previous app run", "Structured metadata only. Diagnostic text, names, session identifiers and notes are excluded.", buffer.Length, false)]));
    }

    public static bool IsAllowed(JsonElement root)
    {
        if (!Fields(root, "schema", "bundleId", "createdAt", "recovery")
            || root.GetProperty("schema").ValueKind != JsonValueKind.String
            || root.GetProperty("schema").GetString() != FeedbackLimits.Schema
            || !Date(root.GetProperty("createdAt"), false)) { return false; }
        var r = root.GetProperty("recovery");
        if (!Fields(r, "Version", "Stage", "StartedAt", "HangAt", "SecondsStalled", "ExceptionType", "HResult")) { return false; }
        var duration = r.GetProperty("SecondsStalled");
        var hresult = r.GetProperty("HResult");
        return Text(r.GetProperty("Version"), VersionOf) && Text(r.GetProperty("ExceptionType"), ExceptionType)
            && r.GetProperty("Stage").ValueKind == JsonValueKind.String
            && r.GetProperty("Stage").GetString() is "crash" or "hang" or "unclean-exit" or "clean-exit"
            && Date(r.GetProperty("StartedAt"), true) && Date(r.GetProperty("HangAt"), true)
            && (duration.ValueKind == JsonValueKind.Null || duration.ValueKind == JsonValueKind.Number
                && duration.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds >= 0)
            && (hresult.ValueKind == JsonValueKind.Null || hresult.ValueKind == JsonValueKind.Number && hresult.TryGetInt32(out _));
    }

    static bool Fields(JsonElement e, params string[] names)
    {
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        return e.ValueKind == JsonValueKind.Object && e.EnumerateObject().All(p => remaining.Remove(p.Name)) && remaining.Count == 0;
    }
    static bool Date(JsonElement e, bool nullable) => nullable && e.ValueKind == JsonValueKind.Null
        || e.ValueKind == JsonValueKind.String && e.TryGetDateTimeOffset(out _);
    static bool Text(JsonElement e, Func<string?, string?> normalize) => e.ValueKind == JsonValueKind.Null
        || e.ValueKind == JsonValueKind.String && normalize(e.GetString()) is { } known && known == e.GetString();
    static string? VersionOf(string? value)
    {
        if (value is null || value.Length > 256) { return null; }
        var end = value.IndexOfAny(['-', '+']);
        var numeric = end < 0 ? value : value[..end];
        if (numeric.StartsWith('v')) { numeric = numeric[1..]; }
        return Version.TryParse(numeric, out var version) ? version.ToString() : null;
    }
    static string? ExceptionType(string? crash)
    {
        if (crash is null) { return null; }
        var end = crash.IndexOf('\n');
        var length = end < 0 ? crash.Length : end;
        if (length > 128) { return null; }
        var type = crash[..length].TrimEnd('\r');
        return type switch
        {
            "System.Exception" or "System.InvalidOperationException" or "System.NullReferenceException"
                or "System.ArgumentException" or "System.ArgumentNullException" or "System.ArgumentOutOfRangeException"
                or "System.ObjectDisposedException" or "System.TimeoutException" or "System.OutOfMemoryException"
                or "System.StackOverflowException" or "System.AccessViolationException" or "System.IO.IOException"
                or "System.OperationCanceledException" or "System.Threading.Tasks.TaskCanceledException"
                or "System.Runtime.InteropServices.COMException" => type,
            _ => null,
        };
    }
}
