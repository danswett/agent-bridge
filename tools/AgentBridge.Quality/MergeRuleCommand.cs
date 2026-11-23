using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentBridge.Quality;

/// <summary>What the merge-rule command needs from GitHub, so tests can stand in for it.</summary>
interface IGitHub
{
    JsonDocument GraphQL(string query, IReadOnlyDictionary<string, object> variables);
    JsonDocument Rest(string path);
    /// <summary>A REST answer that isn't JSON, such as a job's log; only the weekly report reads one.</summary>
    string RestText(string path) => throw new NotSupportedException($"{path} can't be read as text here.");
    void PostStatus(string slug, string sha, string state, string context, string description, string? targetUrl);
}

/// <summary>
/// GitHub through the gh CLI, authenticated by GH_TOKEN in a workflow and by gh's own login locally.
/// Posts use MERGE_RULE_POST_TOKEN when it's set: the merge-rule App's token, which can post
/// statuses but not read checks (docs/merge-rule-app.md).
/// </summary>
/// <param name="exec">Runs gh: its exit code, stdout and stderr. A test hands in its own; the default starts the gh process.</param>
sealed class GhCli(Func<IReadOnlyList<string>, string?, (int Exit, string Out, string Err)>? exec = null) : IGitHub
{
    public static readonly GhCli Instance = new();
    readonly Func<IReadOnlyList<string>, string?, (int Exit, string Out, string Err)> run = exec ?? RunProcess;

    public JsonDocument GraphQL(string query, IReadOnlyDictionary<string, object> variables)
    {
        var args = new List<string> { "api", "graphql", "-f", $"query={query}" };
        foreach (var (name, value) in variables)
            if (value is string[] list)
                // gh builds an array from repeated key[]=value fields.
                args.AddRange(list.SelectMany(item => new[] { "-f", $"{name}[]={item}" }));
            else
                args.AddRange(value is int n ? ["-F", $"{name}={n.ToString(CultureInfo.InvariantCulture)}"] : ["-f", $"{name}={value}"]);
        var doc = JsonDocument.Parse(Run(args, body: true));
        // GraphQL answers 200 with errors, and partial data would read as "no review", "no files".
        if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
            throw new InvalidOperationException($"GitHub's GraphQL API answered with errors: {errors}");
        return doc;
    }

    public JsonDocument Rest(string path) => JsonDocument.Parse(Run(["api", path], body: true));

    // A job's log holds the runner's colour codes, which newer gh versions refuse to print without
    // this flag; older ones don't know it, and print them anyway.
    public string RestText(string path)
    {
        try
        {
            return Run(["api", "--allow-escape-sequences", path]);
        }
        catch (InvalidOperationException e) when (e.Message.Contains("unknown flag", StringComparison.Ordinal))
        {
            return Run(["api", path]);
        }
    }

    public void PostStatus(string slug, string sha, string state, string context, string description, string? targetUrl)
    {
        var args = new List<string> { "api", "-X", "POST", $"repos/{slug}/statuses/{sha}", "-f", $"state={state}", "-f", $"context={context}", "-f", $"description={description}" };
        if (!string.IsNullOrEmpty(targetUrl))
            args.AddRange(["-f", $"target_url={targetUrl}"]);
        Run(args, Environment.GetEnvironmentVariable("MERGE_RULE_POST_TOKEN") is { Length: > 0 } token ? token : null);
    }

    // gh's own word for an answer that was cut short or never came: the API's response did not parse. One flaky read must not error every
    // pull request's merge-rule, so a read that gets this, or an empty body where a body is due, is asked for once more.
    static bool Truncated(int exit, string stdout, string stderr, bool body) =>
        exit != 0 ? stderr.Contains("unexpected end of JSON input", StringComparison.OrdinalIgnoreCase) || stderr.Contains("unexpected EOF", StringComparison.OrdinalIgnoreCase)
            : body && string.IsNullOrWhiteSpace(stdout);

    // body: a read whose answer is JSON, so an empty one is a failure. A POST (a status) is never asked for again: it may have gone.
    string Run(IEnumerable<string> args, string? token = null, bool body = false)
    {
        var list = args.ToList();
        var retry = body && !list.Contains("-X", StringComparer.Ordinal);
        var attempts = 0;
        while (true)
        {
            attempts++;
            var (exit, stdout, stderr) = run(list, token);
            var label = $"gh {string.Join(' ', list.Take(4))}";
            if (exit == 0 && !(body && string.IsNullOrWhiteSpace(stdout)))
                return stdout;
            if (retry && attempts == 1 && Truncated(exit, stdout, stderr, body))
                continue;
            var after = attempts > 1 ? $" after {attempts} attempts" : "";
            throw new InvalidOperationException(exit == 0
                ? $"{label} returned an empty response{after}"
                : $"{label} failed ({exit}){after}: {stderr.Trim()}");
        }
    }

    static (int Exit, string Out, string Err) RunProcess(IReadOnlyList<string> args, string? token)
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
        return (process.ExitCode, stdout, stderr.Result);
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
              first: while the latest completed product evidence isn't green, every pull request fails
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
          reviews(last: 100) { nodes { author { __typename login } state body commit { oid } } }
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

    // What a scoped run needs of the open list: which pull requests are on one head. A number and
    // a head each, so a page of a hundred costs a point, where the full list is read twenty-five
    // to a page. Every pull request on the head is then read in full, one at a time.
    static readonly string LightQuery = """
        query($owner: String!, $name: String!, $base: String!, $cursor: String) {
          rateLimit { cost remaining }
          repository(owner: $owner, name: $name) {
            pullRequests(states: OPEN, baseRefName: $base, first: 100, after: $cursor, orderBy: { field: CREATED_AT, direction: ASC }) {
              pageInfo { hasNextPage endCursor }
              nodes { number headRefOid }
            }
          }
        }
        """;

