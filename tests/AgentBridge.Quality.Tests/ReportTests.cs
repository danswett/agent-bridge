using System.Text.Json;

namespace AgentBridge.Quality.Tests;

public sealed class ReportTests
{
    // Monday 08:00 PST, when the report is posted; the window starts a week before.
    static readonly DateTimeOffset Now = new(2026, 11, 9, 16, 0, 0, TimeSpan.Zero);

    static DateTimeOffset Ago(double hours) => Now.AddHours(-hours);

    static ReportPr Open(int number, string head = "quality/x", string baseRef = "main", string? rule = "pending", string? description = "Waiting for a Copilot review of abc1234",
        bool draft = false, string mergeable = "MERGEABLE", int threads = 0, double ageHours = 10) =>
        new(number, "OPEN", draft, Ago(ageHours), null, null, null, head, baseRef, mergeable, threads, rule, description);

    static ReportPr Merged(int number, double openedHoursAgo, double mergedHoursAgo, double? readyHoursAgo = null, string head = "quality/x") =>
        new(number, "MERGED", false, Ago(openedHoursAgo), readyHoursAgo is { } r ? Ago(r) : null, Ago(mergedHoursAgo), Ago(mergedHoursAgo), head, "main");

    static ReportFacts Facts(IReadOnlyList<ReportPr>? prs = null, IReadOnlyList<FlakyIssue>? flaky = null, IReadOnlyList<MainRun>? main = null,
        IReadOnlyList<PrCiRun>? ci = null, IReadOnlyList<CiTestFailure>? failures = null, bool? mainRed = false) => new()
        {
            Now = Now,
            PullRequests = prs ?? [],
            FlakyIssues = flaky ?? [],
            MainRuns = main ?? [],
            MainRedNow = mainRed,
            PrRuns = ci ?? [],
            TestFailures = failures ?? [],
            LogsRead = failures?.Count ?? 0,
            FailedTestJobs = failures?.Count ?? 0,
        };

    static FlakeTally Tally(double hoursAgo, params (string Os, int Failed, int Runs)[] byOs) => new(Ago(hoursAgo), byOs);

    [Fact]
    public void Open_flaky_issues_are_ranked_by_this_weeks_failures_with_their_oses_and_when_last_seen_and_new_and_fixed_ones_are_counted()
    {
        var report = WeeklyReport.Render(Facts(flaky:
        [
            // Many failures, but all before the window: ranked below one failing this week.
            new(10, "Flaky on ubuntu-latest: Host.Tests OldTests.Slow", "OldTests.Slow", true, Ago(400), null, [Tally(400, ("ubuntu-latest", 9, 10))]),
            new(11, "Flaky on windows-latest: Host.Tests NewTests.Racy", "NewTests.Racy", true, Ago(50), null,
                [Tally(50, ("windows-latest", 1, 10), ("ubuntu-latest", 0, 10)), Tally(26, ("windows-11-arm", 2, 10))]),
            new(12, "Flaky: fixed one", "FixedTests.Done", false, Ago(300), Ago(20), [Tally(300, ("ubuntu-latest", 1, 10))]),
        ]));

        Assert.Contains("Open `flaky` issues: **2**. New this week: **1** (#11). Fixed (closed) this week: **1** (#12).", report);
        var racy = report.IndexOf("| #11 | `NewTests.Racy` | 3 / 3 | `windows-11-arm, windows-latest` | 2026-11-08 06:00 PST |", StringComparison.Ordinal);
        var old = report.IndexOf("| #10 | `OldTests.Slow` | 0 / 9 | `ubuntu-latest` |", StringComparison.Ordinal);
        Assert.True(racy > 0 && old > racy, report);
        Assert.DoesNotContain("#12 |", report);
    }

