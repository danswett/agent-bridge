using YamlDotNet.RepresentationModel;
using System.Text.Json;

namespace AgentBridge.Quality.Tests;

public class MergeRuleTests
{
    const string Head = "abcdef0123456789abcdef0123456789abcdef01";
    const string Older = "0000000123456789abcdef0123456789abcdef01";

    static readonly MergeRuleConfig Config = MergeRuleConfig.Parse("""
        base: main
        copilot_reviewer: copilot-pull-request-reviewer
        hold_labels: [hold, do-not-merge]
        independent_review:
          context: independent-review
          posters: [danswett]
        codeql:
          workflow: CodeQL
          check: CodeQL
        ignore_workflows: [Merge rule]
        sensitive_paths:
          - .github/**
          - src/AgentBridge.Transport/**
          - src/**pair**
        """);

    static readonly ReviewFact Copilot = new("copilot-pull-request-reviewer", IsBot: true, "COMMENTED", Head);
    static readonly RunFact Green = new("COMPLETED", "SUCCESS");

    static PullRequestFacts Pr(
        string[]? files = null,
        ReviewFact[]? reviews = null,
        StatusFact[]? statuses = null,
        string[]? labels = null,
        bool draft = false,
        bool filesComplete = true,
        RunFact? codeqlRun = null,
        RunFact? codeqlCheck = null,
        bool noCodeqlRun = false) =>
        new(42, "OPEN", draft, "main", Head, labels ?? [], files ?? ["docs/status.md"], filesComplete,
            reviews ?? [Copilot], statuses ?? [], noCodeqlRun ? null : codeqlRun ?? Green, codeqlCheck);

    static StatusFact Receipt(string state, string creator = "danswett", int minute = 0) =>
        new("independent-review", state, $"sec-42 {state}", creator, new DateTimeOffset(2026, 11, 3, 12, minute, 0, TimeSpan.Zero));

    [Fact]
    public void A_reviewed_change_with_green_CodeQL_and_nothing_sensitive_passes()
    {
        var verdict = MergeRule.Evaluate(Config, Pr())!;

        Assert.Equal("success", verdict.State);
        Assert.Equal("Copilot reviewed abcdef0; every check passed", verdict.Description);
    }

    [Fact]
    public void A_Copilot_review_of_an_earlier_commit_or_by_a_user_with_the_bots_name_leaves_it_waiting()
    {
        var earlier = MergeRule.Evaluate(Config, Pr(reviews: [Copilot with { CommitOid = Older }]))!;
        var impostor = MergeRule.Evaluate(Config, Pr(reviews: [Copilot with { IsBot = false }]))!;

        Assert.Equal(("pending", "Waiting for a Copilot review of abcdef0"), (earlier.State, earlier.Description));
        Assert.Equal("pending", impostor.State);
    }

    [Fact]
    public void A_sensitive_change_needs_a_listed_posters_receipt_on_the_head()
    {
        var files = new[] { "src/AgentBridge.Host/Pairing/Codes.cs", "docs/status.md" };

        var none = MergeRule.Evaluate(Config, Pr(files))!;
        // A workflow's token posting the receipt, as a pull request's own workflow could, counts for nothing.
        var byWorkflow = MergeRule.Evaluate(Config, Pr(files, statuses: [Receipt("success", "github-actions[bot]")]))!;
        var byReviewer = MergeRule.Evaluate(Config, Pr(files, statuses: [Receipt("success")]))!;

        Assert.Equal("failure", none.State);
        Assert.Equal("Needs an independent review of abcdef0: it touches src/AgentBridge.Host/Pairing/Codes.cs", none.Description);
        Assert.Equal(["src/AgentBridge.Host/Pairing/Codes.cs"], none.SensitiveFiles);
        Assert.Equal("failure", byWorkflow.State);
        Assert.Equal(("success", "Copilot reviewed abcdef0; every check passed; independent review on file"), (byReviewer.State, byReviewer.Description));
    }