    /// <summary>One run's reads: what it cost, and each head's files, which never change, read once.</summary>
    sealed class Session(IGitHub github, MergeRuleConfig config, string slug, bool arm64Waiver)
    {
        /// <summary>The CODEX_WAIVER variable is "on" (codex_waiver); unset is off.</summary>
        public bool CodexWaiver { get; init; }
        /// <summary>The time, for how long a main run has been unreadable.</summary>
        public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;
        public IGitHub GitHub { get; } = github;
        public MergeRuleConfig Config { get; } = config;
        public string Slug { get; } = slug;
        public bool Arm64Waiver { get; } = arm64Waiver;
        /// <summary>The catalog's post-merge job names (quality/README.md §4.9): shown, never counted.</summary>
        public IReadOnlySet<string> PostMerge { get; init; } = new HashSet<string>();
        readonly object gate = new();
        public int GraphQLPoints { get; private set; }
        public int? GraphQLRemaining { get; private set; }
        public int RestCalls { get; private set; }
        public System.Collections.Concurrent.ConcurrentDictionary<string, (List<string> Files, bool Complete)> FilesBySha { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Main's CI, read once at the start of the run (ReadMain), so every verdict in it sees the same main.</summary>
        public MainFact Main { get; set; } = new("error", "main's CI wasn't read");

        public JsonDocument GraphQL(string query, Dictionary<string, object> variables)
        {
            var doc = GitHub.GraphQL(query, variables);
            lock (gate)
            {
                if (doc.RootElement.GetProperty("data").TryGetProperty("rateLimit", out var rate) && rate.ValueKind == JsonValueKind.Object)
                {
                    GraphQLPoints += rate.GetProperty("cost").GetInt32();
                    GraphQLRemaining = rate.GetProperty("remaining").GetInt32();
                }
                else
                    GraphQLPoints++;
            }
            return doc;
        }

        public JsonDocument Rest(string path)
        {
            lock (gate)
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
        var dirty = ChangedFiles.Git(root, "status", "--porcelain", "--", "quality/merge-rule.yml", "quality/catalog.yml", "tools/AgentBridge.Quality").Trim().Length > 0;
        return new(head, baseCommit, dirty, otherRules);
    }

    public static int Run(IReadOnlyList<string> args, TextWriter output, IGitHub github, Func<string, CheckoutFacts>? checkout = null,
        Func<DateTimeOffset>? utcNow = null, Func<string, string?>? variable = null)
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
        var now = (utcNow ?? (() => DateTimeOffset.UtcNow))();
        var session = new Session(github, config, slug, config.Arm64Waiver?.Active() == true)
        {
            Now = utcNow ?? (() => DateTimeOffset.UtcNow),
            CodexWaiver = config.CodexWaiver is { } codexWaiver && codexWaiver.Switch == "CODEX_WAIVER" && (variable ?? Environment.GetEnvironmentVariable)(codexWaiver.Switch) == "on",
            PostMerge = PostMergeJobs(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, "catalog.yml"), output),
        };
        var problems = 0;

        session.Main = ReadMain(session, out var mainLog);
        output.WriteLine(mainLog);
        if (session.Main.State == "error")
            problems++;

        // The status belongs to a commit, not a pull request, and two open pull requests can share
        // a head: so every mode judges all open pull requests on the head it's about, and posts the
        // worst of their verdicts. A held or unreviewed one then can't ride on another's success.
        // A scoped run that finds main not green judges every open pull request, as --all does: a
        // run for another pull request or head may have been displaced (GitHub keeps one waiting
        // run per concurrency group), and its success, posted while main was green, mustn't stand
        // while main is red. Posts that wouldn't change a verdict are skipped, so this is cheap.
        var widened = (options.ContainsKey("pr") || options.ContainsKey("sha")) && session.Main.State != "success";
        if (widened)
            output.WriteLine($"Main isn't green ({session.Main.State}): judging every open pull request, not only the one asked for.");
        List<PullRequestFacts> prs;
        string? listCut = null;
        if (!widened && options.TryGetValue("pr", out var number))
        {
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                throw new ArgumentException($"--pr {number} is not a pull request number.");
            var one = FetchOne(session, n, checks: false, history: true, out var oneLight);
            if (MergeRule.Evaluate(config, session.Main, one) is null)
            {
                output.WriteLine($"#{one.Number}: not judged ({one.State.ToLowerInvariant()}, into {one.BaseRef}).");
                prs = [];
            }
            else
            {
                (prs, listCut) = FetchOnHead(session, one.HeadSha, oneLight);
                problems += Report(output, listCut);
            }
        }
        else
        {
            if (!widened && options.TryGetValue("sha", out var sha))
                (prs, listCut) = FetchOnHead(session, sha, null);
            else
                (prs, listCut) = FetchOpen(session);
            problems += Report(output, listCut);
        }

        // With the open list cut short, a held pull request on any head may be among the unread,
        // so no pull request gets success from this run, in what's posted or what's shown.
        List<(PullRequestFacts Pr, MergeVerdict Verdict)> Judged(IEnumerable<(PullRequestFacts Pr, MergeVerdict? Verdict)> assessed, string sha) =>
            assessed.Where(j => j.Verdict is not null && SameSha(j.Pr.HeadSha, sha))
                .Select(j => (j.Pr, Verdict: listCut is not null && j.Verdict!.State == "success" ? MergeRule.ListCut(sha, listCut) : j.Verdict!))
                .ToList();

