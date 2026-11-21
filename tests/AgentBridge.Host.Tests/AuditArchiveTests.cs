using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class AuditArchiveTests : IDisposable
{
    readonly string profile = Path.Combine(Path.GetTempPath(), "audit-archive-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
    readonly Guid run = Guid.NewGuid();
    string Audit => Path.Combine(profile, "audit");
    string Archive => Path.Combine(Audit, "archive");
    AgentAuditLog Writer(long max = AgentAuditLog.MaxBytes) => new(profile, clock, maxBytes: max);
    AgentAuditEntry Ordinary() => new("local", "fixture", "person:local", [], "lookup", "fixture", "ok");
    public void Dispose() { if (Directory.Exists(profile)) { Directory.Delete(profile, true); } }
    void Pair(bool launch)
    {
        var id = Guid.NewGuid();
        var entry = new AgentAuditEntry("local", id.ToString(), "person:local", [], launch ? "session.launch" : "session.stop",
            id.ToString(), "reserved", Gate: new GateAuditMetadata(1, id, run, 0, "intent", "local", "person-client", id, "none"));
        Assert.True(Writer().Append(entry));
        Assert.True(Writer().Append(entry with { Outcome = launch ? "started" : "ended", Gate = entry.Gate! with { Phase = "terminal" } }));
    }
    string MoveDay(bool launch, int day)
    {
        Directory.CreateDirectory(Archive);
        var name = $"{(launch ? "launches" : "agents")}-2026-11-{day:00}.jsonl";
        File.Move(Path.Combine(Audit, name), Path.Combine(Archive, name));
        return Path.Combine(Archive, name);
    }
    void ReadUnchanged(Action inspect)
    {
        var before = Directory.GetFiles(profile, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        inspect();
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(profile, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in before) { Assert.Equal(bytes, File.ReadAllBytes(path)); }
    }

    [Fact]
    public void Archived_days_replay_with_the_same_heads_and_restart_continues_the_sequence()
    {
        Pair(false); Pair(true);
        MoveDay(false, 1); MoveDay(true, 1);
        ReadUnchanged(() =>
        {
            Assert.True(Writer().Verify().Intact);
            Assert.True(Writer().Verify(launch: true).Intact);
            var report = GateAuditProjection.Read(profile, run);
            Assert.False(report.Invalidated);
            Assert.Empty(report.IncompleteReasons);
            Assert.Equal(2, report.Totals.Sum(t => t.Successes));
        });
        clock.Advance(TimeSpan.FromDays(1));
        Pair(false); Pair(true);
        Assert.Empty(GateAuditProjection.Read(profile, run).IncompleteReasons);
        Assert.Equal(Enumerable.Range(1, 8).Select(i => (long)i),
            GateAuditProjection.Load(profile).Rows.Select(r => r.Gate!.Sequence).Order());
    }

    [Theory]
    [InlineData(false, 1), InlineData(true, 1)]
    [InlineData(false, 2), InlineData(true, 2)]
    [InlineData(false, 3), InlineData(true, 3)]
    public void Deleted_archive_root_middle_or_tail_is_a_visible_coverage_break(bool launch, int missingDay)
    {
        for (var day = 1; day <= 3; day++) { Pair(launch); clock.Advance(TimeSpan.FromDays(1)); }
        for (var day = 1; day <= 3; day++) { MoveDay(launch, day); }
        File.Delete(Path.Combine(Archive, $"{(launch ? "launches" : "agents")}-2026-11-{missingDay:00}.jsonl"));
        ReadUnchanged(() =>
        {
            var report = GateAuditProjection.Read(profile, run);
            Assert.True(report.Invalidated);
            Assert.Contains("coverage-break", report.IncompleteReasons);
            Assert.Equal(2, report.Totals.Sum(t => t.Successes));
        });
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public void Archived_torn_tail_or_missing_head_is_incomplete_not_zero_observations(bool missingHead)
    {
        Pair(false);
        var file = MoveDay(false, 1);
        if (missingHead) { File.Delete(Path.Combine(Audit, "agents.head")); }
        else { File.WriteAllText(file, File.ReadAllText(file).TrimEnd('\n')); }
        ReadUnchanged(() =>
        {
            var report = GateAuditProjection.Read(profile, run);
            Assert.False(report.Invalidated);
            Assert.Contains("audit-incomplete", report.IncompleteReasons);
            Assert.Equal(1, report.Totals.Sum(t => t.Successes));
        });
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public void Retention_preserves_run_records_and_their_earlier_anchor_or_invalidates_without_losing_totals(bool archive)
    {
        Assert.True(Writer().Append(Ordinary()));
        clock.Advance(TimeSpan.FromDays(1));
        Pair(false);
        var anchor = archive ? MoveDay(false, 1) : Path.Combine(Audit, "agents-2026-11-01.jsonl");
        var limit = Directory.GetFiles(Audit, "*.jsonl", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.True(Writer(limit).Append(Ordinary()));
        ReadUnchanged(() =>
        {
            Assert.True(File.Exists(anchor));
            var report = GateAuditProjection.Read(profile, run);
            Assert.True(report.Invalidated);
            Assert.Contains("coverage-break", report.IncompleteReasons);
            Assert.Equal(1, report.Totals.Sum(t => t.Successes));
        });
    }

    [Fact]
    public void Incomplete_replay_cannot_hide_run_records_from_retention()
    {
        Assert.True(Writer().Append(Ordinary()));
        var file = Path.Combine(Audit, "agents-2026-11-01.jsonl");
        File.AppendAllText(file, "{\"gate\":");
        Pair(false);
        Assert.False(GateAuditProjection.Load(profile).Complete);
        var before = File.ReadAllBytes(file);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.True(Writer(before.Length).Append(Ordinary()));
        Assert.True(File.Exists(file));
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Contains("coverage-break", GateAuditProjection.Read(profile, run).IncompleteReasons);
    }
}
