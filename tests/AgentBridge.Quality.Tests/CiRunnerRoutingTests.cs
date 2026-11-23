using Xunit;

namespace AgentBridge.Quality.Tests;

// agent-bridge is a public repository and the ab-ci runners persist (quality/README.md, "CI: speed, runners and what
// gates main"): a self-hosted job may run only for a push or a pull request whose head is this repository, and never
// for a fork or a pull_request_target. A workflow that routes to them without that guard fails here, so the guard
// cannot be dropped by a later edit.
public sealed class CiRunnerRoutingTests
{
    const string Guard = "vars.AB_CI_SELF_HOSTED == 'true' && (github.event_name == 'push' || github.event.pull_request.head.repo.full_name == github.repository)";

    static string Workflow(string runsOn, string triggers = "on: [push, pull_request]", string extra = "") => $$"""
        name: ci
        {{triggers}}
        jobs:
          product:
            runs-on: {{runsOn}}
            {{extra}}
            steps:
              - run: echo hi
        """;

    [Fact]
    public void A_self_hosted_job_with_the_whole_guard_is_allowed()
    {
        var yaml = Workflow($"${{{{ {Guard} && fromJSON('[\"self-hosted\",\"windows\",\"x64\",\"ab-ci\"]') || 'windows-latest' }}}}");

        Assert.Empty(RunnerRouting.Violations(yaml, "ci.yml"));
    }

    [Theory]
    [InlineData("[self-hosted, windows, x64, ab-ci]")]
    [InlineData("ab-ci")]
    [InlineData("${{ vars.AB_CI_SELF_HOSTED == 'true' && 'ab-ci' || 'windows-latest' }}")]
    public void A_self_hosted_job_without_the_guard_is_a_violation_that_names_the_job(string runsOn)
    {
        var violations = RunnerRouting.Violations(Workflow(runsOn), "ci.yml");

        var one = Assert.Single(violations);
        Assert.Contains("ci.yml", one);
        Assert.Contains("job product", one);
    }

    [Fact]
    public void A_guard_that_checks_the_event_but_not_the_head_repository_is_a_violation()
    {
        var yaml = Workflow("${{ github.event_name == 'push' && 'ab-ci' || 'windows-latest' }}");

        Assert.NotEmpty(RunnerRouting.Violations(yaml, "ci.yml"));
    }

    [Fact]
    public void A_workflow_that_can_run_for_pull_request_target_never_has_a_self_hosted_job_even_guarded()
    {
        var yaml = Workflow($"${{{{ {Guard} && 'ab-ci' || 'windows-latest' }}}}", triggers: "on: [push, pull_request_target]");

        var violations = RunnerRouting.Violations(yaml, "ci.yml");

        Assert.Contains(violations, v => v.Contains("pull_request_target"));
    }

    [Fact]
    public void A_matrix_chosen_runner_is_held_to_the_guard_when_the_workflow_names_ab_ci_anywhere()
    {
        var yaml = """
            name: ci
            on: [push, pull_request]
            jobs:
              plan:
                runs-on: ubuntu-latest
                outputs:
                  os: ${{ steps.p.outputs.os }}
                steps:
                  - id: p
                    run: echo "os=[\"ab-ci\"]" >> "$GITHUB_OUTPUT"
              product:
                needs: plan
                strategy:
                  matrix:
                    os: ${{ fromJSON(needs.plan.outputs.os) }}
                runs-on: ${{ matrix.os }}
                steps:
                  - run: echo hi
            """;

        var violations = RunnerRouting.Violations(yaml, "ci.yml");

        Assert.Contains(violations, v => v.Contains("job product"));
    }

    [Fact]
    public void Workflows_that_only_use_hosted_runners_are_untouched_whatever_their_triggers()
    {
        Assert.Empty(RunnerRouting.Violations(Workflow("windows-latest", triggers: "on: [pull_request_target]"), "x.yml"));
        Assert.Empty(RunnerRouting.Violations(Workflow("${{ matrix.os }}", extra: "strategy: { matrix: { os: [ubuntu-latest, windows-latest] } }"), "x.yml"));
    }

    [Fact]
    public void Every_workflow_in_the_repository_passes()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx"))) { root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("No AgentBridge.slnx above the test's output."); }

        var violations = Directory.EnumerateFiles(Path.Combine(root, ".github", "workflows"), "*.yml")
            .SelectMany(file => RunnerRouting.Violations(File.ReadAllText(file), Path.GetFileName(file))).ToList();

        Assert.Empty(violations);
    }
}
