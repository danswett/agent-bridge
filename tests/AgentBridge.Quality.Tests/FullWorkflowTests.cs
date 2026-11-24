using YamlDotNet.RepresentationModel;

namespace AgentBridge.Quality.Tests;

/// <summary>
/// full.yml, the FULL lane (S2, #4104, quality/README.md §4.7). Piece 4 moved the spikes jobs here from ci.yml; a FULL job never
/// gates a merge, so none of it may run for a pull request, and each job tests the commit the FAST run passed on.
/// </summary>
public class FullWorkflowTests
{
    const string Sha = "${{ needs.trigger.outputs.tested }}";

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
        var dispatch = on["workflow_dispatch"];

        Assert.Equal(["workflow_dispatch", "workflow_run"], on.Children.Keys.Select(k => k.ToString()).Order(StringComparer.Ordinal));
        Assert.Equal(["CI"], ((YamlSequenceNode)run["workflows"]).Children.Select(c => c.ToString()));
        Assert.Equal(["completed"], ((YamlSequenceNode)run["types"]).Children.Select(c => c.ToString()));
        Assert.Equal(["main"], ((YamlSequenceNode)run["branches"]).Children.Select(c => c.ToString()));
        Assert.False(dispatch is YamlMappingNode withInputs && withInputs.Children.ContainsKey("inputs")); // a run by hand names no commit (see A_run_by_hand_takes_no_input...)
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
        var hand = MergeRuleTests.IfExpression.Value(Expression(Scalar(concurrency, "group")), new Dictionary<string, string?> { ["github.event_name"] = "workflow_dispatch", ["github.sha"] = "abc123" });

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

