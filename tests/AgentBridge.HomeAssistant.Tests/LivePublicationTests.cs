using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using Xunit;

[Trait("Category", "HomeAssistantIntegration")]
public sealed class LivePublicationTests
{
    [HomeAssistantFact]
    public async Task Fenced_native_card_and_full_fleet_dashboard_are_saved_observed_and_repaired_without_a_legacy_daemon()
    {
        // HomeAssistantFact requires an explicitly disposable, loopback HA. The
        // policy setup below is test provisioning, not automatic writer bootstrap.
        Assert.Null(LiveHomeAssistant.WhyNot());
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var uri = new Uri(LiveHomeAssistant.Url!);
        var ha = new HomeAssistantClient(uri, LiveHomeAssistant.Token!, http);
        const string dashboard = "native-publication-test";
        var folder = Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "native-publication-" + Guid.NewGuid().ToString("N"));
        var initial = await ha.ReadResourcesAsync();
        Assert.DoesNotContain(initial.EnumerateArray(), r => r.GetProperty("url").GetString()!.Contains("agent-bridge-", StringComparison.Ordinal));
        var policyJson = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ha-publication.json")))!["policy"]!;
        policyJson["dashboard"] = dashboard;
        var policy = HaPublicationPolicy.Read(JsonSerializer.SerializeToElement(policyJson), dashboard);
        var createdIds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            await ha.ApplyPublicationAsync(new(new JsonObject { ["type"] = "lovelace/resources/create", ["url"] = policy.ResourceUrl, ["res_type"] = "module" }));
            HaPublicationWriter Writer() => new(ha, dashboard, new(folder, uri, dashboard), new("test-authority", "test-writer", "test-writer", 3));
            var writer = Writer();
            await writer.PublishCardAsync(HaPublicationRenderer.CardUrl);
            var rendered = HaPublicationRenderer.Render(
                [new("agent_bridge_native", "Native session", "Native"), new("agent_bridge_legacy", "Legacy MCP", "Legacy", "mcp")],
                [new("native", "Native", IncludeAgent: true, IncludeDetailed: true), new("legacy", "Legacy", Online: false)], "", HaPublicationRenderer.CardUrl);
            await writer.PublishDashboardAsync(rendered.Config, rendered.InputHash, rendered.RenderedNodes, HaPublicationRenderer.Artifact);
            var state = await Writer().ReadAsync();
            createdIds.UnionWith([state.PolicyResource!.Id, state.CardResource!.Id]);
            var proof = HaVerifiedPublication.Read(state);
            Assert.True(proof.Verified, proof.Reason);
            Assert.Equal(2, proof.RenderedNodes.Count);
            Assert.Equal(HaPublicationRenderer.Artifact, state.Policy!.Render);
            // A successful save response is not proof: detect an actual overwrite,
            // then repair and observe again with a fresh writer/receipt instance.
            await ha.ApplyPublicationAsync(new(new JsonObject { ["type"] = "lovelace/config/save", ["url_path"] = dashboard, ["config"] = new JsonObject { ["title"] = "overwritten", ["views"] = new JsonArray() } }));
            Assert.False(HaVerifiedPublication.Read(await Writer().ReadAsync()).Verified);
            await Writer().PublishDashboardAsync(rendered.Config, rendered.InputHash, rendered.RenderedNodes, HaPublicationRenderer.Artifact);
            Assert.True(HaVerifiedPublication.Read(await Writer().ReadAsync()).Verified);
            await Assert.ThrowsAsync<InvalidDataException>(() => Writer().PublishCardAsync(
                "data:text/javascript;base64,Y29uc3QgQ0FSRF9WRVJTSU9OID0gJzEuMC4wJzs=#agent-bridge-reply-card.js?v=1.0.0"));
        }
        finally
        {
            // Clean only the reserved resources introduced by this isolated test.
            var resources = await ha.ReadResourcesAsync();
            foreach (var resource in resources.EnumerateArray())
            {
                var url = resource.GetProperty("url").GetString()!;
                if (url == HaPublicationRenderer.CardUrl || url.Contains("#agent-bridge-publication-policy.js?", StringComparison.Ordinal))
                {
                    var id = resource.GetProperty("id").GetString()!;
                    if (!initial.EnumerateArray().Any(r => r.GetProperty("id").GetString() == id)) { createdIds.Add(id); }
                }
            }
            foreach (var id in createdIds) { await ha.ApplyPublicationAsync(new(new JsonObject { ["type"] = "lovelace/resources/delete", ["resource_id"] = id })); }
            foreach (var entry in (await ha.ReadDashboardsAsync()).EnumerateArray())
            {
                if (entry.GetProperty("url_path").GetString() == dashboard)
                {
                    await ha.ApplyPublicationAsync(new(new JsonObject { ["type"] = "lovelace/dashboards/delete", ["dashboard_id"] = entry.GetProperty("id").GetString() }));
                }
            }
            if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
        }
    }
}
