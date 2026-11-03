namespace AgentBridge.Quality.Tests;

public class CompletenessTests
{
    static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Workflows = new Dictionary<string, IReadOnlyList<string>>
    {
        ["ci.yml"] = ["product", "ha-integration"],
    };

    static IReadOnlyList<Problem> Check(
        IReadOnlyList<string>? solution = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? workflows = null,
        IReadOnlyList<string>? tracked = null,
        ProjectGraph? graph = null) =>
        Completeness.Check(Fixture.Catalog(), graph ?? Fixture.Graph(), new Completeness.Inputs(
            solution ?? ["src/Protocol", "tests/Protocol.Tests"],
            workflows ?? Workflows,
            tracked ?? []));

    [Fact]
    public void The_fixture_catalog_is_complete_so_each_test_below_shows_one_gap()
    {
        Assert.Empty(Check());
    }

    [Fact]
    public void A_test_project_in_the_solution_that_no_suite_names_is_reported()
    {
        var graph = new ProjectGraph(Fixture.Graph().Projects.Append(Project.Create("tests/New.Tests", isTestProject: true)).Append(Project.Create("tests/Echo")));

        var problems = Check(solution: ["tests/Protocol.Tests", "tests/New.Tests", "tests/Echo"], graph: graph);

        var problem = Assert.Single(problems);
        Assert.Equal("test-project", problem.Kind);
        Assert.StartsWith("tests/New.Tests", problem.Message);
    }

    [Fact]
    public void A_workflow_job_no_suite_names_is_reported_and_a_bare_file_covers_all_its_jobs()
    {
        var problems = Check(workflows: new Dictionary<string, IReadOnlyList<string>>
        {
            ["ci.yml"] = ["product", "ha-integration", "publish"],
        });

        Assert.Equal(["ci.yml job publish isn't named in any suite's where:"], problems.Select(p => p.Message));
        Assert.Equal([("drift.yml", 0)], Completeness.ParseWhere("drift.yml").Select(w => (w.File, w.Jobs.Count)));
        Assert.Equal(["spikes", "s4-relay"], Completeness.ParseWhere("ci.yml spikes, s4-relay").Single().Jobs);
    }

    [Fact]
    public void A_where_that_names_a_job_that_doesnt_exist_is_reported()
    {
        var problems = Check(workflows: new Dictionary<string, IReadOnlyList<string>> { ["ci.yml"] = ["product"] });

        Assert.Equal(["suite ha-integration says it runs in ci.yml job ha-integration, which doesn't exist"], problems.Select(p => p.Message));
    }

    [Fact]
    public void A_catalog_project_with_no_csproj_is_reported()
    {
        var graph = new ProjectGraph(Fixture.Graph().Projects.Where(p => p.Folder != "tests/Transport.Tests"));

        var problem = Assert.Single(Check(graph: graph));

        Assert.Equal("suite transport names project tests/Transport.Tests, which has no .csproj", problem.Message);
    }

    [Fact]
    public void A_pwa_test_folder_that_no_command_runs_is_reported()
    {
        var problems = Check(tracked: [
            "pwa/test/answer.spec.mjs",       // run by the bare "playwright test"
            "pwa/test/unit/a.test.mjs",       // named by pwa-unit's command
            "pwa/test/visual/b.test.mjs",
        ]);

        Assert.Equal(["pwa/test/visual holds tests, but no suite's command runs them"], problems.Select(p => p.Message));
    }

    [Theory]
    [InlineData("cd pwa && node --test \"test/unit/*.test.mjs\"", "pwa/test/unit", false, true)]
    [InlineData("cd pwa && node --test \"test/unit/*.test.mjs\"", "pwa/test", false, false)]
    [InlineData("cd pwa && npx playwright test test/e2e   # through the relay", "pwa/test/e2e", true, true)]
    [InlineData("cd pwa && npx playwright test test/e2e   # through the relay", "pwa/test", true, false)]
    [InlineData("cd pwa && npx playwright test   # Chromium and WebKit", "pwa/test", true, true)]
    [InlineData("cd pwa && npx playwright test   # Chromium and WebKit", "pwa/test/unit", false, false)]
    public void A_command_runs_a_test_folder_only_by_naming_it_or_as_a_bare_playwright_run(string command, string folder, bool hasSpecs, bool runs) =>
        Assert.Equal(runs, Completeness.RunsFolder(command, folder, hasSpecs));

    [Fact]
    public void A_tracked_file_no_rule_claims_is_reported()
    {
        var problems = Check(tracked: ["README.md", "pwa/app.css", "src/Host/Program.cs", "LICENSE"]);

        Assert.Equal(["LICENSE is claimed by no rule, so a change to it runs the full ring"], problems.Select(p => p.Message));
    }

    [Fact]
    public void Workflow_jobs_and_solution_projects_are_read_from_their_files()
    {
        var jobs = Completeness.WorkflowJobs("""
            name: CI
            on: { push: { branches: [main] } }
            jobs:
              product:
                runs-on: ubuntu-latest
                steps: [{ run: echo }]
              pwa:
                runs-on: ubuntu-latest
            """);
        var projects = Completeness.SolutionProjects("""
            <Solution><Folder Name="/tests/"><Project Path="tests/A.Tests/A.Tests.csproj" /></Folder></Solution>
            """);

        Assert.Equal(["product", "pwa"], jobs);
        Assert.Equal(["tests/A.Tests"], projects);
    }
}