        var rows = new List<string>();
        var heads = prs.GroupBy(p => p.HeadSha.ToLowerInvariant()).ToList();
        var assessed = AssessTogether(session, heads);
        foreach (var head in heads)
        {
            var judged = Judged(assessed[head.Key], head.Key);
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
        if (prs.Count == 0 && !(options.ContainsKey("pr") && !widened))
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

    /// <summary>
    /// How many heads' reads are in flight at once: a head's full read is a query and a compare, each
    /// a round trip, so the heads of a sweep are read together, a few at a time.
    /// </summary>
    internal const int HeadsAtOnce = 4;

    /// <summary>
    /// Every head's pull requests assessed, the heads together (a few at once, the pull requests of one
    /// head one after another) and the answers in the heads' own order, so what is printed and posted
    /// reads as it did when they were read one after another. A read that is not a read failure (those
    /// become the head's verdict) ends the run as it did, with its own exception.
    /// </summary>
    static Dictionary<string, List<(PullRequestFacts Pr, MergeVerdict? Verdict)>> AssessTogether(Session session, List<IGrouping<string, PullRequestFacts>> heads)
    {
        var answers = new List<(PullRequestFacts Pr, MergeVerdict? Verdict)>[heads.Count];
        try
        {
            Parallel.For(0, heads.Count, new ParallelOptions { MaxDegreeOfParallelism = HeadsAtOnce },
                i => answers[i] = heads[i].Select(p => Assess(session, p)).ToList());
        }
        catch (AggregateException e)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.Flatten().InnerExceptions[0]).Throw();
        }
        return heads.Select((head, i) => (head.Key, answers[i])).ToDictionary(x => x.Key, x => x.Item2);
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
        // A red main is lifted only by what the files show, so an exempt label needs the full read.
        var needsFiles = session.Main.State != "success" && light.Labels.Any(l => session.Config.MainGreen.ExemptLabels.Contains(l, StringComparer.OrdinalIgnoreCase));
        if (verdict is null || (verdict.State is ("failure" or "error") && !needsFiles && !ReceiptsHidden(session.Config, light)))
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
    /// The catalog beside the rules' post-merge job names. Missing or unreadable, every job blocks.
    /// </summary>
    static IReadOnlySet<string> PostMergeJobs(string path, TextWriter output)
    {
        try
        {
            return Catalog.Load(path).PostMergeJobs();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or YamlDotNet.Core.YamlException)
        {
            output.WriteLine($"::warning::merge-rule: no post-merge tier ({e.Message}); every job counts.");
            return new HashSet<string>();
        }
    }