    [Fact]
    public void Sensitive_paths_match_whatever_the_case() =>
        Assert.Equal("failure", MergeRule.Evaluate(Config, Pr(["SRC/agentbridge.transport/Channel.cs"]))!.State);

    [Fact]
    public void The_latest_receipt_counts_and_a_failed_one_blocks_even_a_change_that_needs_none()
    {
        var verdict = MergeRule.Evaluate(Config, Pr(statuses: [Receipt("success", minute: 1), Receipt("failure", minute: 2)]))!;

        Assert.Equal(("failure", "The independent review of abcdef0 failed: sec-42 failure"), (verdict.State, verdict.Description));
    }

    [Fact]
    public void A_file_list_GitHub_cut_short_counts_as_sensitive()
    {
        var verdict = MergeRule.Evaluate(Config, Pr(filesComplete: false))!;

        Assert.Equal(("failure", "Needs an independent review of abcdef0: its file list is incomplete"), (verdict.State, verdict.Description));
    }

    [Fact]
    public void A_draft_or_a_hold_label_fails_and_every_missing_piece_is_named_failures_first()
    {
        var verdict = MergeRule.Evaluate(Config, Pr(draft: true, labels: ["HOLD"], reviews: []))!;

        Assert.Equal("failure", verdict.State);
        Assert.Equal(["draft: mark it ready for review", "held by the label 'HOLD'", "waiting for a Copilot review of abcdef0"], verdict.Reasons);
    }

    [Theory]
    [InlineData(null, null, null, null, "pending", "No CodeQL run on abcdef0 yet (after a retarget, push or reopen)")]
    [InlineData("IN_PROGRESS", null, null, null, "pending", "CodeQL is running on abcdef0")]
    [InlineData("COMPLETED", "FAILURE", null, null, "failure", "CodeQL ended failure on abcdef0")]
    [InlineData("COMPLETED", "SUCCESS", "COMPLETED", "FAILURE", "failure", "Code scanning's CodeQL check is failure: new alerts?")]
    [InlineData("COMPLETED", "SUCCESS", "COMPLETED", "NEUTRAL", "success", "Copilot reviewed abcdef0; every check passed")]
    [InlineData("COMPLETED", "SUCCESS", "QUEUED", null, "pending", "Code scanning is still comparing abcdef0's CodeQL results")]
    public void CodeQL_must_have_run_green_on_the_head_and_code_scanning_must_not_have_failed(
        string? runStatus, string? runConclusion, string? checkStatus, string? checkConclusion, string state, string description)
    {
        var pr = Pr(noCodeqlRun: runStatus is null,
            codeqlRun: runStatus is null ? null : new RunFact(runStatus, runConclusion),
            codeqlCheck: checkStatus is null ? null : new RunFact(checkStatus, checkConclusion));

        var verdict = MergeRule.Evaluate(Config, pr)!;

        Assert.Equal((state, description), (verdict.State, verdict.Description));
    }

    [Fact]
    public void A_closed_pull_request_or_one_into_another_branch_is_not_judged()
    {
        Assert.Null(MergeRule.Evaluate(Config, Pr() with { State = "MERGED" }));
        Assert.Null(MergeRule.Evaluate(Config, Pr() with { BaseRef = "release" }));
    }

    [Fact]
    public void A_long_verdict_still_fits_GitHubs_140_character_description()
    {
        var verdict = MergeRule.Evaluate(Config, Pr(["src/AgentBridge.Transport/" + new string('a', 200) + ".cs"], draft: true, labels: ["hold"], reviews: []))!;

        Assert.True(verdict.Description.Length <= 140, verdict.Description);
    }

