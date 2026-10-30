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
            try { lines = File.Exists(path) ? SharedLines(path) : []; }
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
            try { lines = File.Exists(path) ? SharedLines(path) : []; }
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

    // Read through a handle that shares writing and deleting: a hook appending its line, or
    // rotating the log, must never fail because this host was reading it (on Windows,
    // File.ReadAllLines would refuse them).
    internal static string[] SharedLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n');
    }

    static string FoldReason(string? reason)    {
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

// The latest run of one agent's hook that was not spooled, kept up to date by reading only what
// the log gained since the last look (it is read every pass), and the whole of it again when it
// has been rotated.
public sealed class HookLogWatch(string runtimeRoot, string agent, TimeProvider? time = null)
{
    readonly string _log = SpoolContract.LogPath(runtimeRoot);
    long _offset = -1;
    DateTime _rotated;
    // The first line of the log this watch is reading, to know it again once rotated to .1.
    string? _head;

    public DateTimeOffset? Last { get; private set; }

    // Since when the retained logs are known to hold every run, or null for since they began.
    // Only one previous log is kept, so runs rotated out of both while nobody watched (the host
    // was down through a megabyte or two of hook traffic) are gone, and an unspooled run among
    // them may have answered an approval. Anything asked before this is treated as if such a
    // run came right after it.
    public DateTimeOffset? HistoryFrom { get; private set; }

    // The clock moved back to now: times read from it before are ahead of every time to come, and
    // would hold the cut-off there (suppressing every new approval) until it caught up. What they
    // said is settled by the sweep that withdraws every approval; they count from now. (A rotation
    // re-read finds them again, which only withholds approvals until the clock catches up.)
    public void Forget(DateTimeOffset now)
    {
        if (Last > now) { Last = now; }
        if (HistoryFrom > now) { HistoryFrom = now; }
    }

    // False when the log could not be read: what happened since the last look is unknown.
    public bool Refresh()
    {
        try
        {
            var old = _log + ".1";
            var rotated = File.Exists(old) ? File.GetLastWriteTimeUtc(old) : default;
            var length = File.Exists(_log) ? new FileInfo(_log).Length : 0;
            if (_offset < 0 || rotated != _rotated || length < _offset)
            {
                var oldHead = File.Exists(old) ? FirstLine(old) : null;
                // Unbroken only if the log this watch was reading is the one now kept as .1 (or,
                // at the first look, if nothing was ever rotated out).
                var unbroken = _offset < 0 ? !File.Exists(old) : rotated != _rotated && _head is not null && oldHead == _head;
                if (!unbroken)
                {
                    var from = File.Exists(old)
                        ? Time(oldHead) ?? File.GetLastWriteTimeUtc(old)
                        : Time(File.Exists(_log) ? FirstLine(_log) : null) ?? (time ?? TimeProvider.System).GetUtcNow();
                    if (HistoryFrom is null || from > HistoryFrom) { HistoryFrom = from; }
                }
                if (File.Exists(old)) { Scan(old, 0); }
                _rotated = rotated;
                _offset = 0;
                _head = null;
            }
            if (File.Exists(_log))
            {
                _offset = Scan(_log, _offset);
                if (_head is null && _offset > 0) { _head = FirstLine(_log); }
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    static string? FirstLine(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadLine();
    }

    static DateTimeOffset? Time(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) { return null; }
        try
        {
            using var doc = JsonDocument.Parse(line);
            return Json.Str(doc.RootElement, "at") is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;
        }
        catch (JsonException) { return null; }
    }

    // Reads whole lines from the offset; returns where the next look starts.
    long Scan(string path, long offset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (offset > stream.Length) { offset = 0; }
        stream.Seek(offset, SeekOrigin.Begin);
        var bytes = new byte[stream.Length - offset];
        stream.ReadExactly(bytes);
        var end = Array.LastIndexOf(bytes, (byte)'\n');
        if (end < 0) { return offset; }
        foreach (var line in System.Text.Encoding.UTF8.GetString(bytes, 0, end).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) { continue; }
            try
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                if (Json.Str(e, "agent") != agent || Json.Str(e, "path") is null or "spool") { continue; }
                if (Json.Str(e, "at") is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) && (Last is null || at > Last)) { Last = at; }
            }
            catch (JsonException) { }
        }
        return offset + end + 1;
    }
}