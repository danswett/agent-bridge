using System.Globalization;
using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

// Why the last restart happened (#1964). A host that hands over for a planned restart writes this once, at the point it
// commits to leaving; the next host reads it and removes it. No marker, an old or damaged one, or a cause this build does not
// know is no cause at all (a crash, a reboot and a stop by hand leave none): diagnostics says "not recorded", never a guess.
public static class RestartMarker
{
    public const string FileName = "last-restart.json";
    const int MaxBytes = 1024;
    static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    // Best effort: a marker that cannot be written must never stop the restart it describes.
    public static void Write(string folder, DiagnosticsRestartCause cause, DateTimeOffset now)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, FileName);
            var temp = $"{path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new { cause = cause.ToString().ToLowerInvariant(), at = now.ToString("O", CultureInfo.InvariantCulture) }));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static DiagnosticsRestartCause? Consume(string folder, DateTimeOffset now)
    {
        var path = Path.Combine(folder, FileName);
        try
        {
            if (!File.Exists(path)) { return null; }
            var info = new FileInfo(path);
            string? text = info.Length <= MaxBytes ? File.ReadAllText(path) : null;
            File.Delete(path);
            return text is null ? null : Read(text, now);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static DiagnosticsRestartCause? Read(string text, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("cause", out var cause) || cause.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("at", out var at) || at.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)) { return null; }
            var age = now - when;
            if (age < TimeSpan.Zero || age > MaxAge) { return null; }
            return cause.GetString() switch
            {
                "app" => DiagnosticsRestartCause.App,
                "device" => DiagnosticsRestartCause.Device,
                "watchdog" => DiagnosticsRestartCause.Watchdog,
                _ => null,
            };
        }
        catch (JsonException) { return null; }
    }
}
