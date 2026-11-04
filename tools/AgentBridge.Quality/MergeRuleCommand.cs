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

/// <summary>
/// GitHub through the gh CLI, authenticated by GH_TOKEN in a workflow and by gh's own login locally.
/// Posts use MERGE_RULE_POST_TOKEN when it's set: the merge-rule App's token, which can post
/// statuses but not read checks (docs/merge-rule-app.md).
/// </summary>
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
        Run(args, Environment.GetEnvironmentVariable("MERGE_RULE_POST_TOKEN") is { Length: > 0 } token ? token : null);
    }

    static string Run(IEnumerable<string> args, string? token = null)
    {
        var start = new ProcessStartInfo("gh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (token is not null)
            start.Environment["GH_TOKEN"] = token;
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
                     [--post] [--target-url URL] [--summary FILE] [--allow-non-main]
              Judges open pull requests into main against quality/merge-rule.yml: those on the
              numbered one's head, those whose head is SHA, or all of them. Main's CI comes
              first: while its latest finished push run isn't green, every pull request fails
              but one with an exempt label (main_green). Prints each verdict;
              with --post, also sets each head's merge-rule status to the worst verdict on it
              (unless the workflow already posted the same), and with --summary, appends a
              Markdown table to FILE. A dry run (no --post) changes nothing.
              The evaluator and its rules are this checkout's, so it refuses unless HEAD is
              origin/main's commit with no local changes to them: run it from a freshly
              fetched origin/main checkout, never a pull request's worktree, which would judge
              itself. --allow-non-main judges anyway, with a warning.
        """;

    // Two reads (#524's review, GITHUB_TOKEN's GraphQL budget): the open list without checks or
    // files, which decides every pull request it can fail; then, for those it can't, the pull
    // request again with its head's checks, and that head's files from REST. Both connections
    // report their totals, so a list cut short fails rather than passes. rateLimit is read with
    // every query, so a run can say what it cost.
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
          number title state isDraft baseRefName headRefOid
          labels(first: 100) { totalCount nodes { name } }
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
          rateLimit { cost remaining }
          repository(owner: $owner, name: $name) { pullRequest(number: $number) { ...pr } }
        }
        """;

    static readonly string OpenQuery = Fragment(checks: false) + """

        query($owner: String!, $name: String!, $base: String!, $cursor: String) {
          rateLimit { cost remaining }
          repository(owner: $owner, name: $name) {
            pullRequests(states: OPEN, baseRefName: $base, first: 25, after: $cursor, orderBy: { field: CREATED_AT, direction: ASC }) {
              pageInfo { hasNextPage endCursor }
              nodes { ...pr }
            }
          }
        }
        """;

    /// <summary>One run's reads: what it cost, and each head's files, which never change, read once.</summary>
    sealed class Session(IGitHub github, MergeRuleConfig config, string slug)
    {
        public IGitHub GitHub { get; } = github;
        public MergeRuleConfig Config { get; } = config;
        public string Slug { get; } = slug;
        public int GraphQLPoints { get; private set; }
        public int? GraphQLRemaining { get; private set; }
        public int RestCalls { get; private set; }
        public Dictionary<string, (List<string> Files, bool Complete)> FilesBySha { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Main's CI, read once at the start of the run (ReadMain), so every verdict in it sees the same main.</summary>
        public MainFact Main { get; set; } = new("error", "main's CI wasn't read");

        public JsonDocument GraphQL(string query, Dictionary<string, object> variables)
        {
            var doc = GitHub.GraphQL(query, variables);
            if (doc.RootElement.GetProperty("data").TryGetProperty("rateLimit", out var rate) && rate.ValueKind == JsonValueKind.Object)
            {
                GraphQLPoints += rate.GetProperty("cost").GetInt32();
                GraphQLRemaining = rate.GetProperty("remaining").GetInt32();
            }
            else
                GraphQLPoints++;
            return doc;
        }

        public JsonDocument Rest(string path)
        {
            RestCalls++;
            return GitHub.Rest(path);
        }

        public string Cost => $"GraphQL {GraphQLPoints} point(s){(GraphQLRemaining is { } left ? $", {left} left this hour" : "")}; REST {RestCalls} call(s)";
    }

    /// <summary>
    /// Where the evaluator and its rules come from: the checkout's HEAD, the base branch's
    /// remote-tracking commit (null when it isn't fetched), whether the rules or the evaluator
    /// differ from HEAD in the working tree, and whether --config named rules from elsewhere.
    /// </summary>
    internal sealed record CheckoutFacts(string Head, string? BaseCommit, bool Dirty, bool OtherRules = false);

    static CheckoutFacts ReadCheckout(string baseBranch, string configPath)
    {
        var root = ChangedFiles.RepoRoot(Directory.GetCurrentDirectory());
        var otherRules = !string.Equals(Path.GetFullPath(configPath), Path.GetFullPath(Path.Combine(root, "quality", "merge-rule.yml")),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var head = ChangedFiles.Git(root, "rev-parse", "HEAD").Trim();
        string? baseCommit;
        try
        {
            baseCommit = ChangedFiles.Git(root, "rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{baseBranch}^{{commit}}").Trim();
        }
        catch (InvalidOperationException)
        {
            baseCommit = null;
        }
        var dirty = ChangedFiles.Git(root, "status", "--porcelain", "--", "quality/merge-rule.yml", "tools/AgentBridge.Quality").Trim().Length > 0;
        return new(head, baseCommit, dirty, otherRules);
    }

    public static int Run(IReadOnlyList<string> args, TextWriter output, IGitHub github, Func<string, CheckoutFacts>? checkout = null)
    {
        var options = ParseOptions(args);
        var modes = new[] { "pr", "sha", "all" }.Count(options.ContainsKey);
        if (modes != 1)
            throw new ArgumentException("merge-rule needs exactly one of --pr N, --sha SHA or --all.");
        var configPath = options.GetValueOrDefault("config")
            ?? Path.Combine(ChangedFiles.RepoRoot(Directory.GetCurrentDirectory()), "quality", "merge-rule.yml");
        var config = MergeRuleConfig.Load(configPath);

        // Run from a pull request's worktree, the pull request's own evaluator and rules would
        // judge it (#524's delta review): say whose they are, and refuse unless they're main's.
        var origin = checkout is not null ? checkout(config.Base) : ReadCheckout(config.Base, configPath);
        var where = $"{origin.Head}{(origin.Dirty ? " with local changes" : "")}{(origin.OtherRules ? $", rules from {configPath}" : "")}";
        output.WriteLine($"Evaluator and rules from {where}; origin/{config.Base} is {origin.BaseCommit ?? "not fetched"}.");
        if (origin.Dirty || origin.OtherRules || origin.BaseCommit is null || !SameSha(origin.Head, origin.BaseCommit))
        {
            if (!options.ContainsKey("allow-non-main"))
            {
                output.WriteLine($"Refusing: these aren't origin/{config.Base}'s evaluator and rules, so a pull request could be judging itself. Run it from a freshly fetched origin/{config.Base} checkout (git fetch origin, then git switch --detach origin/{config.Base} in a worktree kept for it), never a pull request's worktree, or pass --allow-non-main to judge with these anyway.");
                return 2;
            }
            output.WriteLine($"::warning::merge-rule: judging with the evaluator and rules from {where}, not origin/{config.Base}'s.");
        }
        var slug = options.GetValueOrDefault("slug") ?? Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "danswett/agent-bridge";
        var post = options.ContainsKey("post");
        var session = new Session(github, config, slug);
        var problems = 0;

        session.Main = ReadMain(session, out var mainLog);
        output.WriteLine(mainLog);
        if (session.Main.State == "error")
            problems++;

        // The status belongs to a commit, not a pull request, and two open pull requests can share
        // a head: so every mode judges all open pull requests on the head it's about, and posts the
        // worst of their verdicts. A held or unreviewed one then can't ride on another's success.
        List<PullRequestFacts> prs;
        string? listCut = null;
        if (options.TryGetValue("pr", out var number))
        {
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                throw new ArgumentException($"--pr {number} is not a pull request number.");
            var one = FetchOne(session, n, checks: false, history: true);
            if (MergeRule.Evaluate(config, session.Main, one) is null)
            {
                output.WriteLine($"#{one.Number}: not judged ({one.State.ToLowerInvariant()}, into {one.BaseRef}).");
                prs = [];
            }
            else
            {
                (var open, listCut) = FetchOpen(session);
                prs = open.Where(p => SameSha(p.HeadSha, one.HeadSha)).ToList();
                if (prs.Count == 0)
                    prs = [one];
                problems += Report(output, listCut);
            }
        }
        else
        {
            (prs, listCut) = FetchOpen(session);
            problems += Report(output, listCut);
            if (options.TryGetValue("sha", out var sha))
                prs = prs.Where(p => SameSha(p.HeadSha, sha)).ToList();
        }

        // With the open list cut short, a held pull request on any head may be among the unread,
        // so no pull request gets success from this run, in what's posted or what's shown.
        List<(PullRequestFacts Pr, MergeVerdict Verdict)> Judged(IEnumerable<(PullRequestFacts Pr, MergeVerdict? Verdict)> assessed, string sha) =>
            assessed.Where(j => j.Verdict is not null && SameSha(j.Pr.HeadSha, sha))
                .Select(j => (j.Pr, Verdict: listCut is not null && j.Verdict!.State == "success" ? MergeRule.ListCut(sha, listCut) : j.Verdict!))
                .ToList();

        var rows = new List<string>();
        foreach (var head in prs.GroupBy(p => p.HeadSha.ToLowerInvariant()))
        {
            var judged = Judged(head.Select(p => Assess(session, p)), head.Key);
            if (judged.Count == 0)
                continue;
            var combined = MergeRule.Combine(judged);
            if (post && !Unchanged(config, judged[0].Pr, combined))
            {
                // The facts were read up front; a hold label, a failed receipt or a red check may
                // have landed since. So read each pull request again just before posting, rather
                // than overwrite a newer verdict with a stale one. One that has moved to another
                // head is that head's business: its push starts a run of its own.
                judged = Judged(judged.Select(j => Reread(session, j.Pr)), head.Key);
                if (judged.Count == 0)
                    continue;
                combined = MergeRule.Combine(judged);
            }
            if (judged.Any(j => j.Verdict.State == "error"))
                problems++;
            var unchanged = Unchanged(config, judged[0].Pr, combined);
            var action = !post ? "dry run, not posted" : unchanged ? "unchanged, not posted again" : "posted";
            if (post && !unchanged)
            {
                try
                {
                    github.PostStatus(slug, judged[0].Pr.HeadSha, combined.State, MergeRule.StatusContext, combined.Description, options.GetValueOrDefault("target-url"));
                }
                catch (InvalidOperationException e)
                {
                    // One head's failed post mustn't stop the others being judged.
                    problems++;
                    action = "NOT posted: " + e.Message;
                }
            }
            foreach (var (pr, verdict) in judged)
            {
                output.WriteLine($"#{pr.Number} {MergeRule.Short(pr.HeadSha)} {verdict.State}: {verdict.Description} ({action})");
                foreach (var file in verdict.SensitiveFiles)
                    output.WriteLine($"    sensitive: {MergeRule.Printable(file)}");
                rows.Add($"| #{pr.Number} | `{MergeRule.Short(pr.HeadSha)}` | {verdict.State} | {MergeRule.Cell(verdict.Description)} | {MergeRule.Cell(action)} |");
            }
            if (judged.Count > 1)
                output.WriteLine($"    {judged.Count} pull requests share {MergeRule.Short(head.Key)}; its status is the worst verdict, {combined.State}: {combined.Description}");
        }
        if (prs.Count == 0 && !options.ContainsKey("pr"))
            output.WriteLine(options.TryGetValue("sha", out var s) ? $"No open pull request into {config.Base} has head {s}." : $"No open pull requests into {config.Base}.");
        output.WriteLine($"API: {session.Cost}.");
        // A run that can't read fails its heads with error, so say why before the budget runs out.
        if (session.GraphQLRemaining is < 200)
            output.WriteLine($"::warning::merge-rule: only {session.GraphQLRemaining} GraphQL points left this hour; runs may fail with error until it resets.");

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
            md.AppendLine();
            md.AppendLine($"API: {session.Cost}.");
            File.AppendAllText(summary, md.ToString());
        }
        return problems == 0 ? 0 : 1;
    }

    static int Report(TextWriter output, string? error)
    {
        if (error is null)
            return 0;
        output.WriteLine($"The open list stopped partway ({error}); judged only the pull requests read before it, none to success.");
        return 1;
    }

    // By kind, not exact type: JsonDocument.Parse throws JsonReaderException, a JsonException
    // that an exact match missed, so a garbled answer crashed the run instead of failing closed.
    static bool IsReadFailure(Exception e) => e is InvalidOperationException or JsonException or KeyNotFoundException or FormatException;

    /// <summary>
    /// The verdict on a pull request read from the open list: its light facts' failure (or error,
    /// when main's CI couldn't be read), if they have one and its receipts are all in view (a hold
    /// or a red main takes effect without anything more to read), otherwise the verdict on a full
    /// read. A full read that fails gives the light
    /// failure if there is one, otherwise error, never the light read's pending, so the head
    /// doesn't keep an earlier success.
    /// </summary>
    static (PullRequestFacts Pr, MergeVerdict? Verdict) Assess(Session session, PullRequestFacts light)
    {
        var verdict = MergeRule.Evaluate(session.Config, session.Main, light);
        if (verdict is null || (verdict.State is ("failure" or "error") && !ReceiptsHidden(session.Config, light)))
            return (light, verdict);
        try
        {
            var full = FetchFull(session, light.Number);
            return (full, MergeRule.Evaluate(session.Config, session.Main, full));
        }
        catch (Exception e) when (IsReadFailure(e))
        {
            return (light, verdict.State is "failure" or "error" ? verdict : MergeRule.ReadError(light, e.Message));
        }
    }

    static (PullRequestFacts Pr, MergeVerdict? Verdict) Reread(Session session, PullRequestFacts known)
    {
        PullRequestFacts light;
        try
        {
            light = FetchOne(session, known.Number, checks: false, history: false);
        }
        catch (Exception e) when (IsReadFailure(e))
        {
            return (known, MergeRule.ReadError(known, e.Message));
        }
        return Assess(session, light);
    }

    /// <summary>
    /// The base branch's CI: its workflow's newest finished push run there, at its latest attempt
    /// (one REST call). A newer run still going doesn't count, so main stays red until the fix's
    /// run passes; but a finished run being re-run (attempt 2 on) does, as not green, since its
    /// attempt before was newer than any older run's, and is almost always red: no one re-runs a
    /// green main. No finished run, or one that can't be read, fails closed.
    /// </summary>
    static MainFact ReadMain(Session session, out string log)
    {
        var config = session.Config;
        const int Runs = 20;
        var path = $"repos/{session.Slug}/actions/workflows/{Uri.EscapeDataString(config.MainGreen.Workflow)}/runs?branch={Uri.EscapeDataString(config.Base)}&event=push&per_page={Runs}";
        try
        {
            using var doc = session.Rest(path);
            var runs = doc.RootElement.GetProperty("workflow_runs").EnumerateArray()
                .OrderByDescending(r => r.GetProperty("id").GetInt64());
            foreach (var run in runs)
            {
                var sha = MergeRule.Short(run.GetProperty("head_sha").GetString() ?? "");
                var id = run.GetProperty("id").GetInt64();
                var attempt = run.TryGetProperty("run_attempt", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 1;
                if (run.GetProperty("status").GetString() == "completed")
                {
                    var conclusion = run.GetProperty("conclusion").GetString() ?? "without a result";
                    log = $"Main: {config.MainGreen.Workflow} run {id} (attempt {attempt}) on {sha} ended {conclusion}.";
                    return conclusion == "success"
                        ? new MainFact("success", $"{config.Base}'s CI {sha} passed")
                        : new MainFact("failure", $"{config.Base} is red (CI {sha}): waiting for {config.Base} to be fixed");
                }
                if (attempt > 1)
                {
                    log = $"Main: {config.MainGreen.Workflow} run {id} on {sha} is re-running (attempt {attempt}).";
                    return new MainFact("failure", $"{config.Base}'s CI {sha} is being re-run: waiting for {config.Base} to be green");
                }
            }
            log = $"Main: no finished {config.MainGreen.Workflow} push run on {config.Base} among the latest {Runs}.";
            return new MainFact("failure", $"no finished CI run on {config.Base}: waiting for one");
        }
        catch (Exception e) when (IsReadFailure(e))
        {
            log = $"Main: couldn't read {config.MainGreen.Workflow}'s runs on {config.Base}: {e.Message}";
            return new MainFact("error", MergeRule.Printable($"couldn't read {config.Base}'s CI: {e.Message}"));
        }
    }

    // REST names a bot "app[bot]" and GraphQL "app"; read both the GraphQL way.
    static string? Login(JsonElement actor) =>
        actor.ValueKind == JsonValueKind.Object && actor.GetProperty("login").GetString() is { } login
            ? (login.EndsWith("[bot]", StringComparison.Ordinal) ? login[..^5] : login)
            : null;

    static bool SameSha(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // Only the workflow's own post counts as already said: the ruleset requires merge-rule from one
    // poster, so the same words posted by a person (a local --post) don't satisfy it.
    static bool Unchanged(MergeRuleConfig config, PullRequestFacts pr, MergeVerdict verdict) =>
        pr.Statuses.Where(s => s.Context == MergeRule.StatusContext).OrderByDescending(s => s.CreatedAt).FirstOrDefault() is { } current
        && current.Creator == config.StatusPoster
        && current.State == verdict.State && current.Description == verdict.Description;

    static PullRequestFacts FetchOne(Session session, int number, bool checks, bool history)
    {
        var (owner, name) = Split(session.Slug);
        using var doc = session.GraphQL(OneQuery(checks), new Dictionary<string, object> { ["owner"] = owner, ["name"] = name, ["number"] = number });
        var node = doc.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest");
        if (node.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"{session.Slug} has no pull request #{number}.");
        var facts = ParseFacts(node, session.Config);
        return history ? WithReceiptHistory(session, facts) : facts;
    }

    /// <summary>
    /// The pull request with its head's checks, and the files of that same head: compare against
    /// the base, for the exact commit judged, not the pull request's files, which may already be
    /// a newer push's.
    /// </summary>
    static PullRequestFacts FetchFull(Session session, int number)
    {
        var facts = FetchOne(session, number, checks: true, history: true);
        var (files, complete) = FilesOf(session, facts.HeadSha);
        return facts with { Files = files, FilesComplete = complete };
    }

    /// <summary>
    /// What a commit changes since it left the base: compare base...sha, three dots, as a pull
    /// request's own diff does. GitHub lists at most 300 files there, so 300 means "maybe more",
    /// which counts as incomplete, as a missing list does. Renamed files' old paths count too.
    /// </summary>
    static (List<string> Files, bool Complete) FilesOf(Session session, string sha)
    {
        if (session.FilesBySha.TryGetValue(sha, out var known))
            return known;
        using var doc = session.Rest($"repos/{session.Slug}/compare/{Uri.EscapeDataString(session.Config.Base)}...{sha}");
        var files = new List<string>();
        var complete = doc.RootElement.TryGetProperty("files", out var list) && list.ValueKind == JsonValueKind.Array;
        if (complete)
        {
            foreach (var file in list.EnumerateArray())
            {
                files.Add(file.GetProperty("filename").GetString()!);
                if (file.TryGetProperty("previous_filename", out var previous) && previous.GetString() is { } old)
                    files.Add(old);
            }
            complete = list.GetArrayLength() < 300;
        }
        return session.FilesBySha[sha] = (files, complete);
    }

    /// <summary>
    /// The open list, light: no checks, files or receipt history (Assess reads them, where a read
    /// that fails becomes the head's error). A page that fails after others were read gives what
    /// was read and the error, so the pull requests already known are still judged.
    /// </summary>
    static (List<PullRequestFacts> Prs, string? Error) FetchOpen(Session session)
    {
        var (owner, name) = Split(session.Slug);
        var prs = new List<PullRequestFacts>();
        string? cursor = null;
        do
        {
            var variables = new Dictionary<string, object> { ["owner"] = owner, ["name"] = name, ["base"] = session.Config.Base };
            if (cursor is not null)
                variables["cursor"] = cursor;
            try
            {
                using var doc = session.GraphQL(OpenQuery, variables);
                var page = doc.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequests");
                foreach (var node in page.GetProperty("nodes").EnumerateArray())
                    prs.Add(ParseFacts(node, session.Config));
                var info = page.GetProperty("pageInfo");
                cursor = info.GetProperty("hasNextPage").GetBoolean() ? info.GetProperty("endCursor").GetString() : null;
            }
            catch (Exception e) when (IsReadFailure(e) && prs.Count > 0)
            {
                return (prs, e.Message);
            }
        }
        while (cursor is not null);
        return (prs, null);
    }

    /// <summary>
    /// GraphQL gives only each context's newest status: when the newest receipt isn't from a
    /// listed poster, an earlier one from a poster still counts (a failure above all), so the
    /// history comes from REST.
    /// </summary>
    static PullRequestFacts WithReceiptHistory(Session session, PullRequestFacts facts)
    {
        if (!ReceiptsHidden(session.Config, facts))
            return facts;
        var receipts = session.Config.IndependentReview;
        var history = RestPages(session, $"repos/{session.Slug}/commits/{facts.HeadSha}/statuses")
            .Where(s => s.GetProperty("context").GetString() == receipts.Context)
            .Select(s => new StatusFact(
                receipts.Context,
                s.GetProperty("state").GetString() ?? "",
                s.GetProperty("description").GetString(),
                Login(s.GetProperty("creator")),
                DateTimeOffset.Parse(s.GetProperty("created_at").GetString()!, CultureInfo.InvariantCulture)))
            .ToList();
        return facts with { Statuses = facts.Statuses.Where(s => s.Context != receipts.Context).Concat(history).ToList() };
    }

    /// <summary>Whether the newest receipt GraphQL shows isn't a listed poster's, so it may hide one that is.</summary>
    static bool ReceiptsHidden(MergeRuleConfig config, PullRequestFacts facts) =>
        facts.Statuses.Any(s => s.Context == config.IndependentReview.Context
            && (s.Creator is null || !config.IndependentReview.Posters.Contains(s.Creator, StringComparer.OrdinalIgnoreCase)));

    static IEnumerable<JsonElement> RestPages(Session session, string path)
    {
        for (var page = 1; ; page++)
        {
            using var doc = session.Rest($"{path}?per_page=100&page={page}");
            var items = doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
            foreach (var item in items)
                yield return item;
            if (items.Count < 100)
                yield break;
        }
    }

    /// <summary>
    /// One pull request's facts from the GraphQL fragment above. Its files come from FetchFull;
    /// read without checks, it's marked not fully read, which can fail it but never pass it.
    /// </summary>
    internal static PullRequestFacts ParseFacts(JsonElement pr, MergeRuleConfig config)
    {
        var head = pr.GetProperty("headRefOid").GetString()!;
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
        return new PullRequestFacts(
            pr.GetProperty("number").GetInt32(),
            pr.GetProperty("state").GetString() ?? "",
            pr.GetProperty("isDraft").GetBoolean(),
            pr.GetProperty("baseRefName").GetString() ?? "",
            head,
            labels,
            Files: [],
            FilesComplete: true,
            reviews,
            statuses,
            run,
            codeScanning)
        {
            Title = pr.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
            Checks = checks,
            FullyRead = checksRead,
            LabelsComplete = !Truncated(pr.GetProperty("labels"), labels.Count),
        };
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
                checks.AddRange(runs
                    .GroupBy(cr => cr.GetProperty("name").GetString() ?? "")
                    .Select(g => Classify(g.Key, g.MaxBy(cr => cr.GetProperty("databaseId").GetInt64()))));
            // The run's own result counts too, jobs or none: one cancelled between jobs, or failed
            // expanding a later matrix, ends red with every job it made green; one that ended
            // before any job (a workflow file that doesn't parse) is startup_failure. GitHub
            // updates it when failed jobs are rerun, so a green retry clears it. Not
            // action_required: a run waiting for approval, as the review relay's do when Copilot's
            // review starts them.
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

    static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "post", "all", "allow-non-main" };
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
