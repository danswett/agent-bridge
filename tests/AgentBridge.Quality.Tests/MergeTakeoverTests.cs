using System.Text.Json;
using System.Text.Json.Nodes;
using static AgentBridge.Quality.Tests.MergeRuleTests;

namespace AgentBridge.Quality.Tests;

public sealed class MergeTakeoverTests : IDisposable
{
    const string MainCommit = "1111111123456789abcdef0123456789abcdef01";
    const long RulesetId = 26510567;
    static readonly DateTimeOffset Now = new(2026, 11, 4, 16, 30, 0, TimeSpan.Zero);
    static readonly Func<string, MergeRuleCommand.CheckoutFacts> OnMain = _ => new(MainCommit, MainCommit, Dirty: false);

    // Main's ruleset as GitHub returned it on 2026-11-04, server fields and defaults included.
    const string LiveRuleset = """
        {"id":26510567,"name":"main: the merge rule","target":"branch","source_type":"Repository","source":"danswett/agent-bridge","enforcement":"active","conditions":{"ref_name":{"exclude":[],"include":["~DEFAULT_BRANCH"]}},"rules":[{"type":"deletion"},{"type":"non_fast_forward"},{"type":"pull_request","parameters":{"required_approving_review_count":0,"dismiss_stale_reviews_on_push":false,"required_reviewers":[],"require_code_owner_review":false,"require_last_push_approval":false,"required_review_thread_resolution":true,"require_extra_approval_for_unattributed_changes":true,"allowed_merge_methods":["squash"]}},{"type":"required_status_checks","parameters":{"strict_required_status_checks_policy":false,"do_not_enforce_on_create":false,"required_status_checks":[{"context":"quality-gate","integration_id":15368},{"context":"merge-rule","integration_id":5607979}]}},{"type":"copilot_code_review","parameters":{"review_on_push":true,"review_draft_pull_requests":false}}],"node_id":"RRS_x","created_at":"2026-11-03T21:55:32.005-08:00","updated_at":"2026-11-04T08:34:36.774-08:00","bypass_actors":[],"current_user_can_bypass":"never","_links":{"self":{"href":"https://api.github.com/repos/danswett/agent-bridge/rulesets/26510567"}}}
        """;

    readonly string folder = Fixture.ScratchFolder();
    readonly string config;

    public MergeTakeoverTests()
    {
        config = Path.Combine(folder, "merge-rule.yml");
        File.WriteAllText(config, """
            base: main
            status_poster: agent-bridge-merge-rule
            copilot_reviewer: copilot-pull-request-reviewer
            hold_labels: [hold, do-not-merge]
            independent_review: { context: independent-review, posters: [danswett] }
            codeql: { workflow: CodeQL, check: CodeQL }
            ignore_workflows: [Merge rule]
            main_green: { workflow: ci.yml, exempt_labels: [fixes-main] }
            sensitive_paths: [secret/**]
            """);
    }

    public void Dispose() => Fixture.Delete(folder);

    string StatePath => Path.Combine(folder, "state", "merge-takeover.json");

    int Tick(FakeGitHub github, string? status, DateTimeOffset? at = null, bool dryRun = false, StringWriter? output = null)
    {
        string[] args = dryRun
            ? ["--state", StatePath, "--config", config, "--dry-run"]
            : ["--state", StatePath, "--config", config];
        return MergeTakeoverCommand.Run(args, output ?? new StringWriter(), github, new FakeStatusPage(status), at ?? Now, OnMain);
    }

