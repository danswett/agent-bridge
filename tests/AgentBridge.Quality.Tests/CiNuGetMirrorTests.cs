using YamlDotNet.RepresentationModel;

namespace AgentBridge.Quality.Tests;

// ci.yml product's NuGet mirror (#4218): only a self-hosted (ab-ci) leg restores through the runner's mirror, with
// the config quality/nuget-mirror.ps1 writes (NuGetMirrorTests); every hosted leg's restore is as before, and a
// self-hosted leg's NuGet packages never share a cache key with the hosted Windows leg's.
public class CiNuGetMirrorTests
{
    static YamlMappingNode Jobs()
    {
        var path = Path.Combine(ChangedFiles.RepoRoot(AppContext.BaseDirectory), ".github", "workflows", "ci.yml");
        var stream = new YamlStream();
        stream.Load(new StringReader(File.ReadAllText(path)));
        return (YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode).Children[new YamlScalarNode("jobs")];
    }

    static string? Scalar(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar ? scalar.Value : null;

    static List<YamlMappingNode> Steps(YamlMappingNode job)
        => job.Children.TryGetValue(new YamlScalarNode("steps"), out var steps) && steps is YamlSequenceNode list
            ? list.Children.OfType<YamlMappingNode>().ToList() : [];

    [Fact]
    public void Only_a_self_hosted_product_step_reads_the_runners_mirror_and_it_writes_the_tested_config()
    {
        var readers = Jobs().Children
            .SelectMany(job => Steps((YamlMappingNode)job.Value).Select(step => (Job: ((YamlScalarNode)job.Key).Value, Step: step)))
            .Where(s => (Scalar(s.Step, "run") ?? "").Contains("AGENT_BRIDGE_NUGET_SOURCE", StringComparison.Ordinal))
            .ToList();

        var (job, step) = Assert.Single(readers);
        Assert.Equal("product", job);
        Assert.Equal("runner.environment == 'self-hosted'", Scalar(step, "if"));
        Assert.Contains("New-AbCiNuGetConfig", Scalar(step, "run"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_hosted_tool_restore_is_unchanged_and_the_nuget_cache_key_is_per_runner_kind()
    {
        var steps = Steps((YamlMappingNode)Jobs().Children[new YamlScalarNode("product")]);

        // Hosted: the same command and shell as before; its condition only adds what is always true on a hosted runner.
        var hosted = Assert.Single(steps, s => Scalar(s, "run") == "dotnet tool restore");
        Assert.Null(Scalar(hosted, "shell"));
        Assert.Equal("env.DOTNET_TARGET != '' && env.NUGET_MIRROR_CONFIG == ''", Scalar(hosted, "if"));
        var selfHosted = Assert.Single(steps, s => (Scalar(s, "run") ?? "").StartsWith("dotnet tool restore --configfile", StringComparison.Ordinal));
        Assert.Equal("env.DOTNET_TARGET != '' && env.NUGET_MIRROR_CONFIG != ''", Scalar(selfHosted, "if"));

        var cache = steps.Single(s => Scalar(s, "id") == "nuget");
        var with = (YamlMappingNode)cache.Children[new YamlScalarNode("with")];
        Assert.Contains("runner.environment == 'self-hosted' && 'ab-ci-' || ''", Scalar(with, "key"), StringComparison.Ordinal);
        Assert.Contains("runner.environment == 'self-hosted' && 'ab-ci-' || ''", Scalar(with, "restore-keys"), StringComparison.Ordinal);
    }
}
