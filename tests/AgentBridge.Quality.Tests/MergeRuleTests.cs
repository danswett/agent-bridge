using YamlDotNet.RepresentationModel;
using System.Text.Json;

namespace AgentBridge.Quality.Tests;

public class MergeRuleTests
{
    const string Head = "abcdef0123456789abcdef0123456789abcdef01";
    const string Older = "0000000123456789abcdef0123456789abcdef01";

    static readonly MergeRuleConfig Config = MergeRuleConfig.Parse("""
        base: main
        status_poster: github-actions
        copilot_reviewer: copilot-pull-request-reviewer
        hold_labels: [hold, do-not-merge]
        independent_review:
          context: independent-review
          posters: [danswett]
        security_marks:
          title_prefix: Security
          labels: [security]
        codeql:
          workflow: CodeQL
          check: CodeQL
        ignore_workflows: [Merge rule]
        main_green:
          workflow: ci.yml
          exempt_labels: [fixes-main]
        sensitive_paths:
          - .github/**
          - src/AgentBridge.Transport/**
          - src/**pair**
        """);

    static readonly MainFact MainGreen = new("success", "main's CI 2222222 passed");

    static readonly ReviewFact Copilot = new("copilot-pull-request-reviewer", IsBot: true, "COMMENTED", Head);
    static readonly RunFact Green = new("COMPLETED", "SUCCESS");
    const string MainCommit = "1111111123456789abcdef0123456789abcdef01";

    // The command's checkout as on a freshly fetched origin/main: its evaluator and rules are main's.
    static readonly Func<string, MergeRuleCommand.CheckoutFacts> OnMain = _ => new(MainCommit, MainCommit, Dirty: false);

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
        var verdict = MergeRule.Evaluate(Config, MainGreen, Pr())!;