    [Fact]
    public void A_main_run_green_only_on_a_rerun_of_its_first_failed_attempt_is_a_flake_one_still_red_a_break_alone_when_the_next_commit_went_green_and_a_cancelled_one_neither()
    {
        var report = WeeklyReport.Render(Facts(mainRed: true, main:
        [
            new(1, "a", Ago(1), "success", 2, FirstAttemptFailed: true),
            new(2, "b", Ago(2), "failure", 1, false),
            new(3, "c", Ago(3), "failure", 3, true),
            new(4, "d", Ago(4), "success", 1, false),
            new(5, "e", Ago(5), "cancelled", 1, false),
            // A rerun of a green attempt (to re-publish, say) is no flake.
            new(6, "f", Ago(6), "success", 2, false),
            new(7, "g", Ago(200), "failure", 1, false),
        ]));

        Assert.Contains("| 6 | 3 | 1 | 2 | 1 | red |", report);
    }

    [Theory]
    [InlineData("failure", "Draft: mark it ready for review; Waiting for a Copilot review of abc1234", "draft")]
    [InlineData("failure", "#12: Held by the label 'hold'", "held")]
    [InlineData("failure", "Needs an independent review of abc1234: it touches .github/workflows/x.yml", "independent review")]
    [InlineData("failure", "Check product (ubuntu-latest) ended failure on abc1234 (+1 more); Waiting for a Copilot review of abc1234", "a red check")]
    [InlineData("pending", "Waiting for a Copilot review of abc1234; waiting for product (+3 more)", "Copilot review")]
    [InlineData("pending", "Waiting for product (ubuntu-latest) (+3 more)", "checks running")]
    [InlineData("pending", "No codeql.yml run on abc1234 yet (after a retarget, push or reopen)", "CodeQL")]
    public void An_open_pull_request_is_stuck_on_the_first_item_merge_rule_names(string state, string description, string expected) =>
        Assert.Equal(expected, WeeklyReport.StuckOn(Open(1, rule: state, description: description), "main", mainRed: false));

    [Fact]
    public void One_merge_rule_passed_is_stuck_on_conflicts_then_threads_then_a_red_main_and_one_on_another_branch_is_stacked()
    {
        const string ok = "Copilot reviewed abc1234; every check passed";
        Assert.Equal("conflicts", WeeklyReport.StuckOn(Open(1, rule: "success", description: ok, mergeable: "CONFLICTING", threads: 2), "main", true));
        Assert.Equal("unresolved threads", WeeklyReport.StuckOn(Open(1, rule: "success", description: ok, threads: 2), "main", true));
        Assert.Equal("main red", WeeklyReport.StuckOn(Open(1, rule: "success", description: ok), "main", true));
        Assert.Equal("ready: waiting to be merged", WeeklyReport.StuckOn(Open(1, rule: "success", description: ok), "main", false));
        Assert.Equal("stacked on another branch", WeeklyReport.StuckOn(Open(1, baseRef: "codex/base"), "main", false));
        Assert.Equal("no merge-rule status", WeeklyReport.StuckOn(Open(1, rule: null, description: null), "main", false));
    }

    [Fact]
    public void Each_branch_prefix_shows_its_open_pull_requests_how_many_are_stacked_and_how_deep_and_its_merges_this_week()
    {
        var report = WeeklyReport.Render(Facts(prs:
        [
            Open(1, head: "codex/a"),
            Open(2, head: "codex/b", baseRef: "codex/a"),
            Open(3, head: "codex/c", baseRef: "codex/b"),
            Open(4, head: "perf/x"),
            Open(5, head: "lonely"),
            Merged(6, openedHoursAgo: 10, mergedHoursAgo: 6, head: "perf/y"),
            Merged(7, openedHoursAgo: 10, mergedHoursAgo: 8, head: "perf/z"),
        ]));

        Assert.Contains("| `codex/` | 3 | 2 | 3 | 0 | - |", report);
        Assert.Contains("| `perf/` | 1 | 0 | 1 | 2 | 2.0 h |", report);
        Assert.Contains("| `(none)` | 1 | 0 | 1 | 0 | - |", report);
        Assert.Contains("| `stacked on another branch` | 2 |", report);
    }

    [Fact]
    public void A_forks_branch_is_never_a_stack_parent_though_it_shares_a_local_branchs_name()
    {
        var depths = WeeklyReport.StackDepths([Open(1, head: "codex/top", baseRef: "codex/base"), Open(2, head: "codex/base") with { CrossRepository = true }], "main");

        Assert.Equal(1, depths[1]);
    }

