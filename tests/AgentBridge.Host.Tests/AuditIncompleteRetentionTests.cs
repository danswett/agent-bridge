using System.Text.Json.Nodes;
using AgentBridge.Host;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class AuditIncompleteRetentionTests : LaunchFlowFixture
{
    private protected override bool UsesRelay => false;

    [Fact]
    public async Task Retention_after_a_flushed_tail_crash_preserves_uncertainty_and_launch_refusal()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        var profile = Path.Combine(f.Root, "incomplete-retention", "default");
        var folder = Path.Combine(profile, "audit");
        var head = Path.Combine(folder, "agents.head");
        AgentAuditLog Writer() => new(profile, clock, logged.Enqueue);
        var run = Guid.NewGuid();
        var id = Guid.NewGuid();
        var entry = new AgentAuditEntry("local", id.ToString(), "person:local", [], "session.stop", id.ToString(), "reserved",
            Gate: new GateAuditMetadata(1, id, run, 0, "intent", "local", "person-client", id, "none"));
        Assert.True(Writer().Append(entry));
        var checkpoint = File.ReadAllBytes(head);
        var original = JsonNode.Parse(File.ReadAllText(head))!["root"]!.GetValue<string>();
        Assert.True(Writer().Append(entry with { Outcome = "ended", Gate = entry.Gate! with { Phase = "terminal" } }));
        File.WriteAllBytes(head, checkpoint);
        Assert.Contains("audit-incomplete", GateAuditProjection.Read(profile, run).IncompleteReasons);
        for (var day = 0; day <= AgentAuditLog.KeepDays; day++)
        {
            clock.Advance(TimeSpan.FromDays(1));
            Assert.True(Writer().Append(entry with { Gate = null }));
        }
        Assert.True(File.Exists(Path.Combine(folder, original)));
        Assert.Equal(AgentAuditLog.KeepDays + 2, Directory.GetFiles(folder, "agents-*.jsonl").Length);
        Assert.Equal("audit-incomplete", JsonNode.Parse(File.ReadAllText(head))!["fault"]!.GetValue<string>());
        var report = GateAuditProjection.Read(profile, run);
        Assert.False(report.Invalidated);
        Assert.Contains("audit-incomplete", report.IncompleteReasons);
        Assert.False(Writer().Verify().Intact);
        var launch = await GateLaunchAudit.LaunchAsync(Launcher(Config()),
            new LaunchRequest { Request = Guid.NewGuid().ToString(), Agent = "copilot", Workspace = "App", Prompt = "fixture" },
            "local", Writer(), new GateLaunchIngress("local", "person-client", "person:local", null));
        Assert.Equal(LaunchOutcome.Refused, launch.Outcome);
    }
}