    void InTakeover(DateTimeOffset since, params MergedPullRequest[] merged)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(
            new TakeoverState { InTakeover = true, EnteredAt = since, RulesetId = RulesetId, Merged = merged.ToList() },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    TakeoverState State() =>
        JsonSerializer.Deserialize<TakeoverState>(File.ReadAllText(StatePath), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;

    [Theory]
    [InlineData("degraded_performance", "none", false)] // the status page alone
    [InlineData("operational", "queued", false)] // our stall alone
    [InlineData(null, "queued", false)] // the status page unreadable: unknown never enters
    [InlineData("partial_outage", "queued", true)]
    [InlineData("major_outage", "stuck", true)] // shown in progress, every job done
    public void It_takes_over_only_when_the_status_page_and_merge_rules_own_runs_both_say_Actions_is_down(string? status, string stall, bool enters)
    {
        var github = new FakeGitHub(stall: stall);

        var exit = Tick(github, status);

        Assert.Equal(0, exit);
        Assert.Equal(enters ? 1 : 0, github.Puts.Count);
        Assert.Equal(enters ? 1 : 0, github.Comments.Count);
        Assert.Equal(enters, File.Exists(StatePath) && State().InTakeover);
    }

    [Theory]
    [InlineData("enforcement", false)]
    [InlineData("required check", false)]
    [InlineData("extra parameter", false)]
    [InlineData("bypass", true)]
    public void It_refuses_to_take_over_when_the_live_ruleset_has_drifted_or_already_has_a_bypass(string change, bool bypass)
    {
        var live = JsonNode.Parse(LiveRuleset)!.AsObject();
        var rules = live["rules"]!.AsArray();
        switch (change)
        {
            case "enforcement":
                live["enforcement"] = "evaluate";
                break;
            case "required check":
                rules[3]!["parameters"]!["required_status_checks"]!.AsArray().RemoveAt(1);
                break;
            case "extra parameter":
                rules[2]!["parameters"]!["required_reviewers"] = new JsonArray(new JsonObject { ["reviewer"] = 1 });
                break;
            case "bypass":
                live["bypass_actors"] = new JsonArray(MergeTakeoverCommand.AdminBypass.DeepClone());
                break;
        }
        var github = new FakeGitHub(stall: "queued", live: live);
        var output = new StringWriter();

        var exit = Tick(github, "major_outage", output: output);

        Assert.Equal(1, exit);
        Assert.Contains("Refusing to take over", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(bypass ? "bypass actor" : "doesn't match", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(github.Puts);
        Assert.Empty(github.Comments);
        Assert.Empty(github.Merges);
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public void Taking_over_puts_the_ruleset_back_unchanged_but_for_one_admin_pull_request_bypass_keeps_a_backup_and_posts_a_receipt()
    {
        var github = new FakeGitHub(stall: "queued");

        var exit = Tick(github, "degraded_performance");

        Assert.Equal(0, exit);
        var (path, body) = Assert.Single(github.Puts);
        Assert.Equal($"repos/danswett/agent-bridge/rulesets/{RulesetId}", path);
        var put = JsonNode.Parse(body)!.AsObject();
        Assert.True(JsonNode.DeepEquals(new JsonArray(MergeTakeoverCommand.AdminBypass.DeepClone()), put["bypass_actors"]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(LiveRuleset)!["rules"], put["rules"]));
        Assert.False(put.ContainsKey("id") || put.ContainsKey("_links") || put.ContainsKey("source") || put.ContainsKey("updated_at"));
        var receipt = Assert.Single(github.Comments);
        Assert.Contains("automatic takeover of merging", receipt, StringComparison.Ordinal);
        Assert.Contains("2026-11-04 08:30 PST", receipt, StringComparison.Ordinal); // Pacific
        Assert.Contains("Actions is degraded_performance", receipt, StringComparison.Ordinal);
        Assert.Contains("stalled for 20 min", receipt, StringComparison.Ordinal);
        Assert.Contains("--match-head-commit", receipt, StringComparison.Ordinal);
        var state = State();
        Assert.True(state.InTakeover);
        Assert.Equal(LiveRuleset.Trim(), JsonNode.Parse(File.ReadAllText(state.BackupPath!))!.ToJsonString());
    }

    [Fact]
    public void Handing_back_takes_two_healthy_ticks_4_minutes_apart_then_removes_the_bypass_verifies_and_names_what_was_merged()
    {
        InTakeover(Now.AddHours(-1), new MergedPullRequest(611, "6110000000000000000000000000000000000000", Now.AddMinutes(-30)));
        var github = new FakeGitHub(stall: "none", recovered: true, bypass: true);

        Assert.Equal(0, Tick(github, "operational"));
        Assert.Equal(0, Tick(github, "major_outage", Now.AddMinutes(2))); // an unhealthy tick starts the count again
        Assert.Equal(0, Tick(github, "operational", Now.AddMinutes(3)));
        Assert.Equal(0, Tick(github, "operational", Now.AddMinutes(6)));
        Assert.Empty(github.Puts);
        Assert.Equal(0, Tick(github, "operational", Now.AddMinutes(7)));

        var (_, body) = Assert.Single(github.Puts);
        Assert.Empty(JsonNode.Parse(body)!["bypass_actors"]!.AsArray());
        var receipt = Assert.Single(github.Comments);
        Assert.Contains("takeover ended", receipt, StringComparison.Ordinal);
        Assert.Contains("verified: 0 bypass actors", receipt, StringComparison.Ordinal);
        Assert.Contains("lasted 1 h 7 min", receipt, StringComparison.Ordinal);
        Assert.Contains("#611", receipt, StringComparison.Ordinal);
        Assert.False(State().InTakeover);
    }

    [Fact]
    public void A_hand_back_whose_ruleset_doesnt_verify_exits_1_and_asks_the_owner()
    {
        InTakeover(Now.AddHours(-1));
        var github = new FakeGitHub(stall: "none", recovered: true, bypass: true, putKeepsBypass: true);

        Tick(github, "operational", Now.AddMinutes(-5));
        var output = new StringWriter();
        var exit = Tick(github, "operational", output: output);

        Assert.Equal(1, exit);
        Assert.Single(github.Puts);
        Assert.Contains("did NOT verify", output.ToString(), StringComparison.Ordinal);
        var receipt = Assert.Single(github.Comments);
        Assert.Contains("did NOT verify", receipt, StringComparison.Ordinal);
        Assert.Contains("@danswett", receipt, StringComparison.Ordinal);
        Assert.True(State().NeedsOwner);
    }

    [Fact]
    public void A_hand_back_whose_PUT_fails_stays_in_takeover_and_a_receipt_that_fails_to_post_goes_out_next_tick()
    {
        InTakeover(Now.AddHours(-1));
        var github = new FakeGitHub(stall: "none", recovered: true, bypass: true, failPuts: 1, failComments: 1);

        Tick(github, "operational", Now.AddMinutes(-5));
        var exit = Tick(github, "operational");

        Assert.Equal(2, exit);
        Assert.True(State().InTakeover);
        Assert.Empty(github.Comments);

        exit = Tick(github, "operational", Now.AddMinutes(2)); // the PUT lands, the receipt doesn't

        Assert.Equal(2, exit);
        Assert.False(State().InTakeover);
        Assert.Empty(github.Comments);
        Assert.Single(State().PendingComments);

        exit = Tick(github, "operational", Now.AddMinutes(4));

        Assert.Equal(0, exit);
        Assert.Contains("takeover ended", Assert.Single(github.Comments), StringComparison.Ordinal);
        Assert.Empty(State().PendingComments);
    }

    [Theory]
    [InlineData(20, false)]
    [InlineData(35, true)]
    public void After_handing_back_it_takes_over_again_within_15_minutes_only_on_a_stall_30_minutes_old(int stallMinutes, bool enters)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(new TakeoverState { ExitedAt = Now.AddMinutes(-5) }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var github = new FakeGitHub(stall: "queued", stallMinutes: stallMinutes);
        var output = new StringWriter();

        Tick(github, "major_outage", output: output);

        Assert.Equal(enters ? 1 : 0, github.Puts.Count);
        Assert.Contains(enters ? "Decision: enter" : "Decision: hold off", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void In_a_takeover_it_merges_one_armed_unheld_pull_request_that_main_evaluator_passes_with_quality_gate_green_at_the_head_it_judged()
    {
        InTakeover(Now.AddMinutes(-20));
        var github = new FakeGitHub(stall: "queued", prs:
        [
            new(1, Sha(1), Armed: false),
            new(2, Sha(2), Labels: ["hold"]),
            new(3, Sha(3), Gate: false),
            new(4, Sha(4), Unresolved: 1),
            new(5, Sha(5), Mergeable: "CONFLICTING"),
            new(6, Sha(6), Draft: true),
            new(7, Sha(7)),
            new(8, Sha(8)),
        ]);
        var output = new StringWriter();

        var exit = Tick(github, "major_outage", output: output);

        Assert.Equal(0, exit);
        Assert.Equal(new List<(int, string)> { (7, Sha(7)) }, github.Merges);
        Assert.Contains("#3 3333333: not merged: quality-gate isn't green", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(new[] { 7 }, State().Merged.Select(m => m.Number));

        Tick(github, "major_outage", Now.AddMinutes(3));

        Assert.Equal(new List<(int, string)> { (7, Sha(7)), (8, Sha(8)) }, github.Merges);
    }

    [Fact]
    public void Outside_a_takeover_it_never_merges()
    {
        var github = new FakeGitHub(stall: "none", recovered: true, prs: [new(7, Sha(7))]);

        Tick(github, "major_outage");
        Tick(github, "operational", Now.AddMinutes(5));

        Assert.Empty(github.Merges);
        Assert.Empty(github.Puts);
    }

    [Fact]
    public void A_dry_run_says_what_it_would_do_and_writes_nothing()
    {
        var github = new FakeGitHub(stall: "queued", prs: [new(7, Sha(7))]);
        var output = new StringWriter();

        var exit = Tick(github, "major_outage", dryRun: true, output: output);

        Assert.Equal(0, exit);
        Assert.Contains("Decision: enter", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("#7 7777777: would merge", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(github.Puts);
        Assert.Empty(github.Comments);
        Assert.Empty(github.Merges);
        Assert.False(Directory.Exists(Path.GetDirectoryName(StatePath)));
    }

    static string Sha(int n) => new(n.ToString(System.Globalization.CultureInfo.InvariantCulture)[0], 40);

    sealed record Pr(int Number, string Head, bool Armed = true, bool Draft = false, string[]? Labels = null, int Unresolved = 0, string Mergeable = "MERGEABLE", bool Gate = true);

    sealed class FakeStatusPage(string? status) : IStatusPage
    {
        public string? ActionsStatus() => status;
    }

    /// <summary>
    /// GitHub during (or after) an Actions incident: merge-rule's runs stalled one way or another,
    /// main's ruleset as it was live, and the open pull requests. Writes are recorded, and a PUT
    /// changes the ruleset's bypass as GitHub would, unless told to keep it.
    /// </summary>
    sealed class FakeGitHub(string stall, int stallMinutes = 20, bool recovered = false, JsonObject? live = null, bool bypass = false, bool putKeepsBypass = false, Pr[]? prs = null, int failPuts = 0, int failComments = 0) : IGitHub
    {
        readonly JsonObject ruleset = Ruleset(live, bypass);
        readonly Pr[] prs = prs ?? [];
        int failPuts = failPuts;
        int failComments = failComments;

        public List<(string Path, string Body)> Puts { get; } = [];
        public List<string> Comments { get; } = [];
        public List<(int Number, string Sha)> Merges { get; } = [];

        static JsonObject Ruleset(JsonObject? live, bool bypass)
        {
            var ruleset = live ?? JsonNode.Parse(LiveRuleset)!.AsObject();
            if (bypass)
                ruleset["bypass_actors"] = new JsonArray(MergeTakeoverCommand.AdminBypass.DeepClone());
            return ruleset;
        }

        IEnumerable<Pr> Open => prs.Where(p => !Merges.Any(m => m.Number == p.Number));

        public JsonDocument GraphQL(string query, IReadOnlyDictionary<string, object> variables)
        {
            if (query.Contains("autoMergeRequest", StringComparison.Ordinal))
                return JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    data = new
                    {
                        repository = new
                        {
                            pullRequests = new
                            {
                                pageInfo = new { hasNextPage = false, endCursor = (string?)null },
                                nodes = Open.Select(p => new
                                {
                                    number = p.Number,
                                    isDraft = p.Draft,
                                    headRefOid = p.Head,
                                    mergeable = p.Mergeable,
                                    autoMergeRequest = p.Armed ? new { enabledAt = "2026-11-04T15:00:00Z" } : null,
                                    labels = new { totalCount = (p.Labels ?? []).Length, nodes = (p.Labels ?? []).Select(l => new { name = l }) },
                                    reviewThreads = new { totalCount = p.Unresolved + 1, nodes = Enumerable.Range(0, p.Unresolved + 1).Select(i => new { isResolved = i > 0 || p.Unresolved == 0 }) },
                                }),
                            },
                        },
                    },
                }));
            if (variables.TryGetValue("number", out var number))
                return JsonDocument.Parse(Facts(prs.Single(p => p.Number == (int)number)));
            var nodes = Open.Select(p =>
            {
                using var pr = JsonDocument.Parse(Facts(p));
                return pr.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest").GetRawText();
            });
            return JsonDocument.Parse("""{"data": {"repository": {"pullRequests": {"pageInfo": {"hasNextPage": false, "endCursor": null}, "nodes": [""" + string.Join(",", nodes) + "]}}}}");
        }

        // What main's evaluator reads: Copilot's review and green CodeQL on the head, a stale
        // merge-rule failure that Actions couldn't replace (it counts neither way), and
        // quality-gate's job unless the pull request lacks it.
        static string Facts(Pr p) => GraphQLPullRequest(p.Head, number: p.Number, labels: p.Labels, mergeRule: ("failure", "Waiting for Copilot"), mergeRuleCreator: "agent-bridge-merge-rule",
            extraSuites: p.Gate ? [Suite("github-actions", Workflow(20 + p.Number, "CI"), "COMPLETED", "SUCCESS", Job(100 + p.Number, "quality-gate", "COMPLETED", "SUCCESS"))] : []);

        public JsonDocument Rest(string path)
        {
            string Runs(params object[] runs) => JsonSerializer.Serialize(new { workflow_runs = runs });
            var stalledSince = Now.AddMinutes(-stallMinutes).ToString("O");
            var json = path switch
            {
                _ when path.Contains("merge-rule.yml/runs?status=queued", StringComparison.Ordinal) =>
                    stall == "queued" ? Runs(new { id = 1, created_at = stalledSince, run_started_at = stalledSince }) : Runs(),
                _ when path.Contains("merge-rule.yml/runs?status=in_progress", StringComparison.Ordinal) =>
                    stall == "stuck" ? Runs(new { id = 2, created_at = stalledSince, run_started_at = stalledSince }) : Runs(),
                _ when path.Contains("merge-rule.yml/runs?status=success", StringComparison.Ordinal) =>
                    Runs(new { id = 3, updated_at = (recovered ? Now.AddMinutes(-2) : Now.AddHours(-1)).ToString("O") }),
                _ when path.Contains("/actions/runs/2/jobs", StringComparison.Ordinal) =>
                    """{"jobs": [{"status": "completed"}, {"status": "completed"}]}""",
                _ when path.Contains("/actions/workflows/ci.yml/runs", StringComparison.Ordinal) =>
                    """{"workflow_runs": [{"id": 9, "head_sha": "c900000000000000000000000000000000000000", "status": "completed", "conclusion": "success", "run_attempt": 1}]}""",
                _ when path.Contains("/compare/", StringComparison.Ordinal) => """{"files": [{"filename": "docs/app-windows.md"}]}""",
                _ when path.Contains("/statuses", StringComparison.Ordinal) => "[]",
                _ when path.EndsWith("/rulesets?per_page=100", StringComparison.Ordinal) =>
                    $$"""[{"id": 1, "name": "another"}, {"id": {{RulesetId}}, "name": "main: the merge rule"}]""",
                _ when path.EndsWith($"/rulesets/{RulesetId}", StringComparison.Ordinal) => ruleset.ToJsonString(),
                _ => throw new InvalidOperationException($"unexpected GET {path}"),
            };
            return JsonDocument.Parse(json);
        }

        public JsonDocument Send(string method, string path, string jsonBody)
        {
            if (method == "PUT")
            {
                if (failPuts-- > 0)
                    throw new InvalidOperationException("gh: HTTP 502");
                Puts.Add((path, jsonBody));
                var put = JsonNode.Parse(jsonBody)!.AsObject();
                if (!(putKeepsBypass && put["bypass_actors"]!.AsArray().Count == 0))
                    ruleset["bypass_actors"] = put["bypass_actors"]!.DeepClone();
            }
            else if (method == "POST" && path.EndsWith("/issues/170/comments", StringComparison.Ordinal))
            {
                if (failComments-- > 0)
                    throw new InvalidOperationException("gh: HTTP 502");
                Comments.Add(JsonNode.Parse(jsonBody)!["body"]!.GetValue<string>());
            }
            else
                throw new InvalidOperationException($"unexpected {method} {path}");
            return JsonDocument.Parse("{}");
        }

        public void MergePullRequest(string slug, int number, string headSha) => Merges.Add((number, headSha));

        public void PostStatus(string slug, string sha, string state, string context, string description, string? targetUrl) =>
            throw new InvalidOperationException("merge-takeover never posts a status");
    }
}
