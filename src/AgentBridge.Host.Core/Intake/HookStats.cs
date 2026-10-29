using System.Globalization;
using System.Text.Json;
using AgentBridge.Hooks;

namespace AgentBridge.Host.Core.Intake;

// How the hooks have been doing, from the line each run logs (agent-bridge-hook.log and its
// rotated .1, in the runtime folder; both hooks write it): as Get-BridgeHookStats. A run's path
// is spool (handed to the host: the fast path), fallback (agent-ha-bridge's hook ran its
// PowerShell instead) or reply (neither: the fixed reply alone). Not spooled / total is the
// fallback rate; reasons fold numbers together ("daemon heartbeat Ns old").
public sealed record HookStats(
    int Total, int Spooled, int Fallback, int ReplyOnly,
    IReadOnlyDictionary<string, int> ByReason,
    double? MedianMs, double? P95Ms, double? SpoolMedianMs)
{
    public int NotSpooled => Total - Spooled;

    public static HookStats Read(string runtimeRoot, DateTimeOffset now, TimeSpan window)
    {
        var since = now - window;
        var runs = new List<(string Path, string? Reason, double Ms)>();
        var log = SpoolContract.LogPath(runtimeRoot);
        foreach (var path in new[] { log + ".1", log })
        {
            IEnumerable<string> lines;
            try { lines = File.Exists(path) ? File.ReadAllLines(path) : []; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) { continue; }
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var e = doc.RootElement;
                    if (Json.Str(e, "at") is not { } text || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) || at < since || at > now) { continue; }
                    var ms = Json.Get(e, "ms") is { ValueKind: JsonValueKind.Number } m ? m.GetDouble() : 0;
                    runs.Add((Json.Str(e, "path") ?? "", Json.Str(e, "reason"), ms));
                }
                catch (JsonException) { }
            }
        }
        var byReason = runs.Where(r => r.Path != "spool")
            .GroupBy(r => FoldReason(r.Reason))
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());
        return new HookStats(
            runs.Count,
            runs.Count(r => r.Path == "spool"),
            runs.Count(r => r.Path == "fallback"),
            runs.Count(r => r.Path == "reply"),
            byReason,
            Percentile(runs.Select(r => r.Ms), 0.5),
            Percentile(runs.Select(r => r.Ms), 0.95),
            Percentile(runs.Where(r => r.Path == "spool").Select(r => r.Ms), 0.5));
    }

    // When a hook for this agent last ran without spooling (path fallback or reply), from the log
    // and its rotated file; null when none is recorded.
    public static DateTimeOffset? LastUnspooled(string runtimeRoot, string agent)
    {
        DateTimeOffset? last = null;
        var log = SpoolContract.LogPath(runtimeRoot);
        foreach (var path in new[] { log + ".1", log })
        {
            IEnumerable<string> lines;
            try { lines = File.Exists(path) ? File.ReadAllLines(path) : []; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) { continue; }
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var e = doc.RootElement;
                    if (Json.Str(e, "agent") != agent || Json.Str(e, "path") is null or "spool") { continue; }
                    if (Json.Str(e, "at") is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) && (last is null || at > last)) { last = at; }
                }
                catch (JsonException) { }
            }
        }
        return last;
    }

    static string FoldReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) { return "(none)"; }
        var head = reason.Split(':')[0];
        return System.Text.RegularExpressions.Regex.Replace(head, @"\d+", "N").Trim();
    }

    static double? Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? null : sorted[(int)Math.Min(sorted.Length - 1, Math.Floor(p * sorted.Length))];
    }

    // One line for `agent-bridge-host hooks status`, as Format-BridgeHookStats.
    public string Describe(TimeSpan window)
    {
        var hours = window.TotalHours.ToString("0.#", CultureInfo.InvariantCulture);
        if (Total == 0) { return $"no runs in the last {hours} h"; }
        var text = $"{Total} runs in the last {hours} h, {NotSpooled} not spooled ({Math.Round(100.0 * NotSpooled / Total, 1).ToString(CultureInfo.InvariantCulture)}%)";
        if (NotSpooled > 0) { text += ": " + string.Join(", ", ByReason.Select(r => $"{r.Key} x{r.Value}")); }
        text += $"; median {MedianMs?.ToString(CultureInfo.InvariantCulture)} ms, p95 {P95Ms?.ToString(CultureInfo.InvariantCulture)} ms";
        if (SpoolMedianMs is { } s) { text += $" (spooled {s.ToString(CultureInfo.InvariantCulture)} ms)"; }
        return text;
    }
}
