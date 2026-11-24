using YamlDotNet.RepresentationModel;

namespace AgentBridge.Quality.Tests;

/// <summary>
/// stale-pwa.yml (#4369 Fix 3, Part of #4104): the scheduled check that turns a PWA site left behind main into a RED run. It must be what it says: read-only (two GETs,
/// no secrets, no write token), scheduled and by hand only, run quality/stale-pwa.ps1 on what it read, and never swallow that script's failure.
/// </summary>
public class StalePwaWorkflowTests
{
    const string File = ".github/workflows/stale-pwa.yml";

    static string Raw => System.IO.File.ReadAllText(Path.Combine(ChangedFiles.RepoRoot(AppContext.BaseDirectory), File));

    static YamlMappingNode Workflow()
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(Raw));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    static string Scalar(YamlNode node, string key) => ((YamlScalarNode)((YamlMappingNode)node)[key]).Value!;

    static List<YamlMappingNode> Steps() => ((YamlSequenceNode)((YamlMappingNode)((YamlMappingNode)Workflow()["jobs"])["check"])["steps"]).Children.OfType<YamlMappingNode>().ToList();

    [Fact]
    public void It_runs_on_a_schedule_and_by_hand_only_with_read_only_permissions_and_one_job()
    {
        var workflow = Workflow();

        Assert.Equal(["schedule", "workflow_dispatch"], ((YamlMappingNode)workflow["on"]).Children.Keys.Select(k => k.ToString()).Order(StringComparer.Ordinal));
        Assert.Equal(["contents"], ((YamlMappingNode)workflow["permissions"]).Children.Keys.Select(k => k.ToString()));
        Assert.Equal("read", Scalar(workflow["permissions"], "contents"));
        Assert.Equal(["check"], ((YamlMappingNode)workflow["jobs"]).Children.Keys.Select(k => k.ToString()));
        // a day is the limit, so a check at least every six hours notices within a quarter of it; and it is not so often that it floods the API
        var cron = Scalar(((YamlSequenceNode)((YamlMappingNode)workflow["on"])["schedule"]).Children[0], "cron");
        Assert.Matches(@"^\d+ \*/([1-6]) \* \* \*$", cron);
    }

    [Fact]
    public void Its_two_reads_are_GETs_with_no_secret_and_a_failed_read_leaves_no_file_for_the_script_to_fail_closed_on()
    {
        var read = Steps().Single(s => s.Children.ContainsKey("run") && Scalar(s, "run").Contains("curl", StringComparison.Ordinal));
        var script = Scalar(read, "run");

        Assert.Contains("https://app.slipgates.com/config.js", script, StringComparison.Ordinal);
        Assert.Contains("commits?sha=main&path=pwa&per_page=1", script, StringComparison.Ordinal);
        var commands = script.Split('\n').Where(l => l.StartsWith("curl ", StringComparison.Ordinal) || l.StartsWith("gh api ", StringComparison.Ordinal)).Select(l => l.Split("||")[0]).ToList();
        Assert.Equal(2, commands.Count);
        foreach (var write in new[] { " -X ", "--request", "--data", " -d ", " -F ", " -f ", "--method", "--input", "--form" })
            Assert.All(commands, c => Assert.DoesNotContain(write, c, StringComparison.Ordinal));
        Assert.DoesNotContain("secrets.", Raw, StringComparison.Ordinal);
        Assert.Equal(2, script.Split("|| rm -f", StringSplitOptions.None).Length - 1);
        Assert.Equal("${{ github.token }}", Scalar(read["env"], "GH_TOKEN"));
        Assert.False(read.Children.ContainsKey("continue-on-error"));
    }

    [Fact]
    public void It_runs_the_decision_on_what_it_read_and_nothing_can_swallow_its_failure()
    {
        var steps = Steps();
        var decide = steps[^1];

        Assert.Contains("quality/stale-pwa.ps1", Scalar(decide, "run"), StringComparison.Ordinal);
        Assert.Contains("-ConfigPath \"$env:RUNNER_TEMP/config.js\" -NewestPath \"$env:RUNNER_TEMP/newest.json\"", Scalar(decide, "run"), StringComparison.Ordinal);
        Assert.All(steps, s => Assert.False(s.Children.ContainsKey("continue-on-error")));
        Assert.DoesNotContain("|| true", Raw, StringComparison.Ordinal);
        var checkout = steps.Single(s => s.Children.ContainsKey("uses") && Scalar(s, "uses").StartsWith("actions/checkout@", StringComparison.Ordinal));
        Assert.Equal("false", Scalar(checkout["with"], "persist-credentials"));
        Assert.False(((YamlMappingNode)checkout["with"]).Children.ContainsKey("ref"));
    }
}
