using System.Globalization;
using System.Text;

namespace AgentBridge.Host.Shadow;

// The week at a glance: which days were clean, and how many mismatches of each kind there were.
// An episode still going at midnight is in both days' files with the same start; it is one
// mismatch here.
public sealed record ShadowSummary(
    string From,
    string To,
    string? TimeZone,
    int GraceSeconds,
    IReadOnlyList<ShadowSummaryDay> Days,
    IReadOnlyList<ShadowSummaryKind> Kinds,
    IReadOnlyList<ShadowSummaryKind> Transient,
    IReadOnlyList<string> NotCompared)
{
    public int CleanDays => Days.Count(d => d.Verdict == "clean");
    public int Mismatches => Kinds.Sum(k => k.Count);

    public static ShadowSummary Build(string folder, DateOnly today, int days)
    {
        var list = new List<ShadowSummaryDay>();
        var episodes = new Dictionary<(string, string?, DateTimeOffset), ShadowEpisode>();
        var overflow = new Dictionary<string, int>(StringComparer.Ordinal);
        var transient = new Dictionary<string, (int Count, long Longest)>(StringComparer.Ordinal);
        string? zone = null;
        var grace = 0;
        for (var i = days - 1; i >= 0; i--)
        {
            var date = today.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var day = ShadowLedger.Read(folder, date);
            if (day is null) { list.Add(new(date, 0, 0, 0, 0, "no data")); continue; }
            zone ??= day.TimeZone;
            grace = Math.Max(grace, day.GraceSeconds);
            foreach (var m in day.Mismatches)
            {
                var key = (m.Kind, m.Session, m.First);
                if (!episodes.TryGetValue(key, out var seen) || seen.Seconds < m.Seconds) { episodes[key] = m; }
            }
            foreach (var (kind, n) in day.Overflow) { overflow[kind] = overflow.GetValueOrDefault(kind) + n; }
            foreach (var (kind, t) in day.Transient)
            {
                var was = transient.GetValueOrDefault(kind);
                transient[kind] = (was.Count + t.Count, Math.Max(was.Longest, t.LongestSeconds));
            }
            var hours = Math.Round(day.Compared * (double)day.IntervalSeconds / 3600, 3);
            list.Add(new(date, day.Samples, day.Compared, hours, day.Mismatches.Count + day.Overflow.Values.Sum(), day.Verdict));
        }
        var kinds = episodes.Values.GroupBy(e => e.Kind)
            .Select(g => new ShadowSummaryKind(g.Key, g.Count() + overflow.GetValueOrDefault(g.Key), g.Select(e => e.Session).Where(s => s is not null).Distinct().Count(), g.Max(e => e.Seconds)))
            .Concat(overflow.Where(o => !episodes.Values.Any(e => e.Kind == o.Key)).Select(o => new ShadowSummaryKind(o.Key, o.Value, 0, 0)))
            .OrderByDescending(k => k.Count).ThenBy(k => k.Kind, StringComparer.Ordinal).ToList();
        var skew = transient.Select(t => new ShadowSummaryKind(t.Key, t.Value.Count, 0, t.Value.Longest))
            .OrderByDescending(k => k.Count).ThenBy(k => k.Kind, StringComparer.Ordinal).ToList();
        return new(list[0].Date, list[^1].Date, zone, grace, list, kinds, skew, ShadowKinds.NotCompared);
    }

    public string Format()
    {
        var b = new StringBuilder();
        b.AppendLine($"Shadow comparison with agent-ha-bridge, {From} to {To}{(TimeZone is null ? "" : $" ({TimeZone})")}");
        b.AppendLine();
        b.AppendLine("date        compared  mismatches  verdict");
        foreach (var d in Days)
        {
            var hours = d.Compared == 0 ? "-" : d.ComparedHours >= 0.1 ? $"{d.ComparedHours.ToString("0.0", CultureInfo.InvariantCulture)} h" : $"{Math.Max(1, (int)Math.Round(d.ComparedHours * 60))} min";
            b.AppendLine($"{d.Date}  {hours,8}  {d.Mismatches,10}  {d.Verdict}");
        }
        b.AppendLine();
        if (Kinds.Count == 0) { b.AppendLine("No mismatches."); }
        else
        {
            b.AppendLine($"{Mismatches} mismatch{(Mismatches == 1 ? "" : "es")} by kind (each lasted at least {GraceSeconds} s):");
            foreach (var k in Kinds)
            {
                var sessions = k.Sessions == 0 ? "" : $" in {k.Sessions} session{(k.Sessions == 1 ? "" : "s")}";
                b.AppendLine($"  {k.Kind,-26} {k.Count,5}{sessions}, longest {Duration(k.LongestSeconds)}");
            }
        }
        if (Transient.Count > 0)
        {
            b.AppendLine();
            b.AppendLine($"Timing skew (disagreements shorter than {GraceSeconds} s, not mismatches):");
            foreach (var k in Transient) { b.AppendLine($"  {k.Kind,-26} {k.Count,5}, longest {Duration(k.LongestSeconds)}"); }
        }
        b.AppendLine();
        b.AppendLine("Not compared: " + string.Join("; ", NotCompared) + ".");
        b.AppendLine();
        b.AppendLine($"Clean days: {CleanDays} of {Days.Count}.");
        return b.ToString();
    }

    static string Duration(long seconds) =>
        seconds < 60 ? $"{seconds} s" : seconds < 3600 ? $"{seconds / 60} min {seconds % 60} s" : $"{seconds / 3600} h {seconds % 3600 / 60} min";
}

public sealed record ShadowSummaryDay(string Date, int Samples, int Compared, double ComparedHours, int Mismatches, string Verdict);

public sealed record ShadowSummaryKind(string Kind, int Count, int Sessions, long LongestSeconds);
