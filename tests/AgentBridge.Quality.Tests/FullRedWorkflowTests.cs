using YamlDotNet.RepresentationModel;

namespace AgentBridge.Quality.Tests;

/// <summary>
/// full-red.yml (S2, #4104): the alert for a red FULL lane. It reads a finished Full run and opens issues, so it must act only on this repository's own
/// run of main that Full's triggers can make, never on a fork's, and must queue its runs so a burst of completions drops no alert.
/// </summary>
public class FullRedWorkflowTests
{
    static YamlMappingNode Workflow()
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(File.ReadAllText(Path.Combine(ChangedFiles.RepoRoot(AppContext.BaseDirectory), ".github", "workflows", "full-red.yml"))));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    static YamlMappingNode Report() => (YamlMappingNode)((YamlMappingNode)Workflow()["jobs"])["report"];

    [Theory]
    [InlineData("o/r", "workflow_run", "main", true)]
    [InlineData("o/r", "workflow_dispatch", "main", true)]
    [InlineData("evil/r", "workflow_run", "main", false)]
    [InlineData("evil/r", "pull_request", "main", false)]
    [InlineData("o/r", "pull_request", "main", false)]
    [InlineData("o/r", "push", "main", false)]
    [InlineData("o/r", "workflow_run", "feature", false)]
    public void The_job_runs_only_for_this_repositorys_own_Full_run_on_main_started_by_workflow_run_or_by_hand(string repo, string eventName, string branch, bool runs)
    {
        var condition = ((YamlScalarNode)Report()["if"]).Value!.Trim();
        if (condition.StartsWith("${{", StringComparison.Ordinal)) condition = condition[3..^2].Trim();

        var got = MergeRuleTests.IfExpression.Evaluate(condition, new Dictionary<string, string?>
        {
            ["github.repository"] = "o/r",
            ["github.event.workflow_run.head_repository.full_name"] = repo,
            ["github.event.workflow_run.event"] = eventName,
            ["github.event.workflow_run.head_branch"] = branch,
        });

        Assert.Equal(runs, got);
    }

    [Fact]
    public void It_watches_Full_on_main_queues_its_runs_and_holds_only_the_permissions_the_alert_needs()
    {
        var on = (YamlMappingNode)Workflow()["on"];
        var run = (YamlMappingNode)on["workflow_run"];
        var concurrency = (YamlMappingNode)Report()["concurrency"];
        var permissions = (YamlMappingNode)Report()["permissions"];

        Assert.Equal(["workflow_run"], on.Children.Keys.Select(k => k.ToString()));
        Assert.Equal(["Full"], ((YamlSequenceNode)run["workflows"]).Children.Select(c => c.ToString()));
        Assert.Equal(["main"], ((YamlSequenceNode)run["branches"]).Children.Select(c => c.ToString()));
        Assert.Equal(("full-red", "max", "false"), (concurrency["group"].ToString(), concurrency["queue"].ToString(), concurrency["cancel-in-progress"].ToString()));
        Assert.Equal(["actions: read", "contents: read", "issues: write"], permissions.Children.Select(p => $"{p.Key}: {p.Value}").Order(StringComparer.Ordinal));
    }
}
