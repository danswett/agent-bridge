using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentBridge.Host;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class AuditCheckpointRecoveryTests : IDisposable
{
    readonly string profile = Path.Combine(Path.GetTempPath(), "checkpoint-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
    readonly Guid run = Guid.NewGuid(), operation = Guid.NewGuid();
    string Audit => Path.Combine(profile, "audit");
    string Head => Path.Combine(Audit, "agents.head");
    AgentAuditLog Writer() => new(profile, clock);
    AgentAuditEntry Entry() => new("local", operation.ToString(), "person:local", [], "session.stop", operation.ToString(), "reserved",
        Gate: new GateAuditMetadata(1, operation, run, 0, "intent", "local", "person-client", operation, "none"));
    public void Dispose() { if (Directory.Exists(profile)) { Directory.Delete(profile, true); } }

    [Theory]
    [InlineData(false, false), InlineData(true, false)]
    [InlineData(false, true), InlineData(true, true)]
    public void A_pending_retirement_authorizes_only_its_exact_old_file(bool removed, bool loseSuccessor)
    {
        Assert.True(Writer().Append(Entry()));
        clock.Advance(TimeSpan.FromDays(1));
        Assert.True(Writer().Append(Entry() with { Gate = null }));
        var files = Directory.GetFiles(Audit, "agents-*.jsonl").Order(StringComparer.Ordinal).ToArray();
        var head = JsonNode.Parse(File.ReadAllText(Head))!;
        head["root"] = Path.GetFileName(files[1]);
        head["rootSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadLines(files[1]).First()))).ToLowerInvariant();
        head["retiringRoot"] = Path.GetFileName(files[0]);
        head["retainedThrough"] = 1;
        File.WriteAllText(Head, head.ToJsonString());
        if (removed) { File.Delete(files[0]); }
        if (loseSuccessor) { File.Delete(files[1]); }
        var before = File.ReadAllBytes(Head);
        Assert.Equal(!loseSuccessor, Writer().Verify().Intact);
        Assert.Equal(before, File.ReadAllBytes(Head));
        if (loseSuccessor)
        {
            Assert.True(GateAuditProjection.Read(profile, run).Invalidated);
            return;
        }
        Assert.True(Writer().Append(Entry()));
        Assert.True(Writer().Verify().Intact);
        Assert.Equal(2, GateAuditProjection.Load(profile).Rows.Max(row => row.Gate?.Sequence ?? 0));
    }

    [Fact]
    public void Healthy_upgrade_adoption_remains_incomplete_for_run_proof_after_later_appends()
    {
        Assert.True(Writer().Append(Entry()));
        File.Delete(Head);
        Assert.True(Writer().Bootstrap());
        Assert.True(Writer().Verify().Intact);
        var first = GateAuditProjection.Read(profile, run);
        Assert.False(first.Invalidated);
        Assert.Contains("audit-incomplete", first.IncompleteReasons);
        Assert.True(Writer().Append(Entry()));
        Assert.True(Writer().Bootstrap());
        Assert.Contains("audit-incomplete", GateAuditProjection.Read(profile, run).IncompleteReasons);
    }
}