    /// <summary>
    /// Main's newest completed product evidence, with each named job's latest attempt.
    /// Missing or unreadable facts never grant green.
    /// </summary>
    static MainFact ReadMain(Session session, out string log)
    {
        var config = session.Config;
        try
        {
            var skipped = new List<long>();
            using var evidence = CompletedMain(session, skipped).GetEnumerator();
            var found = false;
            while (!found && evidence.MoveNext())
                found = evidence.Current.Usable;
            if (!found)
            {
                log = $"Main: no completed product evidence in {config.MainGreen.Workflow}'s push history.";
                return new("failure", $"no completed CI product evidence on {config.Base}: waiting for one"
                    + (skipped.Count == 0 ? "" : $" (skipped unreadable run {string.Join(", ", skipped)})"));
            }
            var (run, latest, all, _) = evidence.Current;
            var sha = run.GetProperty("head_sha").GetString();
            if (!IsCommitSha(sha))
                throw new InvalidOperationException("CI run has no valid commit SHA");
            var id = run.GetProperty("id").GetInt64();
            var attempt = RunAttempt(run);
            var planGreen = latest.TryGetValue("plan", out var plan)
                && plan.GetProperty("status").GetString() == "completed" && plan.GetProperty("conclusion").GetString() == "success";
            // An infra-only failure never blocks: it alerts for the one rerun, then escalates as
            // INFRA-PERSISTENT. A real failure in any attempt holds until a later attempt of that job
            // succeeds, so a rerun failing in setup can't erase attempt 1's test failure.
            bool RerunUsed(JsonElement job) => Math.Max(attempt, RunAttempt(job)) > 1;
            var memo = new Dictionary<string, bool>(StringComparer.Ordinal);
            bool Infra(JsonElement job)
            {
                var key = job.GetRawText();
                if (!memo.TryGetValue(key, out var infra))
                    memo[key] = infra = InfraOnly(session, job);
                return infra;
            }
            // A job's last real result in one run, across its attempts: a success, or a failure that
            // isn't infra-only. Null when every attempt was infra-only, skipped or neutral.
            JsonElement? LastReal(string name, List<JsonElement> jobs) => jobs
                .Where(j => j.GetProperty("name").GetString() == name && (j.GetProperty("conclusion").GetString() == "success"
                    || (j.GetProperty("conclusion").GetString() is not (null or "skipped" or "neutral") && !Infra(j))))
                .OrderBy(RunAttempt).ThenBy(j => j.GetProperty("id").GetInt64()).Cast<JsonElement?>().LastOrDefault();
            bool Waived(string name, string? conclusion) => session.Arm64Waiver && config.Arm64Waiver?.Allows(name, conclusion) == true;
            // A real result holds unless it passed or is a waived arm64 failure, wherever it was found.
            bool Holds(string name, JsonElement? real) => real is { } r && r.GetProperty("conclusion").GetString() is var c && c != "success" && !Waived(name, c);
            bool Unresolved(JsonElement job) => Holds(job.GetProperty("name").GetString()!, LastReal(job.GetProperty("name").GetString()!, all));
            bool Exempt(JsonElement job) => Infra(job) && !Unresolved(job);
            bool Green(JsonElement job) => session.PostMerge.Contains(job.GetProperty("name").GetString() ?? "")
                || (job.GetProperty("status").GetString() == "completed"
                && (job.GetProperty("conclusion").GetString() == "success"
                    || (job.GetProperty("conclusion").GetString() == "skipped" && !Unresolved(job))
                    || Exempt(job)
                    || Waived(job.GetProperty("name").GetString() ?? "", job.GetProperty("conclusion").GetString())));
            static bool SkippedFamily(string name, string family, JsonElement job) =>
                (name == family || (name.StartsWith($"{family} (${{{{ matrix.", StringComparison.Ordinal)
                    && name.EndsWith(" }})", StringComparison.Ordinal)))
                && job.GetProperty("status").GetString() == "completed" && job.GetProperty("conclusion").GetString() == "skipped";
            bool Passed(string name)
            {
                // An exact post-merge job never holds main, run or not; a family name still checks its instances.
                if (session.PostMerge.Contains(name) && !latest.Keys.Any(k => k.StartsWith($"{name} (", StringComparison.Ordinal)))
                    return true;
                if (!name.Contains(" (", StringComparison.Ordinal))
                {
                    var instances = latest.Where(j => j.Key == name || j.Key.StartsWith($"{name} (", StringComparison.Ordinal)).ToList();
                    return instances.Count > 0 && instances.All(j => Green(j.Value));
                }
                if (latest.TryGetValue(name, out var job))
                    return Green(job);
                var family = name.Split(" (", StringSplitOptions.None)[0];
                // GitHub doesn't expand a matrix when the selector skips the whole family.
                return latest.Any(j => SkippedFamily(j.Key, family, j.Value));
            }
            var held = planGreen ? config.MainGreen.Jobs.Where(name => !Passed(name)).ToList() : ["plan"];
            var families = config.MainGreen.LegFamilies;
            var legs = families.SelectMany(f => f).ToHashSet(StringComparer.Ordinal);
            var stems = legs.Select(n => n.Split(" (", StringSplitOptions.None)[0]).ToHashSet(StringComparer.Ordinal);
            var present = latest.Keys.Where(n => stems.Contains(n.Split(" (", StringSplitOptions.None)[0]))
                .ToHashSet(StringComparer.Ordinal);
            // An absent post-merge name is allowed, but its renamed replacement is not an exemption.
            var pinned = config.MainGreen.Jobs.Concat(legs).ToHashSet(StringComparer.Ordinal);
            foreach (var family in config.MainGreen.Jobs.Where(n => n.Contains(" (", StringComparison.Ordinal))
                .Select(n => n.Split(" (", StringSplitOptions.None)[0]).Distinct(StringComparer.Ordinal).Where(f => !pinned.Contains(f)))
                held.AddRange(latest.Where(j => (j.Key == family || j.Key.StartsWith($"{family} (", StringComparison.Ordinal))
                    && !pinned.Contains(j.Key) && !SkippedFamily(j.Key, family, j.Value)).Select(j => $"unexpected leg {j.Key}"));
            if (families.Count > 0)
            {
                var exact = families.Where(f => present.SetEquals(f)).ToList();
                var possible = families.Where(f => present.IsSubsetOf(f)).ToList();
                var unexpected = present.Except(legs, StringComparer.Ordinal).ToList();
                if (present.Count == 0)
                    held.Add("no leg family");
                else if (unexpected.Count > 0)
                    held.Add($"unexpected leg {string.Join(", ", unexpected)}");
                else if (exact.Count == 1)
                    held.AddRange(exact[0].Where(n => !session.PostMerge.Contains(n)
                        && latest[n].GetProperty("conclusion").GetString() != "success" && !Exempt(latest[n])));
                else if (exact.Count > 1)
                    held.Add("mixed or ambiguous leg families");
                else if (possible.Count > 0)
                {
                    held.AddRange(present.Where(n => !session.PostMerge.Contains(n)
                        && latest[n].GetProperty("conclusion").GetString() != "success" && !Exempt(latest[n])));
                    held.AddRange(possible.SelectMany(f => f).Except(present, StringComparer.Ordinal).Where(n => !session.PostMerge.Contains(n)));
                }
                else
                    held.Add("mixed leg families");
            }
            bool Required(string name) => !session.PostMerge.Contains(name) && (present.Contains(name) || config.MainGreen.Jobs.Any(n =>
                n == name || (!n.Contains(" (", StringComparison.Ordinal) && name.StartsWith($"{n} (", StringComparison.Ordinal))));
            var alerts = latest.Where(j => Required(j.Key) && Exempt(j.Value))
                .OrderBy(j => !RerunUsed(j.Value)).ThenBy(j => j.Key, StringComparer.Ordinal)
                .Select(j => RerunUsed(j.Value) ? $"INFRA-PERSISTENT {j.Key}: rerun used, open an issue" : $"ALERT infra-only {j.Key}: rerun needed (once)")
                .ToList();
            // A job with no real result in this run (only infra-only or skipped after a retry) proved
            // nothing about this commit: the newest older run with a real result decides, so a failure
            // holds until a success, and a success stops the lookup.
            var pending = latest.Where(j => Required(j.Key) && LastReal(j.Key, all) is null && (Infra(j.Value) || (j.Value.GetProperty("conclusion").GetString() == "skipped"
                && all.Any(a => a.GetProperty("name").GetString() == j.Key && a.GetProperty("conclusion").GetString() != "skipped"))))
                .Select(j => j.Key).ToHashSet(StringComparer.Ordinal);
            while (pending.Count > 0 && evidence.MoveNext())
                foreach (var name in pending.Where(evidence.Current.Jobs.ContainsKey).ToList())
                {
                    // An older run's unfinished retry is no result, but its earlier attempts still are.
                    if (LastReal(name, evidence.Current.All) is not { } real)
                        continue;
                    pending.Remove(name);
                    if (Holds(name, real))
                        held.Add($"{name} (run {evidence.Current.Run.GetProperty("id").GetInt64()})");
                }
            log = $"Main: {config.MainGreen.Workflow} run {id} (attempt {attempt}) on {MergeRule.Short(sha!)}; "
                + (held.Count == 0 ? "named jobs passed." : $"named jobs not green: {string.Join(", ", held)}.")
                + "\n" + string.Join("\n", latest.OrderBy(j => j.Key, StringComparer.Ordinal).Select(j =>
                    $"Main job [{(j.Key == "plan" || Required(j.Key) ? "required" : session.PostMerge.Contains(j.Key) ? "post-merge" : "non-gating")}]: {j.Key}: {j.Value.GetProperty("status").GetString()}/{j.Value.GetProperty("conclusion").GetString() ?? "pending"} (attempt {RunAttempt(j.Value)})."))
                + string.Concat(alerts.Select(a => $"\n{a}"))
                + (skipped.Count == 0 ? "" : $"\nMain: skipped unreadable run(s) {string.Join(", ", skipped)}: their jobs could not be read for over {UnreadableGrace.TotalMinutes:0} minutes.");
            var fact = held.Count == 0
                ? new("success", $"{config.Base}'s CI {MergeRule.Short(sha!)} named jobs passed")
                : new MainFact("failure", $"{config.Base} is red (CI {MergeRule.Short(sha!)}): named job {held[0]} is not green");
            // A run whose jobs stayed unreadable past the grace was passed over for an older readable one: the status says which, so it is
            // never silent (alerts follow the blocking reasons in the description).
            // First, ahead of any infra alert: the description shows the first alert and counts the rest, and cuts at 140 characters.
            return fact with { Alerts = skipped.Count == 0 ? alerts : [$"skipped unreadable run {string.Join(", ", skipped)}", .. alerts] };
        }
        catch (Exception e) when (IsReadFailure(e))
        {
            log = $"Main: couldn't read {config.MainGreen.Workflow}'s runs on {config.Base}: {e.Message}";
            return new MainFact("error", MergeRule.Printable($"couldn't read {config.Base}'s CI: {e.Message}"));
        }
    }

