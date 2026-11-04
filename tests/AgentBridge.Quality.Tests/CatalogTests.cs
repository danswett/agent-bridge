namespace AgentBridge.Quality.Tests;

public class CatalogTests
{
    // One of the few tests that read the live repository: the selector is only as good as its ability
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
        // A perf suite's project is the benchmark or tool it runs (dotnet run), not a test project.
        Assert.All(catalog.Suites.Where(s => s.Project is not null), s => Assert.True(s.Kind == "perf" ? graph[s.Project!] is not null : graph[s.Project!]?.IsTestProject, $"{s.Id}: {s.Project}"));
    }

    // Files a CI job checks without owning them, which neither the project graph nor a folder
    // glob would reach: what generate.mjs writes and the design tests read outside design/, what
    // the dev stack links in from other spikes, the tests the AOT checks rerun, and repository
    // files .NET tests read at run time. Each once slipped past a pull request's selection
    // (#443's review).
    [Theory]
    [InlineData("src/AgentBridge.App.Core/DesignLayout.cs", "design-gen")]
    [InlineData("pwa/index.html", "design-gen")]
    [InlineData("pwa/manifest.webmanifest", "design-gen")]
    [InlineData("pwa/icons/icon-192.png", "design-gen")]
    [InlineData("src/AgentBridge.App/Assets/AgentBridge.ico", "design-gen")]
    [InlineData("pwa/app.css", "design-gen")]
    [InlineData("pwa/sw.js", "design-gen")]
    [InlineData("src/AgentBridge.App/App.xaml.cs", "design-gen")]
    [InlineData("build/pack-app.ps1", "design-gen")]
    [InlineData("docs/design/ui.md", "design-gen")]
    [InlineData("tests/AgentBridge.Host.Delivery.Tests/InjectorIntegrationTests.cs", "injector-aot")]
    [InlineData("tests/AgentBridge.Hooks.Tests/HookProcessTests.cs", "hook-aot")]
    [InlineData("docs/parity-checklist.md", "rehearsal-tool")]
    [InlineData("docs/parity.md", "rehearsal-tool")]
    [InlineData("src/AgentBridge.App/AgentBridge.App.csproj", "host")]
    [InlineData("build/pack-app.ps1", "host")]
    [InlineData("docs/protocol/vectors/signed-command.json", "host")]
    [InlineData(".github/actions/install-trivy/action.yml", "container-scan")]
    [InlineData("spikes/s2-noise-interop/dotnet/Noise.cs", "pwa-e2e")]
    [InlineData("spikes/s2-noise-interop/web/noise-p256.js", "pwa-e2e")]
    [InlineData("spikes/s6-tpm-keys/src/DeviceKeys.cs", "pwa-e2e")]
    // Embedded in App.Core for the demo the app job's self-test runs (#487).
    [InlineData("docs/protocol/examples/sessions-list.json", "app-package")]
    public void A_file_a_job_checks_from_outside_its_folders_selects_that_suite_in_the_repository_catalog(string file, string suite)
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("No AgentBridge.slnx above the test's output.");
        var selector = new Selector(Catalog.Load(Path.Combine(root, "quality", "catalog.yml")), ProjectGraph.Load(root));

        var selection = selector.Select([file], ring: 1);

        Assert.True(File.Exists(Path.Combine(root, file)), $"{file} no longer exists; update this test");
        Assert.Contains(suite, Fixture.Ids(selection));
    }

    // What decides selection must not narrow its own: a change to the selector, its tests or the
    // catalog runs the whole ring (#487). CI's plan job also forces ring 2 for these, apart from it.
    [Theory]
    [InlineData("tools/AgentBridge.Quality/ChangedFiles.cs")]
    [InlineData("tests/AgentBridge.Quality.Tests/SelectorTests.cs")]
    [InlineData("quality/catalog.yml")]
    [InlineData(".github/workflows/ci.yml")]
    public void A_change_to_what_decides_selection_runs_the_whole_ring_in_the_repository_catalog(string file)
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("No AgentBridge.slnx above the test's output.");
        var catalog = Catalog.Load(Path.Combine(root, "quality", "catalog.yml"));

        var selection = new Selector(catalog, ProjectGraph.Load(root)).Select([file], ring: 1);

        Assert.Equal("full", selection.Mode);
        Assert.Equal(catalog.Suites.Where(s => s.Ring <= 1).Select(s => s.Id), Fixture.Ids(selection));
    }

    [Fact]
    public void A_catalog_with_no_suites_is_refused_since_nothing_could_ever_be_selected()
    {
        var problem = Assert.Single(Catalog.Parse("sutes:\n  - id: a\n").Validate(_ => 1));

        Assert.Equal("the catalog lists no suites", problem.Message);
    }

    [Fact]
    public void A_catalog_that_lists_a_suite_twice_is_refused()
    {
        var yaml = "suites:\n  - id: a\n    ring: 0\n  - id: a\n    ring: 1\n";

        var error = Assert.Throws<FormatException>(() => Catalog.Parse(yaml));

        Assert.Contains("'a'", error.Message);
    }
}
