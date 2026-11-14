using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class AuditContinuityTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "audit-continuity-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
    readonly Guid run = Guid.NewGuid();
    string Profile => Path.Combine(root, "default");
    string Audit => Path.Combine(Profile, AgentAuditLog.FolderName);
    public void Dispose() { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    AgentAuditLog Writer() => new(Profile, clock);
    AgentAuditEntry Entry(bool launch, Guid operation, string phase, string outcome) =>
        new("local", operation.ToString(), "agent:fixture/session", [], launch ? "session.launch" : "session.stop",
            operation.ToString(), outcome, Gate: new GateAuditMetadata(1, operation, run, 0, phase, "local", "agent", operation, "none"));

    void Pair(bool launch)
    {
        var operation = Guid.NewGuid();
        Assert.True(Writer().Append(Entry(launch, operation, "intent", "reserved")));
        Assert.True(Writer().Append(Entry(launch, operation, "terminal", launch ? "started" : "ended")));
    }

    Dictionary<string, byte[]> Snapshot() =>
        Directory.GetFiles(Profile, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllBytes);

    void Unchanged(Dictionary<string, byte[]> before)
    {
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(Profile, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in before) { Assert.Equal(bytes, File.ReadAllBytes(path)); }
    }

    [Fact]
    public void Complete_replay_after_restart_preserves_both_streams_and_reads_do_not_rewrite_their_anchors()
    {
        Pair(false);
        Pair(true);
        clock.Advance(TimeSpan.FromDays(1));
        Pair(false);
        Pair(true);
        var before = Snapshot();
        for (var restart = 0; restart < 2; restart++)
        {
            Assert.Equal((true, false), Writer().Verify());
            Assert.Equal((true, false), Writer().Verify(launch: true));
            var output = new StringWriter();
            var error = new StringWriter();
            Assert.Equal(0, AuditCommands.Run(["audit"], root, output, error));
            Assert.Empty(error.ToString());
            var report = GateAuditProjection.Read(Profile, run);
            Assert.False(report.Invalidated);
            Assert.Empty(report.IncompleteReasons);
            Assert.Equal(2, report.Totals.Count);
            Assert.All(report.Totals, total => Assert.Equal(2, total.Successes));
        }
        Unchanged(before);
    }

    [Theory]
    [InlineData(false, "tail-record"), InlineData(true, "tail-record")]
    [InlineData(false, "tail-day"), InlineData(true, "tail-day")]
    [InlineData(false, "root-day"), InlineData(true, "root-day")]
    [InlineData(false, "torn"), InlineData(true, "torn")]
    public void Restart_and_owner_reads_refuse_removed_or_torn_evidence_without_reanchoring_a_suffix(bool launch, string fault)
    {
        Pair(false);
        Pair(true);
        clock.Advance(TimeSpan.FromDays(1));
        Pair(false);
        Pair(true);
        var day = fault == "root-day" ? "2026-11-14" : "2026-11-15";
        var file = Path.Combine(Audit, $"{(launch ? "launches" : "agents")}-{day}.jsonl");
        if (fault == "tail-record") { File.WriteAllText(file, string.Join("\n", File.ReadAllLines(file)[..^1]) + "\n"); }
        else if (fault == "torn") { File.WriteAllText(file, File.ReadAllText(file).TrimEnd('\n')); }
        else { File.Delete(file); }
        var before = Snapshot();

        Assert.False(Writer().Verify(launch).Intact);
        var error = new StringWriter();
        var code = AuditCommands.Run(["audit"], root, TextWriter.Null, error);
        Assert.True(code != 0 || error.ToString().Length > 0);
        Assert.DoesNotContain(Profile, error.ToString());
        var report = GateAuditProjection.Read(Profile, run);
        Assert.True(report.Invalidated || report.IncompleteReasons.Count > 0);
        Assert.False(Writer().Append(Entry(launch, Guid.NewGuid(), "intent", "reserved")));
        Unchanged(before);
    }
}
