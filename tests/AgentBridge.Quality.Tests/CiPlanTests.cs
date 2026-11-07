namespace AgentBridge.Quality.Tests;

public class CiPlanTests
{
    // ci.yml's wiring to the plan as the repository has it, cut down to two gated jobs.
    const string Wired = """
        concurrency:
          group: ${{ github.event_name == 'push' && github.ref == 'refs/heads/main' && github.run_attempt == '1' && 'ci-main' || format('{0}-{1}', github.workflow, github.event_name == 'pull_request' && format('pr-{0}', github.event.pull_request.number) || github.run_id) }}
          cancel-in-progress: ${{ github.event_name == 'pull_request' }}
        jobs:
          plan:
            outputs:
              whole-ring: ${{ steps.whole-ring.outputs.whole-ring }}
              suites: ${{ steps.select.outputs.suites }}
              product-os: ${{ steps.whole-ring.outputs.whole-ring == 'true' && '["ubuntu-latest","windows-latest","windows-11-arm"]' || steps.select.outputs.product-os }}
              product: ${{ steps.select.outputs.product }}
              ha-integration: ${{ steps.select.outputs.ha-integration }}
            steps:
              - id: whole-ring
              - id: select
                env:
                  JOBS: product ha-integration
          product:
            needs: plan
            if: needs.plan.outputs.whole-ring == 'true' || needs.plan.outputs.product == 'true'
            strategy:
              matrix:
                os: ${{ fromJSON(needs.plan.outputs.product-os) }}
            runs-on: ${{ matrix.os }}
            env:
              SUITES: ${{ needs.plan.outputs.suites }}
              WHOLE_RING: ${{ needs.plan.outputs.whole-ring }}
            steps:
              - name: The NativeAOT transport
                if: runner.os == 'Windows' && (env.WHOLE_RING == 'true' || contains(fromJSON(env.SUITES), 'transport'))
          ha-integration:
            needs: plan
            if: needs.plan.outputs.whole-ring == 'true' || needs.plan.outputs.ha-integration == 'true'
          relay-image-sarif:
            needs: product
          quality-gate:
            needs: [plan, product, ha-integration]
            if: always()
            outputs:
              app: ${{ needs.app.result }}
          publish-gate:
            needs: [quality-gate]
            if: >-
              !cancelled() && github.event_name == 'push'
              && needs.quality-gate.result == 'success' && needs.quality-gate.outputs.app == 'success'
        """;

    const string StepIf = "runner.os == 'Windows' && (env.WHOLE_RING == 'true' || contains(fromJSON(env.SUITES), 'transport'))";
    const string StepProblem = "ci.yml job product step \"The NativeAOT transport\" must run in the whole ring whatever the plan selected: " +
        "its if: must be [runner.os == 'Windows|Linux|macOS' && ][!cancelled() && ](env.WHOLE_RING == 'true' || contains(fromJSON(env.SUITES), '...') || ...)";

    static IEnumerable<string> Check(string yaml) => CiPlan.Check(Fixture.Catalog(), yaml).Select(p => p.Message);

    // Only main's first-attempt push runs share ci-main, never cancelled once running: a re-run
    // there could take a waiting push run's place and leave its commit untested (#938's review).
    [Theory]
    [InlineData(" && github.run_attempt == '1'", "", false)]
    [InlineData("github.run_attempt == '1'", "github.run_attempt != ''", false)]
    [InlineData("'ci-main' ||", "'ci-main-' ||", false)]
    [InlineData("cancel-in-progress: ${{ github.event_name == 'pull_request' }}", "cancel-in-progress: true", false)]
    [InlineData("cancel-in-progress: ${{ github.event_name == 'pull_request' }}", "cancel-in-progress: false", false)]
    [InlineData("concurrency:", "x-concurrency:", false)]
    [InlineData("'1' && 'ci-main'", "'1'   &&  'ci-main'", true)]
    public void Ci_concurrency_keeps_reruns_out_of_ci_main_and_never_cancels_its_runs(string from, string to, bool fine)
    {
        Assert.Contains(from, Wired, StringComparison.Ordinal);
        var problems = Check(Wired.Replace(from, to, StringComparison.Ordinal));
        string[] expected = fine ? [] : [
            "ci.yml's concurrency isn't group ${{ github.event_name == 'push' && github.ref == 'refs/heads/main' && github.run_attempt == '1' && 'ci-main' || format('{0}-{1}', github.workflow, github.event_name == 'pull_request' && format('pr-{0}', github.event.pull_request.number) || github.run_id) }}"
            + " with cancel-in-progress ${{ github.event_name == 'pull_request' }}"];

        Assert.Equal(expected, problems);
    }