    [Fact]
    public void A_branch_prefix_with_merges_this_week_and_none_open_still_has_its_row()
    {
        var report = WeeklyReport.Render(Facts(prs: [Merged(1, openedHoursAgo: 10, mergedHoursAgo: 7, head: "docs/a")]));

        Assert.Contains("_No open pull request._", report);
        Assert.Contains("| `docs/` | 0 | 0 | 0 | 1 | 3.0 h |", report);
    }

    [Fact]
    public void Throughput_gives_the_median_and_90th_percentile_from_open_and_from_ready_and_names_pull_requests_open_over_a_week()
    {
        var report = WeeklyReport.Render(Facts(prs:
        [
            .. Enumerable.Range(1, 10).Select(i => Merged(i, openedHoursAgo: 100, mergedHoursAgo: 100 - i, readyHoursAgo: 100 - i / 2.0)),
            // Merged before the window: not counted.
            Merged(20, openedHoursAgo: 400, mergedHoursAgo: 300),
            new ReportPr(21, "CLOSED", false, Ago(30), null, null, Ago(5), "x/y", "main"),
            Open(22, ageHours: 24 * 9, draft: true, description: "Draft: mark it ready for review", rule: "failure"),
        ]));

        Assert.Contains("| 1 | 11 | 10 | 1 |", report);
        Assert.Contains("| From open | 5.0 h | 9.0 h |", report);
        Assert.Contains("| From ready (not draft) | 2.5 h | 4.5 h |", report);
        Assert.Contains("Open more than 7 days: **1**:", report);
        Assert.Contains("| #22 | 9.0 d | `draft` |", report);
    }

    [Fact]
    public void The_most_failed_tests_join_ci_logs_and_the_hunts_tallies_this_week_by_class_and_method()
    {
        var report = WeeklyReport.Render(Facts(
            failures:
            [
                new("AgentBridge.Host.Tests.FleetTests.Offline(x: 1)", OnMain: true),
                new("FleetTests.Offline", OnMain: false),
                new("FleetTests.Offline", OnMain: true),
                new("Other.One", OnMain: false),
            ],
            flaky: [new(30, "Flaky", "FleetTests.Offline", true, Ago(300), null, [Tally(10, ("ubuntu-latest", 4, 10)), Tally(300, ("ubuntu-latest", 9, 10))])]));

        Assert.Contains("| `FleetTests.Offline` | 2 | 1 | 4 |", report);
        Assert.True(report.IndexOf("`FleetTests.Offline` | 2", StringComparison.Ordinal) < report.IndexOf("`Other.One`", StringComparison.Ordinal));
    }

    [Fact]
    public void A_jobs_log_yields_each_failed_test_once_coloured_or_not_and_not_dotnet_tests_summary_line()
    {
        const string log = """
            2026-11-04T05:54:41.5774220Z [xUnit.net 00:00:56.67]     LaunchPieceTests.A_probe [FAIL]
            2026-11-04T05:54:41.5775737Z   Failed LaunchPieceTests.A_probe [16 s]
            2026-11-04T05:54:41.6Z   Failed TrustTests.Theory(kind: "a [b]") [< 1 ms]
            2026-11-04T05:55:41.5775737Z   Failed LaunchPieceTests.A_probe [16 s]
            2026-11-04T05:55:38.9088887Z Failed!  - Failed:     1, Passed:   214, Skipped:     0, Total:   215, Duration: 1 m 44 s - X.dll (net10.0)
            """;

        Assert.Equal(["LaunchPieceTests.A_probe", "TrustTests.Theory(kind: \"a [b]\")"], WeeklyReport.FailedTests(log));
        // gh keeps the runner's colour codes in a log.
        Assert.Equal(["Colour.Test"], WeeklyReport.FailedTests("2026-11-04T05:54:41.6Z \u001b[31m  Failed Colour.Test [2 s]\u001b[0m"));
    }

