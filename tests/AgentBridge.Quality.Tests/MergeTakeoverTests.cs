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

    int Tick(FakeGitHub github, string? status, DateTimeOffset? at = null, bool dryRun = false, StringWriter? output = null, string[]? extra = null, Func<string, MergeRuleCommand.CheckoutFacts>? checkout = null)
    {
        string[] args = ["--state", StatePath, "--config", config, .. dryRun ? ["--dry-run"] : Array.Empty<string>(), .. extra ?? []];
        return MergeTakeoverCommand.Run(args, output ?? new StringWriter(), github, new FakeStatusPage(status), at ?? Now, checkout ?? OnMain);
    }

    void InTakeover(DateTimeOffset since, params MergedPullRequest[] merged) =>
        WriteState(new TakeoverState { InTakeover = true, EnteredAt = since, RulesetId = RulesetId, Merged = merged.ToList() });

    void WriteState(TakeoverState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
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
    [InlineData("enforcement")]
    [InlineData("required check")]
    [InlineData("extra parameter")]
    public void Outside_a_takeover_a_drifted_ruleset_exits_1_changes_nothing_and_asks_the_owner_once(string change)
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
        }
        var github = new FakeGitHub(stall: "queued", live: live, prs: [new(7, Sha(7))]);
        var output = new StringWriter();

        var exit = Tick(github, "major_outage", output: output);
        Tick(github, "major_outage", Now.AddMinutes(3));

        Assert.Equal(1, exit);
        Assert.Contains("needs @danswett", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Decision: enter", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(github.Puts);
        Assert.Contains("@danswett", Assert.Single(github.Comments), StringComparison.Ordinal);
        Assert.Empty(github.Merges);
        Assert.False(State().InTakeover);
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
    public void A_hand_back_whose_ruleset_has_drifted_exits_1_and_asks_the_owner()
    {
        InTakeover(Now.AddHours(-1));
        var live = JsonNode.Parse(LiveRuleset)!.AsObject();
        live["enforcement"] = "evaluate";
        var github = new FakeGitHub(stall: "none", recovered: true, live: live, bypass: true);

        Tick(github, "operational", Now.AddMinutes(-5));
        var output = new StringWriter();
        var exit = Tick(github, "operational", output: output);
        Tick(github, "operational", Now.AddMinutes(3)); // the owner isn't asked again

        Assert.Equal(1, exit);
        Assert.Single(github.Puts);
        Assert.Contains("did NOT verify", output.ToString(), StringComparison.Ordinal);
        var receipt = Assert.Single(github.Comments);
        Assert.Contains("did NOT verify", receipt, StringComparison.Ordinal);
        Assert.Contains("@danswett", receipt, StringComparison.Ordinal);
        Assert.True(State().NeedsOwner);
        Assert.False(State().InTakeover);
    }

    [Fact]
    public void A_hand_back_that_reads_back_only_its_own_bypass_stays_in_takeover_and_tries_again_next_tick_even_when_the_PUT_said_it_succeeded()
    {
        InTakeover(Now.AddHours(-1));
        var github = new FakeGitHub(stall: "none", recovered: true, bypass: true, putKeepsBypass: true);

        Tick(github, "operational", Now.AddMinutes(-5));
        var exit = Tick(github, "operational");

        Assert.Equal(2, exit);
        Assert.True(State().InTakeover);
        Assert.False(State().NeedsOwner);
        Assert.Empty(github.Comments);

        github.PutKeepsBypass = false;
        exit = Tick(github, "operational", Now.AddMinutes(2));

        Assert.Equal(0, exit);
        Assert.Equal(2, github.Puts.Count);
        Assert.Empty(github.BypassActors);
        Assert.Contains("takeover ended", Assert.Single(github.Comments), StringComparison.Ordinal);
        Assert.False(State().InTakeover);
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
            new(3, Sha(3), Gate: "none"),
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
        Assert.Contains("#3 3333333: not merged: quality-gate from GitHub Actions isn't green", output.ToString(), StringComparison.Ordinal);
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

    [Theory]
    [InlineData("head")]
    [InlineData("threads")]
    [InlineData("auto-merge")]
    [InlineData("hold")]
    [InlineData("draft")]
    public void A_pull_request_that_changes_between_the_candidates_query_and_the_merge_isnt_merged(string change)
    {
        InTakeover(Now.AddMinutes(-20));
        var pr = new Pr(7, Sha(7));
        var github = new FakeGitHub(stall: "queued", prs: [pr]);
        github.Later[7] = change switch
        {
            "head" => pr with { Head = Sha(9) },
            "threads" => pr with { Unresolved = 1 },
            "auto-merge" => pr with { Armed = false },
            "hold" => pr with { Labels = ["hold"] },
            _ => pr with { Draft = true },
        };
        var output = new StringWriter();

        var exit = Tick(github, "major_outage", output: output);

        Assert.Equal(0, exit);
        Assert.Empty(github.Merges);
        Assert.Contains("#7 7777777: not merged:", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(State().Merged);
    }

    [Theory]
    [InlineData("main red")]
    [InlineData("sensitive without a receipt")]
    public void In_a_takeover_a_pull_request_main_evaluator_fails_isnt_merged(string why)
    {
        InTakeover(Now.AddMinutes(-20));
        var pr = why == "main red" ? new Pr(7, Sha(7)) : new Pr(7, Sha(7), Files: ["secret/key.cs"], Receipt: false);
        var github = new FakeGitHub(stall: "queued", prs: [pr], mainRed: why == "main red");
        var output = new StringWriter();

        Tick(github, "major_outage", output: output);

        Assert.Empty(github.Merges);
        Assert.Contains("#7 7777777: not merged: main's evaluator says failure", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("other app")]
    public void A_quality_gate_that_isnt_GitHub_Actions_own_check_doesnt_count(string gate)
    {
        InTakeover(Now.AddMinutes(-20));
        var github = new FakeGitHub(stall: "queued", prs: [new(7, Sha(7), Gate: gate)]);
        var output = new StringWriter();

        Tick(github, "major_outage", output: output);

        Assert.Empty(github.Merges);
        Assert.Contains("quality-gate from GitHub Actions isn't green", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void After_its_merge_cap_a_takeover_stops_merging_and_asks_the_owner_once()
    {
        InTakeover(Now.AddMinutes(-60), Enumerable.Range(1, 10).Select(n => new MergedPullRequest(600 + n, Sha(1), Now.AddMinutes(-50 + n))).ToArray());
        var github = new FakeGitHub(stall: "queued", prs: [new(7, Sha(7))]);

        Tick(github, "major_outage");
        Tick(github, "major_outage", Now.AddMinutes(3));

        Assert.Empty(github.Merges);
        var note = Assert.Single(github.Comments);
        Assert.Contains("@danswett", note, StringComparison.Ordinal);
        Assert.Contains("--max-merges", note, StringComparison.Ordinal);

        Tick(github, "major_outage", Now.AddMinutes(6), extra: ["--max-merges", "11"]);

        Assert.Equal(new List<(int, string)> { (7, Sha(7)) }, github.Merges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_take_over_whose_PUT_fails_is_a_takeover_only_if_the_bypass_landed(bool landed)
    {
        var github = new FakeGitHub(stall: "queued", failPuts: 1, failedPutLands: landed);

        var exit = Tick(github, "major_outage");

        Assert.Equal(landed ? 0 : 2, exit);
        Assert.Equal(landed, State().InTakeover);
        Assert.Equal(landed ? 1 : 0, github.BypassActors.Count);
        Assert.Equal(landed ? 1 : 0, github.Comments.Count);
    }

    [Fact]
    public void Our_bypass_with_no_state_is_removed_verified_and_receipted()
    {
        var github = new FakeGitHub(stall: "none", recovered: true, bypass: true);
        var output = new StringWriter();

        var exit = Tick(github, "operational", output: output);

        Assert.Equal(0, exit);
        var (_, body) = Assert.Single(github.Puts);
        Assert.Empty(JsonNode.Parse(body)!["bypass_actors"]!.AsArray());
        Assert.Empty(github.BypassActors);
        Assert.Contains("no takeover was recorded", Assert.Single(github.Comments), StringComparison.Ordinal);
        Assert.False(State().NeedsOwner);
    }

    [Fact]
    public void A_bypass_that_isnt_ours_with_no_state_exits_1_and_asks_the_owner_once()
    {
        var live = JsonNode.Parse(LiveRuleset)!.AsObject();
        live["bypass_actors"] = new JsonArray(new JsonObject { ["actor_id"] = 1, ["actor_type"] = "OrganizationAdmin", ["bypass_mode"] = "always" });
        var github = new FakeGitHub(stall: "none", live: live);

        Assert.Equal(1, Tick(github, "operational"));
        Assert.Equal(1, Tick(github, "operational", Now.AddMinutes(3)));

        Assert.Empty(github.Puts);
        Assert.Contains("@danswett", Assert.Single(github.Comments), StringComparison.Ordinal);
        Assert.True(State().NeedsOwner);
    }

    [Fact]
    public void A_ruleset_left_for_the_owner_with_only_our_bypass_is_cleaned_up_and_the_owner_flag_clears()
    {
        WriteState(new TakeoverState { NeedsOwner = true, OwnerAsked = "it has the takeover's admin bypass", ExitedAt = Now.AddHours(-1) });
        var github = new FakeGitHub(stall: "none", bypass: true);

        var exit = Tick(github, "operational");

        Assert.Equal(0, exit);
        Assert.Empty(github.BypassActors);
        Assert.False(State().NeedsOwner);
        Assert.Null(State().OwnerAsked);
    }

    [Fact]
    public void A_corrupt_state_file_is_set_aside_not_crashed_on()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, "{\"inTakeover\": tru");
        var github = new FakeGitHub(stall: "none", bypass: true);
        var output = new StringWriter();

        var exit = Tick(github, "operational", output: output);

        Assert.Equal(0, exit);
        Assert.Contains("can't be read", output.ToString(), StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(StatePath)!, "merge-takeover.json.corrupt-*"));
        Assert.Empty(github.BypassActors); // with no state, our bypass is found and removed
    }

    [Fact]
    public void A_tick_that_finds_the_lock_held_exits_0_and_writes_nothing()
    {
        InTakeover(Now.AddHours(-1));
        var before = File.ReadAllText(StatePath);
        var github = new FakeGitHub(stall: "none", recovered: true, bypass: true, prs: [new(7, Sha(7))]);
        var output = new StringWriter();

        using (new FileStream(StatePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(0, Tick(github, "operational", output: output));
            Assert.Equal(0, Tick(github, "operational", Now.AddMinutes(5)));
        }

        Assert.Contains("holds", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(github.Puts);
        Assert.Empty(github.Comments);
        Assert.Empty(github.Merges);
        Assert.Equal(before, File.ReadAllText(StatePath));
    }

    [Fact]
    public void A_checkout_behind_origin_main_still_hands_back_but_neither_merges_nor_takes_over()
    {
        Func<string, MergeRuleCommand.CheckoutFacts> behind = _ => new("0000000123456789abcdef0123456789abcdef01", MainCommit, Dirty: false, Behind: true);
        InTakeover(Now.AddHours(-1));
        var github = new FakeGitHub(stall: "queued", bypass: true, prs: [new(7, Sha(7))]);

        Tick(github, "major_outage", checkout: behind);

        Assert.Empty(github.Merges);

        var recovered = new FakeGitHub(stall: "none", recovered: true, bypass: true);
        Tick(recovered, "operational", Now.AddMinutes(1), checkout: behind);
        Tick(recovered, "operational", Now.AddMinutes(5), checkout: behind);

        Assert.Empty(recovered.BypassActors);
        Assert.False(State().InTakeover);

        var outage = new FakeGitHub(stall: "queued", stallMinutes: 60);
        Tick(outage, "major_outage", Now.AddMinutes(60), checkout: behind);

        Assert.Empty(outage.Puts);
        Assert.False(State().InTakeover);
    }

    [Fact]
    public void Allow_non_main_is_refused_without_dry_run()
    {
        var github = new FakeGitHub(stall: "queued", prs: [new(7, Sha(7))]);
        var output = new StringWriter();

        var exit = Tick(github, "major_outage", output: output, extra: ["--allow-non-main"]);

        Assert.Equal(2, exit);
        Assert.Contains("only with --dry-run", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(github.Puts);
        Assert.Empty(github.Comments);
        Assert.False(File.Exists(StatePath));
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

    /// <summary>An open pull request; <c>Gate</c> says where its quality-gate comes from: "actions" (ci.yml's job), "status", "other app" or "none".</summary>
    sealed record Pr(int Number, string Head, bool Armed = true, bool Draft = false, string[]? Labels = null, int Unresolved = 0, string Mergeable = "MERGEABLE", string Gate = "actions", string[]? Files = null, bool Receipt = true);

    sealed class FakeStatusPage(string? status) : IStatusPage
    {
        public string? ActionsStatus() => status;
    }

    /// <summary>
    /// GitHub during (or after) an Actions incident: merge-rule's runs stalled one way or another,
    /// main's ruleset as it was live, and the open pull requests. Writes are recorded, and a PUT
    /// changes the ruleset's bypass as GitHub would, unless told to keep it.
    /// </summary>
    sealed class FakeGitHub(string stall, int stallMinutes = 20, bool recovered = false, JsonObject? live = null, bool bypass = false, bool putKeepsBypass = false, Pr[]? prs = null, int failPuts = 0, int failComments = 0, bool failedPutLands = false, bool mainRed = false) : IGitHub
    {
        readonly JsonObject ruleset = Ruleset(live, bypass);
        readonly Pr[] prs = prs ?? [];
        int failPuts = failPuts;
        int failComments = failComments;

        public bool PutKeepsBypass { get; set; } = putKeepsBypass;
        /// <summary>Pull requests as they are when read again just before a merge, after the candidates query and the evaluator.</summary>
        public Dictionary<int, Pr> Later { get; } = [];
        public JsonArray BypassActors => ruleset["bypass_actors"]!.AsArray();
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

        static object Node(Pr p) => new
        {
            number = p.Number,
            state = "OPEN",
            baseRefName = "main",
            isDraft = p.Draft,
            headRefOid = p.Head,
            mergeable = p.Mergeable,
            autoMergeRequest = p.Armed ? new { enabledAt = "2026-11-04T15:00:00Z" } : null,
            labels = new { totalCount = (p.Labels ?? []).Length, nodes = (p.Labels ?? []).Select(l => new { name = l }) },
            reviewThreads = new { totalCount = p.Unresolved + 1, nodes = Enumerable.Range(0, p.Unresolved + 1).Select(i => new { isResolved = i > 0 || p.Unresolved == 0 }) },
        };

        public JsonDocument GraphQL(string query, IReadOnlyDictionary<string, object> variables)
        {
            if (query.Contains("autoMergeRequest", StringComparison.Ordinal) && variables.TryGetValue("number", out var asked))
            {
                var pr = prs.Single(p => p.Number == (int)asked);
                return JsonDocument.Parse(JsonSerializer.Serialize(new { data = new { repository = new { pullRequest = Node(Later.GetValueOrDefault(pr.Number, pr)) } } }));
            }
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
                                nodes = Open.Select(Node),
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
        // quality-gate from wherever the pull request says, and the independent-review receipt
        // from the owner, or from someone who isn't a listed poster.
        static string Facts(Pr p) => GraphQLPullRequest(p.Head, number: p.Number, labels: p.Labels, mergeRule: ("failure", "Waiting for Copilot"), mergeRuleCreator: "agent-bridge-merge-rule",
            receiptCreator: p.Receipt ? "danswett" : "someone-else",
            extraSuites: p.Gate switch
            {
                "actions" => [Suite("github-actions", Workflow(20 + p.Number, "CI"), "COMPLETED", "SUCCESS", Job(100 + p.Number, "quality-gate", "COMPLETED", "SUCCESS"))],
                "other app" => [Suite("not-actions", null, "COMPLETED", "SUCCESS", Job(100 + p.Number, "quality-gate", "COMPLETED", "SUCCESS"))],
                _ => [],
            },
            extraStatuses: p.Gate == "status" ? [new { context = "quality-gate", state = "SUCCESS", description = "green", createdAt = "2026-11-04T15:00:00Z", creator = new { login = "someone" } }] : null);

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
                    $$"""{"workflow_runs": [{"id": 9, "head_sha": "c900000000000000000000000000000000000000", "status": "completed", "conclusion": "{{(mainRed ? "failure" : "success")}}", "run_attempt": 1}]}""",
                _ when path.Contains("/compare/", StringComparison.Ordinal) =>
                    JsonSerializer.Serialize(new { files = (prs.FirstOrDefault(p => path.EndsWith(p.Head, StringComparison.Ordinal))?.Files ?? ["docs/app-windows.md"]).Select(f => new { filename = f }) }),
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
                var put = JsonNode.Parse(jsonBody)!.AsObject();
                if (failPuts-- > 0)
                {
                    if (failedPutLands)
                        ruleset["bypass_actors"] = put["bypass_actors"]!.DeepClone();
                    throw new InvalidOperationException("gh: HTTP 502");
                }
                Puts.Add((path, jsonBody));
                if (!(PutKeepsBypass && put["bypass_actors"]!.AsArray().Count == 0))
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
