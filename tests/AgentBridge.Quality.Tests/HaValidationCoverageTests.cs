using YamlDotNet.RepresentationModel;

namespace AgentBridge.Quality.Tests;

// #527's preparatory slice preserves today's coverage. Changing these contracts needs a
// separately approved cut; neither the catalog nor a missing workflow may retire them silently.
public class HaValidationCoverageTests
{
    static string Root => ChangedFiles.RepoRoot(AppContext.BaseDirectory);
    static Catalog Catalog => Quality.Catalog.Load(Path.Combine(Root, "quality", "catalog.yml"));

    [Theory]
    [InlineData("homeassistant", 0, "affected", "ci.yml product", "exists")]
    [InlineData("ha-integration", 1, "affected", "ci.yml ha-integration", "exists")]
    [InlineData("ha-contract-fixtures", 1, "affected", "ha-contract-drift.yml", "exists")]
    [InlineData("ha-contract-drift", 3, "always", "ha-contract-drift.yml", "exists")]
    [InlineData("migrate-rollback", 4, "always", null, "partial")]
    [InlineData("shadow-soak", 4, "always", null, "exists")]
    public void Retained_HA_checks_keep_their_ring_selection_and_execution_contract(
        string id, int ring, string select, string? where, string status)
    {
        var catalog = Catalog;
        var suite = Assert.Single(catalog.Suites, s => s.Id == id);
        Assert.Equal(ring, suite.Ring);
        Assert.Equal(select, suite.Select);
        Assert.Equal(where, suite.Where);
        Assert.Equal(status, suite.Status);
        Assert.False(string.IsNullOrWhiteSpace(suite.Command));

        var selector = new Selector(catalog, ProjectGraph.Load(Root));
        for (var requested = 0; requested <= 4; requested++)
        {
            var selection = selector.Select(["quality/catalog.yml"], requested);
            Assert.Equal(requested >= ring, Fixture.Ids(selection).Contains(id));
        }
        if (select == "always")
            Assert.Contains(id, Fixture.Ids(selector.Select(["pwa/app.css"], ring)));
    }

    [Fact]
    public void Native_PWA_changes_keep_native_validation_without_selecting_HA_checks()
    {
        var selection = new Selector(Catalog, ProjectGraph.Load(Root))
            .Select(["pwa/index.html"], ring: 1);
        var ids = Fixture.Ids(selection);

        foreach (var id in new[] { "protocol", "pwa-unit", "pwa-ui", "pwa-e2e", "pwa-site-csp" })
            Assert.Contains(id, ids);
        Assert.DoesNotContain("homeassistant", ids);
        Assert.DoesNotContain("ha-integration", ids);
        Assert.Empty(selection.Unclaimed);
    }

    [Fact]
    public void Shared_host_changes_keep_native_generic_rehearsal_and_HA_regressions()
    {
        var selection = new Selector(Catalog, ProjectGraph.Load(Root))
            .Select(["src/AgentBridge.Host.Core/SessionDrivers.cs"], ring: 1);
        var jobs = Program.JobsOf(selection);

        foreach (var id in new[] { "protocol", "host-core", "host", "homeassistant", "rehearsal-tool" })
            Assert.Contains(id, jobs["ci.yml"]["product"]);
        Assert.Contains("ha-integration", jobs["ci.yml"]["ha-integration"]);
        Assert.Empty(selection.Unclaimed);
    }

    [Theory]
    [InlineData("src/AgentBridge.HomeAssistant/HaConnector.cs", "ha-integration", "ci.yml", "ha-integration")]
    [InlineData("tools/ha-ci/onboard.py", "ha-integration", "ci.yml", "ha-integration")]
    [InlineData("tools/ha-contract/Test-HaContractDrift.ps1", "ha-contract-fixtures", "ha-contract-drift.yml", "*")]
    [InlineData("tests/AgentBridge.HomeAssistant.Tests/Fixtures/ha-contract.json", "ha-contract-fixtures", "ha-contract-drift.yml", "*")]
    public void HA_tooling_and_fixture_changes_still_select_their_required_lane(
        string file, string suite, string workflow, string job)
    {
        var selection = new Selector(Catalog, ProjectGraph.Load(Root)).Select([file], ring: 1);

        Assert.True(File.Exists(Path.Combine(Root, file)), file);
        Assert.Contains(suite, Program.JobsOf(selection)[workflow][job]);
        Assert.Empty(selection.Unclaimed);
    }

    [Fact]
    public void Native_and_HA_validation_remain_complete_and_keep_both_delivery_gate_needs()
    {
        var catalog = Catalog;
        Assert.Empty(Completeness.CheckRepo(Root, catalog, ProjectGraph.Load(Root)));
        var jobs = CiJobs();

        Assert.Equal(
            new[] { "plan", "product", "ha-integration", "spikes", "spikes-linux", "app",
                "app-installer", "light", "pwa-ui", "pwa-e2e" },
            Needs(jobs, "quality-gate"));
        Assert.Equal(new[] { "quality-gate" }, Needs(jobs, "publish-gate"));
        Assert.Equal("${{ github.event.pull_request.draft && 'quality-gate (fast ring)' || 'quality-gate' }}",
            ((YamlMappingNode)jobs["quality-gate"])["name"].ToString());
    }

    [Fact]
    public void The_HA_lane_still_provisions_disposable_services_and_runs_integration_tests()
    {
        var lane = (YamlMappingNode)CiJobs()["ha-integration"];
        var steps = (YamlSequenceNode)lane["steps"];
        var commands = string.Join("\n", steps.Children.Cast<YamlMappingNode>()
            .Where(s => s.Children.ContainsKey(new YamlScalarNode("run")))
            .Select(s => s["run"].ToString()));

        Assert.Contains("docker run -d --name mosquitto", commands, StringComparison.Ordinal);
        Assert.Contains("docker run -d --name homeassistant", commands, StringComparison.Ordinal);
        Assert.Contains("-p 127.0.0.1:8123:8123", commands, StringComparison.Ordinal);
        Assert.Contains("python3 tools/ha-ci/onboard.py", commands, StringComparison.Ordinal);
        Assert.Contains("--filter \"Category=HomeAssistantIntegration\"", commands, StringComparison.Ordinal);
    }

    static YamlMappingNode CiJobs()
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(Path.Combine(Root, ".github", "workflows", "ci.yml"))));
        return (YamlMappingNode)((YamlMappingNode)yaml.Documents[0].RootNode)["jobs"];
    }

    static string[] Needs(YamlMappingNode jobs, string job) =>
        ((YamlSequenceNode)((YamlMappingNode)jobs[job])["needs"]).Children.Select(n => n.ToString()).ToArray();
}
