using System.Diagnostics;
using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace AgentBridge.Quality.Tests;

[Trait("Category", "Process")]
public class SdkPinTests
{
    [Theory]
    [InlineData(".")]
    [InlineData("tests/AgentBridge.Quality.Tests")]
    public async Task Builds_from_the_root_and_nested_projects_use_a_stable_pinned_feature_band(string directory)
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        using var global = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "global.json")));
        var sdk = global.RootElement.GetProperty("sdk");
        var minimum = Version.Parse(sdk.GetProperty("version").GetString()!);
        var selected = await DotnetVersion(Path.Combine(root, directory));

        Assert.DoesNotContain("-", selected, StringComparison.Ordinal);
        var version = Version.Parse(selected);
        Assert.Equal(minimum.Major, version.Major);
        Assert.Equal(minimum.Minor, version.Minor);
        Assert.Equal(minimum.Build / 100, version.Build / 100);
        Assert.True(version >= minimum, $"Resolved SDK {version} is older than the pin {minimum}.");
    }

    [Fact]
    public void The_sdk_pin_allows_servicing_patches_but_not_a_new_feature_band_or_prerelease()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        using var global = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "global.json")));
        var sdk = global.RootElement.GetProperty("sdk");

        Assert.Equal("10.0.404", sdk.GetProperty("version").GetString());
        Assert.Equal("latestPatch", sdk.GetProperty("rollForward").GetString());
        Assert.False(sdk.GetProperty("allowPrerelease").GetBoolean());
    }

    [Fact]
    public void Every_ci_sdk_install_uses_the_pin_and_logs_nonfatal_diagnostics_without_changing_dotnet_eight()
    {
        var jobs = CiJobs();
        var pinned = 0;
        var runtime = 0;
        foreach (var job in jobs.Children.Values.Cast<YamlMappingNode>())
        {
            if (!job.Children.TryGetValue("steps", out var node))
                continue;
            var steps = ((YamlSequenceNode)node).Cast<YamlMappingNode>().ToList();
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                if (!step.Children.TryGetValue("uses", out var uses) ||
                    !uses.ToString().StartsWith("actions/setup-dotnet@", StringComparison.Ordinal))
                    continue;
                var inputs = (YamlMappingNode)step["with"];
                if (inputs.Children.TryGetValue("dotnet-version", out var version) && version.ToString() == "8.0.x")
                {
                    runtime++;
                    continue;
                }

                Assert.False(inputs.Children.ContainsKey("dotnet-version"));
                Assert.Equal("global.json", inputs["global-json-file"].ToString());
                Assert.True(i + 1 < steps.Count);
                var diagnostic = steps[i + 1];
                AssertDiagnostics(diagnostic);
                Assert.Equal(step.Children.TryGetValue("if", out var condition) ? condition.ToString() : null,
                    diagnostic.Children.TryGetValue("if", out var diagnosticCondition) ? diagnosticCondition.ToString() : null);
                pinned++;
            }
        }

        Assert.True(pinned > 0);
        Assert.Equal(1, runtime);
    }

    [Fact]
    public void The_artifact_only_installer_job_also_logs_the_sdk_and_image_without_failing_the_job()
    {
        var steps = (YamlSequenceNode)((YamlMappingNode)CiJobs()["app-installer"])["steps"];
        AssertDiagnostics(Assert.Single(steps.Cast<YamlMappingNode>(), s =>
            s.Children.TryGetValue("name", out var name) && name.ToString() == ".NET SDK and runner image"));
    }

    private static void AssertDiagnostics(YamlMappingNode step)
    {
        Assert.Equal("true", step["continue-on-error"].ToString());
        Assert.Equal("pwsh", step["shell"].ToString());
        var script = step["run"].ToString();
        foreach (var text in new[] { "dotnet --info", "$env:ImageOS", "$env:ImageVersion" })
            Assert.Contains(text, script, StringComparison.Ordinal);
    }

    private static YamlMappingNode CiJobs()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        using var reader = File.OpenText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        var yaml = new YamlStream();
        yaml.Load(reader);
        return (YamlMappingNode)((YamlMappingNode)yaml.Documents[0].RootNode)["jobs"];
    }

    private static async Task<string> DotnetVersion(string directory)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--version");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("SDK resolution timed out after 30 seconds.");
        }

        Assert.True(process.ExitCode == 0, await error);
        return (await output).Trim();
    }
}
