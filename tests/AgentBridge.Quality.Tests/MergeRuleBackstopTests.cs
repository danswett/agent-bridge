using System.Diagnostics;
using YamlDotNet.RepresentationModel;

namespace AgentBridge.Quality.Tests;

public class MergeRuleBackstopTests
{
    [Fact]
    public void Cron_checks_the_queue_before_joining_the_writer_and_other_events_bypass_it()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(Path.Combine(root, ".github", "workflows", "merge-rule.yml"))));
        var document = (YamlMappingNode)yaml.Documents[0].RootNode;
        var jobs = (YamlMappingNode)document["jobs"];
        var evaluate = (YamlMappingNode)jobs["evaluate"];
        Assert.True(evaluate.Children.ContainsKey(new YamlScalarNode("needs")),
            "Cron must check queued evaluations before entering the shared writer queue.");
        Assert.Equal("backstop", evaluate["needs"].ToString());
        var backstop = (YamlMappingNode)jobs["backstop"];
        Assert.Equal("github.event_name == 'schedule'", backstop["if"].ToString());
        Assert.Contains("!cancelled()", evaluate["if"].ToString());
        Assert.Contains("github.event_name != 'schedule' || needs.backstop.outputs.proceed != 'false'", evaluate["if"].ToString());
        Assert.Equal("false", ((YamlMappingNode)document["concurrency"])["cancel-in-progress"].ToString());
        Assert.Equal("false", ((YamlMappingNode)evaluate["concurrency"])["cancel-in-progress"].ToString());
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task Cron_yields_only_to_fresh_queued_scoped_evaluations_and_unreadable_state_runs()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var start = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(root, "quality", "test-merge-rule-backstop.ps1") })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, (await output) + (await error));
    }
}
