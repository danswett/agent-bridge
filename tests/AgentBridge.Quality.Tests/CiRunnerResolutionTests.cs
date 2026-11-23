using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace AgentBridge.Quality.Tests;

// Fail closed (R8b, Quality B221/B222): a runner is hosted only when every value it can take resolves to a known
// GitHub-hosted label. A matrix counts when its values (include: too) are literals, or built from literals alone,
// or it is the one named key, ci.yml product's fromJSON(needs.plan.outputs.product-os), whose values this file pins.
public sealed partial class CiRunnerResolutionTests
{
    const string Guard = "vars.AB_CI_SELF_HOSTED == 'true' && (github.event_name == 'push' || github.event.pull_request.head.repo.full_name == github.repository)";
    const string ProductMatrix = "${{ fromJSON(needs.plan.outputs.product-os) }}";

    static string Workflow(string runsOn, string matrix = "", string job = "product")
    {
        var strategy = matrix.Length > 0 ? "strategy: { matrix: " + matrix + " }" : "";
        return $$"""
            name: ci
            on: [push, pull_request]
            jobs:
              {{job}}:
                {{strategy}}
                runs-on: {{runsOn}}
                steps:
                  - run: echo hi
            """;
    }

    static IReadOnlyList<string> Check(string yaml, string file = "ci.yml") => RunnerRouting.Violations(yaml, file);

    [Theory]
    [InlineData("ubuntu-24.04")]
    [InlineData("windows-11-arm")]
    [InlineData("windows-2025")]
    [InlineData("macos-15")]
    [InlineData("[ubuntu-latest]")]
    [InlineData("${{ github.event_name == 'push' && 'windows-latest' || 'ubuntu-latest' }}")]
    [InlineData("${{ fromJSON('[\"ubuntu-latest\"]') }}")]
    public void Known_hosted_labels_pass(string runsOn) => Assert.Empty(Check(Workflow(runsOn)));

    [Theory]
    [InlineData("my-runner")]
    [InlineData("[windows, x64]")]
    // An allowlist of hosted forms, not "windows-anything": a self-hosted pool can carry labels like these.
    [InlineData("windows-x64")]
    [InlineData("windows-gpu")]
    [InlineData("${{ github.event_name == 'push' && 'my-runner' || 'ubuntu-latest' }}")]
    [InlineData("${{ fromJSON('[\"my-runner\"]') }}")]
    [InlineData("${{ format('{0}-latest', 'ubuntu') }}")]
    public void An_unknown_label_or_a_value_this_check_cannot_resolve_fails_closed(string runsOn) => Assert.NotEmpty(Check(Workflow(runsOn)));

    [Theory]
    [InlineData("{ os: [ubuntu-latest, windows-latest] }", true)]
    [InlineData("{ os: [ubuntu-latest, ab-ci] }", false)]
    [InlineData("{ os: [ubuntu-latest, my-runner] }", false)]
    [InlineData("{ os: [ubuntu-latest], include: [{ os: ab-ci }] }", false)]
    [InlineData("{ include: [{ os: windows-latest }, { os: ubuntu-latest, extra: 1 }] }", true)]
    [InlineData("{ os: '${{ fromJSON(needs.other.outputs.os) }}' }", false)]
    [InlineData("{ include: \"${{ fromJSON(format('[{0}]', '{\\\"os\\\":\\\"windows-latest\\\"}')) }}\" }", true)]
    [InlineData("{ include: \"${{ fromJSON(format('[{0}]', vars.ROW)) }}\" }", false)]
    // B226: any read of something the workflow doesn't hold makes the matrix self-hosted-capable.
    [InlineData("{ include: \"${{ fromJSON(inputs.rows) }}\" }", false)]
    [InlineData("{ include: \"${{ fromJSON(github.event.inputs.rows) }}\" }", false)]
    [InlineData("{ include: \"${{ fromJSON(env.ROWS) }}\" }", false)]
    [InlineData("{ os: ['${{ secrets.RUNNER }}'] }", false)]
    public void A_matrix_runner_is_hosted_only_when_every_value_resolves(string matrix, bool hosted)
        => Assert.Equal(hosted, Check(Workflow("${{ matrix.os }}", matrix)).Count == 0);

    [Fact]
    public void Only_ci_yml_product_reads_the_named_product_os_key_as_hosted()
    {
        var matrix = "{ os: '" + ProductMatrix + "' }";
        Assert.Empty(Check(Workflow("${{ matrix.os }}", matrix)));
        Assert.NotEmpty(Check(Workflow("${{ matrix.os }}", matrix, job: "other")));
        Assert.NotEmpty(Check(Workflow("${{ matrix.os }}", matrix), "perf.yml"));
    }

    [Theory]
    // The self-hosted value is a literal label or fromJSON of a literal list, nothing computed.
    [InlineData("${{ " + Guard + " && vars.POOL || 'windows-latest' }}")]
    [InlineData("${{ " + Guard + " && format('{0}', 'ab-ci') || 'windows-latest' }}")]
    [InlineData("${{ " + Guard + " && matrix.os || 'windows-latest' }}")]
    public void The_pool_value_must_be_a_literal(string runsOn) => Assert.NotEmpty(Check(Workflow(runsOn, "{ os: [windows-latest, ab-ci] }")));

    // The named key's chain: ci.yml product's matrix and plan's product-os binding are pinned by CiPlan; the binding's
    // literals are CiPlan.ProductRunners, and the rest comes from plan.ps1's $runners map. All of it must be hosted.
    [Fact]
    public void The_product_os_values_are_the_hosted_allowlist()
    {
        string[] allowlist = ["ubuntu-latest", "windows-latest", "windows-11-arm"];
        Assert.Equal(allowlist, RunnerRouting.ProductRunners);
        var ciPlan = (string[])typeof(RunnerRouting).Assembly.GetType("AgentBridge.Quality.CiPlan")!
            .GetField("ProductRunners", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.Equal(allowlist, ciPlan);

        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx"))) { root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("No AgentBridge.slnx above the test's output."); }
        var plan = File.ReadAllText(Path.Combine(root, "quality", "plan.ps1"));
        var map = RunnersMap().Match(plan);
        Assert.True(map.Success, "plan.ps1 has no $runners = [ordered]@{ ... } map");
        Assert.Equal(allowlist, MapValue().Matches(map.Groups[1].Value).Select(m => m.Groups[1].Value));
        // Every value product-os takes comes out of that map: no literal runner anywhere it is assigned.
        var assignments = plan.Split('\n').Where(l => l.TrimStart().StartsWith("$productOs =", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(assignments);
        Assert.All(assignments, l => Assert.True(l.Contains("$runners[$_]", StringComparison.Ordinal) && !l.Contains('\'') && !l.Contains('"'), l));
    }

    [GeneratedRegex(@"\$runners\s*=\s*\[ordered\]@\{([^}]*)\}")]
    private static partial Regex RunnersMap();
    [GeneratedRegex(@"=\s*'([^']*)'")]
    private static partial Regex MapValue();
}
