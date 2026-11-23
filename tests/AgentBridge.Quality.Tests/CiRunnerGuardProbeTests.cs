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
    // The flag missing, the origin test missing, weakened, negated or widened.
    [InlineData("${{ " + Origin + " && 'ab-ci' || 'windows-latest' }}")]
    [InlineData("${{ " + Flag + " && 'ab-ci' || 'windows-latest' }}")]
    [InlineData("${{ (" + Guard + " || true) && 'ab-ci' || 'windows-latest' }}")]
    [InlineData("${{ !(" + Guard + ") && 'ab-ci' || 'windows-latest' }}")]
    [InlineData("${{ " + Flag + " && (github.event_name == 'push' || github.event_name == 'pull_request') && 'ab-ci' || 'windows-latest' }}")]
    [InlineData("${{ vars.AB_CI_SELF_HOSTED != 'false' && " + Origin + " && 'ab-ci' || 'windows-latest' }}")]
    // A runner named by something the workflow doesn't hold: a variable, an input, the event.
    [InlineData("${{ vars.RUNNER }}")]
    [InlineData("${{ inputs.runner }}")]
    [InlineData("${{ github.event.pull_request.head.ref }}")]
    [InlineData("${{ format('{0}', vars.RUNNER) }}")]
    // Text around an expression, which this check can't follow.
    [InlineData("ab-ci-${{ github.run_id }}")]
    public void A_runner_that_can_be_self_hosted_outside_the_whole_guard_is_a_violation(string runsOn)
    {
        var one = Assert.Single(Check(Workflow(runsOn)));
        Assert.Contains("job product", one);
    }

    [Fact]
    public void A_matrix_that_lists_the_pool_is_held_to_the_guard_in_the_fallback_too()
    {
        const string strategy = "strategy: { matrix: { os: [windows-latest, ab-ci] } }";

        Assert.Empty(Check(Workflow("${{ " + Guard + " && matrix.os || 'windows-latest' }}", strategy: strategy)));
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
    // A runner group is a self-hosted (or larger-runner) pool with no hosted fallback the guard could choose.
    [InlineData("{ group: ab-ci }")]
    [InlineData("{ group: ab-ci, labels: [windows] }")]
    [InlineData("{ labels: [self-hosted, windows] }")]
    [InlineData("[self-hosted, windows, x64, ab-ci]")]
    public void Runner_groups_and_bare_self_hosted_labels_are_violations(string runsOn)
        => Assert.NotEmpty(Check(Workflow(runsOn)));

    [Theory]
    [InlineData("${{ (github.event_name == 'push' && vars.APP_SIGNING == 'true') && 'windows-latest' || 'ubuntu-latest' }}", "on: [push, pull_request_target]")]
    [InlineData("${{ matrix.os }}", "on: [workflow_run]")]
    public void Hosted_only_expressions_pass_whatever_the_triggers(string runsOn, string triggers)
        => Assert.Empty(Check(Workflow(runsOn, triggers, "strategy: { matrix: { os: [ubuntu-latest, windows-latest] } }")));
}
