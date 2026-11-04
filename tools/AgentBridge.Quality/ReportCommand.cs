using System.Globalization;
using System.Text.Json;

namespace AgentBridge.Quality;

/// <summary>
/// <c>report</c>: reads the past week from GitHub and writes the weekly quality report
/// (quality/README.md §5). It only reads; weekly-report.yml posts the report it writes. The reads
/// are bounded: a few paginated GraphQL queries and REST lists, a jobs list per main run that was
/// rerun, and at most --max-logs job logs.
/// </summary>
static class ReportCommand
{
    internal const string Usage = """
          report [--slug OWNER/REPO] [--days N] [--base BRANCH] [--max-logs N] [--out FILE]
                 [--run-url URL]
              The weekly quality report, as Markdown: flaky issues, main's red runs and the tests
              that failed most; pull requests opened, merged and stuck, and their stacks; and
              CI's wall time. Reads GitHub through gh and changes nothing; weekly-report.yml
              posts it on the quality epic. --max-logs (default 30) caps the failed CI test jobs
              whose logs are read for the tests that failed.
        """;

    static readonly HashSet<string> Valued = new(StringComparer.Ordinal) { "slug", "days", "base", "max-logs", "out", "run-url" };

    // The jobs that run dotnet test, whose logs name the tests that failed (ci.yml).
    static readonly string[] TestJobs = ["product", "ha-integration"];

    public static int Run(IReadOnlyList<string> args, TextWriter output, IGitHub github, DateTimeOffset? now = null)
    {
        var options = ParseOptions(args);
        var slug = options.GetValueOrDefault("slug") ?? Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "danswett/agent-bridge";
        if (slug.Split('/') is not [{ Length: > 0 }, { Length: > 0 }])
            throw new ArgumentException($"--slug {slug} is not OWNER/REPO.");
        var days = Positive(options, "days", 7);
        var maxLogs = Positive(options, "max-logs", 30, allowZero: true);
        var reader = new Reader(github, slug, options.GetValueOrDefault("base") ?? "main", (now ?? DateTimeOffset.UtcNow).AddDays(-days), maxLogs);
        var facts = reader.Read(now ?? DateTimeOffset.UtcNow, days, options.GetValueOrDefault("run-url"));
        var report = WeeklyReport.Render(facts);
        if (options.TryGetValue("out", out var file))
            File.WriteAllText(file, report);
        output.Write(report);
        return 0;
    }

