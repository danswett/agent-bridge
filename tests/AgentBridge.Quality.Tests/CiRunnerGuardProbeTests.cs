using Xunit;

namespace AgentBridge.Quality.Tests;

// The ab-ci guard read as an expression, not as two substrings (quality/README.md, "CI: speed, runners and what gates
// main"): every value a job's runs-on can resolve to that may name a self-hosted runner has to sit behind both whole
// conjuncts of the guard, the variable and the push-or-same-repository test. Each probe below once passed the
// substring check.
public sealed class CiRunnerGuardProbeTests
{
    const string Flag = "vars.AB_CI_SELF_HOSTED == 'true'";
    const string Origin = "(github.event_name == 'push' || github.event.pull_request.head.repo.full_name == github.repository)";
    const string Guard = Flag + " && " + Origin;
    const string Pool = "fromJSON('[\"self-hosted\",\"windows\",\"x64\",\"ab-ci\"]')";

    static string Workflow(string runsOn, string triggers = "on: [push, pull_request]", string strategy = "") => $$"""
        name: ci
        {{triggers}}
        jobs:
          product:
            {{strategy}}
            runs-on: {{runsOn}}
            steps:
              - run: echo hi
        """;

    static IReadOnlyList<string> Check(string yaml) => RunnerRouting.Violations(yaml, "ci.yml");

    [Fact]
    public void The_product_line_proposed_for_ci_yml_is_allowed()
    {
        // docs/design/proposals/ci-per-sha-and-ab-ci-routing.diff, as ci.yml would carry it.
        var runsOn = "${{ (matrix.os == 'windows-latest' && vars.AB_CI_SELF_HOSTED == 'true' && (github.event_name == 'push' || github.event.pull_request.head.repo.full_name == github.repository)) && fromJSON('[\"self-hosted\",\"windows\",\"x64\",\"ab-ci\"]') || matrix.os }}";

        Assert.Empty(Check(Workflow(runsOn, strategy: "strategy: { matrix: { os: [ubuntu-latest, windows-latest, windows-11-arm] } }")));
    }

    [Theory]
    [InlineData("${{ ( github.event_name=='push' ||  github.event.pull_request.head.repo.full_name == github.repository ) && vars.AB_CI_SELF_HOSTED=='true' && 'ab-ci' || 'windows-latest' }}")]
    [InlineData("${{ github.ref == 'refs/heads/main' && " + Guard + " && 'ab-ci' || 'windows-latest' }}")]
    public void Spacing_order_and_extra_narrowing_conjuncts_do_not_matter(string runsOn) => Assert.Empty(Check(Workflow(runsOn)));

    [Theory]
    // Swapped arms: the guard picks the hosted label, and the pool is the fallback.
    [InlineData("${{ " + Guard + " && 'windows-latest' || 'ab-ci' }}")]
    // An or-chain that falls through to the pool, or to a value nothing checks.
    [InlineData("${{ " + Guard + " && 'ab-ci' || 'windows-latest' || 'ab-ci' }}")]
    [InlineData("${{ " + Guard + " && 'ab-ci' || vars.RUNNER }}")]
    [InlineData("${{ " + Guard + " || 'ab-ci' }}")]
    [InlineData("${{ " + Origin + " && 'ab-ci' || 'windows-latest' }}")]
    [InlineData("${{ " + Flag + " && 'ab-ci' || 'windows-latest' }}")]
    [InlineData("${{ (" + Guard + " || true) && 'ab-ci' || 'windows-latest' }}")]
    [InlineData("${{ !(" + Guard + ") && 'ab-ci' || 'windows-latest' }}")]
    [InlineData("${{ " + Flag + " && (github.event_name == 'push' || github.event_name == 'pull_request') && 'ab-ci' || 'windows-latest' }}")]
    [InlineData("${{ vars.AB_CI_SELF_HOSTED != 'false' && " + Origin + " && 'ab-ci' || 'windows-latest' }}")]
    [InlineData("${{ vars.RUNNER }}")]
    [InlineData("${{ inputs.runner }}")]
    [InlineData("${{ github.event.pull_request.head.ref }}")]
    [InlineData("${{ format('{0}', vars.RUNNER) }}")]
    [InlineData("ab-ci-${{ github.run_id }}")]
    // B216's shapes: the pool in both arms, the guard folded into format(), a second runner entry.
    [InlineData("${{ " + Guard + " && 'ab-ci' || 'ab-ci' }}")]
    [InlineData("${{ format('{0}', " + Guard + ") && 'ab-ci' || 'windows-latest' }}")]
    [InlineData("${{ format('{0}', " + Guard + " && 'ab-ci') || 'windows-latest' }}")]
    [InlineData("[\"${{ " + Guard + " && 'ab-ci' || 'windows-latest' }}\", self-hosted]")]
    // R8b: the whole runs-on is GUARD && <self-hosted> || <hosted>; narrowing && conditions in front are allowed (the
    // product line's matrix.os test), another ||, a second self-hosted arm or no hosted fallback is not.
    [InlineData("${{ " + Guard + " && 'ab-ci' || 'windows-latest' || 'ubuntu-latest' }}")]
    [InlineData("${{ " + Guard + " && 'ab-ci' }}")]
    [InlineData("${{ " + Guard + " && 'ab-ci' || " + Guard + " && 'self-hosted' || 'windows-latest' }}")]
    [InlineData("${{ " + Guard + " && 'ab-ci' || " + Guard + " && 'windows-latest' || 'ubuntu-latest' }}")]
    [InlineData("${{ 'windows-latest' || " + Guard + " && 'ab-ci' }}")]
    // Runner groups (a pool with no hosted fallback) and bare self-hosted labels.
    [InlineData("{ group: ab-ci }")]
    [InlineData("{ group: ab-ci, labels: [windows] }")]
    [InlineData("{ labels: [self-hosted, windows] }")]
    [InlineData("[self-hosted, windows, x64, ab-ci]")]
    public void A_runner_that_can_be_self_hosted_outside_the_whole_guard_is_a_violation(string runsOn)
    {
        var one = Assert.Single(Check(Workflow(runsOn)));
        Assert.Contains("job product", one);
    }

