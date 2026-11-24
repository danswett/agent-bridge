using YamlDotNet.RepresentationModel;
using Xunit;

namespace AgentBridge.Quality.Tests;

// ci.yml's `app` job on win-arm64 (owner rule: ARM64 never gates, but both packages are required, so an Arm64 leg may be red
// and never unpackaged). A self-test that fails twice must not skip the package, installer input or SBOM uploads: publish-gate
// and app-installer need them. The failure is recorded as a step of its own after them, so the job still ends red.
public sealed class CiAppJobTests
{
    static List<YamlMappingNode> AppSteps()
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(File.ReadAllText(Path.Combine(ChangedFiles.RepoRoot(AppContext.BaseDirectory), ".github", "workflows", "ci.yml"))));
        var jobs = (YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode).Children[new YamlScalarNode("jobs")];
        var app = (YamlMappingNode)jobs.Children[new YamlScalarNode("app")];
        return ((YamlSequenceNode)app.Children[new YamlScalarNode("steps")]).Children.Cast<YamlMappingNode>().ToList();
    }

    static string? Field(YamlMappingNode step, string key) => step.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value.ToString() : null;

    [Fact]
    public void A_win_arm64_self_test_that_fails_twice_is_recorded_after_the_package_and_the_sbom_are_uploaded()
    {
        var steps = AppSteps();
        var retry = steps.Single(s => Field(s, "id") == "selftest-retry");
        var sbomUpload = steps.FindLastIndex(s => Field(s, "name") is null && Field(s, "uses")?.StartsWith("actions/upload-artifact", StringComparison.Ordinal) == true
            && (s.Children[new YamlScalarNode("with")] as YamlMappingNode)?.Children[new YamlScalarNode("name")].ToString().StartsWith("sbom-", StringComparison.Ordinal) == true);
        var recorded = steps.FindIndex(s => Field(s, "if") == "steps.selftest-retry.outcome == 'failure'");

        Assert.Equal("true", Field(retry, "continue-on-error"));
        Assert.True(sbomUpload > steps.IndexOf(retry), "the SBOM upload comes after the retry");
        Assert.True(recorded > sbomUpload, "the failure is recorded after the package, installer input and SBOM uploads");
        Assert.Contains("exit 1", Field(steps[recorded], "run"), StringComparison.Ordinal);
        // The flake notice means the retry PASSED: with continue-on-error, success() is true even when it failed.
        Assert.Contains("steps.selftest-retry.outcome == 'success'", Field(steps.Single(s => Field(s, "name")?.StartsWith("The win-arm64 self-test passed on retry", StringComparison.Ordinal) == true), "if"), StringComparison.Ordinal);
    }
}
