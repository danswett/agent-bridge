namespace AgentBridge.Quality.Tests;

public class CatalogTests
{
    // The one test that reads the live repository: the selector is only as good as its ability
    // to read the real catalog, so a schema change there must fail here, not in CI's plan job.
    [Fact]
    public void The_repository_catalog_keeps_its_schema_and_every_project_it_names_is_a_test_project()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("No AgentBridge.slnx above the test's output.");

        var catalog = Catalog.Load(Path.Combine(root, "quality", "catalog.yml"));
        var graph = ProjectGraph.Load(root);

        Assert.NotEmpty(catalog.Suites);
        Assert.Empty(catalog.Validate(graph.CsprojCount).Select(p => p.Message));
        Assert.All(catalog.Suites.Where(s => s.Project is not null), s => Assert.True(graph[s.Project!]?.IsTestProject, $"{s.Id}: {s.Project}"));
    }

    [Fact]
    public void A_catalog_that_lists_a_suite_twice_is_refused()
    {
        var yaml = "suites:\n  - id: a\n    ring: 0\n  - id: a\n    ring: 1\n";

        var error = Assert.Throws<FormatException>(() => Catalog.Parse(yaml));

        Assert.Contains("'a'", error.Message);
    }
}
