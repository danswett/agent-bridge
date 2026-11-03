namespace AgentBridge.Quality.Tests;

public class CiPlanTests
{
    // ci.yml's wiring to the plan as the repository has it, cut down to two gated jobs.
    const string Wired = """
        jobs:
          plan:
            outputs:
              whole-ring: ${{ steps.whole-ring.outputs.whole-ring }}
              product: ${{ steps.select.outputs.product }}
              ha-integration: ${{ steps.select.outputs.ha-integration }}
            steps:
              - id: select
                env:
                  JOBS: product ha-integration
          product:
            needs: plan
            if: github.event_name != 'pull_request' || needs.plan.outputs.whole-ring == 'true' || needs.plan.outputs.product == 'true'
            steps:
              - name: The NativeAOT transport
                if: runner.os == 'Windows' && (github.event_name != 'pull_request' || env.WHOLE_RING == 'true' || contains(fromJSON(env.SUITES), 'transport'))
          ha-integration:
            needs: plan
            if: github.event_name != 'pull_request' || needs.plan.outputs.whole-ring == 'true' || needs.plan.outputs.ha-integration == 'true'
          relay-image-sarif:
            needs: product
          quality-gate:
            needs: [plan, product, ha-integration]
            if: always()
          publish-gate:
            needs: [quality-gate]
            if: >-
              !cancelled() && github.event_name == 'push' && needs.quality-gate.result == 'success'
        """;

    static IEnumerable<string> Check(string yaml) => CiPlan.Check(Fixture.Catalog(), yaml).Select(p => p.Message);

    [Fact]
    public void The_wired_fixture_has_no_problems_so_each_test_below_shows_one_drift()
    {
        Assert.Empty(Check(Wired));
    }

    [Fact]
    public void A_step_that_tests_for_a_suite_the_catalog_lacks_is_reported()
    {
        var problems = Check(Wired.Replace("'transport'", "'transport-aot'"));

        Assert.Equal(["ci.yml job product step \"The NativeAOT transport\" runs when suite transport-aot is selected, but the catalog has no such suite"], problems);
    }

    [Fact]
    public void A_step_that_tests_for_a_suite_running_in_another_job_is_reported()
    {
        var problems = Check(Wired.Replace("'transport'", "'ha-integration'"));

        Assert.Equal(["ci.yml job product step \"The NativeAOT transport\" runs when suite ha-integration is selected, but that suite's where: doesn't name ci.yml product"], problems);
    }

    [Fact]
    public void A_suite_gated_step_that_main_would_skip_is_reported()
    {
        var problems = Check(Wired.Replace("(github.event_name != 'pull_request' || env.WHOLE_RING == 'true' || contains(fromJSON(env.SUITES), 'transport'))", "contains(fromJSON(env.SUITES), 'transport')"));

        Assert.Equal(["ci.yml job product step \"The NativeAOT transport\" runs only when its suites are selected, even on main: its if: must include github.event_name != 'pull_request' ||"], problems);
    }

    [Fact]
    public void A_gated_job_the_plans_jobs_list_leaves_out_is_reported_and_so_is_a_listed_job_nothing_gates()
    {
        Assert.Equal(
            ["ci.yml job ha-integration is gated on the plan, but the plan's JOBS doesn't list it, so it would never run on a pull request"],
            Check(Wired.Replace("JOBS: product ha-integration", "JOBS: product")));
        Assert.Equal(
            ["ci.yml plan's JOBS lists spikes, but no job of that name is gated on it"],
            Check(Wired.Replace("JOBS: product ha-integration", "JOBS: product ha-integration spikes")));
    }

    [Fact]
    public void A_gated_job_the_plan_has_no_output_for_is_reported()
    {
        var problems = Check(Wired.Replace("ha-integration: ${{ steps.select.outputs.ha-integration }}", ""));

        Assert.Equal(["ci.yml plan has no output ha-integration"], problems);
    }

    [Fact]
    public void A_job_gated_on_another_jobs_output_or_skipped_on_main_is_reported()
    {
        Assert.Equal(
            ["ci.yml job ha-integration is gated on the plan's product output, not its own, so quality-gate can't tell whether it had to run"],
            Check(Wired.Replace("needs.plan.outputs.ha-integration == 'true'", "needs.plan.outputs.product == 'true'")));
        Assert.Equal(
            ["ci.yml job ha-integration runs only when the plan selects it, even on main: its if: must start with github.event_name != 'pull_request' ||"],
            Check(Wired.Replace("if: github.event_name != 'pull_request' || needs.plan.outputs.whole-ring == 'true' || needs.plan.outputs.ha-integration", "if: needs.plan.outputs.ha-integration")));
    }

    [Fact]
    public void A_job_that_needs_the_plan_but_not_the_gate_is_reported()
    {
        var problems = Check(Wired.Replace("needs: [plan, product, ha-integration]", "needs: [plan, product]"));

        Assert.Equal(["ci.yml job ha-integration needs the plan but isn't in quality-gate's needs, so its result wouldn't count"], problems);
    }

    [Fact]
    public void A_publish_gate_that_doesnt_stand_on_quality_gate_is_reported()
    {
        var problems = Check(Wired
            .Replace("needs: [quality-gate]", "needs: [plan, product, ha-integration]")
            .Replace("needs.quality-gate.result == 'success'", "needs.product.result == 'success'"));

        Assert.Equal([
            "ci.yml publish-gate doesn't need quality-gate, so a release could publish past a failed check",
            "ci.yml publish-gate's if: doesn't require needs.quality-gate.result == 'success'",
        ], problems);
    }

    // Reads the live ci.yml and catalog: the wiring the plan depends on must hold there, not only
    // in the fixture.
    [Fact]
    public void The_repository_ci_workflow_is_wired_to_the_plan_and_the_catalog()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("No AgentBridge.slnx above the test's output.");
        var catalog = Catalog.Load(Path.Combine(root, "quality", "catalog.yml"));

        var problems = CiPlan.Check(catalog, File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml")));

        Assert.Empty(problems.Select(p => p.Message));
    }
}