    [Fact]
    public void A_flaky_issue_counts_only_tallies_from_the_hunt_or_the_owner_takes_its_test_from_the_hunts_marker_and_marks_its_total_partial_when_comments_go_unread()
    {
        var table = "| OS | Failed | Runs |\n|---|---|---|\n| ubuntu-latest | 2 | 10 |\n";
        var issue = JsonSerializer.Serialize(new
        {
            number = 40,
            title = "Flaky on ubuntu-latest: Host.Tests FleetTests.Offline",
            state = "OPEN",
            createdAt = "2026-11-03T10:00:00Z",
            closedAt = (string?)null,
            body = "<!-- flake-hunt: flaky AgentBridge.Host.Tests AgentBridge.Host.Tests.FleetTests.Offline -->\n" + table,
            author = new { login = "github-actions" },
            comments = new
            {
                totalCount = 150,
                nodes = new[]
                {
                    new { author = new { login = "danswett" }, createdAt = "2026-11-05T10:00:00Z", body = table.Replace("| 2 |", "| 3 |", StringComparison.Ordinal) },
                    new { author = new { login = "stranger" }, createdAt = "2026-11-06T10:00:00Z", body = table.Replace("| 2 |", "| 99 |", StringComparison.Ordinal) },
                },
            },
        });

        var parsed = ReportCommand.ParseIssue(JsonDocument.Parse(issue).RootElement, "danswett");

        Assert.Equal("FleetTests.Offline", parsed.Test);
        Assert.True(parsed.Open);
        Assert.Equal([2, 3], parsed.Tallies.Select(t => t.Failed));
        // 150 comments, 2 read: the oldest tallies are missing, so the total is a floor.
        Assert.True(parsed.Partial);
        Assert.Contains("| 5 / ≥5 |", WeeklyReport.Render(Facts(flaky: [parsed])));
    }

