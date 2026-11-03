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
        Assert.Equal("Copilot reviewed abcdef0; CodeQL passed", verdict.Description);
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
        Assert.Equal(("success", "Copilot reviewed abcdef0; CodeQL passed; independent review on file"), (byReviewer.State, byReviewer.Description));
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
    [InlineData("COMPLETED", "SUCCESS", "COMPLETED", "NEUTRAL", "success", "Copilot reviewed abcdef0; CodeQL passed")]
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
        var github = new FakeGitHub(GraphQLPullRequest(Head, mergeRule: ("success", "Copilot reviewed abcdef0; CodeQL passed; independent review on file")));
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
            Assert.Equal([$"{Head} success merge-rule Copilot reviewed abcdef0; CodeQL passed; independent review on file https://run"], changed.Posted);
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
        var github = new FakeGitHub(one: GraphQLPullRequest(Head, labels: ["hold"]), listed: GraphQLPullRequest(Head));
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

    static string GraphQLPullRequest(string head, string? commitOid = null, (string State, string Description)? mergeRule = null, string[]? labels = null, string receiptCreator = "danswett")
    {
        var statuses = new List<object>
        {
            new { context = "independent-review", state = "SUCCESS", description = "sec-42 CLEAN", createdAt = "2026-11-03T20:00:00Z", creator = new { login = receiptCreator } },
        };
        if (mergeRule is { } m)
            statuses.Add(new { context = "merge-rule", state = m.State.ToUpperInvariant(), description = m.Description, createdAt = "2026-11-03T20:01:00Z", creator = new { login = "github-actions[bot]" } });
        object Suite(string slug, object? run, string status, string? conclusion, params object[] checkRuns) =>
            new { app = new { slug }, workflowRun = run, status, conclusion, checkRuns = new { nodes = checkRuns } };
        var pr = new
        {
            number = 42,
            state = "OPEN",
            isDraft = false,
            baseRefName = "main",
            headRefOid = head,
            changedFiles = 2,
            labels = new { nodes = (labels ?? []).Select(l => new { name = l }).ToArray() },
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
                                        new { databaseId = 99, status = "COMPLETED", conclusion = "FAILURE" }),
                                    Suite("github-advanced-security", null, "COMPLETED", "NEUTRAL",
                                        new { databaseId = 7, status = "COMPLETED", conclusion = "NEUTRAL" }),
                                },
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
    sealed class FakeGitHub(string one, string? listed = null, string statuses = "[]") : IGitHub
    {
        public List<string> Posted { get; } = [];

        public JsonDocument GraphQL(string query, IReadOnlyDictionary<string, object> variables)
        {
            if (variables.ContainsKey("number"))
                return JsonDocument.Parse(one);
            using var pr = JsonDocument.Parse(listed ?? one);
            var node = pr.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest");
            return JsonDocument.Parse("""{"data": {"repository": {"pullRequests": {"pageInfo": {"hasNextPage": false, "endCursor": null}, "nodes": [""" + node.GetRawText() + "]}}}}");
        }

        public JsonDocument Rest(string path) => JsonDocument.Parse(path.Contains("/statuses", StringComparison.Ordinal)
            ? statuses
            : """[{"filename": "docs/status.md"}, {"filename": "docs/new.md", "previous_filename": "docs/old.md"}]""");

        public void PostStatus(string slug, string sha, string state, string context, string description, string? targetUrl) =>
            Posted.Add($"{sha} {state} {context} {description} {targetUrl}");
    }
}