    [Fact]
    public void The_wired_fixture_has_no_problems_so_each_test_below_shows_one_drift()
    {
        Assert.Empty(Check(Wired));
        Assert.Empty(Check(Wired.Replace(StepIf, "${{ !cancelled() && ( env.WHOLE_RING == 'true' || contains(fromJSON(env.SUITES), 'protocol') || contains(fromJSON(env.SUITES), 'transport') ) }}")));
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
    // Actions reads function and context names in any case; the check holds them to one spelling.
    [InlineData("runner.os == 'Windows' && contains(fromJson(env.SUITES), 'transport')")]
    [InlineData("runner.os == 'Windows' && (env.WHOLE_RING == 'true' || contains(fromJSON(ENV.suites), 'transport'))")]
    // A bypass that's there but can't bypass: the whole ring would still skip it unless protocol is selected.
    [InlineData("runner.os == 'Windows' && (env.WHOLE_RING == 'true' || contains(fromJSON(env.SUITES), 'transport')) && contains(fromJSON(env.SUITES), 'protocol')")]
    [InlineData("runner.os == 'Windows' && (github.event_name != 'pull_request' || contains(fromJSON(env.SUITES), 'transport'))")]
    [InlineData("runner.os == 'Windows' && env.WHOLE_RING == 'true' || contains(fromJSON(env.SUITES), 'transport')")]
    // The old main bypass: every push to main would run the step again, whatever its plan selected.
    [InlineData("runner.os == 'Windows' && (github.event_name != 'pull_request' || env.WHOLE_RING == 'true' || contains(fromJSON(env.SUITES), 'transport'))")]
    // Guards that hold nowhere: two OSes, or one no runner has.
    [InlineData("runner.os == 'Windows' && runner.os == 'Linux' && (env.WHOLE_RING == 'true' || contains(fromJSON(env.SUITES), 'transport'))")]
    [InlineData("runner.os == 'Windwos' && (env.WHOLE_RING == 'true' || contains(fromJSON(env.SUITES), 'transport'))")]
    // The plan's suites read straight from needs, not through env.SUITES.
    [InlineData("runner.os == 'Windows' && github.event_name == 'pull_request' && contains(fromJSON(needs.plan.outputs.suites), 'transport')")]
    public void A_suite_gated_step_the_whole_ring_could_skip_or_main_would_always_run_is_reported(string condition)
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
    public void The_plans_suites_reach_steps_only_as_SUITES_bound_to_the_plan()
    {
        Assert.Equal(["ci.yml job product has steps that run when a suite is selected, but its env SUITES isn't ${{ needs.plan.outputs.suites }}"],
            Check(Wired.Replace("SUITES: ${{ needs.plan.outputs.suites }}", "SUITES: '[]'")));
        // The plan's own output must come from the selection, or every selective PR skips the steps.
        Assert.Equal(["ci.yml plan's output suites is ${{ steps.select.outputs.test-projects }}, not ${{ steps.select.outputs.suites }}"],
            Check(Wired.Replace("suites: ${{ steps.select.outputs.suites }}", "suites: ${{ steps.select.outputs.test-projects }}")));
        Assert.Equal(["ci.yml plan has no output suites"],
            Check(Wired.Replace("suites: ${{ steps.select.outputs.suites }}", "")));
        // A step's own env would win over its job's.
        Assert.Equal(["ci.yml job product step \"The NativeAOT transport\" env sets SUITES, which only a job's env may set, as SUITES, from the plan"],
            Check(Wired.Replace("contains(fromJSON(env.SUITES), 'transport'))\n", "contains(fromJSON(env.SUITES), 'transport'))\n        env:\n          SUITES: '[]'\n")
                .Replace("contains(fromJSON(env.SUITES), 'transport'))\r\n", "contains(fromJSON(env.SUITES), 'transport'))\r\n        env:\r\n          SUITES: '[]'\r\n")));
        // An alias would let a step test the suites without the step checks seeing it.
        Assert.Equal(["ci.yml job product env PICKED reads the plan's suites, which steps may read only as SUITES"],
            Check(Wired.Replace("SUITES: ${{ needs.plan.outputs.suites }}", "SUITES: ${{ needs.plan.outputs.suites }}\n      PICKED: ${{ needs['plan'].outputs.suites }}")));
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
    // Skipped on a whole-ring run.
    [InlineData("needs.plan.outputs.whole-ring == 'true' || needs.plan.outputs.ha-integration", "needs.plan.outputs.ha-integration")]
    // The old main bypass: every push to main would run the job again, whatever its plan selected.
    [InlineData("if: needs.plan.outputs.whole-ring == 'true' || needs.plan.outputs.ha-integration", "if: github.event_name != 'pull_request' || needs.plan.outputs.whole-ring == 'true' || needs.plan.outputs.ha-integration")]
    [InlineData("|| needs.plan.outputs.ha-integration == 'true'", "|| needs.plan.outputs.ha-integration == 'true' && needs.plan.outputs.product == 'true'")]
    // A bare read, or the index syntax, without the comparison the gate shape has.
    [InlineData("|| needs.plan.outputs.ha-integration == 'true'", "|| needs.plan.outputs.ha-integration")]
    [InlineData("|| needs.plan.outputs.ha-integration == 'true'", "|| needs['plan'].outputs['ha-integration'] == 'true'")]
    public void A_gated_job_whose_if_isnt_exactly_the_gate_shape_is_reported(string from, string to)
    {
        var problems = Check(Wired.Replace(from, to));

        Assert.Equal(["ci.yml job ha-integration is gated on the plan, but its if: isn't exactly: needs.plan.outputs.whole-ring == 'true' || needs.plan.outputs.ha-integration == 'true'"], problems);
    }

    [Fact]
    public void A_job_reading_the_plan_bare_is_still_held_to_the_plans_jobs_and_outputs()
    {
        var problems = Check(Wired
            .Replace("|| needs.plan.outputs.ha-integration == 'true'", "|| needs.plan.outputs.ha-integration")
            .Replace("JOBS: product ha-integration", "JOBS: product")
            .Replace("ha-integration: ${{ steps.select.outputs.ha-integration }}", ""));

        Assert.Equal([
            "ci.yml job ha-integration is gated on the plan, but its if: isn't exactly: needs.plan.outputs.whole-ring == 'true' || needs.plan.outputs.ha-integration == 'true'",
            "ci.yml job ha-integration is gated on the plan, but the plan's JOBS doesn't list it, so it would never run on a pull request",
            "ci.yml plan has no output ha-integration",
        ], problems);
    }

    [Fact]
    public void A_check_job_missing_from_the_gate_is_reported_whether_or_not_it_reads_the_plan()
    {
        const string Problem = " isn't in quality-gate's needs, so its result wouldn't count (only attest, publish, publish-gate, relay-image-sarif, sign may stay out)";

        Assert.Equal(["ci.yml job ha-integration" + Problem], Check(Wired.Replace("needs: [plan, product, ha-integration]", "needs: [plan, product]")));
        Assert.Equal(["ci.yml job lint" + Problem], Check(Wired.Replace("  quality-gate:", "  lint:\n    runs-on: ubuntu-latest\n  quality-gate:")));
        // Downstream of the gate, it could fail after the gate passed.
        Assert.Equal(["ci.yml job lint" + Problem], Check(Wired.Replace("  quality-gate:", "  lint:\n    needs: quality-gate\n  quality-gate:")));
    }

    [Theory]
    [InlineData("needs: [quality-gate]", "needs: [plan, product, ha-integration]", "ci.yml delivery job publish-gate must need exactly [quality-gate]")]
    [InlineData("&& needs.quality-gate.result == 'success'", "&& needs.product.result == 'success'", "ci.yml publish-gate's if: doesn't require needs.quality-gate.result == 'success' (as one of its top-level && conditions, with no || and no ${{ }} inside)")]
    [InlineData("&& needs.quality-gate.result == 'success'", "&& needs.quality-gate.result == 'success' || always()", "ci.yml publish-gate's if: doesn't require needs.quality-gate.result == 'success' (as one of its top-level && conditions, with no || and no ${{ }} inside)")]
    // Inside a negation it's always true, not required.
    [InlineData("&& needs.quality-gate.result == 'success'", "&& !(false && needs.quality-gate.result == 'success' && false)", "ci.yml publish-gate's if: doesn't require needs.quality-gate.result == 'success' (as one of its top-level && conditions, with no || and no ${{ }} inside)")]
    public void A_publish_gate_that_doesnt_stand_on_quality_gate_is_reported(string from, string to, string problem)
    {
        Assert.Equal([problem], Check(Wired.Replace(from, to)));
    }

    // On main the app job runs only when selected; a run that skipped it must publish nothing.
    [Theory]
    [InlineData(" && needs.quality-gate.outputs.app == 'success'", "", "ci.yml publish-gate's if: doesn't require needs.quality-gate.outputs.app == 'success' (as one of its top-level && conditions), so a run that skipped the app would publish")]
    [InlineData("needs.quality-gate.outputs.app == 'success'", "needs.quality-gate.outputs.app != 'failure'", "ci.yml publish-gate's if: doesn't require needs.quality-gate.outputs.app == 'success' (as one of its top-level && conditions), so a run that skipped the app would publish")]
    [InlineData("needs.quality-gate.outputs.app == 'success'", "!(false && needs.quality-gate.outputs.app == 'success' && false)", "ci.yml publish-gate's if: doesn't require needs.quality-gate.outputs.app == 'success' (as one of its top-level && conditions), so a run that skipped the app would publish")]
    [InlineData("app: ${{ needs.app.result }}", "app: success", "ci.yml quality-gate's output app isn't exactly ${{ needs.app.result }}, so publish-gate can't tell this run built the app")]
    [InlineData("app: ${{ needs.app.result }}", "apps: ${{ needs.app.result }}", "ci.yml quality-gate's output app isn't exactly ${{ needs.app.result }}, so publish-gate can't tell this run built the app")]
    public void A_publish_gate_that_would_publish_a_run_that_skipped_the_app_is_reported(string from, string to, string problem)
    {
        Assert.Equal([problem], Check(Wired.Replace(from, to)));
    }

    [Fact]
    public void A_publish_gate_if_that_mixes_text_and_an_expression_is_reported_since_any_such_string_is_true()
    {
        var problems = Check(Wired.Replace("!cancelled() && github.event_name == 'push'", "${{ !cancelled() && github.event_name == 'push' }}"));

        Assert.Equal(["ci.yml publish-gate's if: doesn't require needs.quality-gate.result == 'success' (as one of its top-level && conditions, with no || and no ${{ }} inside)"], problems);
    }

    [Fact]
    public void Without_publish_gate_nothing_holds_a_release_to_the_gate()
    {
        var problems = Check(Wired[..Wired.IndexOf("  publish-gate:", StringComparison.Ordinal)]);

        Assert.Equal(["ci.yml has no publish-gate job, so nothing holds a release to quality-gate"], problems);
    }

    // Exactly publish-gate's result, not the implicit success(): a check the plan skipped (PWA,
    // Home Assistant) carries its skip down past publish-gate's !cancelled(), so an app build that
    // passed every check it needed would never publish (#938's review).
    [Theory]
    [InlineData("publish", "[publish-gate, sign]", PublishAfterSignIfLine, null)]
    [InlineData("publish", "[publish-gate, sign]", ">-\n      !cancelled()\n      && needs.publish-gate.result == 'success'\n      && needs.sign.result == 'success'", null)]
    [InlineData("publish", "[publish-gate]", PublishAfterSignIfLine, "ci.yml delivery job publish must need exactly [publish-gate, sign]")]
    [InlineData("publish", "[plan]", PublishAfterSignIfLine, "ci.yml delivery job publish must need exactly [publish-gate, sign]")]
    [InlineData("publish", "[publish-gate, sign]", "${{ !cancelled() && needs.publish-gate.result == 'success' }}", PublishIfProblem)]
    [InlineData("publish", "[publish-gate, sign]", "${{ !cancelled() && needs.sign.result == 'success' }}", PublishIfProblem)]
    [InlineData("publish", "[publish-gate, sign]", "github.ref == 'refs/heads/main'", PublishIfProblem)]
    [InlineData("publish", "[publish-gate, sign]", "''", PublishIfProblem)]
    [InlineData("publish", "[publish-gate, sign]", "${{ success() }}", PublishIfProblem)]
    [InlineData("publish", "[publish-gate, sign]", "${{ always() && needs.publish-gate.result == 'success' && needs.sign.result == 'success' }}", PublishIfProblem)]
    [InlineData("publish", "[publish-gate, sign]", "${{ !cancelled() && needs.publish-gate.result == 'success' && needs.sign.result != 'failure' }}", PublishIfProblem)]
    [InlineData("publish", "[publish-gate, sign]", "${{ !cancelled() && needs.publish-gate.result == 'success' && needs.sign.result == 'success' || always() }}", PublishIfProblem)]
    [InlineData("publish", "[publish-gate, sign]", "x ${{ !cancelled() && needs.publish-gate.result == 'success' && needs.sign.result == 'success' }}", PublishIfProblem)]
    // sign holds the signing identity: it too runs only on a publish-gate that succeeded.
    [InlineData("sign", "[publish-gate]", PublishIfLine, null)]
    [InlineData("sign", "[plan]", PublishIfLine, "ci.yml delivery job sign must need exactly [publish-gate]")]
    [InlineData("sign", "[publish-gate]", "${{ success() }}", SignIfProblem)]
    [InlineData("sign", "[publish-gate]", "${{ always() && needs.publish-gate.result == 'success' }}", SignIfProblem)]
    [InlineData("sign", "[publish-gate]", "${{ !cancelled() && needs.publish-gate.result == 'success' || always() }}", SignIfProblem)]
    public void Publish_and_sign_run_only_after_publish_gate_succeeded(string job, string needs, string condition, string? problem)
    {
        var problems = Check(Wired + $"\n  {job}:\n    needs: {needs}\n    if: {condition}\n" + (job == "publish" ? PublishQueued + "\n" : ""));

        Assert.Equal(problem is null ? [] : [problem], problems);
    }

    [Fact]
    public void Publish_without_an_if_is_reported_since_a_skipped_check_would_stop_every_publish()
    {
        var problems = Check(Wired + "\n  publish:\n    needs: [publish-gate, sign]\n" + PublishQueued + "\n");

        Assert.Equal([PublishIfProblem], problems);
    }

    const string PublishIfLine = "${{ !cancelled() && needs.publish-gate.result == 'success' }}";
    const string PublishAfterSignIfLine = "${{ !cancelled() && needs.publish-gate.result == 'success' && needs.sign.result == 'success' }}";
    const string PublishIfProblem = "ci.yml publish's if: isn't exactly !cancelled() && needs.publish-gate.result == 'success' && needs.sign.result == 'success', so it could run without publish-gate succeeding and sign succeeding or never run after a skipped check";
    const string SignIfProblem = "ci.yml sign's if: isn't exactly !cancelled() && needs.publish-gate.result == 'success', so it could run without publish-gate succeeding or never run after a skipped check";

    // publish's concurrency as the repository has it, after its needs and if:.
    const string PublishQueued = """
            concurrency:
              group: publish-prerelease
              cancel-in-progress: false
              queue: max
        """;
    const string Queued = Wired + "\n  publish:\n    needs: [publish-gate, sign]\n    if: " + PublishAfterSignIfLine + "\n" + PublishQueued + "\n";
    const string QueueProblem = "ci.yml publish's concurrency isn't exactly group publish-prerelease, cancel-in-progress false and queue max, so publishes could run at once or a re-run's could replace a newer run's waiting one";
    const string HaIf = "if: needs.plan.outputs.whole-ring == 'true' || needs.plan.outputs.ha-integration == 'true'";
    const string RootCancel = "cancel-in-progress: ${{ github.event_name == 'pull_request' }}";

    // By default the next publish to arrive replaces the one waiting, so a re-run of an old run
    // could leave a newer run's app unpublished (#938's review): publish keeps every waiting one.
    // Elsewhere the newest is meant to replace the one waiting, as main's push runs do in ci-main.
    [Theory]
    [InlineData("queue: max", "queue: single", QueueProblem)]
    [InlineData("queue: max", "x-queue: max", QueueProblem)]
    [InlineData("cancel-in-progress: false", "cancel-in-progress: true", QueueProblem)]
    [InlineData("cancel-in-progress: false", "x-cancel-in-progress: false", QueueProblem)]
    [InlineData("group: publish-prerelease", "group: publish-prerelease-queued", QueueProblem)]
    [InlineData(RootCancel, RootCancel + "\n  queue: max", "ci.yml's concurrency sets queue, but only publish's may: in ci-main the newest push run replaces the one waiting")]
    [InlineData(HaIf, HaIf + "\n    concurrency: { group: ha, queue: max }", "ci.yml job ha-integration's concurrency sets queue, but only publish's may")]
    [InlineData("group: publish-prerelease", "group: 'publish-prerelease'", null)]
    public void Only_publish_queues_and_it_keeps_every_waiting_publish(string from, string to, string? problem)
    {
        Assert.Contains(from, Queued, StringComparison.Ordinal);
        var problems = Check(Queued.Replace(from, to, StringComparison.Ordinal));

        Assert.Equal(problem is null ? [] : [problem], problems);
    }

    [Fact]
    public void A_check_job_renamed_to_a_delivery_job_is_reported_since_delivery_jobs_are_pinned_to_their_place()
    {
        var problems = Check(Wired.Replace("relay-image-sarif:\n    needs: product", "relay-image-sarif:\n    needs: plan").Replace("relay-image-sarif:\r\n    needs: product", "relay-image-sarif:\r\n    needs: plan"));

        Assert.Equal(["ci.yml delivery job relay-image-sarif must need exactly [product]"], problems);
    }

    [Fact]
    public void Products_runners_are_pinned_so_main_and_the_whole_ring_get_every_os()
    {
        Assert.Equal(["ci.yml plan's output product-os isn't ${{ steps.whole-ring.outputs.whole-ring == 'true' && '[\"ubuntu-latest\",\"windows-latest\",\"windows-11-arm\"]' || steps.select.outputs.product-os }}"],
            Check(Wired.Replace("&& '[\"ubuntu-latest\",\"windows-latest\",\"windows-11-arm\"]'", "&& '[\"ubuntu-latest\"]'")));
        // And with Linux alone, the Windows step could never run.
        Assert.Equal(["ci.yml job product's strategy.matrix.os isn't ${{ fromJSON(needs.plan.outputs.product-os) }}", StepProblem],
            Check(Wired.Replace("os: ${{ fromJSON(needs.plan.outputs.product-os) }}", "os: [ubuntu-latest]")));
    }

    [Fact]
    public void A_step_guarded_on_an_os_its_job_never_runs_on_is_reported()
    {
        Assert.Equal([StepProblem], Check(Wired.Replace(StepIf, StepIf.Replace("'Windows'", "'macOS'"))));
    }

    [Fact]
    public void A_quality_gate_that_a_failed_check_would_skip_is_reported()
    {
        var problems = Check(Wired.Replace("if: always()", "if: success()"));

        Assert.Equal(["ci.yml quality-gate's if: isn't always(), so a failed check would skip the gate, and a skipped required check passes"], problems);
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
