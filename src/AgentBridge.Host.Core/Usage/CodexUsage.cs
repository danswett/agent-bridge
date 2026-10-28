using System.Text;
using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Usage;

// Codex's two rate-limit windows, read from its own transcripts. There is no endpoint to ask:
// Codex learns its limits from the headers on its replies and records them only in the
// token_count events it writes as it goes. So this is the newest figure Codex itself saw,
// and it ages between runs, which is why the reading carries when it was measured.
public sealed class CodexUsageCollector(UsageOptions options) : IUsageCollector
{
    public AgentKind Agent => AgentKind.Codex;

    public Task<AgentUsage?> CollectAsync(CancellationToken cancellationToken = default) => Task.FromResult(Read(cancellationToken));

    AgentUsage? Read(CancellationToken cancellationToken)
    {
        var root = Path.Combine(options.CodexHome, "sessions");
        if (!Directory.Exists(root)) { return null; }
        // Newest first, and only a handful: a session that ended without being told its limits
        // has no such event, so the search walks back a little, but not through years.
        List<FileInfo> files;
        try
        {
            files = [.. new DirectoryInfo(root).EnumerateFiles("*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(options.CodexFiles)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Length > options.MaxStateBytes) { continue; }
            string[] lines;
            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                lines = reader.ReadToEnd().Split('\n');
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                if (!lines[i].Contains("\"rate_limits\"", StringComparison.Ordinal)) { continue; }
                if (Parse(lines[i], file.LastWriteTimeUtc) is { } usage) { return usage; }
            }
        }
        return null;
    }

    // Without a timestamp on the event, the file's last write is the latest it can have been.
    internal static AgentUsage? Parse(string line, DateTimeOffset written)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(line); }
        catch (JsonException) { return null; }
        using (document)
        {
            var root = document.RootElement;
            if (UsageRead.Property(root, "payload") is not { } payload || UsageRead.Property(payload, "rate_limits") is not { ValueKind: JsonValueKind.Object } limits) { return null; }
            var windows = new List<UsageWindow>();
            foreach (var (name, fallback) in new[] { ("primary", "5h limit"), ("secondary", "Weekly") })
            {
                if (UsageRead.Property(limits, name) is not { } limit || UsageRead.Number(limit, "used_percent") is not { } percent) { continue; }
                var label = UsageRead.Number(limit, "window_minutes") is { } minutes and > 0
                    ? minutes >= 10080 ? "Weekly" : minutes >= 1440 ? $"{Math.Round(minutes / 1440)}d limit" : $"{Math.Round(minutes / 60)}h limit"
                    : fallback;
                DateTimeOffset? resets = UsageRead.Number(limit, "resets_at") is { } seconds and > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)seconds) : null;
                // No start: Codex says when a window resets but not when it began, and guessing it
                // from the length would quietly turn a rolling window into a fixed one.
                windows.Add(UsageRead.Window(name, label, percent, resets));
            }
            if (windows.Count == 0) { return null; }
            return new AgentUsage
            {
                Agent = AgentKind.Codex,
                Label = UsageRead.Label(AgentKind.Codex),
                Plan = UsageRead.String(limits, "plan_type"),
                Source = UsageSource.Transcript,
                Windows = windows,
                MeasuredAt = UsageRead.Instant(root, "timestamp") ?? written,
            };
        }
    }
}
