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
        copilot_waiver:
          labels: [copilot-waived]
          second_review: independent-review-2
          one_review_paths: [docs/status.md]
        sensitive_paths:
          - .github/**
          - src/AgentBridge.Transport/**
          - src/**pair**
        """);

    static readonly MainFact MainGreen = new("success", "main's CI 2222222 passed");

    // Bodies as Copilot posted them on this repository's pull requests.
    const string GenuineReview = """
        <!-- ccr-overview-v2 -->

        ### 🟢 Approval recommended

        No active review issues require changes.

        **No open issues** 🎉

        <sub>Review effort: Balanced</sub>
        """;

    // #406's review 5774609210 on 73e3cca0 and #358's 5774844846 on 473c1db9: a COMMENTED review
    // by the bot on the exact head, which the rule once counted as Copilot's review.
    const string BillingError = "Copilot encountered an error: Your billing is not configured or you have Copilot licenses from multiple standalone organizations or enterprises. To use premium requests, select a billing entity via the GitHub site, under [Settings > Copilot > Features](/settings/copilot/features).";

    static readonly ReviewFact Copilot = new("copilot-pull-request-reviewer", IsBot: true, "COMMENTED", Head, GenuineReview);
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

    // Receipts as Engineering words them, naming the agent and its model.
    static StatusFact First(string model = "claude-opus-5.5", string state = "success") =>
        new("independent-review", state, $"CLEAN ENTIRE: security-review, {model} (Engineering)", "danswett", new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));

    static StatusFact Second(string model = "gpt-6.1-sol", string state = "success", string creator = "danswett") =>
        new("independent-review-2", state, $"sr-42b {(state == "success" ? "CLEAN" : "HIGH")}: security-review, {model}", creator, new DateTimeOffset(2026, 11, 3, 12, 1, 0, TimeSpan.Zero));

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
    public void Copilots_billing_error_on_the_exact_head_fails_rather_than_passing_as_its_review()
    {
        var verdict = MergeRule.Evaluate(Config, MainGreen, Pr(reviews: [Copilot with { Body = BillingError }]))!;

        Assert.Equal(("failure", "Copilot's review of abcdef0 didn't complete (it reported an error): request another"), (verdict.State, verdict.Description));
    }

    [Theory]
    [InlineData("Copilot encountered an error and was unable to review this pull request. You can try again by re-requesting a review.", "it reported an error")]
    [InlineData("Copilot wasn't able to review any files in this pull request. Copilot's per-file or total diff limits were exceeded.", "it couldn't review the change")]
    [InlineData("Copilot wasn't able to review this pull request because it exceeds the maximum number of lines (20,000).", "it couldn't review the change")]
    [InlineData("> [!WARNING]\n> Copilot encountered an error partway.\n\n<!-- ccr-overview-v2 -->\n\n### 🟢 Approval recommended", "it reported an error")]
    [InlineData("<!-- ccr-overview-v2 -->\n\n", "its overview is empty")]
    [InlineData("## Pull request overview\n\nLooks good.", "its outcome isn't recognized")]
    [InlineData("", "its outcome isn't recognized")]
    [InlineData(null, "its outcome isn't recognized")]
    public void An_error_unavailable_incomplete_or_unrecognized_Copilot_outcome_on_the_head_is_not_a_review(string? body, string why)
    {
        var verdict = MergeRule.Evaluate(Config, MainGreen, Pr(reviews: [Copilot with { Body = body }]))!;

        Assert.Equal(("failure", $"Copilot's review of abcdef0 didn't complete ({why}): request another"), (verdict.State, verdict.Description));
    }

    [Fact]
    public void A_completed_review_counts_even_beside_a_failed_retry_on_the_same_head()
    {
        var verdict = MergeRule.Evaluate(Config, MainGreen, Pr(reviews: [Copilot with { Body = BillingError }, Copilot]))!;

        Assert.Equal(("success", "Copilot reviewed abcdef0; every check passed"), (verdict.State, verdict.Description));
    }

    // As Copilot posted them on #632 (5767545859) and #558 (5767406640): an overview and findings
    // below, but the review says itself that it didn't run in full.
    [Theory]
    [InlineData("> [!WARNING]\n> Copilot couldn't run its full agentic review because it didn't start before the timeout. Make sure your repository has a runner available, or add a `copilot-code-review.yml` file specifying one with the `runs-on` attribute. See the [docs](https://gh.io/AA11tgch) for more details.\n\n")]
    [InlineData("> [!NOTE]\n> Copilot was unable to run its full agentic suite in this review.\n\n")]
    public void A_review_that_says_it_didnt_run_in_full_fails_even_with_an_overview_and_findings(string notice)
    {
        const string overview = "<!-- ccr-overview-v2 -->\n\n## Copilot review overview\n\n<details open>\n<summary><strong>2 open issues</strong></summary>\n\n- [Encode the default iOS app icon without an alpha channel](https://github.com/danswett/agent-bridge/pull/558#discussion_r4463254963)\n</details>";

        var verdict = MergeRule.Evaluate(Config, MainGreen, Pr(reviews: [Copilot with { Body = notice + overview }]))!;

        Assert.Equal(("failure", "Copilot's review of abcdef0 didn't complete (it was incomplete): request another"), (verdict.State, verdict.Description));
    }

    [Fact]
    public void A_completed_review_of_an_earlier_commit_doesnt_stand_in_for_an_error_on_the_head()
    {
        var verdict = MergeRule.Evaluate(Config, MainGreen, Pr(reviews: [Copilot with { CommitOid = Older }, Copilot with { Body = BillingError }]))!;

        Assert.Equal("failure", verdict.State);
    }

    [Fact]
    public void An_error_review_still_needs_the_independent_review_of_a_sensitive_change()
    {
        var verdict = MergeRule.Evaluate(Config, MainGreen, Pr([".github/workflows/ci.yml"], reviews: [Copilot with { Body = BillingError }]))!;

        Assert.Equal(["Copilot's review of abcdef0 didn't complete (it reported an error): request another", "needs an independent review of abcdef0: it touches .github/workflows/ci.yml"], verdict.Reasons);
    }

    // What every Copilot review ended with from 2026-11-05 18:02 PT (#955).
    const string QuotaError = "Copilot was unable to review this pull request because the user who requested the review has insufficient remaining quota.";

    [Fact]
    public void A_quota_failed_Copilot_review_is_waived_by_the_label_with_a_posters_success_receipt()
    {
        var verdict = MergeRule.Evaluate(Config, MainGreen, Pr(reviews: [Copilot with { Body = QuotaError }], labels: ["Copilot-Waived"], statuses: [Receipt("success")]))!;
        var noReview = MergeRule.Evaluate(Config, MainGreen, Pr(reviews: [], labels: ["copilot-waived"], statuses: [Receipt("success")]))!;

        Assert.Equal(("success", "Copilot review waived (label); independent review on file; every check passed"), (verdict.State, verdict.Description));
        Assert.Equal("success", noReview.State);
    }

    [Fact]
    public void The_waiver_label_without_a_receipt_fails_and_says_the_receipt_is_missing()
    {
        var verdict = MergeRule.Evaluate(Config, MainGreen, Pr(reviews: [Copilot with { Body = QuotaError }], labels: ["copilot-waived"]))!;

        Assert.Equal(["Copilot review waived by the label 'copilot-waived', but the waiver needs an independent review's receipt on abcdef0 in its place"], verdict.Reasons);
        Assert.Equal("failure", verdict.State);
    }

    [Fact]
    public void The_waiver_label_with_a_receipt_from_someone_not_a_poster_fails()
    {
        var verdict = MergeRule.Evaluate(Config, MainGreen, Pr(reviews: [], labels: ["copilot-waived"], statuses: [Receipt("success", creator: "github-actions")]))!;

        Assert.Equal(["Copilot review waived by the label 'copilot-waived', but the waiver needs an independent review's receipt on abcdef0 in its place"], verdict.Reasons);
    }

    [Fact]
    public void A_quota_failed_review_without_the_label_still_fails_even_with_a_receipt()
    {
        var verdict = MergeRule.Evaluate(Config, MainGreen, Pr(reviews: [Copilot with { Body = QuotaError }], statuses: [Receipt("success")]))!;

        Assert.Equal(("failure", "Copilot's review of abcdef0 didn't complete (its outcome isn't recognized): request another"), (verdict.State, verdict.Description));
    }

    [Fact]
    public void The_waiver_doesnt_lift_a_red_main_a_hold_a_draft_or_a_failed_receipt()
    {
        var red = new MainFact("failure", "main is red (CI c500000): waiting for main to be fixed");
        ReviewFact[] quota = [Copilot with { Body = QuotaError }];
        StatusFact[] clean = [Receipt("success")];

        var onRedMain = MergeRule.Evaluate(Config, red, Pr(reviews: quota, labels: ["copilot-waived"], statuses: clean))!;
        var held = MergeRule.Evaluate(Config, MainGreen, Pr(reviews: quota, labels: ["copilot-waived", "hold"], statuses: clean))!;
        var draft = MergeRule.Evaluate(Config, MainGreen, Pr(reviews: quota, labels: ["copilot-waived"], statuses: clean, draft: true))!;
        var failed = MergeRule.Evaluate(Config, MainGreen, Pr(reviews: quota, labels: ["copilot-waived"], statuses: [Receipt("success", minute: 0), Receipt("failure", minute: 5)]))!;

        Assert.Equal(["main is red (CI c500000): waiting for main to be fixed"], onRedMain.Reasons);
        Assert.Equal(["held by the label 'hold'"], held.Reasons);
        Assert.Equal(["draft: mark it ready for review"], draft.Reasons);
        Assert.All(new[] { onRedMain, held, draft, failed }, v => Assert.Equal("failure", v.State));
        Assert.Equal(
            ["Copilot review waived by the label 'copilot-waived', but the waiver needs an independent review's receipt on abcdef0 in its place",
             "the independent review of abcdef0 failed: sec-42 failure"],
            failed.Reasons);
    }

    [Fact]
    public void A_waived_sensitive_change_needs_a_second_receipt_from_another_model_family()
    {
        string[] files = [".github/workflows/ci.yml"];
        ReviewFact[] quota = [Copilot with { Body = QuotaError }];

        var oneFamily = MergeRule.Evaluate(Config, MainGreen, Pr(files, reviews: quota, labels: ["copilot-waived"], statuses: [First()]))!;
        var twoFamilies = MergeRule.Evaluate(Config, MainGreen, Pr(files, reviews: quota, labels: ["copilot-waived"], statuses: [First(), Second()]))!;

        Assert.Equal("failure", oneFamily.State);
        Assert.Equal(["Copilot review waived by the label 'copilot-waived', but it touches .github/workflows/ci.yml, so the waiver needs a second review's receipt (independent-review-2) on abcdef0, from another model family"], oneFamily.Reasons);
        Assert.Equal(("success", "Copilot review waived (label); two model families' reviews on file; every check passed"), (twoFamilies.State, twoFamilies.Description));
    }

    [Fact]
    public void Two_receipts_from_one_model_family_or_naming_no_model_dont_waive_a_sensitive_change()
    {
        string[] files = ["src/AgentBridge.Host/Pairing/Codes.cs"];
        ReviewFact[] quota = [Copilot with { Body = QuotaError }];
        const string why = "Copilot review waived by the label 'copilot-waived', but the two receipts on abcdef0 must each name a model, from different families";

        var sameFamily = MergeRule.Evaluate(Config, MainGreen, Pr(files, reviews: quota, labels: ["copilot-waived"], statuses: [First(), Second("claude-sonnet-5.5")]))!;
        var unnamed = MergeRule.Evaluate(Config, MainGreen, Pr(files, reviews: quota, labels: ["copilot-waived"], statuses: [Receipt("success"), Second()]))!;

        Assert.Equal([why], sameFamily.Reasons);
        Assert.Equal([why], unnamed.Reasons);
    }

    [Fact]
    public void A_second_receipt_from_someone_not_a_poster_doesnt_count()
    {
        var verdict = MergeRule.Evaluate(Config, MainGreen, Pr([".github/workflows/ci.yml"], reviews: [], labels: ["copilot-waived"], statuses: [First(), Second(creator: "github-actions")]))!;

        Assert.Equal("failure", verdict.State);
        Assert.Contains("needs a second review's receipt (independent-review-2)", verdict.Reasons[0]);
    }

    [Fact]
    public void A_security_marked_change_needs_the_second_family_even_when_it_touches_nothing_sensitive()
    {
        var verdict = MergeRule.Evaluate(Config, MainGreen, Pr(reviews: [], labels: ["copilot-waived"], statuses: [First()]) with { Title = "Security: bind approvals to what was shown" })!;

        Assert.Equal(["Copilot review waived by the label 'copilot-waived', but it's marked as a security change, so the waiver needs a second review's receipt (independent-review-2) on abcdef0, from another model family"], verdict.Reasons);
    }

    [Fact]
    public void A_failed_second_receipt_blocks_even_a_change_that_one_review_would_waive()
    {
        var verdict = MergeRule.Evaluate(Config, MainGreen, Pr(reviews: [], labels: ["copilot-waived"], statuses: [First(), Second(state: "failure")]))!;

        Assert.Equal("failure", verdict.State);
        Assert.Equal(["the second review of abcdef0 failed: sr-42b HIGH: security-review, gpt-6.1-sol"], verdict.Reasons);
    }

    [Fact]
    public void A_status_md_row_alone_is_waived_on_one_receipt_but_not_beside_another_sensitive_file()
    {
        var config = MergeRuleConfig.Parse("""
            base: main
            status_poster: github-actions
            copilot_reviewer: copilot-pull-request-reviewer
            independent_review:
              context: independent-review
              posters: [danswett]
            codeql:
              workflow: CodeQL
              check: CodeQL
            main_green:
              workflow: ci.yml
            copilot_waiver:
              labels: [copilot-waived]
              second_review: independent-review-2
              one_review_paths: [docs/status.md]
            sensitive_paths:
              - docs/status.md
              - .github/**
            """);

        var rowAlone = MergeRule.Evaluate(config, MainGreen, Pr(["docs/status.md", "docs/design/ui.md"], reviews: [], labels: ["copilot-waived"], statuses: [First()]))!;
        var withCi = MergeRule.Evaluate(config, MainGreen, Pr(["docs/status.md", ".github/workflows/ci.yml"], reviews: [], labels: ["copilot-waived"], statuses: [First()]))!;

        Assert.Equal(("success", "Copilot review waived (label); independent review on file; every check passed"), (rowAlone.State, rowAlone.Description));
        Assert.Equal("failure", withCi.State);
    }

    [Fact]
    public void A_waiver_label_with_no_second_review_context_refuses_to_load()
    {
        var error = Assert.Throws<FormatException>(() => MergeRuleConfig.Parse("""
            base: main
            status_poster: github-actions
            copilot_reviewer: copilot-pull-request-reviewer
            independent_review:
              context: independent-review
              posters: [danswett]
            codeql:
              workflow: CodeQL
              check: CodeQL
            main_green:
              workflow: ci.yml
            copilot_waiver:
              labels: [copilot-waived]
            sensitive_paths: [.github/**]
            """));

        Assert.Contains("copilot_waiver.second_review", error.Message);
    }

    [Theory]
    [InlineData("CLEAN: code-review agent, claude-opus-5.5 (Engineering)", "sr-1b CLEAN: security-review, gpt-6.1-sol", true)]
    [InlineData("CLEAN: claude-opus-5.5", "CLEAN: Gemini-4-argon", true)]
    [InlineData("CLEAN: claude-opus-5.5", "CLEAN: claude-haiku-5.5", false)]
    [InlineData("CLEAN: claude-opus-5.5 and gpt-6.1-sol", "CLEAN: gpt-5.6-terra", false)]
    [InlineData("sec-42 CLEAN", "CLEAN: gpt-6.1-sol", false)]
    [InlineData("CLEAN: claude-opus-5.5", "CLEAN by Gptbot", false)]
    [InlineData(null, "CLEAN: gpt-6.1-sol", false)]
    public void Model_families_are_read_from_the_model_each_receipt_names(string? first, string second, bool different)
    {
        Assert.Equal(different, MergeRule.DifferentFamilies(first, second));
    }

    [Fact]
    public void Removing_the_waiver_label_doesnt_discard_a_failed_second_review()
    {
        // sr-962b: Copilot completed, the first receipt passed, the second family failed; taking the
        // label off used to read the second receipt as absent and pass.
        var verdict = MergeRule.Evaluate(Config, MainGreen, Pr([".github/workflows/ci.yml"], statuses: [First(), Second(state: "failure")]))!;

        Assert.Equal("failure", verdict.State);
        Assert.Equal(["the second review of abcdef0 failed: sr-42b HIGH: security-review, gpt-6.1-sol"], verdict.Reasons);
    }

    [Fact]
    public void A_completed_Copilot_review_is_the_other_family_so_the_label_then_changes_nothing()
    {
        var labelled = MergeRule.Evaluate(Config, MainGreen, Pr([".github/workflows/ci.yml"], labels: ["copilot-waived"], statuses: [First()]))!;
        var unlabelled = MergeRule.Evaluate(Config, MainGreen, Pr([".github/workflows/ci.yml"], statuses: [First()]))!;

        Assert.Equal(("success", "Copilot reviewed abcdef0; every check passed; independent review on file"), (labelled.State, labelled.Description));
        Assert.Equal((labelled.State, labelled.Description), (unlabelled.State, unlabelled.Description));
    }

    [Theory]
    [InlineData("gemini-4-argon", "gpt-6.1-sol", "success")]
    [InlineData("claude-opus-5.5 and gpt-5.6-terra", "gpt-6.1-sol", "failure")]
    public void The_waiver_gate_passes_any_two_families_and_refuses_a_shared_one(string firstModel, string secondModel, string state)
    {
        var verdict = MergeRule.Evaluate(Config, MainGreen, Pr(["src/AgentBridge.Transport/Relay.cs"], reviews: [], labels: ["copilot-waived"], statuses: [First(firstModel), Second(secondModel)]))!;

        Assert.Equal(state, verdict.State);
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

    [Fact]
    public void The_billing_error_Copilot_posted_on_406_and_358s_heads_is_read_from_GitHub_and_fails_the_rule()
    {
        var github = new FakeGitHub(GraphQLPullRequest(Head, reviewBody: BillingError));
        var config = WriteConfig();
        try
        {
            MergeRuleCommand.Run(["--pr", "42", "--config", config, "--post"], new StringWriter(), github, OnMain);

            Assert.Equal([$"{Head} failure merge-rule Copilot's review of abcdef0 didn't complete (it reported an error): request another "], github.Posted);
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

    [Fact]
    public void The_second_review_receipt_is_judged_as_a_receipt_not_counted_as_a_check()
    {
        using var doc = JsonDocument.Parse(GraphQLPullRequest(Head,
            extraStatuses: [new { context = "independent-review-2", state = "FAILURE", description = "spoofed", createdAt = "2026-11-03T20:02:00Z", creator = new { login = "someone" } }]));

        var facts = MergeRuleCommand.ParseFacts(doc.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest"), Config);

        Assert.DoesNotContain(facts.Checks, c => c.Name == "independent-review-2");
        Assert.Contains(facts.Statuses, s => s.Context == "independent-review-2");
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

    // FLEET_MERGERULE=off must put back the wait, the build and the one shared group; the fast path
    // (cached evaluator, scoped judging) is only for when it isn't off.
    [Fact]
    public void The_merge_rule_workflows_kill_switch_restores_the_wait_the_build_and_the_single_group()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var stream = new YamlStream();
        stream.Load(new StringReader(File.ReadAllText(Path.Combine(root, ".github", "workflows", "merge-rule.yml"))));
        var job = (YamlMappingNode)((YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode)
            .Children[new YamlScalarNode("jobs")]).Children[new YamlScalarNode("evaluate")];
        string Scalar(YamlMappingNode node, string key) => ((YamlScalarNode)node.Children[new YamlScalarNode(key)]).Value!;
        var steps = ((YamlSequenceNode)job.Children[new YamlScalarNode("steps")]).Cast<YamlMappingNode>().ToList();
        YamlMappingNode Step(string name) => steps.Single(s => s.Children.ContainsKey(new YamlScalarNode("name")) && Scalar(s, "name") == name);
        var concurrency = (YamlMappingNode)job.Children[new YamlScalarNode("concurrency")];

        Assert.Equal("merge-rule", Scalar(concurrency, "group"));
        Assert.Equal("false", Scalar(concurrency, "cancel-in-progress"));
        Assert.Contains("vars.FLEET_MERGERULE == 'off'", Scalar(Step("Let a burst of events settle"), "if"), StringComparison.Ordinal);
        Assert.Contains("vars.FLEET_MERGERULE != 'off'", Scalar(Step("Restore main's built evaluator"), "if"), StringComparison.Ordinal);
        Assert.Contains("vars.FLEET_MERGERULE == 'off'", Scalar(Step("Build main's evaluator"), "if"), StringComparison.Ordinal);
        Assert.Contains("FAST: ${{ vars.FLEET_MERGERULE != 'off' }}", File.ReadAllText(Path.Combine(root, ".github", "workflows", "merge-rule.yml")), StringComparison.Ordinal);
    }

    // A sweep displaced by a scoped run for another head must not leave a stale success: while
    // main is red, any scoped run judges every open pull request and posts failure on each.
    [Theory]
    [InlineData("--sha")]
    [InlineData("--pr")]
    public void A_scoped_run_while_main_is_red_replaces_every_stale_success(string scope)
    {
        const string Success = "Copilot reviewed abcdef0; every check passed; independent review on file";
        var a = GraphQLPullRequest(Head, mergeRule: ("success", Success), number: 42);
        var b = GraphQLPullRequest(Older, mergeRule: ("success", Success), number: 7);
        var github = new FakeGitHub(a, listed: [a, b], byNumber: new Dictionary<int, string> { [42] = a, [7] = b },
            mainRuns: MainRuns((5, "completed", "failure", 1), (4, "completed", "success", 1)));
        var config = WriteConfig();
        try
        {
            var output = new StringWriter();
            MergeRuleCommand.Run([scope, scope == "--sha" ? Head : "42", "--config", config, "--post"], output, github, OnMain);

            Assert.Equal(2, github.Posted.Count);
            Assert.All(github.Posted, p => Assert.Contains(" failure merge-rule Main is red", p, StringComparison.Ordinal));
            Assert.Contains(github.Posted, p => p.StartsWith(Head, StringComparison.Ordinal));
            Assert.Contains(github.Posted, p => p.StartsWith(Older, StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Fact]
    public void A_scoped_run_while_main_is_green_judges_only_its_own_head()
    {
        var a = GraphQLPullRequest(Head, number: 42);
        var b = GraphQLPullRequest(Older, number: 7);
        var github = new FakeGitHub(a, listed: [a, b], byNumber: new Dictionary<int, string> { [42] = a, [7] = b });
        var config = WriteConfig();
        try
        {
            MergeRuleCommand.Run(["--sha", Head, "--config", config, "--post"], new StringWriter(), github, OnMain);

            Assert.Single(github.Posted);
            Assert.StartsWith(Head, github.Posted[0], StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(config);
        }
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

    // Main's CI concurrency group replaces a pending run with a newer one, cancelling it before it
    // starts: it tested nothing, so the run before it still decides, green or red, whether a newer
    // run is waiting or not.
    [Theory]
    [InlineData("success", "success", "Copilot reviewed abcdef0; every check passed; independent review on file")]
    [InlineData("failure", "failure", "Main is red (CI c500000): waiting for main to be fixed")]
    public void A_main_run_replaced_before_it_started_is_passed_over_for_the_run_before_it(string before, string state, string description)
    {
        var runs = MainRuns((7, "queued", null, 1), (6, "completed", "cancelled", 1), (5, "completed", before, 1));
        var empty = new FakeGitHub(GraphQLPullRequest(Head), mainRuns: runs, mainJobs: MainJobs());
        var unpicked = new FakeGitHub(GraphQLPullRequest(Head), mainRuns: runs, mainJobs: MainJobs((null, 0), (0, 0)));
        var config = WriteConfig();
        try
        {
            var output = new StringWriter();
            MergeRuleCommand.Run(["--all", "--config", config, "--post"], output, empty, OnMain);
            MergeRuleCommand.Run(["--all", "--config", config, "--post"], new StringWriter(), unpicked, OnMain);

            Assert.Equal([$"{Head} {state} merge-rule {description} "], empty.Posted);
            Assert.Equal([$"{Head} {state} merge-rule {description} "], unpicked.Posted);
            Assert.Contains("repos/danswett/agent-bridge/actions/runs/6/jobs?per_page=100", empty.RestPaths);
            Assert.Contains($"repos/danswett/agent-bridge/compare/{"c6".PadRight(40, '0')}...{"c7".PadRight(40, '0')}?per_page=1", empty.RestPaths);
            Assert.Contains($"Main: ci.yml run 5 (attempt 1) on c500000 ended {before}. Passed over run(s) 6 on c600000", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(config);
        }
    }

    // The newest run covers a replaced run's commit only if it builds on it: GitHub doesn't promise
    // pushes enter main's concurrency group in order, so a newest run on an older or diverged
    // commit (the replaced commit then never tested), or a compare that can't be read, blocks.
    [Theory]
    [InlineData("""{"status": "ahead"}""", "success", "Copilot reviewed abcdef0; every check passed; independent review on file")]
    [InlineData("""{"status": "identical"}""", "success", "Copilot reviewed abcdef0; every check passed; independent review on file")]
    [InlineData("""{"status": "behind"}""", "failure", "Main is red (CI c600000): waiting for main to be fixed")]
    [InlineData("""{"status": "diverged"}""", "failure", "Main is red (CI c600000): waiting for main to be fixed")]
    [InlineData(null, "error", "Couldn't read main's CI: gh: HTTP 502")]
    public void A_replaced_main_run_is_passed_over_only_when_the_newest_run_builds_on_its_commit(string? compare, string state, string description)
    {
        var github = new FakeGitHub(GraphQLPullRequest(Head), mainRuns: MainRuns((7, "queued", null, 1), (6, "completed", "cancelled", 1), (5, "completed", "success", 1)), mainJobs: MainJobs(), mainCompare: compare);
        var config = WriteConfig();
        try
        {
            MergeRuleCommand.Run(["--all", "--config", config, "--post"], new StringWriter(), github, OnMain);

            Assert.Equal([$"{Head} {state} merge-rule {description} "], github.Posted);
        }
        finally
        {
            File.Delete(config);
        }
    }

    // A passed run vouches only for main up to its own commit: an older run by id on a later
    // commit (runs created out of push order) that was replaced, failed or is being re-run blocks
    // until it passes, and so do main's later commits that can't all be listed.
    [Theory]
    [InlineData("completed", "cancelled", 1, true, "ahead", 1, "failure", "Main's CI hasn't passed on c600000, a commit after CI c700000: waiting for main to be green")]
    [InlineData("completed", "failure", 1, true, "ahead", 1, "failure", "Main's CI hasn't passed on c600000, a commit after CI c700000: waiting for main to be green")]
    [InlineData("in_progress", null, 2, true, "ahead", 1, "failure", "Main's CI hasn't passed on c600000, a commit after CI c700000: waiting for main to be green")]
    [InlineData("completed", "success", 2, true, "ahead", 1, "success", "Copilot reviewed abcdef0; every check passed; independent review on file")]
    [InlineData("completed", "cancelled", 1, false, "identical", 0, "success", "Copilot reviewed abcdef0; every check passed; independent review on file")]
    [InlineData("completed", "cancelled", 1, true, "ahead", 101, "failure", "Main's commits after CI c700000 can't all be listed: waiting for main's CI")]
    [InlineData("completed", "cancelled", 1, false, "diverged", 0, "failure", "Main's commits after CI c700000 can't all be listed: waiting for main's CI")]
    [InlineData("completed", "cancelled", 1, true, null, 1, "error", "Couldn't read main's CI: gh: HTTP 502")]
    public void A_passed_main_run_does_not_vouch_for_a_later_commit_whose_older_run_has_not_passed(string status, string? conclusion, int attempt, bool later, string? compare, int total, string state, string description)
    {
        var commits = new[] { new { sha = "c6".PadRight(40, '0') } }.Take(later ? 1 : 0);
        var after = compare is null ? null : JsonSerializer.Serialize(new { status = compare, total_commits = total, commits });
        var github = new FakeGitHub(GraphQLPullRequest(Head), mainRuns: MainRuns((7, "completed", "success", 1), (6, status, conclusion, attempt), (5, "completed", "success", 1)), mainAfter: after);
        var config = WriteConfig();
        try
        {
            MergeRuleCommand.Run(["--all", "--config", config, "--post"], new StringWriter(), github, OnMain);

            Assert.Equal([$"{Head} {state} merge-rule {description} "], github.Posted);
            Assert.Contains($"repos/danswett/agent-bridge/compare/{"c7".PadRight(40, '0')}...main?per_page=100", github.RestPaths);
        }
        finally
        {
            File.Delete(config);
        }
    }

    // A passed run's later commits each need their run found: one past the first page (runs aren't
    // promised to be created in push order) is read from the next few, and blocks unless it
    // passed; one not found within them, or never run, or a page that can't be read, fails closed.
    [Theory]
    [InlineData("cancelled", "failure", "Main's CI hasn't passed on c500000, a commit after CI c400000: waiting for main to be green")]
    [InlineData("passed", "success", "Copilot reviewed abcdef0; every check passed; independent review on file")]
    [InlineData("beyond", "failure", "Main's CI on c500000, a commit after CI c400000, can't be found: waiting for main's CI")]
    [InlineData("never", "failure", "Main's CI on c500000, a commit after CI c400000, can't be found: waiting for main's CI")]
    [InlineData("unreadable", "error", "Couldn't read main's CI: gh: HTTP 502")]
    public void A_passed_main_run_does_not_vouch_for_a_later_commit_whose_run_is_past_the_first_page(string where, string state, string description)
    {
        // Twenty finished runs, ids down from the given one; run 5 is the later commit's.
        static string Page(int from, int cancelled = 0) =>
            MainRuns([.. Enumerable.Range(from - 19, 20).Reverse().Select(id => ((long)id, "completed", (string?)(id == cancelled ? "cancelled" : "success"), 1))]);
        string?[] pages = where switch
        {
            "cancelled" => [Page(40), Page(20, cancelled: 5)],
            "passed" => [Page(40), Page(20)],
            "beyond" => [Page(40), Page(200), Page(180), Page(160), Page(140), Page(20, cancelled: 5)],
            "never" => [Page(40), MainRuns((20, "completed", "success", 1))],
            _ => [Page(40), null],
        };
        var after = JsonSerializer.Serialize(new { status = "ahead", total_commits = 1, commits = new[] { new { sha = "c5".PadRight(40, '0') } } });
        var github = new FakeGitHub(GraphQLPullRequest(Head), mainAfter: after, mainPages: pages);
        var config = WriteConfig();
        try
        {
            MergeRuleCommand.Run(["--all", "--config", config, "--post"], new StringWriter(), github, OnMain);

            Assert.Equal([$"{Head} {state} merge-rule {description} "], github.Posted);
            Assert.Contains("repos/danswett/agent-bridge/actions/workflows/ci.yml/runs?branch=main&event=push&per_page=20&page=2", github.RestPaths);
            Assert.DoesNotContain(github.RestPaths, p => p.EndsWith("&page=6", StringComparison.Ordinal) || (where == "never" && p.EndsWith("&page=3", StringComparison.Ordinal)));
        }
        finally
        {
            File.Delete(config);
        }
    }

    // Only a cancelled run proven never to have started, with a newer run after it, is passed over:
    // one a runner picked up, the newest run, a re-run, or one whose jobs can't be read or are cut
    // short could hide a real failure, so it still blocks, whether the run before it passed or
    // failed (a red main stays red: the newest finished run cancelled, nothing newer, run 5 red).
    // The newest run cancelled pending, by whatever took its place, never tested its commit: it
    // blocks until it's re-run.
    [Theory]
    [InlineData(7L, 1, "[[12,0]]", "success", "failure", "Main is red (CI c600000): waiting for main to be fixed")]
    [InlineData(7L, 1, "[[null,3]]", "success", "failure", "Main is red (CI c600000): waiting for main to be fixed")]
    [InlineData(0L, 1, "[]", "success", "failure", "Main is red (CI c600000): waiting for main to be fixed")]
    [InlineData(7L, 2, "[]", "success", "failure", "Main is red (CI c600000): waiting for main to be fixed")]
    [InlineData(7L, 1, "truncated", "success", "failure", "Main is red (CI c600000): waiting for main to be fixed")]
    [InlineData(7L, 1, null, "success", "error", "Couldn't read main's CI: gh: HTTP 502")]
    [InlineData(7L, 1, "[[12,0]]", "failure", "failure", "Main is red (CI c600000): waiting for main to be fixed")]
    [InlineData(0L, 1, "[]", "failure", "failure", "Main is red (CI c600000): waiting for main to be fixed")]
    [InlineData(7L, 2, "[]", "failure", "failure", "Main is red (CI c600000): waiting for main to be fixed")]
    [InlineData(7L, 1, "truncated", "failure", "failure", "Main is red (CI c600000): waiting for main to be fixed")]
    [InlineData(7L, 1, null, "failure", "error", "Couldn't read main's CI: gh: HTTP 502")]
    public void A_cancelled_main_run_not_proven_replaced_before_it_started_still_blocks(long newer, int attempt, string? jobs, string before, string state, string description)
    {
        var runs = new List<(long, string, string?, int)>();
        if (newer != 0)
            runs.Add((newer, "queued", null, 1));
        runs.Add((6, "completed", "cancelled", attempt));
        runs.Add((5, "completed", before, 1));
        var listed = jobs switch
        {
            null => null,
            "truncated" => """{"total_count": 101, "jobs": []}""",
            _ => MainJobs([.. JsonSerializer.Deserialize<long?[][]>(jobs)!.Select(j => (j[0], (int)j[1]!.Value))]),
        };
        var github = new FakeGitHub(GraphQLPullRequest(Head), mainRuns: MainRuns([.. runs]), mainJobs: listed);
        var config = WriteConfig();
        try
        {
            MergeRuleCommand.Run(["--all", "--config", config, "--post"], new StringWriter(), github, OnMain);

            Assert.Equal([$"{Head} {state} merge-rule {description} "], github.Posted);
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

    // A run's jobs as REST lists them: each with its runner (null before one picks it up) and steps.
    static string MainJobs(params (long? Runner, int Steps)[] jobs) =>
        JsonSerializer.Serialize(new
        {
            total_count = jobs.Length,
            jobs = jobs.Select(j => new { runner_id = j.Runner, steps = Enumerable.Range(1, j.Steps).Select(n => new { number = n }) }),
        });

    static object Suite(string slug, object? run, string status, string? conclusion, params object[] checkRuns) =>
        new { app = new { slug }, workflowRun = run, status, conclusion, checkRuns = new { nodes = checkRuns } };

    static object TruncatedSuite(string slug, object run, params object[] checkRuns) =>
        new { app = new { slug }, workflowRun = run, status = "COMPLETED", conclusion = "SUCCESS", checkRuns = new { totalCount = 70, nodes = checkRuns } };

    static object Workflow(long id, string name) => new { databaseId = id, @event = "pull_request", workflow = new { name } };

    static object Job(long id, string name, string status, string? conclusion = null) => new { databaseId = id, name, status, conclusion };

    static string GraphQLPullRequest(string head, string? commitOid = null, (string State, string Description)? mergeRule = null, string[]? labels = null, string receiptCreator = "danswett", string mergeRuleCreator = "github-actions", int number = 42, object[]? extraSuites = null, object[]? extraStatuses = null, int? labelsTotal = null, string title = "A change", string reviewBody = GenuineReview)
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
                    new { author = new { __typename = "User", login = "copilot-pull-request-reviewer" }, state = "COMMENTED", body = GenuineReview, commit = new { oid = head } },
                    new { author = new { __typename = "Bot", login = "copilot-pull-request-reviewer" }, state = "COMMENTED", body = reviewBody, commit = new { oid = head } },
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
    sealed class FakeGitHub(string one, string[]? listed = null, string statuses = "[]", IReadOnlyDictionary<int, string>? byNumber = null, string? files = null, bool failChecks = false, bool listFailsAfterFirstPage = false, bool failStatuses = false, string? mainRuns = null, bool failMain = false, string? mainJobs = null, string? mainCompare = """{"status": "ahead"}""", string? mainAfter = """{"status": "identical", "total_commits": 0, "commits": []}""", string?[]? mainPages = null) : IGitHub
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
            // Main's runs a page at a time when given pages (null: one that can't be read), past
            // the last none.
            if (mainPages is not null && path.Contains("/actions/workflows/", StringComparison.Ordinal))
            {
                var page = path.Contains("&page=", StringComparison.Ordinal) ? int.Parse(path[(path.LastIndexOf('=') + 1)..], System.Globalization.CultureInfo.InvariantCulture) : 1;
                return JsonDocument.Parse(page > mainPages.Length ? """{"workflow_runs": []}""" : mainPages[page - 1] ?? throw new InvalidOperationException("gh: HTTP 502"));
            }
            if (path.Contains("/actions/workflows/", StringComparison.Ordinal))
                return failMain ? throw new InvalidOperationException("gh: HTTP 502") : JsonDocument.Parse(mainRuns ?? MainRuns((1, "completed", "success", 1)));
            if (path.Contains("/actions/runs/", StringComparison.Ordinal))
                return JsonDocument.Parse(mainJobs ?? throw new InvalidOperationException("gh: HTTP 502"));
            // Main's commits after a passed run's, then two of main's commits compared, not a pull
            // request's head against main.
            if (path.Contains("...main?", StringComparison.Ordinal))
                return JsonDocument.Parse(mainAfter ?? throw new InvalidOperationException("gh: HTTP 502"));
            if (path.Contains("/compare/", StringComparison.Ordinal) && !path.Contains("/compare/main...", StringComparison.Ordinal))
                return JsonDocument.Parse(mainCompare ?? throw new InvalidOperationException("gh: HTTP 502"));
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