        Assert.Equal("${{ steps.commit.outputs.tested }}", Scalar(trigger["outputs"], "tested"));
        Assert.Equal("read", Scalar(trigger["permissions"], "actions"));
        foreach (var part in new[] { "gh api \"repos/$REPO/actions/runs/$RUN_ID\"", "head_branch", "^[0-9]+$", "^[0-9a-f]{40}$", "tested=$sha\" >> \"$GITHUB_OUTPUT\"" })
            Assert.Contains(part, script, StringComparison.Ordinal);
        Assert.DoesNotContain("github.event.workflow_run.head_sha", text, StringComparison.Ordinal);
    }

    // The trigger's commit step, run for real under bash with a stub gh, as CriticalReleaseTests does for ci.yml's guard.
    static (int Code, string Output, string Log) RunCommitStep(string eventName, string refSha, string? runLine = null, bool ghFails = false)
    {
        var bash = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe") : "/bin/bash";
        var step = ((YamlSequenceNode)((YamlMappingNode)Jobs("full.yml")["trigger"])["steps"]).Children.OfType<YamlMappingNode>()
            .Single(s => s.Children.ContainsKey("id") && Scalar(s, "id") == "commit");
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"trigger-{Guid.NewGuid():N}")).FullName;
        var script = "gh() { echo \"gh $*\" >> \"$GH_LOG\"; [ -z \"$GH_FAILS\" ] || return 1; printf '%s\\n' \"$GH_RUN\"; }\n" + Scalar(step, "run");
        var start = new System.Diagnostics.ProcessStartInfo(bash, ["-c", script]) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var (key, value) in new[] { ("EVENT", eventName), ("RUN_ID", eventName == "workflow_run" ? "4242" : ""), ("REF_SHA", refSha), ("REPO", "danswett/agent-bridge"), ("GH_TOKEN", "x"),
                     ("GH_LOG", Path.Combine(dir, "gh.log")), ("GH_RUN", runLine ?? ""), ("GH_FAILS", ghFails ? "1" : ""),
                     ("GITHUB_OUTPUT", Path.Combine(dir, "out")), ("GITHUB_STEP_SUMMARY", Path.Combine(dir, "summary")) })
            start.Environment[key] = value;
        using var p = System.Diagnostics.Process.Start(start)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        var outFile = Path.Combine(dir, "out"); var log = Path.Combine(dir, "gh.log");
        return (p.ExitCode, output + (File.Exists(outFile) ? File.ReadAllText(outFile) : ""), File.Exists(log) ? File.ReadAllText(log) : "");
    }

    static readonly string Tip = new('c', 40), RunSha = new('a', 40);

    /// <summary>
    /// CodeQL followed the by-hand sha INPUT through the trigger job into every checkout and, seeing cache restores and npm ci in the same privileged job, raised cache-poisoning
    /// alerts on #4254. A static analyser cannot see an on-main check, so no input names a commit at all: a run by hand tests the tip of the ref it was dispatched on, and a
    /// workflow_run's commit comes only from the API read of the CI run, for a push to main. Every checkout uses the trigger's output and nothing else.
    /// </summary>
    [Fact]
    public void A_run_by_hand_takes_no_input_and_no_checkout_derives_from_one()
    {
        var text = string.Join("\n", File.ReadAllLines(Path.Combine(Root, ".github", "workflows", "full.yml")).Where(l => !l.TrimStart().StartsWith('#')));
        var trigger = (YamlMappingNode)Jobs("full.yml")["trigger"];
        var step = ((YamlSequenceNode)trigger["steps"]).Children.OfType<YamlMappingNode>().Single(s => s.Children.ContainsKey("id") && Scalar(s, "id") == "commit");
        var on = (YamlMappingNode)Load(".github/workflows/full.yml")["on"];

        Assert.False(on["workflow_dispatch"] is YamlMappingNode m && m.Children.ContainsKey("inputs") && ((YamlMappingNode)m["inputs"]).Children.ContainsKey("sha"));
        Assert.DoesNotContain("inputs.sha", text, StringComparison.Ordinal);
        Assert.DoesNotContain("inputs.", Scalar(step, "run") + string.Join("", ((YamlMappingNode)step["env"]).Children.Select(c => c.Value.ToString())), StringComparison.Ordinal);
        Assert.Equal("${{ github.sha }}", Scalar(step["env"], "REF_SHA"));
        foreach (var job in ((YamlMappingNode)Load(".github/workflows/full.yml")["jobs"]).Children.Where(j => j.Key.ToString() != "trigger"))
            foreach (var checkout in ((YamlSequenceNode)((YamlMappingNode)job.Value)["steps"]).Children.OfType<YamlMappingNode>()
                         .Where(s => s.Children.ContainsKey("uses") && Scalar(s, "uses").StartsWith("actions/checkout@", StringComparison.Ordinal)))
                Assert.Equal("${{ needs.trigger.outputs.tested }}", Scalar(checkout["with"], "ref"));
    }

    [Fact]
    public void The_trigger_takes_a_workflow_runs_commit_only_from_the_API_read_of_a_push_to_main_and_a_run_by_hand_the_tip_it_runs_on()
    {
        var push = RunCommitStep("workflow_run", Tip, $"{RunSha}\tmain\tpush");
        var elsewhere = RunCommitStep("workflow_run", Tip, $"{RunSha}\ttopic\tpush");
        var pull = RunCommitStep("workflow_run", Tip, $"{RunSha}\tmain\tpull_request");
        var garbage = RunCommitStep("workflow_run", Tip, "not-a-sha\tmain\tpush");
        var unreadable = RunCommitStep("workflow_run", Tip, ghFails: true);
        var byHand = RunCommitStep("workflow_dispatch", Tip);
        var badTip = RunCommitStep("workflow_dispatch", "main");

        Assert.Equal(0, push.Code);
        Assert.Contains($"tested={RunSha}", push.Output, StringComparison.Ordinal);
        Assert.All(new[] { elsewhere, pull, garbage, unreadable }, r => { Assert.NotEqual(0, r.Code); Assert.DoesNotContain("tested=", r.Output, StringComparison.Ordinal); });
        Assert.Equal(0, byHand.Code);
        Assert.Contains($"tested={Tip}", byHand.Output, StringComparison.Ordinal);
        Assert.Equal("", byHand.Log); // a run by hand makes no API call at all: nothing to follow
        Assert.NotEqual(0, badTip.Code);
    }
    /// <summary>
    /// full.yml runs from a workflow_run, in the default branch's context, and checks out a commit to test. A cache written there could be poisoned by that code
    /// (CodeQL's cache-poisoning alerts on #4254), and CodeQL cannot see through an input that gates a save. So FULL has no save at all: it uses only the system-packages
    /// action, which restores and never writes a cache, and never the separate save action, which only ci.yml calls (one per restore call, after it).
    /// </summary>
    [Fact]
    public void Full_yml_never_writes_a_cache_it_uses_only_the_restore_only_package_action_and_never_the_save_action()
    {
        var text = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "full.yml"));
        IEnumerable<YamlMappingNode> StepsOf(string file) => ((YamlMappingNode)Load($".github/workflows/{file}")["jobs"]).Children.Values
            .SelectMany(j => ((YamlSequenceNode)((YamlMappingNode)j)["steps"]).Children.OfType<YamlMappingNode>());
        bool Uses(YamlMappingNode s, string action) => s.Children.ContainsKey("uses") && Scalar(s, "uses") == action;
        const string Restore = "./.github/actions/playwright-system-packages", Save = "./.github/actions/playwright-system-packages-save";

        Assert.DoesNotContain("actions/cache/save", text, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/cache@", text, StringComparison.Ordinal);
        Assert.DoesNotContain("playwright-system-packages-save", text, StringComparison.Ordinal);
        Assert.All(StepsOf("full.yml").Where(s => s.Children.ContainsKey("uses") && Scalar(s, "uses").StartsWith(Restore, StringComparison.Ordinal)), s => Assert.Equal(Restore, Scalar(s, "uses")));
        Assert.Contains(StepsOf("full.yml"), s => Uses(s, Restore));
        // The restore action holds no cache write and takes no save input; the save action holds only the save.
        var restoreText = File.ReadAllText(Path.Combine(Root, ".github", "actions", "playwright-system-packages", "action.yml"));
        Assert.DoesNotContain("actions/cache/save", restoreText, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/cache@", restoreText, StringComparison.Ordinal);
        Assert.False(((YamlMappingNode)Load(".github/actions/playwright-system-packages/action.yml")["inputs"]).Children.ContainsKey("save"));
        var saveSteps = ((YamlSequenceNode)((YamlMappingNode)Load(".github/actions/playwright-system-packages-save/action.yml")["runs"])["steps"]).Children.OfType<YamlMappingNode>().ToList();
        Assert.Single(saveSteps);
        Assert.StartsWith("actions/cache/save@", Scalar(saveSteps[0], "uses"), StringComparison.Ordinal);
        // ci.yml saves once per restore call, right after it, even when a later step failed, and only when the archives are ready.
        var ci = StepsOf("ci.yml").ToList();
        var saves = ci.Where(s => Uses(s, Save)).ToList();
        Assert.Equal(ci.Count(s => Uses(s, Restore)), saves.Count);
        Assert.All(saves, s => Assert.Equal("always() && steps.system-packages.outputs.cache-ready == 'true'", Scalar(s, "if")));

        // P2: every local action full.yml reaches, through any depth of composites, writes no cache either, so a later `uses: ./.github/actions/install-trivy` is read too, not only
        // the one named above. An action this cannot read (no action.yml, no composite steps) fails: what it writes would be unknown.
        static bool WritesCache(YamlMappingNode s)
        {
            if (!s.Children.ContainsKey("uses")) return false;
            var uses = Scalar(s, "uses");
            return uses.StartsWith("actions/cache@", StringComparison.Ordinal) || uses.StartsWith("actions/cache/save@", StringComparison.Ordinal)
                || (uses.StartsWith("actions/setup-", StringComparison.Ordinal) && s.Children.TryGetValue("with", out var with) && with is YamlMappingNode m && m.Children.ContainsKey("cache"));
        }
        static bool IsLocal(YamlMappingNode s) => s.Children.ContainsKey("uses") && Scalar(s, "uses").StartsWith("./", StringComparison.Ordinal);
        Assert.All(StepsOf("full.yml"), s => Assert.False(WritesCache(s), "a full.yml step writes a cache"));
        var queue = new Queue<string>(StepsOf("full.yml").Where(IsLocal).Select(s => Scalar(s, "uses")));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var local = queue.Dequeue();
            if (!seen.Add(local)) continue;
            var file = Path.Combine(Root, local[2..].Replace('/', Path.DirectorySeparatorChar), "action.yml");
            Assert.True(File.Exists(file), $"{local} has no action.yml to read, so what it writes is unknown");
            var stream = new YamlStream();
            stream.Load(new StringReader(File.ReadAllText(file)));
            var runs = (YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode)["runs"];
            Assert.True(runs.Children.ContainsKey("steps"), $"{local} is not a composite action this can read through");
            foreach (var step in ((YamlSequenceNode)runs["steps"]).Children.OfType<YamlMappingNode>())
            {
                Assert.False(WritesCache(step), $"{local} writes a cache, and full.yml reaches it");
                if (IsLocal(step)) queue.Enqueue(Scalar(step, "uses"));
            }
        }
        Assert.Contains("./.github/actions/playwright-system-packages", seen);
    }

    /// <summary>
    /// P3 (Q10): ci.yml's save step is gated on the restore action's cache-ready OUTPUT, and a real warm run only shows it skipped. So the chain that makes it true on a cold run is
    /// pinned: download.sh writes cache-ready=true to GITHUB_OUTPUT, the step that runs it has the id the action maps, the action's cache-ready output is that step's, and every
    /// ci.yml save step reads that output through the id of the restore step right before it.
    /// </summary>
    [Fact]
    public void The_save_step_reads_the_restore_actions_cache_ready_output_through_ids_that_exist()
    {
        var action = Load(".github/actions/playwright-system-packages/action.yml");
        var steps = ((YamlSequenceNode)((YamlMappingNode)action["runs"])["steps"]).Children.OfType<YamlMappingNode>().ToList();
        var download = File.ReadAllText(Path.Combine(Root, ".github", "actions", "playwright-system-packages", "download.sh"));

        Assert.Contains("cache-ready=true' >> \"$GITHUB_OUTPUT\"", download, StringComparison.Ordinal);
        Assert.Contains(steps, s => s.Children.ContainsKey("id") && Scalar(s, "id") == "packages" && Scalar(s, "run").Contains("download.sh", StringComparison.Ordinal));
        Assert.Equal("${{ steps.packages.outputs.cache-ready }}", Scalar(((YamlMappingNode)action["outputs"])["cache-ready"], "value"));
        Assert.Equal("${{ steps.debs.outputs.cache-primary-key }}", Scalar(((YamlMappingNode)action["outputs"])["cache-key"], "value"));
        var ci = ((YamlMappingNode)Load(".github/workflows/ci.yml")["jobs"]).Children.Values
            .Select(j => ((YamlSequenceNode)((YamlMappingNode)j)["steps"]).Children.OfType<YamlMappingNode>().ToList()).ToList();
        foreach (var job in ci)
            for (var i = 0; i < job.Count; i++)
                if (job[i].Children.ContainsKey("uses") && Scalar(job[i], "uses") == "./.github/actions/playwright-system-packages-save")
                {
                    var restore = job[i - 1];
                    Assert.Equal("./.github/actions/playwright-system-packages", Scalar(restore, "uses"));
                    Assert.Equal("system-packages", Scalar(restore, "id"));
                    Assert.Equal("${{ steps.system-packages.outputs.cache-key }}", Scalar(job[i]["with"], "key"));
                }
    }
}