using System.Diagnostics;
using YamlDotNet.RepresentationModel;

namespace AgentBridge.Quality.Tests;

public class StallAlarmTests
{
    [Fact]
    public void The_alarm_workflow_uses_operational_nodes_without_a_recursive_merge_trigger()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(Path.Combine(root, ".github", "workflows", "stall-alarm.yml"))));
        var workflow = (YamlMappingNode)yaml.Documents[0].RootNode;
        var on = (YamlMappingNode)workflow.Children[new YamlScalarNode("on")];
        Assert.False(on.Children.ContainsKey(new YamlScalarNode("workflow_run")));
        Assert.True(on.Children.ContainsKey(new YamlScalarNode("schedule")));
        var jobs = (YamlMappingNode)workflow.Children[new YamlScalarNode("jobs")];
        var alarm = (YamlMappingNode)jobs.Children[new YamlScalarNode("alarm")];
        var steps = (YamlSequenceNode)alarm.Children[new YamlScalarNode("steps")];
        var command = steps.Children.OfType<YamlMappingNode>().Single(s => s.Children.ContainsKey(new YamlScalarNode("run")))
            .Children[new YamlScalarNode("run")].ToString();
        Assert.Contains("-MainGreenNode", command);
        Assert.Contains("-ChiefNode", command);
        Assert.DoesNotContain("-Mentions", command);
    }

    [Theory]
    [Trait("Category", "Process")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task Stall_alarm_uses_complete_evidence_and_one_issue(int scenario)
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var start = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File",
            Path.Combine(root, "quality", "test-stall-alarm.ps1"), "-Case", scenario.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, (await output) + (await error));
    }
}
