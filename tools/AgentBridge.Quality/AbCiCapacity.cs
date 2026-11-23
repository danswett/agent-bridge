using System.Globalization;
using System.Text.Json;

namespace AgentBridge.Quality;

/// <summary>
/// The capacity check before <c>AB_CI_SELF_HOSTED</c> is set to <c>true</c>, and while it is (quality/README.md, "CI:
/// speed, runners and what gates main", capacity first; #2454): the ab-ci pool has enough runners online and its queue
/// moves, no more than <c>--max-queued</c> ab-ci jobs waiting and none waiting longer than <c>--max-wait</c>. It reads
/// what quality/ab-ci-capacity.ps1 fetched: the repository's runners (GET /repos/{repo}/actions/runners, which needs
/// administration read) and the jobs of its queued and in-progress runs. Input it can't read throws: unread is never a pass.
/// </summary>
static class AbCiCapacity
{
    public const string Label = "ab-ci";

    public const string Usage = """
          ab-ci-capacity --runners FILE --jobs DIR [--max-queued N] [--max-wait SECONDS] [--min-online N] [--now ISO-8601]
              The ab-ci pool's capacity check (quality/ab-ci-capacity.ps1 fetches its input): FILE is the repository's
              runners (GET actions/runners), DIR holds one jobs JSON per queued or in-progress run. Exits 1 when fewer
              than --min-online (1) ab-ci runners are online, more than --max-queued (0) ab-ci jobs are queued, or one
              has waited longer than --max-wait (60) seconds; set AB_CI_SELF_HOSTED to true only while it exits 0.
        """;

    public sealed record Result(bool Ok, int Online, int Busy, int Idle, int Queued, TimeSpan OldestWait, IReadOnlyList<string> Lines);

    public static Result Evaluate(string runnersJson, IEnumerable<string> jobsJson, DateTimeOffset now, int maxQueued, TimeSpan maxWait, int minOnline)
    {
        using var runnersDoc = JsonDocument.Parse(runnersJson);
        if (!runnersDoc.RootElement.TryGetProperty("runners", out var runners) || runners.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("the runners file has no runners array (GET /repos/{repo}/actions/runners)");
        }
        var pool = runners.EnumerateArray().Where(r => Labels(r, "labels", l => l.GetProperty("name").GetString())).ToList();
        var online = pool.Where(r => r.GetProperty("status").GetString() == "online").ToList();
        var busy = online.Count(r => r.GetProperty("busy").GetBoolean());
        var waits = new List<TimeSpan>();
        foreach (var text in jobsJson)
        {
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array) { throw new FormatException("a jobs file has no jobs array"); }
            foreach (var job in jobs.EnumerateArray().Where(j => j.GetProperty("status").GetString() == "queued" && Labels(j, "labels", l => l.GetString())))
            {
                waits.Add(now - DateTimeOffset.Parse(job.GetProperty("created_at").GetString()!, CultureInfo.InvariantCulture));
            }
        }
        var oldest = waits.Count == 0 ? TimeSpan.Zero : waits.Max();
        var lines = new List<string> { $"ab-ci pool: {online.Count} online ({busy} busy, {online.Count - busy} idle), {pool.Count - online.Count} offline; {waits.Count} ab-ci job(s) queued, oldest {(int)oldest.TotalSeconds} s" };
        if (online.Count < minOnline) { lines.Add($"FAIL: {online.Count} ab-ci runner(s) online, fewer than {minOnline}"); }
        if (waits.Count > maxQueued) { lines.Add($"FAIL: {waits.Count} ab-ci job(s) queued, more than {maxQueued}: add runners or set AB_CI_SELF_HOSTED back"); }
        if (oldest > maxWait) { lines.Add($"FAIL: an ab-ci job has waited {(int)oldest.TotalSeconds} s, longer than {(int)maxWait.TotalSeconds} s"); }
        var ok = lines.Count == 1;
        lines.Add(ok ? "OK: the pool keeps up" : "Not OK: leave AB_CI_SELF_HOSTED unset, or set it back");
        return new Result(ok, online.Count, busy, online.Count - busy, waits.Count, oldest, lines);
    }

    static bool Labels(JsonElement item, string property, Func<JsonElement, string?> name)
        => item.TryGetProperty(property, out var labels) && labels.ValueKind == JsonValueKind.Array
            && labels.EnumerateArray().Any(l => string.Equals(name(l), Label, StringComparison.OrdinalIgnoreCase));

    public static int Run(Dictionary<string, string> options, TextWriter output)
    {
        var runners = options.GetValueOrDefault("runners") ?? throw new ArgumentException("ab-ci-capacity needs --runners FILE.");
        var jobs = options.GetValueOrDefault("jobs") ?? throw new ArgumentException("ab-ci-capacity needs --jobs DIR.");
        var now = options.TryGetValue("now", out var n) ? DateTimeOffset.Parse(n, CultureInfo.InvariantCulture) : DateTimeOffset.UtcNow;
        int Number(string key, int fallback) => options.TryGetValue(key, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;
        var result = Evaluate(File.ReadAllText(runners), Directory.EnumerateFiles(jobs, "*.json").Order(StringComparer.Ordinal).Select(File.ReadAllText),
            now, Number("max-queued", 0), TimeSpan.FromSeconds(Number("max-wait", 60)), Number("min-online", 1));
        foreach (var line in result.Lines) { output.WriteLine(line); }
        return result.Ok ? 0 : 1;
    }
}