    [Theory]
    [InlineData("sensitive_path: [a]", "Property 'sensitive_path' not found")]
    [InlineData("base: main", "has no copilot_reviewer")]
    public void A_misspelled_or_missing_key_fails_rather_than_loosening_the_rule(string yaml, string message)
    {
        var e = Record.Exception(() => MergeRuleConfig.Parse(yaml));

        Assert.NotNull(e);
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Facts_come_from_the_head_only_with_CodeQL_from_its_latest_run_and_code_scanning_from_its_own_app()
    {
        using var doc = JsonDocument.Parse(GraphQLPullRequest(Head));

        var (facts, needsRest, changed) = MergeRuleCommand.ParseFacts(doc.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest"), Config);

        Assert.Equal(["copilot-pull-request-reviewer"], facts.Reviews.Where(r => r.IsBot).Select(r => r.Author));
        Assert.Equal(new RunFact("COMPLETED", "SUCCESS"), facts.CodeQlRun);
        // The workflow job named CodeQL is ignored; only code scanning's app speaks for it.
        Assert.Equal(new RunFact("COMPLETED", "NEUTRAL"), facts.CodeQlCheck);
        Assert.Equal("success", Assert.Single(facts.Statuses).State);
        Assert.True(needsRest); // a renamed file: GraphQL has no old path
        Assert.Equal(2, changed);
    }

    [Fact]
    public void Statuses_and_checks_of_a_commit_other_than_the_head_are_not_read()
    {
        using var doc = JsonDocument.Parse(GraphQLPullRequest(Head, commitOid: Older));

        var (facts, _, _) = MergeRuleCommand.ParseFacts(doc.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest"), Config);

        Assert.Empty(facts.Statuses);
        Assert.Null(facts.CodeQlRun);
    }

    [Fact]
    public void A_dry_run_posts_nothing_a_post_skips_a_status_that_already_says_the_same_and_a_renamed_files_old_path_counts()
    {
        var github = new FakeGitHub(GraphQLPullRequest(Head, mergeRule: ("success", "Copilot reviewed abcdef0; every check passed; independent review on file")));
        var config = WriteConfig();
        try
        {
            var dry = new StringWriter();
            Assert.Equal(0, MergeRuleCommand.Run(["--pr", "42", "--config", config], dry, github));
            Assert.Equal(0, MergeRuleCommand.Run(["--pr", "42", "--config", config, "--post"], new StringWriter(), github));
            var changed = new FakeGitHub(GraphQLPullRequest(Head, mergeRule: ("pending", "Waiting for a Copilot review of abcdef0")));
            Assert.Equal(0, MergeRuleCommand.Run(["--pr", "42", "--config", config, "--post", "--target-url", "https://run"], new StringWriter(), changed));

            Assert.Contains("dry run, not posted", dry.ToString(), StringComparison.Ordinal);
            // GraphQL lists the renamed file only by its new path; REST adds the old one.
            Assert.Contains("sensitive: docs/old.md", dry.ToString(), StringComparison.Ordinal);
            Assert.Empty(github.Posted);
            Assert.Equal([$"{Head} success merge-rule Copilot reviewed abcdef0; every check passed; independent review on file https://run"], changed.Posted);
        }
        finally
        {
            File.Delete(config);
        }
    }

    // GraphQL gives only each context's newest status, so a receipt posted since by anyone else
    // would hide the reviewer's: the history comes from REST.
    [Fact]
    public void A_reviewers_failed_receipt_still_counts_after_someone_else_posts_a_newer_one()
    {
        var github = new FakeGitHub(GraphQLPullRequest(Head, receiptCreator: "github-actions[bot]"), statuses: """
            [
              {"context": "independent-review", "state": "success", "description": "forged", "creator": {"login": "github-actions[bot]"}, "created_at": "2026-11-03T20:00:00Z"},
              {"context": "independent-review", "state": "failure", "description": "sec-42 found a bypass", "creator": {"login": "danswett"}, "created_at": "2026-11-03T19:00:00Z"}
            ]
            """);
        var config = WriteConfig();
        try
        {
            var output = new StringWriter();
            MergeRuleCommand.Run(["--pr", "42", "--config", config], output, github);

            Assert.Contains("failure: The independent review of abcdef0 failed: sec-42 found a bypass", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Fact]
    public void A_sweep_reads_a_pull_request_again_before_posting_so_a_label_added_since_isnt_overwritten()
    {
        var github = new FakeGitHub(one: GraphQLPullRequest(Head, labels: ["hold"]), listed: [GraphQLPullRequest(Head)]);
        var config = WriteConfig();
        try
        {
            MergeRuleCommand.Run(["--all", "--config", config, "--post"], new StringWriter(), github);

            Assert.Equal([$"{Head} failure merge-rule Held by the label 'hold' "], github.Posted);
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Fact]
    public void Pull_requests_sharing_a_head_get_the_worst_verdict_so_a_held_one_cant_ride_on_anothers_success()
    {
        var held = GraphQLPullRequest(Head, labels: ["hold"], number: 7);
        var github = new FakeGitHub(GraphQLPullRequest(Head), listed: [GraphQLPullRequest(Head), held],
            byNumber: new Dictionary<int, string> { [42] = GraphQLPullRequest(Head), [7] = held });
        var config = WriteConfig();
        try
        {
            MergeRuleCommand.Run(["--pr", "42", "--config", config, "--post"], new StringWriter(), github);

            Assert.Equal([$"{Head} failure merge-rule #7: Held by the label 'hold' "], github.Posted);
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Fact]
    public void The_same_verdict_posted_by_a_person_is_posted_again_by_the_workflow()
    {
        var github = new FakeGitHub(GraphQLPullRequest(Head, mergeRule: ("success", "Copilot reviewed abcdef0; every check passed; independent review on file"), mergeRuleCreator: "danswett"));
        var config = WriteConfig();
        try
        {
            MergeRuleCommand.Run(["--all", "--config", config, "--post"], new StringWriter(), github);

            Assert.Single(github.Posted);
        }
        finally
        {
            File.Delete(config);
        }
    }

    // The live list: what rule 5 means by a sensitive change, against paths that aren't one.
    [Theory]
    [InlineData("src/AgentBridge.Host/CallerGuard.cs", true)]
    [InlineData("src/AgentBridge.Host/SessionTransfers.cs", true)]
    [InlineData("src/AgentBridge.Host/Program.cs", true)]
    [InlineData("src/AgentBridge.Host/HostSettings.cs", true)]
    [InlineData("src/AgentBridge.App.Core/Transfers.cs", true)]
    [InlineData("pwa/js/transfer.js", true)]
    [InlineData("pwa/js/launch.js", true)]
    [InlineData("pwa/js/ui/launch-view.js", true)]
    [InlineData("pwa/js/answers.js", true)]
    [InlineData("pwa/js/answer-service.js", true)]
    [InlineData("pwa/js/host-session.js", true)]
    [InlineData("pwa/tools/build-site.mjs", true)]
    [InlineData("pwa/package-lock.json", true)]
    [InlineData("src/AgentBridge.Host.Delivery/KeyPlan.cs", true)]
    [InlineData("src/AgentBridge.Host/AgentBridge.Host.csproj", true)]
    [InlineData("AgentBridge.slnx", true)]
    [InlineData("src/AgentBridge.Transport/SecureChannel.cs", true)]
    [InlineData("src/AgentBridge.Host/TrustedPeerStore.cs", true)]
    [InlineData("pwa/js/host-client.js", true)]
    [InlineData(".github/workflows/ci.yml", true)]
    [InlineData("docs/status.md", false)]
    [InlineData("src/AgentBridge.App/Design/DesignKeys.cs", false)]
    [InlineData("pwa/js/markdown.js", false)]
    [InlineData("apple/Tests/AgentBridgeCoreTests/ProtocolTests.swift", false)]
    public void The_repositorys_sensitive_paths_take_in_authorization_delivery_and_the_build_but_not_docs_or_design(string path, bool sensitive)
    {
        var config = MergeRuleConfig.Load(Path.Combine(ChangedFiles.RepoRoot(AppContext.BaseDirectory), "quality", "merge-rule.yml"));

        Assert.Equal(sensitive, MergeRule.Evaluate(config, Pr([path]))!.SensitiveFiles.Count > 0);
    }

    [Fact]
    public void Any_red_check_on_the_head_fails_it_and_any_unfinished_one_keeps_it_waiting()
    {
        var red = MergeRule.Evaluate(Config, Pr() with { Checks = [new("lint", "success", "success"), new("analyze (csharp)", "failure", "failure"), new("native-clients", "failure", "cancelled")] })!;
        var running = MergeRule.Evaluate(Config, Pr() with { Checks = [new("pwa-ui", "success", "skipped"), new("native-clients", "pending", "in_progress")] })!;
        var green = MergeRule.Evaluate(Config, Pr() with { Checks = [new("pwa-ui", "success", "skipped"), new("CodeQL", "success", "neutral")] })!;

        Assert.Equal(("failure", "Check analyze (csharp) ended failure on abcdef0 (+1 more)"), (red.State, red.Description));
        Assert.Equal(("pending", "Waiting for native-clients"), (running.State, running.Description));
        Assert.Equal("success", green.State);
    }

    [Fact]
    public void Read_without_its_checks_a_pull_request_can_fail_but_never_pass()
    {
        var unread = MergeRule.Evaluate(Config, Pr() with { ChecksRead = false, CodeQlRun = null })!;
        var draft = MergeRule.Evaluate(Config, Pr(draft: true) with { ChecksRead = false, CodeQlRun = null })!;

        Assert.Equal(("pending", "Its checks on abcdef0 aren't read yet"), (unread.State, unread.Description));
        Assert.Equal(("failure", "Draft: mark it ready for review"), (draft.State, draft.Description));
    }

    [Fact]
    public void More_labels_than_the_rule_reads_fail_since_a_hold_could_be_among_them()
    {
        using var doc = JsonDocument.Parse(GraphQLPullRequest(Head, labels: ["area/app"], labelsTotal: 101));

        var (facts, _, _) = MergeRuleCommand.ParseFacts(doc.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest"), Config);

        Assert.Equal("failure", MergeRule.Evaluate(Config, facts)!.State);
    }

    [Fact]
    public void Each_workflow_counts_by_its_latest_run_and_that_runs_own_result_each_job_by_its_latest_attempt_and_the_merge_rules_own_not_at_all()
    {
        using var doc = JsonDocument.Parse(GraphQLPullRequest(Head,
            extraSuites:
            [
                // A draft's run that ready for review cancelled, then the run that replaced it.
                Suite("github-actions", Workflow(10, "CI"), "COMPLETED", "CANCELLED", Job(100, "product", "COMPLETED", "CANCELLED")),
                Suite("github-actions", Workflow(11, "CI"), "COMPLETED", "SUCCESS",
                    Job(110, "product", "COMPLETED", "FAILURE"), Job(111, "product", "COMPLETED", "SUCCESS"), Job(112, "pwa-ui", "COMPLETED", "SKIPPED")),
                Suite("github-actions", Workflow(12, "Apple clients"), "IN_PROGRESS", null, Job(120, "native-clients", "IN_PROGRESS")),
                // Cancelled between jobs: every job it made passed, but the run didn't.
                Suite("github-actions", Workflow(20, "HA contract drift"), "COMPLETED", "CANCELLED", Job(200, "drift", "COMPLETED", "SUCCESS")),
                // Failed jobs rerun green: GitHub updates the run's result, so it passes.
                Suite("github-actions", Workflow(21, "IaC scan"), "COMPLETED", "SUCCESS", Job(210, "psrule", "COMPLETED", "FAILURE"), Job(211, "psrule", "COMPLETED", "SUCCESS")),
                // Every job so far passed, but the run isn't over: a job that needs them is still to start.
                Suite("github-actions", Workflow(18, "Perf"), "IN_PROGRESS", null, Job(180, "perf", "COMPLETED", "SUCCESS")),
                TruncatedSuite("github-actions", Workflow(19, "Relay deploy"), Job(190, "scan", "COMPLETED", "SUCCESS")),
                Suite("github-actions", Workflow(13, "Merge rule"), "IN_PROGRESS", null, Job(130, "evaluate", "IN_PROGRESS")),
                Suite("another-app", null, "COMPLETED", "FAILURE", Job(140, "external", "COMPLETED", "FAILURE")),
                // No jobs: a workflow file that doesn't parse, a run not started, a run awaiting approval.
                Suite("github-actions", Workflow(15, "Workflow lint"), "COMPLETED", "STARTUP_FAILURE"),
                Suite("github-actions", Workflow(16, "Dependencies"), "QUEUED", null),
                Suite("github-actions", Workflow(17, "Merge rule review relay"), "COMPLETED", "ACTION_REQUIRED"),
            ],
            extraStatuses: [new { context = "ci/legacy", state = "ERROR", description = "broke", createdAt = "2026-11-03T20:02:00Z", creator = new { login = "someone" } }]));

        var (facts, _, _) = MergeRuleCommand.ParseFacts(doc.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest"), Config);

        Assert.Equal(
            ["Apple clients pending", "CodeQL success", "Dependencies pending", "HA contract drift failure", "Perf pending", "Relay deploy failure", "Workflow lint failure", "ci/legacy failure", "drift success", "external failure", "native-clients pending", "perf success", "product success", "psrule success", "pwa-ui success", "scan success"],
            facts.Checks.Select(c => $"{c.Name} {c.State}").Order(StringComparer.Ordinal));
    }

    // apple.yml (#260) sits outside quality-gate: a red native-clients must hold the merge on its own.
    [Fact]
    public void A_red_native_clients_job_fails_the_merge_rule_though_quality_gate_passed()
    {
        using var doc = JsonDocument.Parse(GraphQLPullRequest(Head, extraSuites:
        [
            Suite("github-actions", Workflow(30, "CI"), "COMPLETED", "SUCCESS", Job(300, "quality-gate", "COMPLETED", "SUCCESS")),
            Suite("github-actions", Workflow(31, "Apple clients"), "COMPLETED", "FAILURE", Job(310, "native-clients", "COMPLETED", "FAILURE")),
        ]));
        var (facts, _, _) = MergeRuleCommand.ParseFacts(doc.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest"), Config);

        var verdict = MergeRule.Evaluate(Config, facts)!;

        Assert.Equal(("failure", "Check native-clients ended failure on abcdef0 (+1 more)"), (verdict.State, verdict.Description));
    }

    // A workflow left out of merge-rule.yml's workflow_run list could still fail after the last
    // evaluation, and its red would never reach merge-rule until the schedule came round.
    [Fact]
    public void A_finished_run_of_every_other_workflow_has_the_merge_rule_judge_again()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        static YamlMappingNode Load(string file)
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(File.ReadAllText(file)));
            return (YamlMappingNode)stream.Documents[0].RootNode;
        }
        var names = Directory.EnumerateFiles(Path.Combine(root, ".github", "workflows"), "*.yml")
            .Select(f => Load(f).Children[new YamlScalarNode("name")].ToString())
            .Where(n => n != "Merge rule")
            .Order(StringComparer.Ordinal);
        var on = (YamlMappingNode)Load(Path.Combine(root, ".github", "workflows", "merge-rule.yml")).Children[new YamlScalarNode("on")];
        var listed = ((YamlSequenceNode)((YamlMappingNode)on.Children[new YamlScalarNode("workflow_run")])
            .Children[new YamlScalarNode("workflows")]).Children.Select(n => n.ToString()).Order(StringComparer.Ordinal);

        Assert.Equal(names, listed);
    }

    // The native Apple client (#260, #548) holds a transport, keys, signed commands and trust.
    [Theory]
    [InlineData("apple/Sources/AgentBridgeCore/NoiseChannel.swift")]
    [InlineData("apple/State/Keychain.swift")]
    [InlineData("apple/Apps/iOS/SignIn.swift")]
    [InlineData("apple/project.yml")]
    [InlineData("apple/Package.swift")]
    [InlineData("apple/scripts/build.sh")]
    public void An_apple_client_change_needs_the_independent_review(string path)
    {
        var config = MergeRuleConfig.Load(Path.Combine(ChangedFiles.RepoRoot(AppContext.BaseDirectory), "quality", "merge-rule.yml"));

        var verdict = MergeRule.Evaluate(config, Pr([path]))!;

        Assert.Equal(("failure", $"Needs an independent review of abcdef0: it touches {path}"), (verdict.State, verdict.Description));
    }

    // Reads the live repository, like CatalogTests: the ruleset must require only what something
    // posts, or every pull request waits forever; and nothing else may post merge-rule's name.
    [Fact]
    public void The_rulesets_required_checks_are_the_gate_and_the_merge_rule_and_no_workflow_job_takes_either_name_by_mistake()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        MergeRuleConfig.Load(Path.Combine(root, "quality", "merge-rule.yml"));
        static string[] Contexts(JsonElement checks) =>
            checks.EnumerateArray().Select(c => c.GetProperty("context").GetString()!).Order(StringComparer.Ordinal).ToArray();
        using var ruleset = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "quality", "ruleset-main.json")));
        using var classic = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "quality", "branch-protection-main.json")));
        var required = Contexts(ruleset.RootElement.GetProperty("rules").EnumerateArray()
            .Single(r => r.GetProperty("type").GetString() == "required_status_checks")
            .GetProperty("parameters").GetProperty("required_status_checks"));

        Assert.Equal(["merge-rule", "quality-gate"], required);
        // Migrating from the classic protection drops none of its checks.
        Assert.Empty(Contexts(classic.RootElement.GetProperty("required_status_checks").GetProperty("checks")).Except(required));
        var jobs = Directory.EnumerateFiles(Path.Combine(root, ".github", "workflows"), "*.yml")
            .SelectMany(f => Completeness.WorkflowJobs(File.ReadAllText(f)).Select(j => (File: Path.GetFileName(f), Job: j)))
            .ToList();
        Assert.DoesNotContain(jobs, j => j.Job == MergeRule.StatusContext);
        Assert.Contains(("ci.yml", "quality-gate"), jobs);
    }