    [Fact]
    public void A_second_runs_on_key_is_a_violation_not_a_crash()
    {
        var yaml = Workflow("${{ " + Guard + " && 'ab-ci' || 'windows-latest' }}").Replace("    steps:", "    runs-on: ab-ci\n    steps:", StringComparison.Ordinal);

        Assert.NotEmpty(Check(yaml));
    }

    static string Caller(string runner, string triggers = "on: [push, pull_request]") => $$"""
        name: caller
        {{triggers}}
        jobs:
          build:
            uses: ./.github/workflows/build.yml
            with:
              runner: {{runner}}
        """;

    [Theory]
    // A caller's other inputs (a sha, a version) aren't runners: only one naming the pool is held to the guard here. A
    // runner handed over any other way (vars.RUNNER) reaches the callee as inputs.runner, which the callee must guard.
    [InlineData("ab-ci")]
    [InlineData("[self-hosted, windows]")]
    [InlineData("${{ " + Guard + " && 'windows-latest' || 'ab-ci' }}")]
    public void A_reusable_workflow_caller_that_hands_over_the_pool_unguarded_is_a_violation(string runner)
        => Assert.Contains(Check(Caller(runner)), v => v.Contains("job build", StringComparison.Ordinal));

    [Fact]
    public void A_reusable_workflow_caller_and_callee_that_both_guard_are_allowed_unless_the_caller_is_privileged()
    {
        var guarded = "${{ " + Guard + " && 'ab-ci' || 'windows-latest' }}";
        Assert.Empty(Check(Caller(guarded)));
        Assert.Empty(Check(Caller("windows-latest")));
        Assert.Contains(Check(Caller(guarded, "on: [pull_request_target]")), v => v.Contains("pull_request_target", StringComparison.Ordinal));

        // The callee: a pool handed over as an input is never a literal (R8b), so even guarded it fails. A callee that
        // wants the pool names it itself.
        var callee = Workflow("${{ " + Guard + " && inputs.runner || 'windows-latest' }}", triggers: "on: { workflow_call: { inputs: { runner: { type: string } } } }");
        Assert.NotEmpty(Check(callee));
        Assert.Empty(Check(Workflow(guarded, triggers: "on: { workflow_call: {} }")));
        Assert.NotEmpty(Check(Workflow("${{ inputs.runner }}", triggers: "on: { workflow_call: {} }")));
    }

    [Fact]
    public void A_matrix_that_lists_the_pool_is_held_to_the_guard_in_the_fallback_too()
    {
        const string strategy = "strategy: { matrix: { os: [windows-latest, ab-ci] } }";

        // The pool value is a literal (R8b): a matrix that may hold the pool is never it, even guarded.
        Assert.NotEmpty(Check(Workflow("${{ " + Guard + " && matrix.os || 'windows-latest' }}", strategy: strategy)));
        Assert.Single(Check(Workflow("${{ " + Guard + " && " + Pool + " || matrix.os }}", strategy: strategy)));
    }

    [Theory]
    [InlineData("pull_request_target")]
    [InlineData("workflow_run")]
    public void A_privileged_trigger_never_shares_a_workflow_with_a_self_hosted_job_even_guarded(string trigger)
    {
        var violations = Check(Workflow("${{ " + Guard + " && 'ab-ci' || 'windows-latest' }}", triggers: $"on: {{ push: {{}}, {trigger}: {{}} }}"));

        Assert.Contains(violations, v => v.Contains(trigger, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("${{ (github.event_name == 'push' && vars.APP_SIGNING == 'true') && 'windows-latest' || 'ubuntu-latest' }}", "on: [push, pull_request_target]")]
    [InlineData("${{ matrix.os }}", "on: [workflow_run]")]
    public void Hosted_only_expressions_pass_whatever_the_triggers(string runsOn, string triggers)
        => Assert.Empty(Check(Workflow(runsOn, triggers, "strategy: { matrix: { os: [ubuntu-latest, windows-latest] } }")));
}
