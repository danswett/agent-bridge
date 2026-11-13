using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace AgentBridge.Quality.Tests;

public class PrConcurrencyTests
{
    [Theory]
    [InlineData("pull_request_target", "synchronize", "", true)]
    [InlineData("pull_request_target", "synchronize", "off", false)]
    [InlineData("pull_request_target", "labeled", "", false)]
    [InlineData("pull_request_target", "converted_to_draft", "", false)]
    [InlineData("status", "", "", false)]
    [InlineData("workflow_run", "completed", "", false)]
    [InlineData("push", "", "", false)]
    [InlineData("release", "", "", false)]
    [InlineData("schedule", "", "", false)]
    [InlineData("workflow_dispatch", "", "", false)]
    public void Only_a_new_head_cancels_its_PR_admission_and_never_a_sweep(string name, string action, string kill, bool cancels)
    {
        var context = Context(name, "42");
        context["github.event.action"] = action;
        context["vars.FLEET_MERGERULE"] = kill;
        var concurrency = Load("merge-rule.yml");
        Assert.Equal(cancels, Cancels(concurrency, context));
        if (name == "pull_request_target")
            Assert.Equal(kill == "off" ? "merge-rule-admit-sweep" : "merge-rule-admit-42", Group(concurrency, context));
    }

    [Theory]
    [InlineData("weekly-report.yml", "pull_request")]
    [InlineData("merge-rule-review.yml", "pull_request_review")]
    [InlineData("merge-rule.yml", "pull_request_target")]
    public void Superseded_heads_share_only_their_own_PR_slot(string file, string eventName)
    {
        var concurrency = Load(file);
        var context = Context(eventName, "42");
        var first = Group(concurrency, context);
        if (eventName != "pull_request_target") { Assert.True(Cancels(concurrency, context)); }
        context["github.event.pull_request.head.sha"] = "new-head";
        context["github.run_id"] = "new-run";
        Assert.Equal(first, Group(concurrency, context));
        context["github.event.pull_request.number"] = "43";
        Assert.NotEqual(first, Group(concurrency, context));
        foreach (var other in new[] { "push", "release", "schedule", "workflow_dispatch", "workflow_run" })
        {
            context = Context(other, "");
            Assert.NotEqual(first, Group(concurrency, context));
            Assert.False(Cancels(concurrency, context));
        }
    }

    static Dictionary<string, string?> Context(string name, string number) => new()
    {
        ["github.event_name"] = name, ["github.event.pull_request.number"] = number,
        ["github.event.pull_request.head.sha"] = number.Length > 0 ? "old-head" : null,
        ["github.run_id"] = "old-run", ["github.ref"] = name == "pull_request" ? "refs/pull/42/merge" : "refs/heads/main",
    };

    static string Group(YamlMappingNode node, Dictionary<string, string?> context) =>
        Regex.Replace(node["group"].ToString(), @"\$\{\{(.*?)\}\}", m =>
            Convert.ToString(MergeRuleTests.IfExpression.Value(m.Groups[1].Value, context)) ?? "", RegexOptions.Singleline);

    static bool Cancels(YamlMappingNode node, Dictionary<string, string?> context) =>
        MergeRuleTests.IfExpression.Evaluate(node["cancel-in-progress"].ToString().Replace("${{", "").Replace("}}", ""), context);

    [Fact]
    public void The_cancelled_PR_paths_cannot_post_reports_or_merge_verdicts()
    {
        var jobs = (YamlMappingNode)Document("weekly-report.yml")["jobs"];
        var post = (YamlMappingNode)jobs["post"];
        var context = Context("pull_request", "42");
        context["needs.report.outputs.post"] = "true";
        context["github.repository"] = "danswett/agent-bridge";
        Assert.False(MergeRuleTests.IfExpression.Evaluate(post["if"].ToString(), context));
        var relay = Document("merge-rule-review.yml");
        Assert.Empty(((YamlMappingNode)relay["permissions"]).Children);
        var step = Assert.Single((YamlSequenceNode)((YamlMappingNode)((YamlMappingNode)relay["jobs"])["relay"])["steps"]);
        Assert.StartsWith("echo ", ((YamlMappingNode)step)["run"].ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("codeql.yml")]
    [InlineData("dependencies.yml")]
    [InlineData("workflow-lint.yml")]
    [InlineData("apple.yml")]
    [InlineData("ha-contract-drift.yml")]
    [InlineData("iac-scan.yml")]
    [InlineData("relay.yml")]
    [InlineData("weekly-report.yml")]
    [InlineData("merge-rule.yml")]
    [InlineData("merge-rule-review.yml")]
    public void Every_PR_workflow_preserves_non_PR_runs_and_limits_cancellation_to_its_own_PR(string file)
    {
        var yaml = Document(file);
        var triggers = (YamlMappingNode)yaml["on"];
        foreach (var name in new[] { "pull_request", "pull_request_target", "pull_request_review", "push", "release", "schedule", "workflow_dispatch", "workflow_run" })
        {
            var context = Context(name, name.StartsWith("pull_request", StringComparison.Ordinal) ? "42" : "");
            context["github.workflow"] = file;
            context["github.run_attempt"] = "1";
            context["github.event.action"] = "synchronize";
            if (!yaml.Children.TryGetValue("concurrency", out var node)) { Assert.Equal("relay.yml", file); continue; }
            var concurrency = (YamlMappingNode)node;
            var canRun = triggers.Children.ContainsKey(name);
            var expected = canRun && name.StartsWith("pull_request", StringComparison.Ordinal);
            Assert.Equal(expected, canRun && Cancels(concurrency, context));
            if (!expected) { continue; }
            var first = Group(concurrency, context);
            context["github.event.pull_request.number"] = "43";
            context["github.ref"] = "refs/pull/43/merge";
            Assert.NotEqual(first, Group(concurrency, context));
        }
    }

    static YamlMappingNode Load(string file)
        => (YamlMappingNode)Document(file)["concurrency"];

    static YamlMappingNode Document(string file)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(Path.Combine(ChangedFiles.RepoRoot(AppContext.BaseDirectory), ".github", "workflows", file))));
        return (YamlMappingNode)yaml.Documents[0].RootNode;
    }
}
