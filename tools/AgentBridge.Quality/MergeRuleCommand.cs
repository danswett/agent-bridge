using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentBridge.Quality;

/// <summary>What the merge-rule command needs from GitHub, so tests can stand in for it.</summary>
interface IGitHub
{
    JsonDocument GraphQL(string query, IReadOnlyDictionary<string, object> variables);
    JsonDocument Rest(string path);
    void PostStatus(string slug, string sha, string state, string context, string description, string? targetUrl);
}

/// <summary>GitHub through the gh CLI, authenticated by GH_TOKEN in a workflow and by gh's own login locally.</summary>
sealed class GhCli : IGitHub
{
    public static readonly GhCli Instance = new();

    public JsonDocument GraphQL(string query, IReadOnlyDictionary<string, object> variables)
    {
        var args = new List<string> { "api", "graphql", "-f", $"query={query}" };
        foreach (var (name, value) in variables)
            args.AddRange(value is int n ? ["-F", $"{name}={n.ToString(CultureInfo.InvariantCulture)}"] : ["-f", $"{name}={value}"]);
        var doc = JsonDocument.Parse(Run(args));
        // GraphQL answers 200 with errors, and partial data would read as "no review", "no files".
        if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
            throw new InvalidOperationException($"GitHub's GraphQL API answered with errors: {errors}");
        return doc;
    }

    public JsonDocument Rest(string path) => JsonDocument.Parse(Run(["api", path]));

    public void PostStatus(string slug, string sha, string state, string context, string description, string? targetUrl)
    {
        var args = new List<string> { "api", "-X", "POST", $"repos/{slug}/statuses/{sha}", "-f", $"state={state}", "-f", $"context={context}", "-f", $"description={description}" };
        if (!string.IsNullOrEmpty(targetUrl))
            args.AddRange(["-f", $"target_url={targetUrl}"]);
        Run(args);
    }

    static string Run(IEnumerable<string> args)
    {
        var start = new ProcessStartInfo("gh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Couldn't start gh.");
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"gh {string.Join(' ', start.ArgumentList.Take(4))} failed ({process.ExitCode}): {stderr.Result.Trim()}");
        return stdout;
    }
}

/// <summary>
/// <c>merge-rule</c>: judges open pull requests into main against quality/merge-rule.yml and, with
/// --post, sets each head's `merge-rule` status (quality/README.md §4.9). Without --post it's a dry
/// run that only prints, so anyone can see what the workflow would post.
/// </summary>
static class MergeRuleCommand
{
    internal const string Usage = """
          merge-rule (--pr N | --sha SHA | --all) [--slug OWNER/REPO] [--config FILE]
                     [--post] [--target-url URL] [--summary FILE]
              Judges open pull requests into main against quality/merge-rule.yml: those on the
              numbered one's head, those whose head is SHA, or all of them. Prints each verdict;
              with --post, also sets each head's merge-rule status to the worst verdict on it
              (unless the workflow already posted the same), and with --summary, appends a
              Markdown table to FILE. A dry run (no --post) changes nothing.
        """;

    // A head's checks cost most of the query, so the list of open pull requests reads without them,
    // and a pull request is read again with them only when its other facts don't already fail it.
    // That keeps a run within GITHUB_TOKEN's GraphQL budget however often it's triggered. Both
    // connections report their totals, so a list cut short fails rather than passes.
    const string ChecksFields = """
            checkSuites(last: 80) { totalCount nodes {
              app { slug }
              workflowRun { databaseId event workflow { name } }
              status conclusion
              checkRuns(last: 60) { totalCount nodes { databaseId name status conclusion } }
            } }
        """;

    static string Fragment(bool checks) => """
        fragment pr on PullRequest {
          number state isDraft baseRefName headRefOid changedFiles
          labels(first: 100) { totalCount nodes { name } }
          files(first: 100) { pageInfo { hasNextPage } nodes { path changeType } }
          reviews(last: 100) { nodes { author { __typename login } state commit { oid } } }
          commits(last: 1) { nodes { commit {
            oid
            status { contexts { context state description createdAt creator { login } } }
        """ + (checks ? ChecksFields : "") + """

          } } }
        }
        """;

    static string OneQuery(bool checks) => Fragment(checks) + """

        query($owner: String!, $name: String!, $number: Int!) {
          repository(owner: $owner, name: $name) { pullRequest(number: $number) { ...pr } }
        }
        """;

