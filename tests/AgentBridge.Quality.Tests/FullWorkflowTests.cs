using YamlDotNet.RepresentationModel;

namespace AgentBridge.Quality.Tests;

/// <summary>
/// full.yml, the FULL lane (S2, #4104, quality/README.md §4.7). Piece 4 moved the spikes jobs here from ci.yml; a FULL job never
/// gates a merge, so none of it may run for a pull request, and each job tests the commit the FAST run passed on.
/// </summary>
public class FullWorkflowTests
{
    const string Sha = "${{ needs.trigger.outputs.sha }}";

    static string Root => ChangedFiles.RepoRoot(AppContext.BaseDirectory);

    static YamlMappingNode Load(string path)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(File.ReadAllText(Path.Combine(Root, path))));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    static YamlMappingNode Jobs(string workflow) => (YamlMappingNode)Load($".github/workflows/{workflow}")["jobs"];

    static string Scalar(YamlNode node, string key) => ((YamlScalarNode)((YamlMappingNode)node)[key]).Value!;

    static string Expression(string value) => value.Trim().StartsWith("${{", StringComparison.Ordinal) ? value.Trim()[3..^2].Trim() : value.Trim();

    [Fact]
    public void Full_yml_has_the_trigger_job_the_spikes_and_the_gate_and_ci_yml_no_longer_has_the_spikes()
    {
        Assert.Equal(["full-gate", "spikes", "spikes-linux", "trigger"], Jobs("full.yml").Children.Keys.Select(k => k.ToString()).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(Jobs("ci.yml").Children.Keys, k => k.ToString().StartsWith("spikes", StringComparison.Ordinal));
    }

    [Fact]
    public void Full_yml_runs_after_a_finished_ci_run_on_main_or_by_hand_and_never_for_a_pull_request_or_a_push()
    {
        var on = (YamlMappingNode)Load(".github/workflows/full.yml")["on"];
        var run = (YamlMappingNode)on["workflow_run"];
        var dispatch = (YamlMappingNode)on["workflow_dispatch"];

        Assert.Equal(["workflow_dispatch", "workflow_run"], on.Children.Keys.Select(k => k.ToString()).Order(StringComparer.Ordinal));
        Assert.Equal(["CI"], ((YamlSequenceNode)run["workflows"]).Children.Select(c => c.ToString()));
        Assert.Equal(["completed"], ((YamlSequenceNode)run["types"]).Children.Select(c => c.ToString()));
        Assert.Equal(["main"], ((YamlSequenceNode)run["branches"]).Children.Select(c => c.ToString()));
        Assert.Equal("true", Scalar(((YamlMappingNode)dispatch["inputs"])["sha"], "required"));
    }

    [Theory]
    [InlineData("workflow_run", "success", "push", "main", true)]
    [InlineData("workflow_run", "failure", "push", "main", false)]
    [InlineData("workflow_run", "cancelled", "push", "main", false)]
    [InlineData("workflow_run", "success", "pull_request", "main", false)]
    [InlineData("workflow_run", "success", "push", "feature", false)]
    [InlineData("workflow_dispatch", null, null, null, true)]
    public void The_trigger_job_says_ok_only_for_a_green_push_run_of_ci_on_main_or_a_run_by_hand(string eventName, string? conclusion, string? runEvent, string? branch, bool ok)
    {
        var job = (YamlMappingNode)Jobs("full.yml")["trigger"];
        var output = Scalar(job["outputs"], "ok");

        var value = MergeRuleTests.IfExpression.Value(Expression(output), new Dictionary<string, string?>
        {
            ["github.event_name"] = eventName,
            ["github.event.workflow_run.conclusion"] = conclusion,
            ["github.event.workflow_run.event"] = runEvent,
            ["github.event.workflow_run.head_branch"] = branch,
        });

        Assert.Equal(ok, value is true || value is string s && s == "true");
    }

    [Fact]
    public void Every_other_job_waits_for_the_trigger_and_runs_only_when_it_says_ok_and_each_checkout_is_the_commit_ci_passed_on()
    {
        var jobs = Jobs("full.yml");
        foreach (var (id, job) in jobs.Children.Where(j => j.Key.ToString() is not ("trigger" or "full-gate")))
        {
            var needs = ((YamlMappingNode)job)["needs"];
            Assert.Equal(["trigger"], needs is YamlSequenceNode many ? many.Children.Select(c => c.ToString()) : [needs.ToString()]);
            Assert.Equal("needs.trigger.outputs.ok == 'true'", Expression(Scalar(job, "if")));
        }
        foreach (var (id, job) in jobs.Children)
            foreach (var step in ((YamlSequenceNode)((YamlMappingNode)job)["steps"]).Children.OfType<YamlMappingNode>()
                .Where(s => s.Children.ContainsKey("uses") && Scalar(s, "uses").StartsWith("actions/checkout@", StringComparison.Ordinal)))
            {
                var with = (YamlMappingNode)step["with"];
                Assert.True(Scalar(with, "ref") == Sha, $"{id}'s checkout isn't the commit the FAST run passed on");
                Assert.Equal("false", Scalar(with, "persist-credentials"));
            }
    }

    [Fact]
    public void A_newer_main_run_cancels_the_older_one_and_a_run_by_hand_has_a_group_per_commit()
    {
        var concurrency = (YamlMappingNode)Load(".github/workflows/full.yml")["concurrency"];

        var run = MergeRuleTests.IfExpression.Value(Expression(Scalar(concurrency, "group")), new Dictionary<string, string?> { ["github.event_name"] = "workflow_run" });
        var hand = MergeRuleTests.IfExpression.Value(Expression(Scalar(concurrency, "group")), new Dictionary<string, string?> { ["github.event_name"] = "workflow_dispatch", ["inputs.sha"] = "abc123" });

        Assert.Equal("full-main", run);
        Assert.Equal("full-abc123", hand);
        Assert.Equal("true", Scalar(concurrency, "cancel-in-progress"));
    }

    [Fact]
    public void The_merge_rule_waits_only_on_jobs_ci_yml_has()
    {
        var ciJobs = Jobs("ci.yml").Children.Keys.Select(k => k.ToString()).ToHashSet(StringComparer.Ordinal);
        var green = (YamlMappingNode)Load("quality/merge-rule.yml")["main_green"];

        var missing = ((YamlSequenceNode)green["jobs"]).Children.Select(j => j.ToString()).Where(j => !ciJobs.Contains(j.Split(" (")[0]));

        Assert.Empty(missing);
    }

    /// <summary>
    /// full.yml runs from a workflow_run, in the default branch's context, and checks out a commit to test. A cache written from there could be
    /// poisoned by that code (CodeQL's cache-poisoning alerts on #4254), so FULL only restores: no job saves a cache, and the system-package
    /// action, which saves by default for ci.yml, is told not to.
    /// </summary>
    [Fact]
    public void Full_yml_never_writes_a_cache_it_only_restores_and_tells_the_package_action_not_to_save()
    {
        var text = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "full.yml"));
        var steps = ((YamlMappingNode)Load(".github/workflows/full.yml")["jobs"]).Children.Values
            .SelectMany(j => ((YamlSequenceNode)((YamlMappingNode)j)["steps"]).Children.OfType<YamlMappingNode>()).ToList();
        var packageCalls = steps.Where(s => s.Children.ContainsKey("uses") && Scalar(s, "uses").StartsWith("./.github/actions/playwright-system-packages", StringComparison.Ordinal)).ToList();

        Assert.DoesNotContain("actions/cache/save", text, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/cache@", text, StringComparison.Ordinal);
        Assert.NotEmpty(packageCalls);
        Assert.All(packageCalls, s => Assert.Equal("false", Scalar(s["with"], "save")));
        var action = Load(".github/actions/playwright-system-packages/action.yml");
        Assert.Equal("false", Scalar(((YamlMappingNode)action["inputs"])["save"], "default")); // the safe default: a call that forgets it never writes a cache
        var ciCalls = ((YamlMappingNode)Load(".github/workflows/ci.yml")["jobs"]).Children.Values
            .SelectMany(j => ((YamlSequenceNode)((YamlMappingNode)j)["steps"]).Children.OfType<YamlMappingNode>())
            .Where(s => s.Children.ContainsKey("uses") && Scalar(s, "uses").StartsWith("./.github/actions/playwright-system-packages", StringComparison.Ordinal)).ToList();
        Assert.All(ciCalls, s => Assert.Equal("true", Scalar(s["with"], "save"))); // ci.yml saves today, so it says so
        var save = ((YamlSequenceNode)((YamlMappingNode)action["runs"])["steps"]).Children.OfType<YamlMappingNode>()
            .Single(s => s.Children.ContainsKey("uses") && Scalar(s, "uses").StartsWith("actions/cache/save@", StringComparison.Ordinal));
        Assert.Contains("inputs.save == 'true'", Scalar(save, "if"), StringComparison.Ordinal);
    }

    /// <summary>
    /// CodeQL's cache-poisoning alerts on #4254: FULL runs in the default branch's context and checks out a commit to test. Only the trigger job reads the
    /// event's commit: it reads the CI run itself through the API (by number), refuses anything but 40 lowercase hex, and every other job checks out that
    /// output, so no checkout or cache step takes a commit straight from the event.
    /// </summary>
    [Fact]
    public void Only_the_trigger_reads_the_events_commit_and_hands_on_a_verified_one_that_every_checkout_uses()
    {
        var text = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "full.yml")).Replace("\r\n", "\n");
        var trigger = (YamlMappingNode)Jobs("full.yml")["trigger"];
        var step = ((YamlSequenceNode)trigger["steps"]).Children.OfType<YamlMappingNode>().Single(s => s.Children.ContainsKey("id") && Scalar(s, "id") == "commit");
        var script = Scalar(step, "run");

        Assert.Equal("${{ steps.commit.outputs.sha }}", Scalar(trigger["outputs"], "sha"));
        Assert.Equal("read", Scalar(trigger["permissions"], "actions"));
        foreach (var part in new[] { "gh api \"repos/$REPO/actions/runs/$RUN_ID\" --jq .head_sha", "^[0-9]+$", "^[0-9a-f]{40}$", "sha=$sha\" >> \"$GITHUB_OUTPUT\"" })
            Assert.Contains(part, script, StringComparison.Ordinal);
        Assert.DoesNotContain("github.event.workflow_run.head_sha", text, StringComparison.Ordinal);
    }
}