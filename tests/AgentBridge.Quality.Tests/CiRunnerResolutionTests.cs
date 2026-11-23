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

    static string Workflow(string runsOn, string matrix = "", string job = "product", string triggers = "on: [push, pull_request]")
    {
        var strategy = matrix.Length > 0 ? "strategy: { matrix: " + matrix + " }" : "";
        return $$"""
            name: ci
            {{triggers}}
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
    // B421 P2-1: a built include is parsed, not scraped. GitHub reads keys case-insensitively and JSON decodes escapes,
    // so an array or object value, an "OS" key or an escaped "os" key is read too, and an unreadable row fails.
    [InlineData("{ os: [ubuntu-latest], include: \"${{ fromJSON('[{\\\"os\\\":[\\\"ab-ci\\\"]}]') }}\" }", false)]
    [InlineData("{ os: [ubuntu-latest], include: \"${{ fromJSON('[{\\\"os\\\":{\\\"x\\\":\\\"ab-ci\\\"}}]') }}\" }", false)]
    [InlineData("{ os: [ubuntu-latest], include: \"${{ fromJSON('[{\\\"OS\\\":\\\"ab-ci\\\"}]') }}\" }", false)]
    [InlineData("{ os: [ubuntu-latest], include: \"${{ fromJSON('[{\\\"\\\\u006fs\\\":\\\"ab-ci\\\"}]') }}\" }", false)]
    [InlineData("{ os: [ubuntu-latest], include: \"${{ fromJSON('[{\\\"OS\\\":\\\"windows-latest\\\"}]') }}\" }", true)]
    [InlineData("{ os: [ubuntu-latest], include: \"${{ fromJSON('not json') }}\" }", false)]
    [InlineData("{ os: [ubuntu-latest], include: [{ OS: ab-ci }] }", false)]
    [InlineData("{ os: [ubuntu-latest], OS: [ab-ci] }", false)]
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

    // R8b: a run by hand on main with use_ab_ci may take the pool before AB_CI_SELF_HOSTED flips, as the one alternative
    // to the guard, exactly: (guard) || dispatch on main && inputs.use_ab_ci.
    const string Dispatch = "github.event_name == 'workflow_dispatch' && github.ref == 'refs/heads/main' && inputs.use_ab_ci";

    [Theory]
    [InlineData("${{ (" + Guard + " || " + Dispatch + ") && 'ab-ci' || 'windows-latest' }}", true)]
    [InlineData("${{ (matrix.os == 'windows-latest' && (" + Guard + " || " + Dispatch + ")) && fromJSON('[\"self-hosted\",\"ab-ci\"]') || 'windows-latest' }}", true)]
    [InlineData("${{ " + Dispatch + " && 'ab-ci' || 'windows-latest' }}", false)]
    [InlineData("${{ (" + Guard + " || github.event_name == 'workflow_dispatch' && inputs.use_ab_ci) && 'ab-ci' || 'windows-latest' }}", false)]
    [InlineData("${{ (" + Guard + " || github.event_name == 'workflow_dispatch' && github.ref == 'refs/heads/main') && 'ab-ci' || 'windows-latest' }}", false)]
    [InlineData("${{ (" + Guard + " || inputs.use_ab_ci) && 'ab-ci' || 'windows-latest' }}", false)]
    [InlineData("${{ (" + Guard + " || " + Dispatch + " || true) && 'ab-ci' || 'windows-latest' }}", false)]
    public void A_dispatch_on_main_with_use_ab_ci_is_the_one_alternative_to_the_guard(string runsOn, bool allowed)
        => Assert.Equal(allowed, Check(Workflow(runsOn, triggers: BooleanInput)).Count == 0);

    const string BooleanInput = "on: { push: {}, pull_request: {}, workflow_dispatch: { inputs: { use_ab_ci: { type: boolean, default: false } } } }";

    // B421 P3: use_ab_ci must be a boolean input. As a string, 'false' is non-empty and so true in an expression: a run by
    // hand that says no would still take the pool. Undeclared, it is always empty, and the alternative is a typo.
    [Theory]
    [InlineData(BooleanInput, true)]
    [InlineData("on: { push: {}, workflow_dispatch: { inputs: { use_ab_ci: { type: string } } } }", false)]
    [InlineData("on: { push: {}, workflow_dispatch: { inputs: { use_ab_ci: { default: false } } } }", false)]
    [InlineData("on: { push: {}, workflow_dispatch: {} }", false)]
    [InlineData("on: [push, pull_request]", false)]
    public void Use_ab_ci_must_be_a_boolean_workflow_dispatch_input(string triggers, bool allowed)
        => Assert.Equal(allowed, Check(Workflow("${{ (" + Guard + " || " + Dispatch + ") && 'ab-ci' || 'windows-latest' }}", triggers: triggers)).Count == 0);

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
        // Every value product-os takes comes out of that map: no literal runner anywhere it is written, and the output
        // is exactly the variable (B421 P2-2).
        Assert.Empty(ProductOsProblems(plan));
    }

    // Every write to $productOs, wherever it sits (an if, a +=), must take its values from $runners[$_] alone, and
    // $outputs['product-os'] must be ConvertTo-JsonArray $productOs.
    internal static List<string> ProductOsProblems(string plan)
    {
        var problems = new List<string>();
        var writes = ProductOsWrite().Matches(plan).Select(m => m.Value.Trim()).ToList();
        if (writes.Count == 0) { problems.Add("no write to $productOs"); }
        problems.AddRange(writes.Where(w => !w.Contains("$runners[$_]", StringComparison.Ordinal) || w.Contains('\'') || w.Contains('"') || w.Contains("+=", StringComparison.Ordinal)));
        var outputs = ProductOsOutput().Matches(plan).Select(m => m.Groups[1].Value.Trim()).ToList();
        if (outputs.Count != 1 || outputs[0] != "ConvertTo-JsonArray $productOs") { problems.Add($"$outputs['product-os'] is written {outputs.Count} time(s): {string.Join(" | ", outputs)}"); }
        return problems;
    }

    [Theory]
    [InlineData("$productOs = @($runners.Keys | ForEach-Object { $runners[$_] })\nif ($x) { $productOs += 'my-runner' }\n$outputs['product-os'] = ConvertTo-JsonArray $productOs")]
    [InlineData("$productOs = @($runners.Keys | ForEach-Object { $runners[$_] })\nif ($x) { $productOs = @('ab-ci') }\n$outputs['product-os'] = ConvertTo-JsonArray $productOs")]
    [InlineData("$productOs = @($runners.Keys | ForEach-Object { $runners[$_] })\n$outputs['product-os'] = '[\"ab-ci\"]'")]
    [InlineData("$productOs = @($runners.Keys | ForEach-Object { $runners[$_] })\n$outputs['product-os'] = ConvertTo-JsonArray $productOs\n$outputs['product-os'] = '[]'")]
    public void A_product_os_write_or_output_from_anything_but_the_map_is_caught(string plan) => Assert.NotEmpty(ProductOsProblems(plan.Replace("\\n", "\n")));

    [GeneratedRegex(@"\$productOs\s*\+?=(?!=)[^\n]*")]
    private static partial Regex ProductOsWrite();
    [GeneratedRegex(@"\$outputs\[\s*'product-os'\s*\]\s*=([^\n]*)")]
    private static partial Regex ProductOsOutput();

    [GeneratedRegex(@"\$runners\s*=\s*\[ordered\]@\{([^}]*)\}")]
    private static partial Regex RunnersMap();
    [GeneratedRegex(@"=\s*'([^']*)'")]
    private static partial Regex MapValue();
}