    static readonly string OpenQuery = Fragment(checks: false) + """

        query($owner: String!, $name: String!, $base: String!, $cursor: String) {
          repository(owner: $owner, name: $name) {
            pullRequests(states: OPEN, baseRefName: $base, first: 25, after: $cursor, orderBy: { field: CREATED_AT, direction: ASC }) {
              pageInfo { hasNextPage endCursor }
              nodes { ...pr }
            }
          }
        }
        """;

    public static int Run(IReadOnlyList<string> args, TextWriter output, IGitHub github)
    {
        var options = ParseOptions(args);
        var modes = new[] { "pr", "sha", "all" }.Count(options.ContainsKey);
        if (modes != 1)
            throw new ArgumentException("merge-rule needs exactly one of --pr N, --sha SHA or --all.");
        var configPath = options.GetValueOrDefault("config")
            ?? Path.Combine(ChangedFiles.RepoRoot(Directory.GetCurrentDirectory()), "quality", "merge-rule.yml");
        var config = MergeRuleConfig.Load(configPath);
        var slug = options.GetValueOrDefault("slug") ?? Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "danswett/agent-bridge";
        var post = options.ContainsKey("post");

        // The status belongs to a commit, not a pull request, and two open pull requests can share
        // a head: so every mode judges all open pull requests on the head it's about, and posts the
        // worst of their verdicts. A held or unreviewed one then can't ride on another's success.
        List<PullRequestFacts> prs;
        if (options.TryGetValue("pr", out var number))
        {
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                throw new ArgumentException($"--pr {number} is not a pull request number.");
            var one = FetchOne(github, config, slug, n, checks: false);
            if (MergeRule.Evaluate(config, one) is null)
            {
                output.WriteLine($"#{one.Number}: not judged ({one.State.ToLowerInvariant()}, into {one.BaseRef}).");
                prs = [];
            }
            else
                prs = FetchOpen(github, config, slug).Where(p => SameSha(p.HeadSha, one.HeadSha)).ToList();
        }
        else
        {
            prs = FetchOpen(github, config, slug);
            if (options.TryGetValue("sha", out var sha))
                prs = prs.Where(p => SameSha(p.HeadSha, sha)).ToList();
        }
        prs = prs.Select(p => WithChecks(github, config, slug, p)).ToList();

        var rows = new List<string>();
        var failedPosts = 0;
        foreach (var head in prs.GroupBy(p => p.HeadSha.ToLowerInvariant()))
        {
            var members = head.ToList();
            var judged = Judge(config, members);
            if (judged.Count == 0)
                continue;
            var combined = MergeRule.Combine(judged);
            if (post && !Unchanged(members[0], combined))
            {
                // The facts were read up front; a hold label, a failed receipt or a red check may
                // have landed since. So read each pull request again just before posting, rather
                // than overwrite a newer verdict with a stale one. One that has moved to another
                // head is that head's business: its push starts a run of its own.
                members = members.Select(p => WithChecks(github, config, slug, FetchOne(github, config, slug, p.Number, checks: false)))
                    .Where(p => SameSha(p.HeadSha, head.Key)).ToList();
                judged = Judge(config, members);
                if (judged.Count == 0)
                    continue;
                combined = MergeRule.Combine(judged);
            }
            var unchanged = Unchanged(members[0], combined);
            var action = !post ? "dry run, not posted" : unchanged ? "unchanged, not posted again" : "posted";
            if (post && !unchanged)
            {
                try
                {
                    github.PostStatus(slug, members[0].HeadSha, combined.State, MergeRule.StatusContext, combined.Description, options.GetValueOrDefault("target-url"));
                }
                catch (InvalidOperationException e)
                {
                    // One head's failed post mustn't stop the others being judged.
                    failedPosts++;
                    action = "NOT posted: " + e.Message;
                }
            }
            foreach (var (pr, verdict) in judged)
            {
                output.WriteLine($"#{pr.Number} {MergeRule.Short(pr.HeadSha)} {verdict.State}: {verdict.Description} ({action})");
                foreach (var file in verdict.SensitiveFiles)
                    output.WriteLine($"    sensitive: {file}");
                rows.Add($"| #{pr.Number} | `{MergeRule.Short(pr.HeadSha)}` | {verdict.State} | {MergeRule.Cell(verdict.Description)} | {MergeRule.Cell(action)} |");
            }
            if (judged.Count > 1)
                output.WriteLine($"    {judged.Count} pull requests share {MergeRule.Short(head.Key)}; its status is the worst verdict, {combined.State}: {combined.Description}");
        }
        if (prs.Count == 0 && !options.ContainsKey("pr"))
            output.WriteLine(options.TryGetValue("sha", out var s) ? $"No open pull request into {config.Base} has head {s}." : $"No open pull requests into {config.Base}.");

        if (options.TryGetValue("summary", out var summary) && summary.Length > 0)
        {
            var md = new StringBuilder();
            md.AppendLine($"### merge-rule: {rows.Count} pull request(s) judged by main's quality/merge-rule.yml");
            md.AppendLine();
            if (rows.Count > 0)
            {
                md.AppendLine("| PR | Head | Verdict | Why | Status |");
                md.AppendLine("|---|---|---|---|---|");
                foreach (var row in rows)
                    md.AppendLine(row);
            }
            File.AppendAllText(summary, md.ToString());
        }
        return failedPosts == 0 ? 0 : 1;
    }