        Assert.Equal("success", verdict.State);
        Assert.Equal("Copilot reviewed abcdef0; every check passed", verdict.Description);
    }

    [Fact]
    public void A_Copilot_review_of_an_earlier_commit_or_by_a_user_with_the_bots_name_leaves_it_waiting()
    {
        var earlier = MergeRule.Evaluate(Config, MainGreen, Pr(reviews: [Copilot with { CommitOid = Older }]))!;
        var impostor = MergeRule.Evaluate(Config, MainGreen, Pr(reviews: [Copilot with { IsBot = false }]))!;

        Assert.Equal(("pending", "Waiting for a Copilot review of abcdef0"), (earlier.State, earlier.Description));
        Assert.Equal("pending", impostor.State);
    }

    [Fact]
    public void A_sensitive_change_needs_a_listed_posters_receipt_on_the_head()
    {
        var files = new[] { "src/AgentBridge.Host/Pairing/Codes.cs", "docs/status.md" };

        var none = MergeRule.Evaluate(Config, MainGreen, Pr(files))!;
        // A workflow's token posting the receipt, as a pull request's own workflow could, counts for nothing.
        var byWorkflow = MergeRule.Evaluate(Config, MainGreen, Pr(files, statuses: [Receipt("success", "github-actions[bot]")]))!;
        var byReviewer = MergeRule.Evaluate(Config, MainGreen, Pr(files, statuses: [Receipt("success")]))!;

        Assert.Equal("failure", none.State);
        Assert.Equal("Needs an independent review of abcdef0: it touches src/AgentBridge.Host/Pairing/Codes.cs", none.Description);
        Assert.Equal(["src/AgentBridge.Host/Pairing/Codes.cs"], none.SensitiveFiles);
        Assert.Equal("failure", byWorkflow.State);
        Assert.Equal(("success", "Copilot reviewed abcdef0; every check passed; independent review on file"), (byReviewer.State, byReviewer.Description));
    }

    [Fact]
    public void Sensitive_paths_match_whatever_the_case() =>
        Assert.Equal("failure", MergeRule.Evaluate(Config, MainGreen, Pr(["SRC/agentbridge.transport/Channel.cs"]))!.State);

    [Fact]
    public void The_latest_receipt_counts_and_a_failed_one_blocks_even_a_change_that_needs_none()
    {
        var verdict = MergeRule.Evaluate(Config, MainGreen, Pr(statuses: [Receipt("success", minute: 1), Receipt("failure", minute: 2)]))!;

        Assert.Equal(("failure", "The independent review of abcdef0 failed: sec-42 failure"), (verdict.State, verdict.Description));
    }

    [Fact]
    public void A_file_list_GitHub_cut_short_counts_as_sensitive()
    {
        var verdict = MergeRule.Evaluate(Config, MainGreen, Pr(filesComplete: false))!;

        Assert.Equal(("failure", "Needs an independent review of abcdef0: its file list is incomplete"), (verdict.State, verdict.Description));
    }

    [Fact]
    public void A_draft_or_a_hold_label_fails_and_every_missing_piece_is_named_failures_first()
    {
        var verdict = MergeRule.Evaluate(Config, MainGreen, Pr(draft: true, labels: ["HOLD"], reviews: []))!;

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

        var verdict = MergeRule.Evaluate(Config, MainGreen, pr)!;

        Assert.Equal((state, description), (verdict.State, verdict.Description));
    }

    [Fact]
    public void A_closed_pull_request_or_one_into_another_branch_is_not_judged()
    {
        Assert.Null(MergeRule.Evaluate(Config, MainGreen, Pr() with { State = "MERGED" }));
        Assert.Null(MergeRule.Evaluate(Config, MainGreen, Pr() with { BaseRef = "release" }));
    }

    [Fact]
    public void A_long_verdict_still_fits_GitHubs_140_character_description()
    {
        var verdict = MergeRule.Evaluate(Config, MainGreen, Pr(["src/AgentBridge.Transport/" + new string('a', 200) + ".cs"], draft: true, labels: ["hold"], reviews: []))!;

        Assert.True(verdict.Description.Length <= 140, verdict.Description);
    }

    [Theory]
    [InlineData("sensitive_path: [a]", "Property 'sensitive_path' not found")]
    [InlineData("base: main", "has no status_poster, copilot_reviewer")]
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

        var facts = MergeRuleCommand.ParseFacts(doc.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest"), Config);

        Assert.Equal(["copilot-pull-request-reviewer"], facts.Reviews.Where(r => r.IsBot).Select(r => r.Author));
        Assert.Equal(new RunFact("COMPLETED", "SUCCESS"), facts.CodeQlRun);
        // The workflow job named CodeQL is ignored; only code scanning's app speaks for it.
        Assert.Equal(new RunFact("COMPLETED", "NEUTRAL"), facts.CodeQlCheck);
        Assert.Equal("success", Assert.Single(facts.Statuses).State);
        Assert.True(facts.FullyRead);
        Assert.Equal("A change", facts.Title);
    }

    [Fact]
    public void Statuses_and_checks_of_a_commit_other_than_the_head_are_not_read()
    {
        using var doc = JsonDocument.Parse(GraphQLPullRequest(Head, commitOid: Older));

        var facts = MergeRuleCommand.ParseFacts(doc.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest"), Config);

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
            Assert.Equal(0, MergeRuleCommand.Run(["--pr", "42", "--config", config], dry, github, OnMain));
            Assert.Equal(0, MergeRuleCommand.Run(["--pr", "42", "--config", config, "--post"], new StringWriter(), github, OnMain));
            var changed = new FakeGitHub(GraphQLPullRequest(Head, mergeRule: ("pending", "Waiting for a Copilot review of abcdef0")));
            Assert.Equal(0, MergeRuleCommand.Run(["--pr", "42", "--config", config, "--post", "--target-url", "https://run"], new StringWriter(), changed, OnMain));

            Assert.Contains("dry run, not posted", dry.ToString(), StringComparison.Ordinal);
            // The files are the evaluated head's, from compare, and a renamed file's old path counts.
            Assert.Contains($"repos/danswett/agent-bridge/compare/main...{Head}", github.RestPaths);
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
            MergeRuleCommand.Run(["--pr", "42", "--config", config], output, github, OnMain);

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
            MergeRuleCommand.Run(["--all", "--config", config, "--post"], new StringWriter(), github, OnMain);

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
            MergeRuleCommand.Run(["--pr", "42", "--config", config, "--post"], new StringWriter(), github, OnMain);

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
            MergeRuleCommand.Run(["--all", "--config", config, "--post"], new StringWriter(), github, OnMain);

            Assert.Single(github.Posted);
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Fact]
    public void A_path_with_a_line_break_or_any_control_character_needs_the_independent_review()
    {
        var acrossLines = MergeRule.Evaluate(Config, MainGreen, Pr(["src/AgentBridge.Transport/a\nb.cs"]))!;
        var bell = MergeRule.Evaluate(Config, MainGreen, Pr(["docs/a\u0007.md"]))!;
        var undecodable = MergeRule.Evaluate(Config, MainGreen, Pr(["docs/a\uFFFD.md"]))!;

        Assert.True(new Glob("src/AgentBridge.Relay/**").IsMatch("src/AgentBridge.Relay/a\nb.cs"));
        Assert.Equal(("failure", "Needs an independent review of abcdef0: it touches src/AgentBridge.Transport/a\\u000ab.cs"), (acrossLines.State, acrossLines.Description));
        Assert.Equal("failure", bell.State);
        Assert.Equal("failure", undecodable.State);
    }

    [Theory]
    [InlineData("Security: escape blank-looking characters in approvals", null)]
    [InlineData("SECURITY fix", null)]
    [InlineData("Escape blank-looking characters in approvals", "Security")]
    public void A_pull_request_marked_as_a_security_change_needs_the_receipt_whatever_it_touches(string title, string? label)
    {
        var pr = Pr(labels: label is null ? [] : [label]) with { Title = title };

        var none = MergeRule.Evaluate(Config, MainGreen, pr)!;
        var receipted = MergeRule.Evaluate(Config, MainGreen, pr with { Statuses = [Receipt("success")] })!;

        Assert.Equal(("failure", "Needs an independent review of abcdef0: it's marked as a security change"), (none.State, none.Description));
        Assert.Equal("success", receipted.State);
        Assert.Equal("success", MergeRule.Evaluate(Config, MainGreen, Pr() with { Title = "Docs: security notes" })!.State);
    }

    [Fact]
    public void A_head_whose_facts_cant_be_read_gets_error_not_its_earlier_success_and_a_hold_still_takes_effect()
    {
        var unread = GraphQLPullRequest(Head, mergeRule: ("success", "Copilot reviewed abcdef0; every check passed"));
        var held = GraphQLPullRequest(Older, labels: ["hold"], number: 7);
        var github = new FakeGitHub(unread, listed: [unread, held], byNumber: new Dictionary<int, string> { [42] = unread, [7] = held }, failChecks: true);
        var config = WriteConfig();
        try
        {
            var exit = MergeRuleCommand.Run(["--all", "--config", config, "--post"], new StringWriter(), github, OnMain);

            Assert.Equal(1, exit);
            Assert.Equal([$"{Head} error merge-rule Couldn't read #42 on abcdef0: gh: HTTP 502 ", $"{Older} failure merge-rule Held by the label 'hold' "], github.Posted);
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Fact]
    public void With_the_open_list_cut_short_no_head_gets_success_since_a_held_pull_request_on_it_may_be_unread()
    {
        var github = new FakeGitHub(GraphQLPullRequest(Head), listFailsAfterFirstPage: true);
        var config = WriteConfig();
        try
        {
            var shown = new StringWriter();
            var all = MergeRuleCommand.Run(["--all", "--config", config, "--post"], shown, github, OnMain);
            var one = MergeRuleCommand.Run(["--pr", "42", "--config", config, "--post"], new StringWriter(), github, OnMain);

            Assert.Equal((1, 1), (all, one));
            Assert.Equal(2, github.Posted.Count);
            Assert.All(github.Posted, p => Assert.StartsWith($"{Head} error merge-rule Couldn't read every open pull request, so one on abcdef0 may be unread: gh: HTTP 502", p, StringComparison.Ordinal));
            // What the log and the summary show is what was posted, not the pull request's own success.
            Assert.Contains("#42 abcdef0 error: Couldn't read every open pull request", shown.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(" success:", shown.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Fact]
    public void A_receipt_history_that_cant_be_read_drops_no_pull_request_and_a_hold_still_fails_it()
    {
        // #7's newest receipt isn't a listed poster's, so its history is needed, and that read fails.
        var held = GraphQLPullRequest(Older, labels: ["hold"], number: 7, receiptCreator: "github-actions[bot]");
        var green = GraphQLPullRequest(Head);
        var github = new FakeGitHub(green, listed: [held, green], byNumber: new Dictionary<int, string> { [7] = held, [42] = green }, failStatuses: true);
        var config = WriteConfig();
        try
        {
            MergeRuleCommand.Run(["--all", "--config", config, "--post"], new StringWriter(), github, OnMain);

            Assert.Equal([$"{Older} failure merge-rule Held by the label 'hold' ", $"{Head} success merge-rule Copilot reviewed abcdef0; every check passed; independent review on file "], github.Posted);
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Theory]
    [InlineData(Older, MainCommit, false, false)] // a pull request's worktree
    [InlineData(MainCommit, MainCommit, true, false)] // main's commit, with the rules edited locally
    [InlineData(MainCommit, MainCommit, false, true)] // main's commit, but --config names other rules
    [InlineData(MainCommit, null, false, false)] // origin/main never fetched
    public void Run_anywhere_but_origin_mains_checkout_it_refuses_unless_told_to_judge_anyway(string head, string? main, bool dirty, bool otherRules)
    {
        var github = new FakeGitHub(GraphQLPullRequest(Head));
        var config = WriteConfig();
        try
        {
            var refused = new StringWriter();
            var exit = MergeRuleCommand.Run(["--pr", "42", "--config", config, "--post"], refused, github, _ => new(head, main, dirty, otherRules));
            var allowed = new StringWriter();
            var allowedExit = MergeRuleCommand.Run(["--pr", "42", "--config", config, "--allow-non-main"], allowed, github, _ => new(head, main, dirty, otherRules));

            Assert.Equal(2, exit);
            Assert.Contains($"Evaluator and rules from {head}", refused.ToString(), StringComparison.Ordinal);
            Assert.Contains("Refusing", refused.ToString(), StringComparison.Ordinal);
            Assert.Empty(github.Posted);
            Assert.Equal(0, allowedExit);
            Assert.Contains("::warning::merge-rule: judging with the evaluator and rules from", allowed.ToString(), StringComparison.Ordinal);
            Assert.Contains("#42 abcdef0", allowed.ToString(), StringComparison.Ordinal);
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
    [InlineData("src/AgentBridge.App.Core/Machines.cs", true)]
    [InlineData("src/AgentBridge.App.Core/LaunchViewModel.cs", true)]
    [InlineData("src/AgentBridge.App.Core/HostConnection.cs", true)]
    [InlineData("src/AgentBridge.App.Core/Deliveries.cs", true)]
    [InlineData("src/AgentBridge.App.Core/QuestionViewModel.cs", true)]
    [InlineData("src/AgentBridge.App.Core/ReplyViewModel.cs", true)]
    [InlineData("src/AgentBridge.App.Core/EndSessionViewModel.cs", true)]
    [InlineData("src/AgentBridge.App.Core/ToastResponder.cs", true)]
    [InlineData("src/AgentBridge.App.Core/MachineTabs.cs", false)]
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
    [InlineData("docs/status.md", true)]
    [InlineData("AGENTS.md", true)]
    [InlineData("docs/app-windows.md", false)]
    [InlineData("src/AgentBridge.App/Design/DesignKeys.cs", false)]
    [InlineData("pwa/js/markdown.js", true)]
    [InlineData("pwa/js/hidden-text.js", true)]
    [InlineData("src/AgentBridge.App/QuestionView.cs", true)]
    [InlineData("src/AgentBridge.App.Core/HiddenText.cs", true)]
    [InlineData("src/AgentBridge.App.Core/MainViewModel.NeedsYou.cs", true)]
    [InlineData("pwa/js/ui/session-card.js", true)]
    [InlineData("pwa/js/launch-service.js", true)]
    [InlineData("pwa/js/reply-service.js", true)]
    [InlineData("pwa/js/stop-service.js", true)]
    [InlineData("pwa/js/ui/sessions-page.js", true)]
    [InlineData("pwa/js/ui/reply-view.js", true)]
    [InlineData("pwa/js/ui/usage-view.js", false)]
    [InlineData("src/AgentBridge.Host/HostControl.cs", true)]
    [InlineData("src/AgentBridge.Host/HostConfigFile.cs", true)]
    [InlineData("src/AgentBridge.App/LaunchView.xaml", true)]
    [InlineData("pwa/app.css", true)]
    [InlineData("pwa/js/ui/glyph.js", false)]
    [InlineData("apple/Tests/AgentBridgeCoreTests/ProtocolTests.swift", false)]
    public void The_repositorys_sensitive_paths_take_in_authorization_delivery_and_the_build_but_not_docs_or_design(string path, bool sensitive)
    {
        var config = MergeRuleConfig.Load(Path.Combine(ChangedFiles.RepoRoot(AppContext.BaseDirectory), "quality", "merge-rule.yml"));

        Assert.Equal(sensitive, MergeRule.Evaluate(config, MainGreen, Pr([path]))!.SensitiveFiles.Count > 0);
    }

    [Fact]
    public void Any_red_check_on_the_head_fails_it_and_any_unfinished_one_keeps_it_waiting()
    {
        var red = MergeRule.Evaluate(Config, MainGreen, Pr() with { Checks = [new("lint", "success", "success"), new("analyze (csharp)", "failure", "failure"), new("native-clients", "failure", "cancelled")] })!;
        var running = MergeRule.Evaluate(Config, MainGreen, Pr() with { Checks = [new("pwa-ui", "success", "skipped"), new("native-clients", "pending", "in_progress")] })!;
        var green = MergeRule.Evaluate(Config, MainGreen, Pr() with { Checks = [new("pwa-ui", "success", "skipped"), new("CodeQL", "success", "neutral")] })!;

        Assert.Equal(("failure", "Check analyze (csharp) ended failure on abcdef0 (+1 more)"), (red.State, red.Description));
        Assert.Equal(("pending", "Waiting for native-clients"), (running.State, running.Description));
        Assert.Equal("success", green.State);
    }

    [Fact]
    public void Read_without_its_checks_a_pull_request_can_fail_but_never_pass()
    {
        var unread = MergeRule.Evaluate(Config, MainGreen, Pr() with { FullyRead = false, CodeQlRun = null })!;
        var draft = MergeRule.Evaluate(Config, MainGreen, Pr(draft: true) with { FullyRead = false, CodeQlRun = null })!;

        Assert.Equal(("pending", "Its checks and files on abcdef0 aren't read yet"), (unread.State, unread.Description));
        Assert.Equal(("failure", "Draft: mark it ready for review"), (draft.State, draft.Description));
    }

    [Fact]
    public void More_labels_than_the_rule_reads_fail_since_a_hold_could_be_among_them()
    {
        using var doc = JsonDocument.Parse(GraphQLPullRequest(Head, labels: ["area/app"], labelsTotal: 101));

        var facts = MergeRuleCommand.ParseFacts(doc.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest"), Config);

        Assert.Equal("failure", MergeRule.Evaluate(Config, MainGreen, facts)!.State);
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

        var facts = MergeRuleCommand.ParseFacts(doc.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest"), Config);

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
        var facts = MergeRuleCommand.ParseFacts(doc.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest"), Config);

        var verdict = MergeRule.Evaluate(Config, MainGreen, facts)!;

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

        var verdict = MergeRule.Evaluate(config, MainGreen, Pr([path]))!;

        Assert.Equal(("failure", $"Needs an independent review of abcdef0: it touches {path}"), (verdict.State, verdict.Description));
    }

    // The workflow posts merge-rule only with the App's token, from the environment only main can
    // deploy from, and skips the status events its own posts start.
    [Fact]
    public void The_merge_rule_workflow_posts_with_the_apps_token_from_mains_environment_and_ignores_its_own_posts()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var stream = new YamlStream();
        stream.Load(new StringReader(File.ReadAllText(Path.Combine(root, ".github", "workflows", "merge-rule.yml"))));
        var job = (YamlMappingNode)((YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode)
            .Children[new YamlScalarNode("jobs")]).Children[new YamlScalarNode("evaluate")];
        string Scalar(YamlMappingNode node, string key) => ((YamlScalarNode)node.Children[new YamlScalarNode(key)]).Value!;
        var permissions = (YamlMappingNode)job.Children[new YamlScalarNode("permissions")];
        var steps = ((YamlSequenceNode)job.Children[new YamlScalarNode("steps")]).Cast<YamlMappingNode>().ToList();
        var mint = steps.Single(s => s.Children.ContainsKey(new YamlScalarNode("uses"))
            && Scalar(s, "uses").StartsWith("actions/create-github-app-token@", StringComparison.Ordinal));
        var judge = steps.Single(s => s.Children.ContainsKey(new YamlScalarNode("name")) && Scalar(s, "name") == "Judge and post merge-rule");
        var env = (YamlMappingNode)judge.Children[new YamlScalarNode("env")];

        Assert.Equal("merge-rule", Scalar(job, "environment"));
        Assert.Equal("read", Scalar(permissions, "statuses"));
        Assert.Matches("^actions/create-github-app-token@[0-9a-f]{40} ?", Scalar(mint, "uses"));
        Assert.Equal("${{ steps." + Scalar(mint, "id") + ".outputs.token }}", Scalar(env, "MERGE_RULE_POST_TOKEN"));
        Assert.True(steps.IndexOf(mint) < steps.IndexOf(judge));
        Assert.Contains("github.event.context != 'merge-rule'", Scalar(job, "if"));
        Assert.Equal("agent-bridge-merge-rule", MergeRuleConfig.Load(Path.Combine(root, "quality", "merge-rule.yml")).StatusPoster);
    }

    // Auto-merge doesn't pause while main is red, so the merge rule must: judged by main's CI
    // push run that finished last, at its latest attempt.
    [Fact]
    public void A_red_main_fails_a_pull_request_that_is_otherwise_ready()
    {
        var github = new FakeGitHub(GraphQLPullRequest(Head), mainRuns: MainRuns((5, "completed", "failure", 1), (4, "completed", "success", 1)));
        var config = WriteConfig();
        try
        {
            var exit = MergeRuleCommand.Run(["--all", "--config", config, "--post"], new StringWriter(), github, OnMain);

            Assert.Equal(0, exit);
            Assert.Equal([$"{Head} failure merge-rule Main is red (CI c500000): waiting for main to be fixed "], github.Posted);
            Assert.Contains("repos/danswett/agent-bridge/actions/workflows/ci.yml/runs?branch=main&event=push&per_page=20", github.RestPaths);
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Fact]
    public void A_green_main_lets_a_ready_pull_request_pass_once_its_run_is_the_latest_to_finish()
    {
        var github = new FakeGitHub(GraphQLPullRequest(Head), mainRuns: MainRuns((5, "completed", "success", 1), (4, "completed", "failure", 1)));
        var config = WriteConfig();
        try
        {
            var output = new StringWriter();
            MergeRuleCommand.Run(["--all", "--config", config, "--post"], output, github, OnMain);

            Assert.Contains("Main: ci.yml run 5 (attempt 1) on c500000 ended success.", output.ToString(), StringComparison.Ordinal);
            Assert.Equal([$"{Head} success merge-rule Copilot reviewed abcdef0; every check passed; independent review on file "], github.Posted);
        }
        finally
        {
            File.Delete(config);
        }
    }

    // A fix's own run still going doesn't free main: the latest finished run says it's red until
    // that run passes. A red run being re-run is no better: its last finished attempt was red.
    [Theory]
    [InlineData(6, "in_progress", 1, "Main is red (CI c500000): waiting for main to be fixed")]
    [InlineData(5, "queued", 2, "Main's CI c500000 is being re-run: waiting for main to be green")]
    public void A_main_run_still_going_after_a_red_one_still_blocks(long id, string status, int attempt, string description)
    {
        var runs = new List<(long, string, string?, int)> { (id, status, null, attempt) };
        if (id != 5)
            runs.Add((5, "completed", "failure", 1));
        runs.Add((4, "completed", "success", 1));
        var github = new FakeGitHub(GraphQLPullRequest(Head), mainRuns: MainRuns([.. runs]));
        var config = WriteConfig();
        try
        {
            MergeRuleCommand.Run(["--all", "--config", config, "--post"], new StringWriter(), github, OnMain);

            Assert.Equal([$"{Head} failure merge-rule {description} "], github.Posted);
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Fact]
    public void The_fixes_main_label_exempts_only_the_main_check_so_a_sensitive_fix_still_needs_its_receipt()
    {
        var red = new MainFact("failure", "main is red (CI c500000): waiting for main to be fixed");
        string[] files = ["src/AgentBridge.Transport/Channel.cs"];

        var unreviewed = MergeRule.Evaluate(Config, red, Pr(files, labels: ["fixes-main"]))!;
        var reviewed = MergeRule.Evaluate(Config, red, Pr(files, labels: ["Fixes-Main"], statuses: [Receipt("success")]))!;
        var unlabelled = MergeRule.Evaluate(Config, red, Pr(files, statuses: [Receipt("success")]))!;

        Assert.Equal(("failure", "Needs an independent review of abcdef0: it touches src/AgentBridge.Transport/Channel.cs"), (unreviewed.State, unreviewed.Description));
        Assert.Equal(("success", "Copilot reviewed abcdef0; every check passed; independent review on file; main isn't green, but it's labelled Fixes-Main"), (reviewed.State, reviewed.Description));
        Assert.Equal(("failure", "Main is red (CI c500000): waiting for main to be fixed"), (unlabelled.State, unlabelled.Description));
    }

    // Like any other read, main's CI unread or never finished fails every pull request it would
    // block, rather than let one merge onto a main no one could see.
    [Fact]
    public void Main_unreadable_or_with_no_finished_run_fails_closed()
    {
        var plain = GraphQLPullRequest(Head);
        var fix = GraphQLPullRequest(Older, labels: ["fixes-main"], number: 7);
        var unreadable = new FakeGitHub(plain, listed: [plain, fix], byNumber: new Dictionary<int, string> { [42] = plain, [7] = fix }, failMain: true);
        var unfinished = new FakeGitHub(plain, mainRuns: MainRuns((5, "in_progress", null, 1)));
        var config = WriteConfig();
        try
        {
            var exit = MergeRuleCommand.Run(["--all", "--config", config, "--post"], new StringWriter(), unreadable, OnMain);
            MergeRuleCommand.Run(["--all", "--config", config, "--post"], new StringWriter(), unfinished, OnMain);

            Assert.Equal(1, exit);
            Assert.Equal(
                [$"{Head} error merge-rule Couldn't read main's CI: gh: HTTP 502 ",
                 $"{Older} success merge-rule Copilot reviewed 0000000; every check passed; independent review on file; main isn't green, but it's labelled fixes-main "],
                unreadable.Posted);
            Assert.Equal([$"{Head} failure merge-rule No finished CI run on main: waiting for one "], unfinished.Posted);
        }
        finally
        {
            File.Delete(config);
        }
    }

    // JsonDocument.Parse throws JsonReaderException, a subclass of JsonException: a garbled
    // answer must fail closed like any other unreadable one, not crash the run.
    [Fact]
    public void A_garbled_answer_about_main_fails_closed_rather_than_crashing_the_run()
    {
        var github = new FakeGitHub(GraphQLPullRequest(Head), mainRuns: "<html>502 Bad Gateway</html>");
        var config = WriteConfig();
        try
        {
            var exit = MergeRuleCommand.Run(["--all", "--config", config, "--post"], new StringWriter(), github, OnMain);

            Assert.Equal(1, exit);
            Assert.StartsWith($"{Head} error merge-rule Couldn't read main's CI: ", Assert.Single(github.Posted), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(config);
        }
    }

    // CI finishing on main must re-judge every pull request, so their verdicts follow main to red
    // and back; main's other runs still start nothing.
    [Theory]
    [InlineData("workflow_run", "completed", "CI", "main", "push", "danswett/agent-bridge", null, true)]
    [InlineData("workflow_run", "requested", "CI", "main", "push", "danswett/agent-bridge", null, false)]
    [InlineData("workflow_run", "in_progress", "CI", "main", "push", "danswett/agent-bridge", null, false)]
    [InlineData("workflow_run", "completed", "Pages", "main", "push", "danswett/agent-bridge", null, false)]
    [InlineData("workflow_run", "completed", "CI", "main", "workflow_dispatch", "danswett/agent-bridge", null, false)]
    [InlineData("workflow_run", "requested", "CI", "quality/x", "pull_request", "danswett/agent-bridge", null, true)]
    [InlineData("workflow_run", "requested", "CI", "main", "pull_request", "someone/fork", null, true)]
    [InlineData("status", null, null, null, null, null, "merge-rule", false)]
    [InlineData("status", null, null, null, null, null, "independent-review", true)]
    public void The_merge_rule_workflow_judges_again_when_CI_finishes_on_main(string eventName, string? action, string? workflow, string? branch, string? runEvent, string? headRepo, string? context, bool runs)
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var stream = new YamlStream();
        stream.Load(new StringReader(File.ReadAllText(Path.Combine(root, ".github", "workflows", "merge-rule.yml"))));
        var job = (YamlMappingNode)((YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode)
            .Children[new YamlScalarNode("jobs")]).Children[new YamlScalarNode("evaluate")];
        var condition = ((YamlScalarNode)job.Children[new YamlScalarNode("if")]).Value!;
        var github = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["github.repository"] = "danswett/agent-bridge",
            ["github.event_name"] = eventName,
            ["github.event.action"] = action,
            ["github.event.context"] = context,
            ["github.event.workflow_run.name"] = workflow,
            ["github.event.workflow_run.head_branch"] = branch,
            ["github.event.workflow_run.event"] = runEvent,
            ["github.event.workflow_run.head_repository.full_name"] = headRepo,
        };

        Assert.Equal(runs, IfExpression.Evaluate(condition, github));
    }

    /// <summary>
    /// Just enough of GitHub's expression language for a job's <c>if</c>: (), !, ==, !=, &amp;&amp;
    /// and ||, over string literals and context values, compared without regard to case as
    /// GitHub does. A value the context doesn't hold is null, which equals no string.
    /// </summary>
    static class IfExpression
    {
        public static bool Evaluate(string condition, IReadOnlyDictionary<string, string?> context)
        {
            var tokens = System.Text.RegularExpressions.Regex.Matches(condition, @"\s*(\(|\)|&&|\|\||==|!=|!|'(?:[^']|'')*'|[A-Za-z_][A-Za-z0-9_.\-]*)\s*")
                .Select(m => m.Groups[1].Value).ToList();
            Assert.Equal(System.Text.RegularExpressions.Regex.Replace(condition, @"\s", ""), string.Concat(tokens).Replace(" ", "", StringComparison.Ordinal));
            var position = 0;
            var result = Or();
            Assert.Equal(tokens.Count, position);
            return Truthy(result);

            object? Or()
            {
                var left = And();
                while (position < tokens.Count && tokens[position] == "||")
                {
                    position++;
                    var right = And();
                    left = Truthy(left) || Truthy(right);
                }
                return left;
            }

            object? And()
            {
                var left = Compare();
                while (position < tokens.Count && tokens[position] == "&&")
                {
                    position++;
                    var right = Compare();
                    left = Truthy(left) && Truthy(right);
                }
                return left;
            }

            object? Compare()
            {
                var left = Primary();
                if (position < tokens.Count && tokens[position] is "==" or "!=")
                {
                    var equal = tokens[position++] == "==";
                    var right = Primary();
                    var same = left is string l && right is string r ? string.Equals(l, r, StringComparison.OrdinalIgnoreCase) : left is null && right is null;
                    return equal == same;
                }
                return left;
            }

            object? Primary()
            {
                var token = tokens[position++];
                if (token == "(")
                {
                    var inner = Or();
                    Assert.Equal(")", tokens[position++]);
                    return inner;
                }
                if (token == "!")
                    return !Truthy(Primary());
                if (token.StartsWith('\''))
                    return token[1..^1].Replace("''", "'", StringComparison.Ordinal);
                return context.TryGetValue(token, out var value) ? value : null;
            }

            static bool Truthy(object? value) => value switch { bool b => b, string s => s.Length > 0, _ => false };
        }
    }

    // The merge-rule GitHub App (docs/merge-rule-app.md).
    const long MergeRuleAppId = 5607979;

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
        // merge-rule must come from the merge-rule App, whose key only main's environment holds:
        // from GitHub Actions (15368), any pull request's own workflow could post it (#524's review).
        var apps = ruleset.RootElement.GetProperty("rules").EnumerateArray()
            .Single(r => r.GetProperty("type").GetString() == "required_status_checks")
            .GetProperty("parameters").GetProperty("required_status_checks").EnumerateArray()
            .ToDictionary(c => c.GetProperty("context").GetString()!, c => c.GetProperty("integration_id").GetInt64());
        Assert.Equal(15368, apps["quality-gate"]);
        Assert.Equal(MergeRuleAppId, apps["merge-rule"]);
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
            status_poster: github-actions
            copilot_reviewer: copilot-pull-request-reviewer
            hold_labels: [hold]
            independent_review: { context: independent-review, posters: [danswett] }
            codeql: { workflow: CodeQL, check: CodeQL }
            main_green: { workflow: ci.yml, exempt_labels: [fixes-main] }
            sensitive_paths: [docs/old.md]
            """);
        return path;
    }

    // Main's CI runs as REST lists them, newest first: an id, then its status, conclusion and attempt.
    static string MainRuns(params (long Id, string Status, string? Conclusion, int Attempt)[] runs) =>
        JsonSerializer.Serialize(new
        {
            workflow_runs = runs.Select(r => new { id = r.Id, head_sha = $"c{r.Id}".PadRight(40, '0'), status = r.Status, conclusion = r.Conclusion, run_attempt = r.Attempt }),
        });

    static object Suite(string slug, object? run, string status, string? conclusion, params object[] checkRuns) =>
        new { app = new { slug }, workflowRun = run, status, conclusion, checkRuns = new { nodes = checkRuns } };

    static object TruncatedSuite(string slug, object run, params object[] checkRuns) =>
        new { app = new { slug }, workflowRun = run, status = "COMPLETED", conclusion = "SUCCESS", checkRuns = new { totalCount = 70, nodes = checkRuns } };

    static object Workflow(long id, string name) => new { databaseId = id, @event = "pull_request", workflow = new { name } };

    static object Job(long id, string name, string status, string? conclusion = null) => new { databaseId = id, name, status, conclusion };

    static string GraphQLPullRequest(string head, string? commitOid = null, (string State, string Description)? mergeRule = null, string[]? labels = null, string receiptCreator = "danswett", string mergeRuleCreator = "github-actions", int number = 42, object[]? extraSuites = null, object[]? extraStatuses = null, int? labelsTotal = null, string title = "A change")
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
            title,
            state = "OPEN",
            isDraft = false,
            baseRefName = "main",
            headRefOid = head,
            labels = new { totalCount = labelsTotal ?? (labels ?? []).Length, nodes = (labels ?? []).Select(l => new { name = l }).ToArray() },
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
    /// <paramref name="listed"/> (or <paramref name="one"/>) the open list's,
    /// <paramref name="statuses"/> REST's status history, and <paramref name="mainRuns"/> (or one
    /// green run) main's CI.
    /// </summary>
    sealed class FakeGitHub(string one, string[]? listed = null, string statuses = "[]", IReadOnlyDictionary<int, string>? byNumber = null, string? files = null, bool failChecks = false, bool listFailsAfterFirstPage = false, bool failStatuses = false, string? mainRuns = null, bool failMain = false) : IGitHub
    {
        public List<string> Posted { get; } = [];

        public List<string> RestPaths { get; } = [];

        public JsonDocument GraphQL(string query, IReadOnlyDictionary<string, object> variables)
        {
            if (failChecks && query.Contains("checkSuites", StringComparison.Ordinal))
                throw new InvalidOperationException("gh: HTTP 502");
            if (variables.TryGetValue("number", out var number))
                return JsonDocument.Parse(byNumber?.GetValueOrDefault((int)number) ?? one);
            if (listFailsAfterFirstPage && variables.ContainsKey("cursor"))
                throw new InvalidOperationException("gh: HTTP 502");
            var nodes = (listed ?? [one]).Select(json =>
            {
                using var pr = JsonDocument.Parse(json);
                return pr.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest").GetRawText();
            });
            var pageInfo = listFailsAfterFirstPage ? """{"hasNextPage": true, "endCursor": "c1"}""" : """{"hasNextPage": false, "endCursor": null}""";
            return JsonDocument.Parse("""{"data": {"repository": {"pullRequests": {"pageInfo": """ + pageInfo + """, "nodes": [""" + string.Join(",", nodes) + "]}}}}");
        }

        public JsonDocument Rest(string path)
        {
            RestPaths.Add(path);
            if (path.Contains("/actions/workflows/", StringComparison.Ordinal))
                return failMain ? throw new InvalidOperationException("gh: HTTP 502") : JsonDocument.Parse(mainRuns ?? MainRuns((1, "completed", "success", 1)));
            if (failStatuses && path.Contains("/statuses", StringComparison.Ordinal))
                throw new InvalidOperationException("gh: HTTP 502");
            return JsonDocument.Parse(path.Contains("/statuses", StringComparison.Ordinal)
                ? statuses
                : files ?? """{"files": [{"filename": "docs/status.md"}, {"filename": "docs/new.md", "previous_filename": "docs/old.md"}]}""");
        }

        public void PostStatus(string slug, string sha, string state, string context, string description, string? targetUrl) =>
            Posted.Add($"{sha} {state} {context} {description} {targetUrl}");
    }
}