    /// <summary>
    /// How long a main run's jobs must have been unreadable before it is passed over (a fresh run can 404 briefly): until then the judge errors,
    /// as it always has. A zombie run the API never serves must not hold every pull request's merge-rule for good.
    /// </summary>
    internal static readonly TimeSpan UnreadableGrace = TimeSpan.FromMinutes(15);

    /// <summary>
    /// A run old enough to skip is read once more after this pause before it is: one transient 404 or 5xx must not skip a main run (they take
    /// 26 to 35 minutes, so most are older than the grace). There is no memory between evaluations: a run unreadable on both reads of one evaluation is skipped.
    /// </summary>
    internal static TimeSpan UnreadableRetryAfter { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How many of main's older runs are read together once the newest has not answered.</summary>
    internal const int RunsAtOnce = 4;

    /// <summary>One run's job list, all its pages; or that it can't be read (skipped only when it may be).</summary>
    static (List<JsonElement> Jobs, bool Unreadable) ReadRunJobs(Session session, JsonElement run)
    {
        var id = run.GetProperty("id").GetInt64();
        var jobs = new List<JsonElement>();
        for (var jobPage = 1; ; jobPage++)
        {
            JsonDocument listed;
            JsonElement batch;
            var jobsPath = $"repos/{session.Slug}/actions/runs/{id}/jobs?filter=all&per_page=100&page={jobPage}";
            try
            {
                (listed, batch) = ReadJobs(session, jobsPath);
            }
            // A 404 or other non-2xx (gh exits 1 and says so), an empty or cut-short body, or a body with no jobs: unreadable.
            catch (Exception e) when (e is InvalidOperationException or JsonException or KeyNotFoundException)
            {
                // Skipped only once unreadable for over the grace, never a failure or a state not known (it would hide a red main),
                // and never when the run's age isn't known: those error, as before. An old enough run is read once more first.
                if (!MayBeSkipped(session, run)) { throw; }
                Thread.Sleep(UnreadableRetryAfter);
                try
                {
                    (listed, batch) = ReadJobs(session, jobsPath);
                }
                catch (Exception again) when (again is InvalidOperationException or JsonException or KeyNotFoundException)
                {
                    return (jobs, true);
                }
            }
            using var held = listed;
            jobs.AddRange(batch.EnumerateArray().Select(j => j.Clone()));
            if (jobs.Count >= listed.RootElement.GetProperty("total_count").GetInt32())
                return (jobs, false);
            if (batch.GetArrayLength() == 0 || jobPage >= 20)
                throw new InvalidOperationException("CI job list is incomplete");
        }
    }

    static IEnumerable<(JsonElement Run, Dictionary<string, JsonElement> Jobs, List<JsonElement> All, bool Usable)> CompletedMain(Session session, List<long>? skipped = null)
    {
        var config = session.Config;
        var seen = 0;
        for (var page = 1; ; page++)
        {
            using var doc = session.Rest($"repos/{session.Slug}/actions/workflows/{Uri.EscapeDataString(config.MainGreen.Workflow)}/runs?branch={Uri.EscapeDataString(config.Base)}&event=push&per_page=20&page={page}");
            var runs = doc.RootElement.GetProperty("workflow_runs");
            // The newest run is read alone: it usually answers, and a read is a quota point. Once a run has
            // not, the runs below it are read together, a few at a time, and each answer is used in order,
            // so the answer is the one reading them one after another gave. A failed read of a run not yet
            // reached is raised when that run is, not before.
            var ordered = runs.EnumerateArray().OrderByDescending(r => r.GetProperty("id").GetInt64()).Select(r => r.Clone()).ToList();
            var ahead = new Task<(List<JsonElement> Jobs, bool Unreadable)>?[ordered.Count];
            for (var at = 0; at < ordered.Count; at++)
            {
                if (ahead[at] is null)
                    for (var next = at; next < Math.Min(ordered.Count, at + (at == 0 ? 1 : RunsAtOnce)); next++)
                    {
                        var readRun = ordered[next];
                        ahead[next] = Task.Run(() => ReadRunJobs(session, readRun));
                    }
                var run = ordered[at];
                var id = run.GetProperty("id").GetInt64();
                var (jobs, unreadable) = ahead[at]!.GetAwaiter().GetResult();
                if (unreadable)
                {
                    skipped?.Add(id);
                    continue;
                }
                // A concurrency-displaced run is cancelled before any job exists.
                if (jobs.Count == 0 && (Incomplete(run) || run.GetProperty("conclusion").GetString() == "cancelled"))
                    continue;
                if (jobs.Any(j => !j.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(name.GetString())))
                    throw new InvalidOperationException("CI job has missing or invalid name");
                // Failed-jobs-only reruns retain earlier successful instances.
                var latest = jobs.GroupBy(j => j.GetProperty("name").GetString() ?? "", StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(RunAttempt).ThenByDescending(j => j.GetProperty("id").GetInt64()).First(), StringComparer.Ordinal);
                // A failed plan's unexpanded skipped family is replaced when a retry expands it.
                foreach (var placeholder in latest.Where(j => j.Value.GetProperty("conclusion").GetString() == "skipped"
                    && (!j.Key.Contains(" (", StringComparison.Ordinal) || j.Key.Contains(" (${{ matrix.", StringComparison.Ordinal))).ToList())
                    if (latest.Any(j => j.Key.StartsWith(placeholder.Key.Split(" (", StringSplitOptions.None)[0] + " (", StringComparison.Ordinal)
                        && j.Key != placeholder.Key && RunAttempt(j.Value) > RunAttempt(placeholder.Value)))
                        latest.Remove(placeholder.Key);
                if (!latest.TryGetValue("plan", out var plan))
                    throw new InvalidOperationException("CI job list has no plan");
                var planPending = Incomplete(plan);
                var planConclusion = plan.GetProperty("conclusion").GetString();
                if (!planPending && planConclusion is null)
                    throw new InvalidOperationException("completed plan has no conclusion");
                // An unusable run (plan, product or required jobs) still answers an older-failure lookup.
                var usable = !planPending && planConclusion == "success";
                static bool Product(string? name) => name == "product" || name?.StartsWith("product (", StringComparison.Ordinal) == true;
                var product = latest.Where(j => Product(j.Key)).ToList();
                if (usable && product.Count == 0)
                    throw new InvalidOperationException("CI job list has no product family");
                // A classification-skipped family passes over; a retry skipped after any instance ran
                // doesn't. A post-merge instance still running doesn't hold the evidence back.
                usable = usable && !product.Any(j => Incomplete(j.Value) && !session.PostMerge.Contains(j.Key)) && (product.All(j => j.Value.GetProperty("conclusion").GetString() != "skipped")
                    || jobs.Any(a => Product(a.GetProperty("name").GetString()) && a.GetProperty("conclusion").GetString() != "skipped"));
                usable = usable && !latest.Any(j => (config.MainGreen.Jobs.Any(n => n.Split(" (", StringSplitOptions.None)[0] == j.Key.Split(" (", StringSplitOptions.None)[0])
                    || config.MainGreen.LegFamilies.Any(f => f.Contains(j.Key, StringComparer.Ordinal)))
                    && Incomplete(j.Value) && !session.PostMerge.Contains(j.Key));
                yield return (run.Clone(), latest, jobs, usable);
            }
            seen += runs.GetArrayLength();
            if (seen >= doc.RootElement.GetProperty("total_count").GetInt32())
                yield break;
            if (runs.GetArrayLength() == 0 || page >= 5)
                throw new InvalidOperationException("CI run history is incomplete (100-run window)");
        }
    }

    // A page of a run's jobs, the document disposed if its body turns out to have no jobs.
    static (JsonDocument Doc, JsonElement Jobs) ReadJobs(Session session, string path)
    {
        var doc = session.Rest(path);
        try
        {
            return (doc, doc.RootElement.GetProperty("jobs"));
        }
        catch
        {
            doc.Dispose();
            throw;
        }
    }

    // A run may be passed over when its jobs can't be read only if it is old enough and isn't a completed failure.
    // An ALLOW-list, so what isn't known fails closed: a run not yet completed, or one that ended in a conclusion GitHub documents as not a failure.
    // An unknown status or conclusion, a completed run with none, and a failure of any kind are never skipped.
    static bool MayBeSkipped(Session session, JsonElement run)
    {
        if (!run.TryGetProperty("status", out var statusNode) || statusNode.ValueKind != JsonValueKind.String)
            return false;
        if (statusNode.GetString() == "completed")
        {
            if (!run.TryGetProperty("conclusion", out var conclusion) || conclusion.ValueKind != JsonValueKind.String
                || conclusion.GetString() is not ("cancelled" or "success" or "neutral" or "skipped" or "stale" or "action_required"))
                return false;
        }
        else if (statusNode.GetString() is not ("queued" or "in_progress" or "waiting" or "pending" or "requested"))
            return false;
        return run.TryGetProperty("created_at", out var created) && created.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(created.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            && session.Now() - at > UnreadableGrace;
    }

    // Network or runner evidence in a failed job's annotations; a step's name only locates the failure.
    static readonly Regex InfraEvidence = new(@"exit code 124\b|timed out|ETIMEDOUT|exceeded the maximum execution time"
        + @"|\b(?:HTTP|status|returned error)\b[^\n\d]{0,40}\b(?:429|5\d\d)\b|ECONNRESET|connection (?:was )?reset"
        + @"|ENOTFOUND|EAI_AGAIN|name resolution|could not resolve host|lost communication with the server"
        + @"|runner has received a shutdown signal", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static bool InfraOnly(Session session, JsonElement job)
    {
        var config = session.Config.MainGreen;
        if (job.GetProperty("status").GetString() != "completed"
            || job.GetProperty("conclusion").GetString() is not ("failure" or "timed_out" or "cancelled" or "startup_failure")
            || !job.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() == 0)
            return false;
        var name = job.GetProperty("name").GetString() ?? "";
        if (!config.InfraSteps.TryGetValue(name, out var patterns)
            && !config.InfraSteps.TryGetValue(name.Split(" (", StringSplitOptions.None)[0], out patterns))
            return false;
        var globs = patterns.Select(p => new Glob(p)).ToList();
        var cancelled = job.GetProperty("conclusion").GetString() == "cancelled";
        var failedInfra = 0;
        var hasInfra = false;
        var tested = false;
        var allSkipped = true;
        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object
                || !step.TryGetProperty("name", out var stepName) || stepName.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(stepName.GetString())
                || !step.TryGetProperty("status", out var status) || status.GetString() != "completed"
                || !step.TryGetProperty("conclusion", out var result) || result.ValueKind != JsonValueKind.String)
                return false;
            var infra = globs.Any(g => g.IsMatch(stepName.GetString()!));
            hasInfra |= infra;
            allSkipped &= result.GetString() == "skipped";
            tested |= !infra && result.GetString() != "skipped";
            if (result.GetString() is "success" or "neutral" or "skipped")
                continue;
            if (!infra || result.GetString() is not ("failure" or "timed_out" or "cancelled"))
                return false;
            // In a cancelled job the step running at the cancel shows cancelled; that is no failure.
            if (!cancelled || result.GetString() != "cancelled")
                failedInfra++;
        }
        // A cancellation can be exempt only before any test/build step started, and a step that failed
        // before it is still judged by its own evidence below. A never-assigned runner still needs a
        // nonempty step manifest; absent step data is not proof of infra.
        if (cancelled && (!hasInfra || tested))
            return false;
        if (hasInfra && allSkipped && job.TryGetProperty("runner_id", out var runner)
            && runner.TryGetInt64(out var runnerId) && runnerId == 0)
            return true;
        if (failedInfra == 0)
            return cancelled;
        // Annotations don't name their step, so one step's evidence can't vouch for another's: a job
        // with more than one failed step holds.
        if (failedInfra > 1)
            return false;
        // The job id is its check run id; a truncated list proves nothing. Every failure annotation
        // needs infra evidence.
        using var notes = session.Rest($"repos/{session.Slug}/check-runs/{job.GetProperty("id").GetInt64()}/annotations?per_page=100");
        if (notes.RootElement.ValueKind != JsonValueKind.Array || notes.RootElement.GetArrayLength() >= 100)
            return false;
        var failures = notes.RootElement.EnumerateArray().Where(n => n.ValueKind != JsonValueKind.Object
            || !n.TryGetProperty("annotation_level", out var level) || level.ValueKind != JsonValueKind.String || level.GetString() == "failure").ToList();
        return failures.Count > 0 && failures.All(n => n.ValueKind == JsonValueKind.Object && n.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.String && InfraEvidence.IsMatch(message.GetString()!));
    }

    static bool Incomplete(JsonElement job) => job.GetProperty("status").GetString() switch
    {
        "completed" => false,
        "queued" or "in_progress" or "waiting" or "pending" or "requested" => true,
        _ => throw new InvalidOperationException("CI job has missing or invalid status"),
    };

    static int RunAttempt(JsonElement run) =>
        run.TryGetProperty("run_attempt", out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var attempt) && attempt > 0
            ? attempt
            : throw new InvalidOperationException("CI run has missing or invalid run_attempt");

    // REST names a bot "app[bot]" and GraphQL "app"; read both the GraphQL way.
    static string? Login(JsonElement actor) =>
        actor.ValueKind == JsonValueKind.Object && actor.GetProperty("login").GetString() is { } login
            ? (login.EndsWith("[bot]", StringComparison.Ordinal) ? login[..^5] : login)
            : null;

    static bool SameSha(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    static bool IsCommitSha(string? sha) => sha is { Length: 40 } && sha.All(char.IsAsciiHexDigitLower);

    // Only the workflow's own post counts as already said: the ruleset requires merge-rule from one
    // poster, so the same words posted by a person (a local --post) don't satisfy it.
    static bool Unchanged(MergeRuleConfig config, PullRequestFacts pr, MergeVerdict verdict) =>
        pr.Statuses.Where(s => s.Context == MergeRule.StatusContext).OrderByDescending(s => s.CreatedAt).FirstOrDefault() is { } current
        && current.Creator == config.StatusPoster
        && current.State == verdict.State && current.Description == verdict.Description;

    static PullRequestFacts FetchOne(Session session, int number, bool checks, bool history) =>
        FetchOne(session, number, checks, history, out _);

    /// <summary>
    /// <paramref name="light"/> is the same pull request read as the open list reads it (no waivers,
    /// no receipt history), so a scoped run judges it as a sweep would have.
    /// </summary>
    static PullRequestFacts FetchOne(Session session, int number, bool checks, bool history, out PullRequestFacts light)
    {
        var (owner, name) = Split(session.Slug);
        using var doc = session.GraphQL(OneQuery(checks), new Dictionary<string, object> { ["owner"] = owner, ["name"] = name, ["number"] = number });
        var node = doc.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest");
        if (node.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"{session.Slug} has no pull request #{number}.");
        light = ParseFacts(node, session.Config);
        var facts = ParseFacts(node, session.Config, session.Arm64Waiver, session.PostMerge) with { CodexWaiverOn = session.CodexWaiver };
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
    /// The open pull requests whose head is <paramref name="head"/>, as the open list would give
    /// them (light facts), from the light list: only these are read in full, one query each, where
    /// the whole list is read twenty-five to a page. <paramref name="known"/> is the pull request
    /// the run is about, already read: it is among them whether or not the list has it yet. A list
    /// that fails after a page was read, or a pull request on the head that can't be read, gives
    /// the error with what was read, as FetchOpen does: no head gets success from it.
    /// </summary>
    static (List<PullRequestFacts> Prs, string? Error) FetchOnHead(Session session, string head, PullRequestFacts? known)
    {
        var (owner, name) = Split(session.Slug);
        var numbers = new List<int>();
        string? error = null, cursor = null;
        var read = 0;
        do
        {
            var variables = new Dictionary<string, object> { ["owner"] = owner, ["name"] = name, ["base"] = session.Config.Base };
            if (cursor is not null)
                variables["cursor"] = cursor;
            try
            {
                using var doc = session.GraphQL(LightQuery, variables);
                var page = doc.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequests");
                foreach (var node in page.GetProperty("nodes").EnumerateArray())
                {
                    read++;
                    if (SameSha(node.GetProperty("headRefOid").GetString() ?? "", head))
                        numbers.Add(node.GetProperty("number").GetInt32());
                }
                var info = page.GetProperty("pageInfo");
                cursor = info.GetProperty("hasNextPage").GetBoolean() ? info.GetProperty("endCursor").GetString() : null;
            }
            catch (Exception e) when (IsReadFailure(e) && read > 0)
            {
                error = e.Message;
                cursor = null;
            }
        }
        while (cursor is not null);
        var prs = new List<PullRequestFacts>();
        foreach (var number in numbers)
        {
            if (known is not null && known.Number == number)
            {
                prs.Add(known);
                continue;
            }
            try
            {
                FetchOne(session, number, checks: false, history: false, out var light);
                prs.Add(light);
            }
            catch (Exception e) when (IsReadFailure(e))
            {
                error ??= e.Message;
            }
        }
        if (known is not null && prs.All(p => p.Number != known.Number))
            prs.Add(known);
        return (prs, error);
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
        var contexts = ReceiptContexts(session.Config);
        var history = RestPages(session, $"repos/{session.Slug}/commits/{facts.HeadSha}/statuses")
            .Where(s => s.GetProperty("context").GetString() is { } c && contexts.Contains(c))
            .Select(s => new StatusFact(
                s.GetProperty("context").GetString()!,
                s.GetProperty("state").GetString() ?? "",
                s.GetProperty("description").GetString(),
                Login(s.GetProperty("creator")),
                DateTimeOffset.Parse(s.GetProperty("created_at").GetString()!, CultureInfo.InvariantCulture)))
            .ToList();
        return facts with { Statuses = facts.Statuses.Where(s => !contexts.Contains(s.Context)).Concat(history).ToList() };
    }

    /// <summary>The receipt contexts: the independent review's, and the second review's.</summary>
    static HashSet<string> ReceiptContexts(MergeRuleConfig config) =>
        new[] { config.IndependentReview.Context, config.CopilotWaiver.SecondReview }
            .Where(c => !string.IsNullOrWhiteSpace(c)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether the newest receipt GraphQL shows isn't a listed poster's, so it may hide one that is.</summary>
    static bool ReceiptsHidden(MergeRuleConfig config, PullRequestFacts facts)
    {
        var contexts = ReceiptContexts(config);
        return facts.Statuses.Any(s => contexts.Contains(s.Context)
            && (s.Creator is null || !config.IndependentReview.Posters.Contains(s.Creator, StringComparer.OrdinalIgnoreCase)));
    }

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
    internal static PullRequestFacts ParseFacts(JsonElement pr, MergeRuleConfig config, bool arm64Waiver = false, IReadOnlySet<string>? postMerge = null)
    {
        var head = pr.GetProperty("headRefOid").GetString()!;
        var reviews = pr.GetProperty("reviews").GetProperty("nodes").EnumerateArray()
            .Select(r => new ReviewFact(
                r.GetProperty("author") is { ValueKind: JsonValueKind.Object } a ? a.GetProperty("login").GetString() ?? "" : "",
                r.GetProperty("author") is { ValueKind: JsonValueKind.Object } b && b.GetProperty("__typename").GetString() == "Bot",
                r.GetProperty("state").GetString() ?? "",
                r.GetProperty("commit") is { ValueKind: JsonValueKind.Object } c ? c.GetProperty("oid").GetString() : null,
                // A body that isn't there reads as one that says nothing, which doesn't count.
                r.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() : null))
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
                (run, codeScanning, checks) = ReadChecks(suites, config, arm64Waiver, postMerge);
            // Every other status counts like a check; merge-rule is this verdict, and the receipts
            // are judged on their own terms above.
            var receiptContexts = ReceiptContexts(config);
            checks.AddRange(statuses
                .Where(s => s.Context != MergeRule.StatusContext && !receiptContexts.Contains(s.Context))
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
    static (RunFact? CodeQlRun, RunFact? CodeQlCheck, List<CheckFact> Checks) ReadChecks(JsonElement connection, MergeRuleConfig config, bool arm64Waiver, IReadOnlySet<string>? postMerge)
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
        foreach (var (id, suite) in latestRun.Values)
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
            var jobs = runs.GroupBy(cr => cr.GetProperty("name").GetString() ?? "")
                .Select(g => Classify(g.Key, g.MaxBy(cr => cr.GetProperty("databaseId").GetInt64())))
                .Select(c => arm64Waiver && id > 0 && workflow == "CI"
                    && suite.GetProperty("app") is { ValueKind: JsonValueKind.Object } source
                    && source.GetProperty("slug").GetString() == "github-actions"
                    && c.State == "failure" && config.Arm64Waiver?.Allows(c.Name, c.Detail) == true
                        ? c with { State = "success", WaivedRunId = id } : c)
                // A post-merge CI job (the catalog's tier) is shown, not counted: after the waiver,
                // so a waived job still needs its receipt.
                .Select(c => c.State != "success" && workflow == "CI" && postMerge?.Contains(c.Name) == true
                    && suite.GetProperty("app") is { ValueKind: JsonValueKind.Object } source
                    && source.GetProperty("slug").GetString() == "github-actions"
                        ? c with { State = "success", PostMerge = true } : c).ToList();
            checks.AddRange(jobs);
            var waivedFailure = conclusion == "FAILURE" && jobs.Any(c => c.WaivedRunId is not null || c.PostMerge)
                && jobs.All(c => c.State == "success")
                && jobs.Any(c => c.Name == "quality-gate" && c.Detail == "success")
                && !Truncated(suite.GetProperty("checkRuns"), runs.Count);
            // The run's own result counts too, jobs or none: one cancelled between jobs, or failed
            // expanding a later matrix, ends red with every job it made green; one that ended
            // before any job (a workflow file that doesn't parse) is startup_failure. GitHub
            // updates it when failed jobs are rerun, so a green retry clears it. Not
            // action_required: a run waiting for approval, as the review relay's do when Copilot's
            // review starts them.
            if (status == "COMPLETED" && conclusion is ("STARTUP_FAILURE" or "FAILURE" or "TIMED_OUT" or "CANCELLED") && !waivedFailure)
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