    // REST names a bot "app[bot]" and GraphQL "app"; read both the GraphQL way.
    static string? Login(JsonElement actor) =>
        actor.ValueKind == JsonValueKind.Object && actor.GetProperty("login").GetString() is { } login
            ? (login.EndsWith("[bot]", StringComparison.Ordinal) ? login[..^5] : login)
            : null;

    static bool SameSha(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static List<(PullRequestFacts Pr, Verdict Verdict)> Judge(MergeRuleConfig config, IEnumerable<PullRequestFacts> prs) =>
        prs.Select(p => (Pr: p, Verdict: MergeRule.Evaluate(config, p)))
            .Where(j => j.Verdict is not null)
            .Select(j => (j.Pr, j.Verdict!))
            .ToList();

    // Only the workflow's own post counts as already said: the ruleset requires merge-rule from
    // GitHub Actions, so the same words posted by a person (a local --post) don't satisfy it.
    static bool Unchanged(PullRequestFacts pr, Verdict verdict) =>
        pr.Statuses.Where(s => s.Context == MergeRule.StatusContext).OrderByDescending(s => s.CreatedAt).FirstOrDefault() is { } current
        && current.Creator == MergeRule.Poster
        && current.State == verdict.State && current.Description == verdict.Description;

    static PullRequestFacts FetchOne(IGitHub github, MergeRuleConfig config, string slug, int number, bool checks)
    {
        var (owner, name) = Split(slug);
        using var doc = github.GraphQL(OneQuery(checks), new Dictionary<string, object> { ["owner"] = owner, ["name"] = name, ["number"] = number });
        var node = doc.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest");
        if (node.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"{slug} has no pull request #{number}.");
        return Complete(github, config, slug, ParseFacts(node, config));
    }

    /// <summary>
    /// The pull request read without its checks, unless its other facts don't already fail it:
    /// then read again with them, since only failure is decided without them.
    /// </summary>
    static PullRequestFacts WithChecks(IGitHub github, MergeRuleConfig config, string slug, PullRequestFacts light) =>
        MergeRule.Evaluate(config, light) is { State: "failure" } ? light : FetchOne(github, config, slug, light.Number, checks: true);

    static List<PullRequestFacts> FetchOpen(IGitHub github, MergeRuleConfig config, string slug)
    {
        var (owner, name) = Split(slug);
        var prs = new List<PullRequestFacts>();
        string? cursor = null;
        do
        {
            var variables = new Dictionary<string, object> { ["owner"] = owner, ["name"] = name, ["base"] = config.Base };
            if (cursor is not null)
                variables["cursor"] = cursor;
            using var doc = github.GraphQL(OpenQuery, variables);
            var page = doc.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequests");
            foreach (var node in page.GetProperty("nodes").EnumerateArray())
                prs.Add(Complete(github, config, slug, ParseFacts(node, config)));
            var info = page.GetProperty("pageInfo");
            cursor = info.GetProperty("hasNextPage").GetBoolean() ? info.GetProperty("endCursor").GetString() : null;
        }
        while (cursor is not null);
        return prs;
    }

    /// <summary>
    /// GraphQL lists at most 100 files and no renamed file's old path, so a longer list, or any
    /// rename, is read again from REST, which has both (up to GitHub's 3000). And GraphQL gives
    /// only each context's newest status: when the newest receipt isn't from a listed poster, an
    /// earlier one from a poster still counts (a failure above all), so the history comes from REST.
    /// </summary>
    static PullRequestFacts Complete(IGitHub github, MergeRuleConfig config, string slug, (PullRequestFacts Facts, bool NeedsRestFiles, int ChangedFiles) parsed)
    {
        var facts = parsed.Facts;
        if (parsed.NeedsRestFiles)
        {
            var files = new List<string>();
            var listed = 0;
            foreach (var file in RestPages(github, $"repos/{slug}/pulls/{facts.Number}/files"))
            {
                listed++;
                files.Add(file.GetProperty("filename").GetString()!);
                if (file.TryGetProperty("previous_filename", out var previous) && previous.GetString() is { } old)
                    files.Add(old);
            }
            facts = facts with { Files = files, FilesComplete = listed >= parsed.ChangedFiles };
        }
        var receipts = config.IndependentReview;
        if (facts.Statuses.Any(s => s.Context == receipts.Context && (s.Creator is null || !receipts.Posters.Contains(s.Creator, StringComparer.OrdinalIgnoreCase))))
        {
            var history = RestPages(github, $"repos/{slug}/commits/{facts.HeadSha}/statuses")
                .Where(s => s.GetProperty("context").GetString() == receipts.Context)
                .Select(s => new StatusFact(
                    receipts.Context,
                    s.GetProperty("state").GetString() ?? "",
                    s.GetProperty("description").GetString(),
                    Login(s.GetProperty("creator")),
                    DateTimeOffset.Parse(s.GetProperty("created_at").GetString()!, CultureInfo.InvariantCulture)))
                .ToList();
            facts = facts with { Statuses = facts.Statuses.Where(s => s.Context != receipts.Context).Concat(history).ToList() };
        }
        return facts;
    }

    static IEnumerable<JsonElement> RestPages(IGitHub github, string path)
    {
        for (var page = 1; ; page++)
        {
            using var doc = github.Rest($"{path}?per_page=100&page={page}");
            var items = doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
            foreach (var item in items)
                yield return item;
            if (items.Count < 100)
                yield break;
        }
    }

    /// <summary>One pull request's facts from the GraphQL fragment above.</summary>
    internal static (PullRequestFacts Facts, bool NeedsRestFiles, int ChangedFiles) ParseFacts(JsonElement pr, MergeRuleConfig config)
    {
        var head = pr.GetProperty("headRefOid").GetString()!;
        var changed = pr.GetProperty("changedFiles").GetInt32();
        var filesNode = pr.GetProperty("files");
        var fileNodes = filesNode.GetProperty("nodes").EnumerateArray().ToList();
        var files = fileNodes.Select(f => f.GetProperty("path").GetString()!).ToList();
        var needsRest = filesNode.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean()
            || fileNodes.Any(f => f.GetProperty("changeType").GetString() is "RENAMED" or "COPIED");

        var reviews = pr.GetProperty("reviews").GetProperty("nodes").EnumerateArray()
            .Select(r => new ReviewFact(
                r.GetProperty("author") is { ValueKind: JsonValueKind.Object } a ? a.GetProperty("login").GetString() ?? "" : "",
                r.GetProperty("author") is { ValueKind: JsonValueKind.Object } b && b.GetProperty("__typename").GetString() == "Bot",
                r.GetProperty("state").GetString() ?? "",
                r.GetProperty("commit") is { ValueKind: JsonValueKind.Object } c ? c.GetProperty("oid").GetString() : null))
            .ToList();

        var statuses = new List<StatusFact>();
        var checks = new List<CheckFact>();
        RunFact? run = null, codeScanning = null;
        var checksRead = false;
        var commits = pr.GetProperty("commits").GetProperty("nodes").EnumerateArray().ToList();
        // The last commit is the head, unless the head moved between GraphQL's reads; then judge
        // nothing on it rather than another commit's statuses and checks.
        if (commits.Count == 1 && commits[0].GetProperty("commit") is var commit && SameSha(commit.GetProperty("oid").GetString() ?? "", head))
        {
            if (commit.GetProperty("status") is { ValueKind: JsonValueKind.Object } status)
                statuses = status.GetProperty("contexts").EnumerateArray()
                    .Select(s => new StatusFact(
                        s.GetProperty("context").GetString() ?? "",
                        (s.GetProperty("state").GetString() ?? "").ToLowerInvariant(),
                        s.GetProperty("description").GetString(),
                        Login(s.GetProperty("creator")),
                        DateTimeOffset.Parse(s.GetProperty("createdAt").GetString()!, CultureInfo.InvariantCulture)))
                    .ToList();
            // Read without its checks (the open list), a pull request is judged on its other facts only.
            checksRead = commit.TryGetProperty("checkSuites", out var suites);
            if (checksRead)
                (run, codeScanning, checks) = ReadChecks(suites, config);
            // Every other status counts like a check; merge-rule is this verdict, and the receipt
            // is judged on its own terms above.
            checks.AddRange(statuses
                .Where(s => s.Context != MergeRule.StatusContext && s.Context != config.IndependentReview.Context)
                .Select(s => new CheckFact(s.Context, s.State switch { "success" => "success", "pending" or "expected" => "pending", _ => "failure" }, s.State)));
        }

        var labels = pr.GetProperty("labels").GetProperty("nodes").EnumerateArray().Select(l => l.GetProperty("name").GetString() ?? "").ToList();
        var facts = new PullRequestFacts(
            pr.GetProperty("number").GetInt32(),
            pr.GetProperty("state").GetString() ?? "",
            pr.GetProperty("isDraft").GetBoolean(),
            pr.GetProperty("baseRefName").GetString() ?? "",
            head,
            labels,
            files,
            FilesComplete: files.Count >= changed,
            reviews,
            statuses,
            run,
            codeScanning)
        { Checks = checks, ChecksRead = checksRead, LabelsComplete = !Truncated(pr.GetProperty("labels"), labels.Count) };
        return (facts, needsRest, changed);
    }

    /// <summary>
    /// The head's check runs, as the verdict reads them. A workflow that ran more than once on the
    /// head (a draft's run, then ready for review's; a cancelled run, then its replacement) counts
    /// by its latest run only, and a job retried within a run by its latest attempt. The merge
    /// rule's own workflows are left out (they're running while it judges), and CodeQL's workflow
    /// and code scanning's check are read apart, for their own messages.
    /// </summary>
    static (RunFact? CodeQlRun, RunFact? CodeQlCheck, List<CheckFact> Checks) ReadChecks(JsonElement connection, MergeRuleConfig config)
    {
        var suites = connection.GetProperty("nodes");
        RunFact? codeqlRun = null, codeqlCheck = null;
        long codeqlRunId = -1, codeqlCheckId = -1;
        var latestRun = new Dictionary<string, (long Id, JsonElement Suite)>(StringComparer.Ordinal);
        var otherApps = new Dictionary<string, (long Id, CheckFact Check)>(StringComparer.Ordinal);
        foreach (var suite in suites.EnumerateArray())
        {
            var slug = suite.GetProperty("app") is { ValueKind: JsonValueKind.Object } app ? app.GetProperty("slug").GetString() ?? "" : "";
            if (suite.GetProperty("workflowRun") is { ValueKind: JsonValueKind.Object } wr)
            {
                var workflow = wr.GetProperty("workflow").GetProperty("name").GetString() ?? "";
                var id = wr.GetProperty("databaseId").GetInt64();
                if (workflow == config.Codeql.Workflow)
                {
                    // The latest pull_request run: a ready-for-review rerun supersedes the draft's,
                    // which skipped everything.
                    if (wr.GetProperty("event").GetString() == "pull_request" && id > codeqlRunId)
                    {
                        codeqlRunId = id;
                        codeqlRun = new RunFact(suite.GetProperty("status").GetString() ?? "", suite.GetProperty("conclusion").GetString());
                    }
                    continue;
                }
                if (config.IgnoreWorkflows.Contains(workflow, StringComparer.Ordinal))
                    continue;
                if (!latestRun.TryGetValue(workflow, out var current) || id > current.Id)
                    latestRun[workflow] = (id, suite);
                continue;
            }
            if (Truncated(suite.GetProperty("checkRuns"), suite.GetProperty("checkRuns").GetProperty("nodes").GetArrayLength()))
                otherApps[slug + "/"] = (long.MaxValue, new CheckFact(slug, "failure", "more checks than the rule reads"));
            foreach (var cr in suite.GetProperty("checkRuns").GetProperty("nodes").EnumerateArray())
            {
                var id = cr.GetProperty("databaseId").GetInt64();
                var name = cr.GetProperty("name").GetString() ?? "";
                // Code scanning's check comes from its own app, not from a workflow, so a workflow
                // job can't stand in for it by taking its name.
                if (slug == "github-advanced-security" && name == config.Codeql.Check)
                {
                    if (id > codeqlCheckId)
                    {
                        codeqlCheckId = id;
                        codeqlCheck = new RunFact(cr.GetProperty("status").GetString() ?? "", cr.GetProperty("conclusion").GetString());
                    }
                    continue;
                }
                var key = slug + "/" + name;
                if (!otherApps.TryGetValue(key, out var current) || id > current.Id)
                    otherApps[key] = (id, Classify(name, cr));
            }
        }
        var checks = new List<CheckFact>();
        if (Truncated(connection, suites.GetArrayLength()))
            checks.Add(new CheckFact("the head's checks", "failure", "more than the rule reads"));
        foreach (var (_, suite) in latestRun.Values)
        {
            var workflow = suite.GetProperty("workflowRun").GetProperty("workflow").GetProperty("name").GetString() ?? "";
            var status = suite.GetProperty("status").GetString();
            var conclusion = suite.GetProperty("conclusion").GetString();
            var runs = suite.GetProperty("checkRuns").GetProperty("nodes").EnumerateArray().ToList();
            if (Truncated(suite.GetProperty("checkRuns"), runs.Count))
                checks.Add(new CheckFact(workflow, "failure", "more jobs than the rule reads"));
            // A run still going is pending even when every job so far passed: a later job (one
            // that needs the others) hasn't started yet.
            if (status != "COMPLETED")
                checks.Add(new CheckFact(workflow, "pending", status?.ToLowerInvariant()));
            if (runs.Count > 0)
            {
                checks.AddRange(runs
                    .GroupBy(cr => cr.GetProperty("name").GetString() ?? "")
                    .Select(g => Classify(g.Key, g.MaxBy(cr => cr.GetProperty("databaseId").GetInt64()))));
                continue;
            }
            // A run that ended before any job (a workflow file that doesn't parse:
            // startup_failure) failed. Not action_required: a run waiting for approval, as the
            // review relay's do when Copilot's review starts them.
            if (status == "COMPLETED" && conclusion is "STARTUP_FAILURE" or "FAILURE" or "TIMED_OUT" or "CANCELLED")
                checks.Add(new CheckFact(workflow, "failure", conclusion.ToLowerInvariant()));
        }
        checks.AddRange(otherApps.Values.Select(v => v.Check));
        return (codeqlRun, codeqlCheck, checks);
    }

    // A connection cut short (more nodes than the query asked for) could hide a failure.
    static bool Truncated(JsonElement connection, int read) =>
        connection.TryGetProperty("totalCount", out var total) && total.GetInt32() > read;

    // GitHub's own rule: success, skipped and neutral pass; anything still to finish is pending;
    // every other end (failure, cancelled, timed out, action required, stale...) fails.
    static CheckFact Classify(string name, JsonElement checkRun)
    {
        var status = checkRun.GetProperty("status").GetString();
        var conclusion = checkRun.GetProperty("conclusion").GetString();
        var state = status != "COMPLETED" ? "pending"
            : conclusion is "SUCCESS" or "SKIPPED" or "NEUTRAL" ? "success"
            : "failure";
        return new CheckFact(name, state, (status != "COMPLETED" ? status : conclusion)?.ToLowerInvariant());
    }

    static (string Owner, string Name) Split(string slug)
    {
        var parts = slug.Split('/');
        if (parts.Length != 2 || parts.Any(p => p.Length == 0))
            throw new ArgumentException($"--slug {slug} is not OWNER/REPO.");
        return (parts[0], parts[1]);
    }

    static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "post", "all" };
    static readonly HashSet<string> Valued = new(StringComparer.Ordinal) { "pr", "sha", "slug", "config", "target-url", "summary" };

    static Dictionary<string, string> ParseOptions(IReadOnlyList<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i].StartsWith("--", StringComparison.Ordinal) ? args[i][2..] : null;
            if (name is not null && Flags.Contains(name))
                options[name] = "";
            else if (name is not null && Valued.Contains(name))
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"--{name} needs a value.");
                options[name] = args[++i];
            }
            else
                throw new ArgumentException($"Unexpected argument '{args[i]}'.");
        }
        return options;
    }
}
