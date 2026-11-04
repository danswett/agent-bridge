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
              - id: whole-ring
              - id: select
                env:
                  JOBS: product ha-integration
          product:
            needs: plan
            if: github.event_name != 'pull_request' || needs.plan.outputs.whole-ring == 'true' || needs.plan.outputs.product == 'true'
            env:
              WHOLE_RING: ${{ needs.plan.outputs.whole-ring }}
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
              !cancelled() && github.event_name == 'push'
              && needs.quality-gate.result == 'success'
        """;

    const string StepIf = "runner.os == 'Windows' && (github.event_name != 'pull_request' || env.WHOLE_RING == 'true' || contains(fromJSON(env.SUITES), 'transport'))";
    const string StepProblem = "ci.yml job product step \"The NativeAOT transport\" must run on main and in the whole ring whatever the plan selected: " +
        "its if: must be [runner.os == '...' && | !cancelled() && ](github.event_name != 'pull_request' || env.WHOLE_RING == 'true' || contains(fromJSON(env.SUITES), '...') || ...)";

    static IEnumerable<string> Check(string yaml) => CiPlan.Check(Fixture.Catalog(), yaml).Select(p => p.Message);

    [Fact]
    public void The_wired_fixture_has_no_problems_so_each_test_below_shows_one_drift()
    {
        Assert.Empty(Check(Wired));
        Assert.Empty(Check(Wired.Replace(StepIf, "${{ !cancelled() && ( github.event_name != 'pull_request' || env.WHOLE_RING == 'true' || contains(fromJSON(env.SUITES), 'protocol') || contains(fromJSON(env.SUITES), 'transport') ) }}")));
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
    public void A_step_whose_suite_names_only_the_workflow_file_is_reported_since_the_plan_gates_jobs_by_name()
    {
        var catalog = Fixture.Catalog();
        catalog.Suites.Single(s => s.Id == "transport").Where = "ci.yml";

        var problems = CiPlan.Check(catalog, Wired).Select(p => p.Message);

        Assert.Equal(["ci.yml job product step \"The NativeAOT transport\" runs when suite transport is selected, but that suite's where: doesn't name ci.yml product"], problems);
    }

    [Theory]
    [InlineData("runner.os == 'Windows' && contains(fromJSON(env.SUITES), 'transport')")]
    // A bypass that's there but can't bypass: main would still skip it unless protocol is selected.
    [InlineData("runner.os == 'Windows' && (github.event_name != 'pull_request' || env.WHOLE_RING == 'true' || contains(fromJSON(env.SUITES), 'transport')) && contains(fromJSON(env.SUITES), 'protocol')")]
    [InlineData("runner.os == 'Windows' && (github.event_name != 'pull_request' || contains(fromJSON(env.SUITES), 'transport'))")]
    [InlineData("runner.os == 'Windows' && github.event_name != 'pull_request' || env.WHOLE_RING == 'true' || contains(fromJSON(env.SUITES), 'transport')")]
    public void A_suite_gated_step_that_main_or_the_whole_ring_could_skip_is_reported(string condition)
    {
        Assert.Equal([StepProblem], Check(Wired.Replace(StepIf, condition)));
    }

    [Fact]
    public void A_job_with_suite_gated_steps_must_be_handed_the_plans_whole_ring_output()
    {
        var problems = Check(Wired.Replace("WHOLE_RING: ${{ needs.plan.outputs.whole-ring }}", "WHOLE_RING: ${{ needs.plan.outputs.whole_ring }}"));

        Assert.Equal(["ci.yml job product has steps that run when a suite is selected, but its env WHOLE_RING isn't ${{ needs.plan.outputs.whole-ring }}"], problems);
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
    public void A_gated_output_that_is_missing_or_bound_to_another_value_is_reported()
    {
        Assert.Equal(["ci.yml plan has no output ha-integration"],
            Check(Wired.Replace("ha-integration: ${{ steps.select.outputs.ha-integration }}", "")));
        // A typo exports an empty value, which the job and quality-gate would both read as not selected.
        Assert.Equal(["ci.yml plan's output ha-integration is ${{ steps.select.outputs.ha-integraton }}, not ${{ steps.select.outputs.ha-integration }}"],
            Check(Wired.Replace("${{ steps.select.outputs.ha-integration }}", "${{ steps.select.outputs.ha-integraton }}")));
        Assert.Equal(["ci.yml plan's output whole-ring is ${{ steps.select.outputs.whole-ring }}, not ${{ steps.whole-ring.outputs.whole-ring }}"],
            Check(Wired.Replace("${{ steps.whole-ring.outputs.whole-ring }}", "${{ steps.select.outputs.whole-ring }}")));
    }

    [Theory]
    // Gated on another job's output: quality-gate couldn't tell whether it had to run.
    [InlineData("needs.plan.outputs.ha-integration == 'true'", "needs.plan.outputs.product == 'true'")]
    // Skipped on main, or on a whole-ring pull request.
    [InlineData("github.event_name != 'pull_request' || needs.plan.outputs.whole-ring == 'true' || needs.plan.outputs.ha-integration", "needs.plan.outputs.ha-integration")]
    [InlineData("|| needs.plan.outputs.ha-integration == 'true'", "|| needs.plan.outputs.ha-integration == 'true' && needs.plan.outputs.product == 'true'")]
    // A bare read, or the index syntax, without the comparison the gate shape has.
    [InlineData("|| needs.plan.outputs.ha-integration == 'true'", "|| needs.plan.outputs.ha-integration")]
    [InlineData("|| needs.plan.outputs.ha-integration == 'true'", "|| needs['plan'].outputs['ha-integration'] == 'true'")]
    public void A_gated_job_whose_if_isnt_exactly_the_gate_shape_is_reported(string from, string to)
    {
        var problems = Check(Wired.Replace(from, to));

        Assert.Equal(["ci.yml job ha-integration is gated on the plan, but its if: isn't exactly: github.event_name != 'pull_request' || needs.plan.outputs.whole-ring == 'true' || needs.plan.outputs.ha-integration == 'true'"], problems);
    }

    [Fact]
    public void A_job_reading_the_plan_bare_is_still_held_to_the_plans_jobs_and_outputs()
    {
        var problems = Check(Wired
            .Replace("|| needs.plan.outputs.ha-integration == 'true'", "|| needs.plan.outputs.ha-integration")
            .Replace("JOBS: product ha-integration", "JOBS: product")
            .Replace("ha-integration: ${{ steps.select.outputs.ha-integration }}", ""));

        Assert.Equal([
            "ci.yml job ha-integration is gated on the plan, but its if: isn't exactly: github.event_name != 'pull_request' || needs.plan.outputs.whole-ring == 'true' || needs.plan.outputs.ha-integration == 'true'",
            "ci.yml job ha-integration is gated on the plan, but the plan's JOBS doesn't list it, so it would never run on a pull request",
            "ci.yml plan has no output ha-integration",
        ], problems);
    }

    [Fact]
    public void A_check_job_missing_from_the_gate_is_reported_whether_or_not_it_reads_the_plan()
    {
        const string Problem = " isn't in quality-gate's needs, so its result wouldn't count (only attest, publish, publish-gate, relay-image-sarif may stay out)";

        Assert.Equal(["ci.yml job ha-integration" + Problem], Check(Wired.Replace("needs: [plan, product, ha-integration]", "needs: [plan, product]")));
        Assert.Equal(["ci.yml job lint" + Problem], Check(Wired.Replace("  quality-gate:", "  lint:\n    runs-on: ubuntu-latest\n  quality-gate:")));
        // Downstream of the gate, it could fail after the gate passed.
        Assert.Equal(["ci.yml job lint" + Problem], Check(Wired.Replace("  quality-gate:", "  lint:\n    needs: quality-gate\n  quality-gate:")));
    }

    [Theory]
    [InlineData("needs: [quality-gate]", "needs: [plan, product, ha-integration]", "ci.yml publish-gate doesn't need quality-gate, so a release could publish past a failed check")]
    [InlineData("&& needs.quality-gate.result == 'success'", "&& needs.product.result == 'success'", "ci.yml publish-gate's if: doesn't require needs.quality-gate.result == 'success' (as one of its && conditions, with no ||)")]
    [InlineData("&& needs.quality-gate.result == 'success'", "&& needs.quality-gate.result == 'success' || always()", "ci.yml publish-gate's if: doesn't require needs.quality-gate.result == 'success' (as one of its && conditions, with no ||)")]
    public void A_publish_gate_that_doesnt_stand_on_quality_gate_is_reported(string from, string to, string problem)
    {
        Assert.Equal([problem], Check(Wired.Replace(from, to)));
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