    [Fact]
    public void A_pull_request_is_read_with_when_it_left_draft_its_unresolved_threads_counting_those_past_the_first_100_and_its_merge_rule_status()
    {
        const string json = """
            {"number": 7, "state": "OPEN", "isDraft": false, "createdAt": "2026-11-08T10:00:00Z", "mergedAt": null, "closedAt": null,
             "updatedAt": "2026-11-09T10:00:00Z", "headRefName": "quality/a", "baseRefName": "main", "mergeable": "CONFLICTING",
             "timelineItems": {"nodes": [{"createdAt": "2026-11-08T12:00:00Z"}]},
             "reviewThreads": {"totalCount": 102, "nodes": [{"isResolved": true}, {"isResolved": false}]},
             "commits": {"nodes": [{"commit": {"status": {"context": {"state": "FAILURE", "description": "Draft: mark it ready for review"}}}}]}}
            """;

        var pr = ReportCommand.ParsePullRequest(JsonDocument.Parse(json).RootElement);

        Assert.Equal(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero), pr.ReadyAt);
        Assert.Equal(101, pr.UnresolvedThreads);
        Assert.Equal(("failure", "Draft: mark it ready for review", "CONFLICTING"), (pr.MergeRuleState, pr.MergeRuleDescription, pr.Mergeable));
    }

    [Fact]
    public void Ci_health_gives_the_median_wall_time_and_the_three_slowest_jobs_by_median_counting_a_rerun_job_once_and_leaving_out_skipped_ones()
    {
        const string run = """
            {"databaseId": 1, "checkSuite": {"checkRuns": {"nodes": [
              {"databaseId": 10, "name": "product (ubuntu-latest)", "startedAt": "2026-11-08T09:00:00Z", "completedAt": "2026-11-08T09:40:00Z", "conclusion": "FAILURE"},
              {"databaseId": 11, "name": "product (ubuntu-latest)", "startedAt": "2026-11-08T10:00:00Z", "completedAt": "2026-11-08T10:12:00Z", "conclusion": "SUCCESS"},
              {"name": "app (win-x64)", "startedAt": "2026-11-08T10:00:00Z", "completedAt": "2026-11-08T10:08:00Z", "conclusion": "FAILURE"},
              {"name": "light", "startedAt": "2026-11-08T10:00:00Z", "completedAt": "2026-11-08T10:01:00Z", "conclusion": "SUCCESS"},
              {"name": "pwa-e2e", "startedAt": "2026-11-08T10:00:00Z", "completedAt": "2026-11-08T10:00:00Z", "conclusion": "SKIPPED"},
              {"name": "plan", "startedAt": "2026-11-08T10:00:00Z", "completedAt": "2026-11-08T10:00:30Z", "conclusion": "SUCCESS"}]}}}
            """;
        var jobs = ReportCommand.ParseJobTimes(JsonDocument.Parse(run).RootElement);

        var report = WeeklyReport.Render(Facts(ci:
        [
            new(1, TimeSpan.FromMinutes(14), jobs),
            new(2, TimeSpan.FromMinutes(4), [("light", TimeSpan.FromMinutes(3))]),
            new(3, TimeSpan.FromMinutes(20), []),
        ]));

        Assert.DoesNotContain(jobs, j => j.Name == "pwa-e2e");
        Assert.Contains("median wall time **14 min**, 90th percentile 20 min", report);
        Assert.Contains("| `product (ubuntu-latest)` | 12 min | 12 min | 1 |\n| `app (win-x64)` | 8 min | 8 min | 1 |\n| `light` | 1 min | 3 min | 2 |", report);
    }

    [Fact]
    public void Others_names_are_shown_in_code_spans_so_a_mention_pings_no_one_and_a_pipe_cant_break_the_table() =>
        Assert.Contains("| `x\\|y @someone` | 1 |", WeeklyReport.Render(Facts(prs: [Open(1, rule: "failure", description: "x|y @someone")]))
            .Replace("other: ", "", StringComparison.Ordinal));

    [Fact]
    public void The_report_reads_recent_pull_requests_only_until_one_changed_before_the_window_and_splits_its_log_budget()
    {
        var github = new FakeGitHub();
        var output = new StringWriter();

        Assert.Equal(0, ReportCommand.Run(["--slug", "o/r", "--max-logs", "2"], output, github, Now));

        Assert.Equal(1, github.Queries.Count(q => q.Contains("states: [MERGED, CLOSED]", StringComparison.Ordinal)));
        Assert.Equal(["repos/o/r/actions/jobs/501/logs", "repos/o/r/actions/jobs/601/logs"], github.Logs);
        Assert.Contains("| 1 | 2 | 1 | 0 |", output.ToString());
        Assert.Contains("| `MainTests.Broke` | 1 | 0 | 0 |", output.ToString());
        Assert.Contains("| `PrTests.Broke` | 0 | 1 | 0 |", output.ToString());
    }

    [Fact]
    public void A_log_that_cant_be_read_is_counted_and_said_rather_than_read_as_no_failures()
    {
        var output = new StringWriter();

        Assert.Equal(0, ReportCommand.Run(["--slug", "o/r", "--max-logs", "2"], output, new FakeGitHub(failPrLog: true), Now));

        Assert.Contains("CI's logs of 1 of the 2 failed test job(s) found (main's and pull requests', newest first); **1 more couldn't be read**", output.ToString());
    }

    [Fact]
    public void A_pull_request_run_green_only_on_a_rerun_has_its_failed_first_attempts_tests_counted()
    {
        var github = new FakeGitHub(rerunGreen: true);
        var output = new StringWriter();

        Assert.Equal(0, ReportCommand.Run(["--slug", "o/r", "--max-logs", "4"], output, github, Now));

        Assert.Contains("repos/o/r/actions/jobs/701/logs", github.Logs);
        Assert.Contains("| `RerunTests.Flaky` | 0 | 1 | 0 |", output.ToString());
    }

    /// <summary>
    /// A week on GitHub: one open pull request; a merged one, then one that changed before the
    /// window with more pages promised (which mustn't be read); one red main run and one red pull
    /// request run, each with a failed product job.
    /// </summary>
    sealed class FakeGitHub(bool failPrLog = false, bool rerunGreen = false) : IGitHub
    {
        public List<string> Queries { get; } = [];
        public List<string> Logs { get; } = [];

        static string Pr(int number, string state, string updated, string? merged = null) =>
            "{\"number\": " + number + ", \"state\": \"" + state + "\", \"isDraft\": false, \"createdAt\": \"2026-11-08T10:00:00Z\", \"mergedAt\": " + (merged is null ? "null" : "\"" + merged + "\"") + ", \"closedAt\": null, \"updatedAt\": \"" + updated + "\", \"headRefName\": \"quality/p" + number + "\", \"baseRefName\": \"main\", \"timelineItems\": {\"nodes\": []}}";

        public JsonDocument GraphQL(string query, IReadOnlyDictionary<string, object> variables)
        {
            Queries.Add(query);
            if (query.Contains("states: OPEN", StringComparison.Ordinal))
                return Page("pullRequests", Pr(1, "OPEN", "2026-11-09T10:00:00Z"), more: false);
            if (query.Contains("states: [MERGED, CLOSED]", StringComparison.Ordinal))
                return Page("pullRequests", Pr(2, "MERGED", "2026-11-09T10:00:00Z", "2026-11-09T09:00:00Z") + "," + Pr(3, "MERGED", "2026-10-01T10:00:00Z", "2026-10-01T09:00:00Z"), more: true);
            if (query.Contains("issues(", StringComparison.Ordinal))
                return Page("issues", "", more: false);
            if (query.Contains("startedAt", StringComparison.Ordinal))
                return JsonDocument.Parse("{\"data\": {\"nodes\": [null]}}");
            var ids = (string[])variables["ids"];
            var job = ids[0] == "main-node" ? 501 : 601;
            return JsonDocument.Parse("{\"data\": {\"nodes\": [{\"databaseId\": 1, \"checkSuite\": {\"checkRuns\": {\"nodes\": [{\"databaseId\": " + job + ", \"name\": \"product (ubuntu-latest)\"}, {\"databaseId\": 9, \"name\": \"quality-gate\"}]}}}]}}");
        }

        static JsonDocument Page(string connection, string nodes, bool more) =>
            JsonDocument.Parse("{\"data\": {\"repository\": {\"" + connection + "\": {\"pageInfo\": {\"hasNextPage\": " + (more ? "true" : "false") + ", \"endCursor\": \"c\"}, \"nodes\": [" + nodes + "]}}}}");

        public JsonDocument Rest(string path)
        {
            const string run = """{"id": 1, "node_id": "%N", "conclusion": "failure", "run_attempt": 1, "head_sha": "abc", "created_at": "2026-11-08T10:00:00Z", "updated_at": "2026-11-08T10:10:00Z", "run_started_at": "2026-11-08T10:00:00Z"}""";
            if (path.Contains("branch=main", StringComparison.Ordinal))
                return JsonDocument.Parse("""{"workflow_runs": [""" + run.Replace("%N", "main-node", StringComparison.Ordinal) + "]}");
            if (path.Contains("status=failure", StringComparison.Ordinal))
                return JsonDocument.Parse("""{"workflow_runs": [""" + run.Replace("%N", "pr-node", StringComparison.Ordinal) + "]}");
            if (rerunGreen && path.Contains("status=completed", StringComparison.Ordinal))
                return JsonDocument.Parse("""{"workflow_runs": [""" + run.Replace("%N", "rerun-node", StringComparison.Ordinal).Replace("\"failure\", \"run_attempt\": 1", "\"success\", \"run_attempt\": 2", StringComparison.Ordinal) + "]}");
            if (rerunGreen && path.Contains("attempts/1/jobs", StringComparison.Ordinal))
                return JsonDocument.Parse("""{"jobs": [{"id": 701, "name": "product (windows-latest)", "conclusion": "failure"}, {"id": 702, "name": "light", "conclusion": "failure"}]}""");
            return JsonDocument.Parse("""{"workflow_runs": []}""");
        }

        public string RestText(string path)
        {
            Logs.Add(path);
            if (failPrLog && path.Contains("601", StringComparison.Ordinal))
                throw new InvalidOperationException("gh: HTTP 410: the log has expired");
            if (path.Contains("701", StringComparison.Ordinal))
                return "2026-11-08T10:05:00.0Z   Failed RerunTests.Flaky [1 s]";
            return path.Contains("501", StringComparison.Ordinal) ? "2026-11-08T10:05:00.0Z   Failed MainTests.Broke [1 s]" : "2026-11-08T10:05:00.0Z   Failed PrTests.Broke [1 s]";
        }

        public void PostStatus(string slug, string sha, string state, string context, string description, string? targetUrl) =>
            throw new InvalidOperationException("The report never posts.");
    }
}