    static int Positive(Dictionary<string, string> options, string name, int fallback, bool allowZero = false)
    {
        if (!options.TryGetValue(name, out var value))
            return fallback;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < (allowZero ? 0 : 1) || n > 1000)
            throw new ArgumentException($"--{name} {value} is not a count.");
        return n;
    }

    static Dictionary<string, string> ParseOptions(IReadOnlyList<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i].StartsWith("--", StringComparison.Ordinal) ? args[i][2..] : null;
            if (name is null || !Valued.Contains(name))
                throw new ArgumentException($"Unexpected argument '{args[i]}'.");
            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"--{name} needs a value.");
            options[name] = args[++i];
        }
        return options;
    }

    const string PrFields = """
        number state isDraft createdAt mergedAt closedAt updatedAt headRefName baseRefName
        timelineItems(itemTypes: [READY_FOR_REVIEW_EVENT], last: 1) { nodes { ... on ReadyForReviewEvent { createdAt } } }
        """;

    // Open pull requests carry what decides where they're stuck; GitHub's mergeable may be
    // UNKNOWN until something asks, and is then read as not conflicting.
    static readonly string OpenQuery = $$"""
        query($owner: String!, $name: String!, $cursor: String) {
          rateLimit { cost remaining }
          repository(owner: $owner, name: $name) {
            pullRequests(states: OPEN, first: 50, after: $cursor, orderBy: { field: CREATED_AT, direction: ASC }) {
              pageInfo { hasNextPage endCursor }
              nodes {
                {{PrFields}}
                mergeable
                reviewThreads(first: 100) { nodes { isResolved } }
                commits(last: 1) { nodes { commit { status { context(name: "merge-rule") { state description } } } } }
              }
            }
          }
        }
        """;

    // Newest change first, until one changed before the window: any pull request merged or closed
    // in it changed then.
    static readonly string RecentQuery = $$"""
        query($owner: String!, $name: String!, $cursor: String) {
          rateLimit { cost remaining }
          repository(owner: $owner, name: $name) {
            pullRequests(states: [MERGED, CLOSED], first: 100, after: $cursor, orderBy: { field: UPDATED_AT, direction: DESC }) {
              pageInfo { hasNextPage endCursor }
              nodes { {{PrFields}} }
            }
          }
        }
        """;

    // The hunt's tallies are in an issue's body and its comments; the newest 30 are enough for a
    // week of nightly runs.
    const string IssueQuery = """
        query($owner: String!, $name: String!, $cursor: String, $states: [IssueState!], $since: DateTime) {
          rateLimit { cost remaining }
          repository(owner: $owner, name: $name) {
            issues(first: 50, after: $cursor, filterBy: { labels: ["flaky"], states: $states, since: $since }, orderBy: { field: UPDATED_AT, direction: DESC }) {
              pageInfo { hasNextPage endCursor }
              nodes {
                number title state createdAt closedAt body author { login }
                comments(last: 30) { nodes { author { login } createdAt body } }
              }
            }
          }
        }
        """;

    // The failed jobs of up to 100 runs in one query; checkRuns are the latest attempt's.
    const string FailedJobsQuery = """
        query($ids: [ID!]!) {
          rateLimit { cost remaining }
          nodes(ids: $ids) { ... on WorkflowRun { databaseId checkSuite { checkRuns(first: 100, filterBy: { conclusions: [FAILURE, TIMED_OUT] }) { nodes { databaseId name } } } } }
        }
        """;

    const string JobTimesQuery = """
        query($ids: [ID!]!) {
          rateLimit { cost remaining }
          nodes(ids: $ids) { ... on WorkflowRun { databaseId checkSuite { checkRuns(first: 100) { nodes { name startedAt completedAt conclusion } } } } }
        }
        """;

    const int MaxPages = 10;

    sealed class Reader(IGitHub github, string slug, string baseBranch, DateTimeOffset since, int maxLogs)
    {
        readonly string owner = slug.Split('/')[0];
        readonly string name = slug.Split('/')[1];
        int points;
        int? remaining;
        int restCalls;

        public ReportFacts Read(DateTimeOffset now, int days, string? runUrl)
        {
            var prs = PullRequests();
            var flaky = FlakyIssues();
            var (mainRuns, mainRedNow, mainFailedJobs) = MainRuns();
            var prRuns = PrRuns();
            var (failures, logsRead, failedJobs) = TestFailures(mainFailedJobs);
            return new ReportFacts
            {
                Now = now,
                Days = days,
                Base = baseBranch,
                PullRequests = prs,
                FlakyIssues = flaky,
                MainRuns = mainRuns,
                MainRedNow = mainRedNow,
                PrRuns = prRuns,
                TestFailures = failures,
                LogsRead = logsRead,
                FailedTestJobs = failedJobs,
                RunUrl = runUrl,
                Cost = $"GraphQL {points} point(s){(remaining is { } left ? $" ({left} left this hour)" : "")} and {restCalls} REST call(s)",
            };
        }

        JsonDocument GraphQL(string query, Dictionary<string, object> variables)
        {
            var doc = github.GraphQL(query, variables);
            if (doc.RootElement.GetProperty("data").TryGetProperty("rateLimit", out var rate) && rate.ValueKind == JsonValueKind.Object)
            {
                points += rate.GetProperty("cost").GetInt32();
                remaining = rate.GetProperty("remaining").GetInt32();
            }
            return doc;
        }

        JsonDocument Rest(string path)
        {
            restCalls++;
            return github.Rest(path);
        }

        string RestText(string path)
        {
            restCalls++;
            return github.RestText(path);
        }

        string Since => since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        IReadOnlyList<ReportPr> PullRequests()
        {
            var prs = new Dictionary<int, ReportPr>();
            foreach (var node in Pages(OpenQuery, new(), "pullRequests", _ => false))
                prs[node.GetProperty("number").GetInt32()] = ParsePullRequest(node);
            foreach (var node in Pages(RecentQuery, new(), "pullRequests", n => Time(n, "updatedAt") < since))
            {
                if (Time(node, "updatedAt") < since)
                    break;
                prs.TryAdd(node.GetProperty("number").GetInt32(), ParsePullRequest(node));
            }
            return [.. prs.Values.OrderBy(p => p.Number)];
        }

        IReadOnlyList<FlakyIssue> FlakyIssues()
        {
            var issues = new Dictionary<int, FlakyIssue>();
            foreach (var node in Pages(IssueQuery, new() { ["states"] = "OPEN" }, "issues", _ => false))
                issues[node.GetProperty("number").GetInt32()] = ParseIssue(node, owner);
            foreach (var node in Pages(IssueQuery, new() { ["states"] = "CLOSED", ["since"] = Since }, "issues", _ => false))
                if (ParseIssue(node, owner) is { ClosedAt: { } closed } issue && closed >= since)
                    issues.TryAdd(issue.Number, issue);
            return [.. issues.Values.OrderBy(i => i.Number)];
        }

        /// <summary>
        /// GraphQL connection pages, newest cursor first; stop says, of a page's last node, that
        /// the rest are outside the window.
        /// </summary>
        IEnumerable<JsonElement> Pages(string query, Dictionary<string, object> variables, string connection, Func<JsonElement, bool> stop)
        {
            string? cursor = null;
            for (var page = 0; page < MaxPages; page++)
            {
                var vars = new Dictionary<string, object>(variables) { ["owner"] = owner, ["name"] = name };
                if (cursor is not null)
                    vars["cursor"] = cursor;
                using var doc = GraphQL(query, vars);
                var conn = doc.RootElement.GetProperty("data").GetProperty("repository").GetProperty(connection);
                var nodes = conn.GetProperty("nodes").EnumerateArray().Select(n => n.Clone()).ToList();
                foreach (var node in nodes)
                    yield return node;
                var info = conn.GetProperty("pageInfo");
                if (!info.GetProperty("hasNextPage").GetBoolean() || nodes.Count == 0 || stop(nodes[^1]))
                    yield break;
                cursor = info.GetProperty("endCursor").GetString();
            }
            throw new InvalidOperationException($"More than {MaxPages} pages of {connection}: the report would be cut short.");
        }

        IEnumerable<JsonElement> RestPages(string path, string array, int perPage = 100, int maxPages = MaxPages)
        {
            for (var page = 1; page <= maxPages; page++)
            {
                using var doc = Rest($"{path}{(path.Contains('?', StringComparison.Ordinal) ? '&' : '?')}per_page={perPage}&page={page}");
                var items = doc.RootElement.GetProperty(array).EnumerateArray().Select(n => n.Clone()).ToList();
                foreach (var item in items)
                    yield return item;
                if (items.Count < perPage)
                    yield break;
            }
        }

        /// <summary>
        /// Main's push runs of ci.yml in the window, newest first, and for each rerun to green
        /// whether its first attempt failed: one jobs list per such run, which also names the
        /// failed jobs whose logs say which tests failed.
        /// </summary>
        (IReadOnlyList<MainRun>, bool?, List<(long Job, DateTimeOffset At)>) MainRuns()
        {
            var runs = new List<MainRun>();
            var flakyJobs = new List<(long, DateTimeOffset)>();
            var nodeIds = new Dictionary<long, string>();
            bool? redNow = null;
            foreach (var run in RestPages($"repos/{slug}/actions/workflows/ci.yml/runs?branch={Uri.EscapeDataString(baseBranch)}&event=push&created=%3E%3D{Since}", "workflow_runs"))
            {
                var id = run.GetProperty("id").GetInt64();
                var conclusion = run.GetProperty("conclusion").GetString();
                var attempt = run.GetProperty("run_attempt").GetInt32();
                var created = Time(run, "created_at");
                if (redNow is null && conclusion is not (null or "cancelled" or "skipped"))
                    redNow = conclusion != "success";
                var firstFailed = false;
                if (conclusion == "success" && attempt > 1)
                {
                    var failed = RestPages($"repos/{slug}/actions/runs/{id}/attempts/1/jobs", "jobs")
                        .Where(j => j.GetProperty("conclusion").GetString() is "failure" or "timed_out")
                        .ToList();
                    firstFailed = failed.Count > 0;
                    flakyJobs.AddRange(failed.Where(j => IsTestJob(j.GetProperty("name").GetString())).Select(j => (j.GetProperty("id").GetInt64(), created)));
                }
                var parsed = new MainRun(id, run.GetProperty("head_sha").GetString() ?? "", created, conclusion, attempt, firstFailed);
                if (parsed.Break)
                    nodeIds[id] = run.GetProperty("node_id").GetString()!;
                runs.Add(parsed);
            }
            // Main's still-red runs' failed test jobs too, newest first.
            var jobs = flakyJobs.Concat(FailedTestJobs(runs.Where(r => r.Break).Take(maxLogs).Select(r => (nodeIds[r.Id], r.CreatedAt))))
                .OrderByDescending(j => j.Item2).ToList();
            return (runs, redNow, jobs);
        }

        List<(long Job, DateTimeOffset At)> FailedTestJobs(IEnumerable<(string NodeId, DateTimeOffset At)> runs)
        {
            var list = runs.ToList();
            if (list.Count == 0)
                return [];
            var at = new Dictionary<long, DateTimeOffset>();
            var jobs = new List<(long, DateTimeOffset)>();
            foreach (var chunk in list.Chunk(100))
            {
                using var doc = GraphQL(FailedJobsQuery, new() { ["ids"] = chunk.Select(c => c.NodeId).ToArray() });
                foreach (var (node, (_, when)) in doc.RootElement.GetProperty("data").GetProperty("nodes").EnumerateArray().Zip(chunk))
                {
                    if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("checkSuite", out var suite) || suite.ValueKind != JsonValueKind.Object)
                        continue;
                    foreach (var check in suite.GetProperty("checkRuns").GetProperty("nodes").EnumerateArray())
                        if (IsTestJob(check.GetProperty("name").GetString()))
                            jobs.Add((check.GetProperty("databaseId").GetInt64(), when));
                }
            }
            return jobs;
        }

        static bool IsTestJob(string? name) =>
            name is not null && TestJobs.Any(t => name == t || name.StartsWith(t + " ", StringComparison.Ordinal));

        /// <summary>
        /// The tests the logs of failed CI test jobs name: main's first, then pull requests' (the
        /// newest 50 failed runs), newest first, up to --max-logs logs split between them.
        /// </summary>
        (IReadOnlyList<CiTestFailure>, int, int) TestFailures(List<(long Job, DateTimeOffset At)> mainJobs)
        {
            if (maxLogs == 0)
                return ([], 0, mainJobs.Count);
            var prRuns = RestPages($"repos/{slug}/actions/workflows/ci.yml/runs?event=pull_request&status=failure&created=%3E%3D{Since}", "workflow_runs", perPage: 50, maxPages: 1)
                .Select(r => (r.GetProperty("node_id").GetString()!, Time(r, "created_at"))).ToList();
            var prJobs = FailedTestJobs(prRuns).OrderByDescending(j => j.At).ToList();
            // Half the logs for each, and what one side doesn't need to the other: a week of red
            // main runs would otherwise leave no pull request's log read.
            var mainQuota = Math.Min(mainJobs.Count, Math.Max(maxLogs / 2, maxLogs - prJobs.Count));
            var prQuota = Math.Min(prJobs.Count, maxLogs - mainQuota);
            var queue = mainJobs.Take(mainQuota).Select(j => (j.Job, OnMain: true)).Concat(prJobs.Take(prQuota).Select(j => (j.Job, OnMain: false))).DistinctBy(j => j.Job).ToList();
            var failures = new List<CiTestFailure>();
            var read = 0;
            foreach (var (job, onMain) in queue)
            {
                string log;
                try
                {
                    log = RestText($"repos/{slug}/actions/jobs/{job}/logs");
                }
                catch (InvalidOperationException)
                {
                    // An expired or missing log costs one job's tests, not the report.
                    continue;
                }
                read++;
                failures.AddRange(WeeklyReport.FailedTests(log).Select(t => new CiTestFailure(t, onMain)));
            }
            return (failures, read, mainJobs.Count + prJobs.Count);
        }

        /// <summary>The last 50 completed pull request runs of ci.yml that passed or failed, with their jobs' times.</summary>
        IReadOnlyList<PrCiRun> PrRuns()
        {
            var runs = RestPages($"repos/{slug}/actions/workflows/ci.yml/runs?event=pull_request&status=completed", "workflow_runs", maxPages: 2)
                .Where(r => r.GetProperty("conclusion").GetString() is "success" or "failure")
                .Take(50)
                .Select(r => (Id: r.GetProperty("id").GetInt64(), Node: r.GetProperty("node_id").GetString()!,
                    Wall: Time(r, "updated_at") - Time(r, "run_started_at")))
                .ToList();
            if (runs.Count == 0)
                return [];
            using var doc = GraphQL(JobTimesQuery, new() { ["ids"] = runs.Select(r => r.Node).ToArray() });
            return [.. doc.RootElement.GetProperty("data").GetProperty("nodes").EnumerateArray().Zip(runs).Select(pair =>
                new PrCiRun(pair.Second.Id, pair.Second.Wall, ParseJobTimes(pair.First)))];
        }
    }

    static DateTimeOffset Time(JsonElement e, string property) =>
        DateTimeOffset.Parse(e.GetProperty(property).GetString()!, CultureInfo.InvariantCulture);

    static DateTimeOffset? OptionalTime(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? DateTimeOffset.Parse(v.GetString()!, CultureInfo.InvariantCulture) : null;

    internal static ReportPr ParsePullRequest(JsonElement pr)
    {
        DateTimeOffset? ready = pr.TryGetProperty("timelineItems", out var timeline)
            ? timeline.GetProperty("nodes").EnumerateArray().Select(n => OptionalTime(n, "createdAt")).LastOrDefault(t => t is not null)
            : null;
        var threads = pr.TryGetProperty("reviewThreads", out var t) ? t.GetProperty("nodes").EnumerateArray().Count(n => !n.GetProperty("isResolved").GetBoolean()) : 0;
        string? state = null, description = null;
        if (pr.TryGetProperty("commits", out var commits)
            && commits.GetProperty("nodes").EnumerateArray().LastOrDefault() is { ValueKind: JsonValueKind.Object } last
            && last.GetProperty("commit").GetProperty("status") is { ValueKind: JsonValueKind.Object } status
            && status.GetProperty("context") is { ValueKind: JsonValueKind.Object } context)
        {
            state = context.GetProperty("state").GetString()?.ToLowerInvariant();
            description = context.TryGetProperty("description", out var d) ? d.GetString() : null;
        }
        return new ReportPr(
            pr.GetProperty("number").GetInt32(),
            pr.GetProperty("state").GetString() ?? "",
            pr.GetProperty("isDraft").GetBoolean(),
            Time(pr, "createdAt"),
            ready,
            OptionalTime(pr, "mergedAt"),
            OptionalTime(pr, "closedAt"),
            pr.GetProperty("headRefName").GetString() ?? "",
            pr.GetProperty("baseRefName").GetString() ?? "",
            pr.TryGetProperty("mergeable", out var m) ? m.GetString() : null,
            threads,
            state,
            description);
    }

    // The hunt's own comments are by github-actions; the owner may file or update one by hand
    // (FlakeContext.Trusts). Anyone else's table would let a stranger rank the report.
    static bool Trusted(JsonElement node, string owner) =>
        node.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object && a.GetProperty("login").GetString() is { } login
        && (login is "github-actions" or "github-actions[bot]" or "app/github-actions" || string.Equals(login, owner, StringComparison.OrdinalIgnoreCase));

    internal static FlakyIssue ParseIssue(JsonElement issue, string owner)
    {
        var body = issue.GetProperty("body").GetString() ?? "";
        var created = Time(issue, "createdAt");
        var tallies = new List<FlakeTally>();
        if (Trusted(issue, owner) && WeeklyReport.Tally(body, created) is { } first)
            tallies.Add(first);
        if (issue.TryGetProperty("comments", out var comments))
            foreach (var comment in comments.GetProperty("nodes").EnumerateArray())
                if (Trusted(comment, owner) && WeeklyReport.Tally(comment.GetProperty("body").GetString() ?? "", Time(comment, "createdAt")) is { } tally)
                    tallies.Add(tally);
        return new FlakyIssue(
            issue.GetProperty("number").GetInt32(),
            issue.GetProperty("title").GetString() ?? "",
            Trusted(issue, owner) ? WeeklyReport.MarkedTest(body) : null,
            issue.GetProperty("state").GetString() == "OPEN",
            created,
            OptionalTime(issue, "closedAt"),
            tallies);
    }

    internal static IReadOnlyList<(string Name, TimeSpan Duration)> ParseJobTimes(JsonElement run)
    {
        if (run.ValueKind != JsonValueKind.Object || !run.TryGetProperty("checkSuite", out var suite) || suite.ValueKind != JsonValueKind.Object)
            return [];
        // A skipped job (one the plan didn't select) took no time worth ranking.
        return [.. suite.GetProperty("checkRuns").GetProperty("nodes").EnumerateArray()
            .Where(c => c.GetProperty("conclusion").GetString() is "SUCCESS" or "FAILURE" or "TIMED_OUT")
            .Select(c => (Name: c.GetProperty("name").GetString() ?? "", Started: OptionalTime(c, "startedAt"), Completed: OptionalTime(c, "completedAt")))
            .Where(c => c.Started is not null && c.Completed is not null)
            .Select(c => (c.Name, c.Completed!.Value - c.Started!.Value))];
    }
}
