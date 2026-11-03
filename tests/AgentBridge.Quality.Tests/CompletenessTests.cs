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
    public void A_catalog_project_folder_must_hold_exactly_one_csproj()
    {
        var none = new ProjectGraph(Fixture.Graph().Projects.Where(p => p.Folder != "tests/Transport.Tests"));
        var two = new ProjectGraph(Fixture.Graph().Projects.Append(Project.Create("tests/Transport.Tests", isTestProject: true)));

        Assert.Equal(["suite transport names project tests/Transport.Tests, which holds no .csproj files, not one"], Check(graph: none).Select(p => p.Message));
        Assert.Equal(["suite transport names project tests/Transport.Tests, which holds 2 .csproj files, not one"], Check(graph: two).Select(p => p.Message));
    }

    [Fact]
    public void Every_suite_needs_the_required_fields_with_known_values()
    {
        var catalog = Catalog.Parse("""
            suites:
              - id: bare
              - id: odd
                kind: vibes
                ring: 7
                select: sometimes
                platforms: [win-x64, amiga]
                runner: cloud
                status: done
                project: tests/Protocol.Tests
            """);

        var problems = catalog.Validate(Fixture.Graph().CsprojCount).Select(p => p.Message);

        Assert.Equal([
            "suite bare has no kind", "suite bare has no ring", "suite bare has no select", "suite bare has no platforms",
            "suite bare has no runner", "suite bare has no status",
            "suite odd has kind 'vibes', not one of unit, component, process, integration, e2e, live, ui, perf, security, a11y, static, packaging",
            "suite odd has ring 7, not 0 to 4",
            "suite odd has select 'sometimes', not one of affected, always",
            "suite odd has platform 'amiga', not one of win-x64, win-arm64, linux-x64, linux-arm64, macos-arm64, ios-sim, ios-device, browser",
            "suite odd has runner 'cloud', not one of local, hosted, lab, staging, device",
            "suite odd has status 'done', not one of exists, partial, planned",
        ], problems);
    }

    [Theory]
    [InlineData("hosted", "command: x", "suite s exists on a hosted runner but has no where")]
    [InlineData("hosted", "where: ci.yml product", "suite s exists but has no command")]
    [InlineData("lab", "command: x", null)]
    [InlineData("staging", "command: x", null)]
    [InlineData("device", "command: x", null)]
    public void An_existing_suite_needs_a_command_and_a_where_unless_a_person_runs_it(string runner, string field, string? problem)
    {
        var catalog = Catalog.Parse($"""
            suites:
              - id: s
                kind: e2e
                ring: 3
                select: always
                platforms: [linux-x64]
                runner: {runner}
                status: exists
                {field}
            """);

        var problems = catalog.Validate(_ => 1).Select(p => p.Message);

        Assert.Equal(problem is null ? [] : [problem], problems);
    }

    [Fact]
    public void An_affected_suite_with_neither_project_nor_triggers_is_reported()
    {
        var catalog = Catalog.Parse("""
            suites:
              - id: orphan
                kind: unit
                ring: 1
                select: affected
                platforms: [macos-arm64]
                runner: hosted
                status: planned
            """);

        var problem = Assert.Single(catalog.Validate(_ => 1));

        Assert.Equal("suite orphan is select: affected with neither project nor triggers, so nothing can select it", problem.Message);
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
