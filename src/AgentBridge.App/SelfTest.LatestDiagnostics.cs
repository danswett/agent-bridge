using System.Diagnostics;

namespace AgentBridge.App;

sealed record LatestTimingRecord(string Kind, string Section, string? Name, bool? Passed,
    double Seconds, double SectionSeconds, double SelfTestSeconds);

sealed class LatestTiming
{
    readonly Action<string> write;
    readonly Func<TimeSpan> elapsed;
    readonly List<LatestTimingRecord> records = [];
    string section = "";
    TimeSpan sectionAt, checkedAt;
    public IReadOnlyList<LatestTimingRecord> Records => records;

    public LatestTiming(Action<string> write, Func<TimeSpan>? elapsed = null)
    {
        this.write = write;
        var clock = Stopwatch.StartNew();
        this.elapsed = elapsed ?? (() => clock.Elapsed);
    }

    public void Boundary(string next)
    {
        var now = elapsed();
        if (section.Length > 0) { Add("end", null, null, now, sectionAt); }
        section = next;
        sectionAt = checkedAt = now;
        if (section.Length > 0) { Add("begin", null, null, now, now); }
    }

    public void Check(bool passed, string name)
    {
        if (section.Length == 0) { return; }
        var now = elapsed();
        Add("check", name, passed, now, checkedAt);
        checkedAt = now;
    }

    void Add(string kind, string? name, bool? passed, TimeSpan now, TimeSpan previous)
    {
        var entry = new LatestTimingRecord(kind, section, name, passed,
            (now - previous).TotalSeconds, (now - sectionAt).TotalSeconds, now.TotalSeconds);
        records.Add(entry);
        write($"latest {kind}: section={section}, seconds={entry.Seconds:0.000}, sectionSeconds={entry.SectionSeconds:0.000}, selfTestSeconds={entry.SelfTestSeconds:0.000}, passed={passed}, name={name}");
    }
}