    static string WriteConfig()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"merge-rule-{Guid.NewGuid():N}.yml");
        File.WriteAllText(path, """
            base: main
            copilot_reviewer: copilot-pull-request-reviewer
            hold_labels: [hold]
            independent_review: { context: independent-review, posters: [danswett] }
            codeql: { workflow: CodeQL, check: CodeQL }
            sensitive_paths: [docs/old.md]
            """);
        return path;
    }

    static object Suite(string slug, object? run, string status, string? conclusion, params object[] checkRuns) =>
        new { app = new { slug }, workflowRun = run, status, conclusion, checkRuns = new { nodes = checkRuns } };

    static object TruncatedSuite(string slug, object run, params object[] checkRuns) =>
        new { app = new { slug }, workflowRun = run, status = "COMPLETED", conclusion = "SUCCESS", checkRuns = new { totalCount = 70, nodes = checkRuns } };

    static object Workflow(long id, string name) => new { databaseId = id, @event = "pull_request", workflow = new { name } };

    static object Job(long id, string name, string status, string? conclusion = null) => new { databaseId = id, name, status, conclusion };

    static string GraphQLPullRequest(string head, string? commitOid = null, (string State, string Description)? mergeRule = null, string[]? labels = null, string receiptCreator = "danswett", string mergeRuleCreator = "github-actions", int number = 42, object[]? extraSuites = null, object[]? extraStatuses = null, int? labelsTotal = null)
    {
        var statuses = new List<object>
        {
            new { context = "independent-review", state = "SUCCESS", description = "sec-42 CLEAN", createdAt = "2026-11-03T20:00:00Z", creator = new { login = receiptCreator } },
        };
        if (mergeRule is { } m)
            statuses.Add(new { context = "merge-rule", state = m.State.ToUpperInvariant(), description = m.Description, createdAt = "2026-11-03T20:01:00Z", creator = new { login = mergeRuleCreator } });
        statuses.AddRange(extraStatuses ?? []);
        var pr = new
        {
            number,
            state = "OPEN",
            isDraft = false,
            baseRefName = "main",
            headRefOid = head,
            changedFiles = 2,
            labels = new { totalCount = labelsTotal ?? (labels ?? []).Length, nodes = (labels ?? []).Select(l => new { name = l }).ToArray() },
            files = new { pageInfo = new { hasNextPage = false }, nodes = new object[] { new { path = "docs/status.md", changeType = "MODIFIED" }, new { path = "docs/new.md", changeType = "RENAMED" } } },
            reviews = new
            {
                nodes = new object[]
                {
                    new { author = new { __typename = "User", login = "copilot-pull-request-reviewer" }, state = "COMMENTED", commit = new { oid = head } },
                    new { author = new { __typename = "Bot", login = "copilot-pull-request-reviewer" }, state = "COMMENTED", commit = new { oid = head } },
                },
            },
            commits = new
            {
                nodes = new object[]
                {
                    new
                    {
                        commit = new
                        {
                            oid = commitOid ?? head,
                            status = new { contexts = statuses },
                            checkSuites = new
                            {
                                nodes = new object[]
                                {
                                    Suite("github-actions", new { databaseId = 1, @event = "pull_request", workflow = new { name = "CodeQL" } }, "COMPLETED", "SKIPPED"),
                                    Suite("github-actions", new { databaseId = 3, @event = "pull_request", workflow = new { name = "CodeQL" } }, "COMPLETED", "SUCCESS"),
                                    Suite("github-actions", new { databaseId = 4, @event = "push", workflow = new { name = "CodeQL" } }, "COMPLETED", "FAILURE"),
                                    Suite("github-actions", new { databaseId = 5, @event = "pull_request", workflow = new { name = "Imposter" } }, "COMPLETED", "SUCCESS",
                                        new { databaseId = 99, name = "CodeQL", status = "COMPLETED", conclusion = "SUCCESS" }),
                                    Suite("github-advanced-security", null, "COMPLETED", "NEUTRAL",
                                        new { databaseId = 7, name = "CodeQL", status = "COMPLETED", conclusion = "NEUTRAL" }),
                                }.Concat(extraSuites ?? []).ToArray(),
                            },
                        },
                    },
                },
            },
        };
        return JsonSerializer.Serialize(new { data = new { repository = new { pullRequest = pr } } });
    }

    /// <summary>
    /// GitHub as the command sees it: <paramref name="one"/> answers a single pull request's query,
    /// <paramref name="listed"/> (or <paramref name="one"/>) the open list's, and
    /// <paramref name="statuses"/> REST's status history.
    /// </summary>
    sealed class FakeGitHub(string one, string[]? listed = null, string statuses = "[]", IReadOnlyDictionary<int, string>? byNumber = null) : IGitHub
    {
        public List<string> Posted { get; } = [];

        public JsonDocument GraphQL(string query, IReadOnlyDictionary<string, object> variables)
        {
            if (variables.TryGetValue("number", out var number))
                return JsonDocument.Parse(byNumber?.GetValueOrDefault((int)number) ?? one);
            var nodes = (listed ?? [one]).Select(json =>
            {
                using var pr = JsonDocument.Parse(json);
                return pr.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest").GetRawText();
            });
            return JsonDocument.Parse("""{"data": {"repository": {"pullRequests": {"pageInfo": {"hasNextPage": false, "endCursor": null}, "nodes": [""" + string.Join(",", nodes) + "]}}}}");
        }

        public JsonDocument Rest(string path) => JsonDocument.Parse(path.Contains("/statuses", StringComparison.Ordinal)
            ? statuses
            : """[{"filename": "docs/status.md"}, {"filename": "docs/new.md", "previous_filename": "docs/old.md"}]""");

        public void PostStatus(string slug, string sha, string state, string context, string description, string? targetUrl) =>
            Posted.Add($"{sha} {state} {context} {description} {targetUrl}");
    }
}
