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
              Judges open pull requests into main against quality/merge-rule.yml: the one numbered,
              those whose head is SHA, or all of them. Prints each verdict; with --post, also sets
              the head's merge-rule status (unless it already says the same), and with --summary,
              appends a Markdown table to FILE. A dry run (no --post) changes nothing.
        """;

    const string PullRequestFields = """
        fragment pr on PullRequest {
          number state isDraft baseRefName headRefOid changedFiles
          labels(first: 50) { nodes { name } }
          files(first: 100) { pageInfo { hasNextPage } nodes { path changeType } }
          reviews(last: 100) { nodes { author { __typename login } state commit { oid } } }
          commits(last: 1) { nodes { commit {
            oid
            status { contexts { context state description createdAt creator { login } } }
            checkSuites(last: 50) { nodes {
              app { slug }
              workflowRun { databaseId event workflow { name } }
              status conclusion
              checkRuns(last: 10, filterBy: { checkName: $check }) { nodes { databaseId status conclusion } }
            } }
          } } }
        }
        """;

    static readonly string OneQuery = PullRequestFields + """
        query($owner: String!, $name: String!, $number: Int!, $check: String!) {
          repository(owner: $owner, name: $name) { pullRequest(number: $number) { ...pr } }
        }
        """;

    static readonly string OpenQuery = PullRequestFields + """
        query($owner: String!, $name: String!, $base: String!, $check: String!, $cursor: String) {
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

        List<PullRequestFacts> prs;
        if (options.TryGetValue("pr", out var number))
        {
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                throw new ArgumentException($"--pr {number} is not a pull request number.");
            prs = [FetchOne(github, config, slug, n)];
        }
        else
        {
            prs = FetchOpen(github, config, slug);
            if (options.TryGetValue("sha", out var sha))
                prs = prs.Where(p => string.Equals(p.HeadSha, sha, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var rows = new List<string>();
        var failedPosts = 0;
        var bulk = !options.ContainsKey("pr");
        foreach (var listedPr in prs)
        {
            var pr = listedPr;
            var verdict = MergeRule.Evaluate(config, pr);
            if (verdict is null)
            {
                output.WriteLine($"#{pr.Number}: not judged ({pr.State.ToLowerInvariant()}, into {pr.BaseRef}).");
                continue;
            }
            if (post && bulk && !Unchanged(pr, verdict))
            {
                // A sweep read every pull request up front; a hold label or a failed receipt may have
                // landed since, and an event's run may already have posted on it. So read this one
                // again just before posting, rather than overwrite a newer verdict with a stale one.
                pr = FetchOne(github, config, slug, pr.Number);
                verdict = MergeRule.Evaluate(config, pr);
                if (verdict is null)
                {
                    output.WriteLine($"#{pr.Number}: not judged ({pr.State.ToLowerInvariant()}, into {pr.BaseRef}).");
                    continue;
                }
            }
            var unchanged = Unchanged(pr, verdict);
            var action = !post ? "dry run, not posted" : unchanged ? "unchanged, not posted again" : "posted";
            if (post && !unchanged)
            {
                try
                {
                    github.PostStatus(slug, pr.HeadSha, verdict.State, MergeRule.StatusContext, verdict.Description, options.GetValueOrDefault("target-url"));
                }
                catch (InvalidOperationException e)
                {
                    // One pull request's failed post mustn't stop the others being judged.
                    failedPosts++;
                    action = "NOT posted: " + e.Message;
                }
            }
            output.WriteLine($"#{pr.Number} {MergeRule.Short(pr.HeadSha)} {verdict.State}: {verdict.Description} ({action})");
            foreach (var file in verdict.SensitiveFiles)
                output.WriteLine($"    sensitive: {file}");
            rows.Add($"| #{pr.Number} | `{MergeRule.Short(pr.HeadSha)}` | {verdict.State} | {MergeRule.Cell(verdict.Description)} | {MergeRule.Cell(action)} |");
        }
        if (prs.Count == 0)
            output.WriteLine(options.TryGetValue("sha", out var s) ? $"No open pull request into {config.Base} has head {s}." : $"No open pull requests into {config.Base}.");

        if (options.TryGetValue("summary", out var summary) && summary.Length > 0)
        {
            var md = new StringBuilder();
            md.AppendLine($"### merge-rule: {prs.Count} pull request(s) judged by main's quality/merge-rule.yml");
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

    static bool Unchanged(PullRequestFacts pr, Verdict verdict) =>
        pr.Statuses.Where(s => s.Context == MergeRule.StatusContext).OrderByDescending(s => s.CreatedAt).FirstOrDefault() is { } current
        && current.State == verdict.State && current.Description == verdict.Description;

    static PullRequestFacts FetchOne(IGitHub github, MergeRuleConfig config, string slug, int number)
    {
        var (owner, name) = Split(slug);
        using var doc = github.GraphQL(OneQuery, new Dictionary<string, object> { ["owner"] = owner, ["name"] = name, ["number"] = number, ["check"] = config.Codeql.Check });
        var node = doc.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest");
        if (node.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"{slug} has no pull request #{number}.");
        return Complete(github, config, slug, ParseFacts(node, config));
    }

    static List<PullRequestFacts> FetchOpen(IGitHub github, MergeRuleConfig config, string slug)
    {
        var (owner, name) = Split(slug);
        var prs = new List<PullRequestFacts>();
        string? cursor = null;
        do
        {
            var variables = new Dictionary<string, object> { ["owner"] = owner, ["name"] = name, ["base"] = config.Base, ["check"] = config.Codeql.Check };
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
                    s.GetProperty("creator") is { ValueKind: JsonValueKind.Object } who ? who.GetProperty("login").GetString() : null,
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
        RunFact? run = null, check = null;
        var commits = pr.GetProperty("commits").GetProperty("nodes").EnumerateArray().ToList();
        // The last commit is the head, unless the head moved between GraphQL's reads; then judge
        // nothing on it rather than another commit's statuses and checks.
        if (commits.Count == 1 && commits[0].GetProperty("commit") is var commit && string.Equals(commit.GetProperty("oid").GetString(), head, StringComparison.OrdinalIgnoreCase))
        {
            if (commit.GetProperty("status") is { ValueKind: JsonValueKind.Object } status)
                statuses = status.GetProperty("contexts").EnumerateArray()
                    .Select(s => new StatusFact(
                        s.GetProperty("context").GetString() ?? "",
                        (s.GetProperty("state").GetString() ?? "").ToLowerInvariant(),
                        s.GetProperty("description").GetString(),
                        s.GetProperty("creator") is { ValueKind: JsonValueKind.Object } who ? who.GetProperty("login").GetString() : null,
                        DateTimeOffset.Parse(s.GetProperty("createdAt").GetString()!, CultureInfo.InvariantCulture)))
                    .ToList();
            long runId = -1, checkId = -1;
            foreach (var suite in commit.GetProperty("checkSuites").GetProperty("nodes").EnumerateArray())
            {
                // The latest run of the CodeQL workflow for this pull request: a ready-for-review
                // rerun supersedes the draft's, which skipped everything.
                if (suite.GetProperty("workflowRun") is { ValueKind: JsonValueKind.Object } wr
                    && wr.GetProperty("event").GetString() == "pull_request"
                    && wr.GetProperty("workflow").GetProperty("name").GetString() == config.Codeql.Workflow
                    && wr.GetProperty("databaseId").GetInt64() > runId)
                {
                    runId = wr.GetProperty("databaseId").GetInt64();
                    run = new RunFact(suite.GetProperty("status").GetString() ?? "", suite.GetProperty("conclusion").GetString());
                }
                // Code scanning's check comes from its own app, not from a workflow, so a
                // workflow job can't stand in for it by taking its name.
                if (suite.GetProperty("app") is { ValueKind: JsonValueKind.Object } app && app.GetProperty("slug").GetString() == "github-advanced-security")
                    foreach (var cr in suite.GetProperty("checkRuns").GetProperty("nodes").EnumerateArray())
                        if (cr.GetProperty("databaseId").GetInt64() > checkId)
                        {
                            checkId = cr.GetProperty("databaseId").GetInt64();
                            check = new RunFact(cr.GetProperty("status").GetString() ?? "", cr.GetProperty("conclusion").GetString());
                        }
            }
        }

        var facts = new PullRequestFacts(
            pr.GetProperty("number").GetInt32(),
            pr.GetProperty("state").GetString() ?? "",
            pr.GetProperty("isDraft").GetBoolean(),
            pr.GetProperty("baseRefName").GetString() ?? "",
            head,
            pr.GetProperty("labels").GetProperty("nodes").EnumerateArray().Select(l => l.GetProperty("name").GetString() ?? "").ToList(),
            files,
            FilesComplete: files.Count >= changed,
            reviews,
            statuses,
            run,
            check);
        return (facts, needsRest, changed);
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
