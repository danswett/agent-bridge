using Xunit;

namespace AgentBridge.Rehearsal.Tests;

public class ConformanceInventoryTests
{
    [Theory]
    [InlineData("PWA")]
    [InlineData("Windows")]
    [InlineData("Mac")]
    [InlineData("iOS")]
    [InlineData("Android")]
    [InlineData("Linux")]
    [InlineData("HA")]
    [InlineData("ThirdParty")]
    public void Selecting_a_client_keeps_required_behaviors_missing_until_evidence_arrives(string client)
    {
        var capabilities = ConformanceInventory.ForClient(client);
        Assert.NotEmpty(capabilities);
        var run = new ConformanceRun(client, "1", 1, new string('a', 40), "inventory-fixture");
        var rows = ClientConformance.Build([run], capabilities, []);
        Assert.Contains(rows, r => r.Capability == "multi-field-questions");
        Assert.All(rows, r => Assert.Equal(ConformanceVerdict.Missing, r.Verdict));
        var unsupported = rows.Select(r => r with { Verdict = ConformanceVerdict.NotSupported, Reason = "Not built." });
        Assert.All(ClientConformance.Build([run], capabilities, unsupported),
            r => Assert.Equal(ConformanceVerdict.NotSupported, r.Verdict));
    }

    [Fact]
    public void Host_and_drop_dispositions_do_not_excuse_related_client_behaviors()
    {
        var names = ConformanceInventory.ForClient("Windows").Select(c => c.Name).ToArray();
        Assert.Contains("machine-presence", names);
        Assert.Contains("credentials-and-audit", names);
        Assert.Contains("multi-field-questions", names);
        Assert.Contains("agent-attribution", names);
        Assert.DoesNotContain("detailed-activity-toggle", names);
        Assert.DoesNotContain("agent-credential-distribution", names);
        Assert.DoesNotContain("ha-helper-liveness", names);
        Assert.DoesNotContain("agent-ha-token-migration", names);
    }

    [Theory]
    [InlineData((int)InventoryDisposition.HostOnly)]
    [InlineData((int)InventoryDisposition.FleetOnly)]
    [InlineData((int)InventoryDisposition.IntentionalDrop)]
    [InlineData((int)InventoryDisposition.OutsideCohort)]
    [InlineData((int)InventoryDisposition.ResolvedInventory)]
    [InlineData((int)InventoryDisposition.OutOfScope)]
    public void An_exclusion_without_a_recorded_decision_is_refused(int disposition)
    {
        foreach (var decision in new string?[] { null, "", " ", "not an approved decision" })
            Assert.Throws<ArgumentException>(() =>
                new ConformanceInventoryRow(17, new("multi-field-questions", true), (InventoryDisposition)disposition, decision));
    }

    [Fact]
    public void An_unknown_client_or_disposition_cannot_produce_a_successful_empty_selection()
    {
        Assert.Throws<ArgumentException>(() => ConformanceInventory.ForClient("unknown"));
        Assert.Throws<ArgumentException>(() => new ConformanceInventoryRow(1, new("reply"), (InventoryDisposition)99));
    }

    [Fact]
    public void Catalog_live_steps_cannot_pass_without_a_recorded_run_link()
    {
        var capabilities = ConformanceInventory.ForClient("PWA");
        Assert.NotEmpty(capabilities);
        var run = new ConformanceRun("PWA", "1", 1, new string('a', 40), "inventory-fixture");
        var claimed = capabilities.Select(c => new ConformanceResult(run, c.Name, ConformanceVerdict.Pass, "Claimed."));
        Assert.All(ClientConformance.Build([run], capabilities, claimed),
            r => Assert.Equal(ConformanceVerdict.Missing, r.Verdict));
    }
}
