using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AgentBridge.Quality;

/// <summary>One test's result in one run: run is the run's number on that OS.</summary>
sealed record TestRun(string Os, string Project, string Test, int Run, bool Passed, string? Message, string? Stack);

/// <summary>
/// What the flake hunt saw (quality/README.md §5): the test projects run N times on each OS, all
/// at once as CI's product job runs them, so under CI's own load. Each run leaves
/// <c>&lt;results&gt;/&lt;os&gt;/run-&lt;i&gt;/</c> holding a TRX file per test project and
/// <c>exit</c>, dotnet test's exit code.
/// </summary>
sealed class FlakeHunt
{
    public required IReadOnlyList<TestRun> Results { get; init; }

    /// <summary>Each OS and project, with the runs that left its results.</summary>
    public required IReadOnlyDictionary<(string Os, string Project), IReadOnlyList<int>> Runs { get; init; }

    /// <summary>
    /// Runs that broke outside any test: a project that left no results (its test host crashed,
    /// or a hang was ended), or dotnet test failing with no test failed.
    /// </summary>
    public required IReadOnlyList<(string Os, string Project, int Run, string Why)> Broken { get; init; }

    public const string WholeRun = "(the run)";

    static readonly Regex RunFolder = new(@"^run-(\d+)$", RegexOptions.CultureInvariant);

    public static FlakeHunt Load(string results)
    {
        if (!Directory.Exists(results))
            throw new IOException($"{results} does not exist.");
        var all = new List<TestRun>();
        var broken = new List<(string, string, int, string)>();
        // Found wherever they are below results, so the artifacts can be downloaded merged or each
        // to a folder of its own: a run's folder is run-<i>, and its parent's name is the OS.
        var folders = Directory.EnumerateDirectories(results, "*", SearchOption.AllDirectories)
            .Select(d => (Dir: d, Match: RunFolder.Match(Path.GetFileName(d))))
            .Where(d => d.Match.Success)
            .Select(d => (d.Dir, Os: Path.GetFileName(Path.GetDirectoryName(d.Dir))!, Run: int.Parse(d.Match.Groups[1].Value, CultureInfo.InvariantCulture)))
            .OrderBy(d => d.Os, StringComparer.Ordinal).ThenBy(d => d.Run)
            .ToList();
        var seen = new Dictionary<(string Os, int Run), HashSet<string>>();
        foreach (var (dir, os, run) in folders)
        {
            var projects = seen[(os, run)] = new HashSet<string>(StringComparer.Ordinal);
            var these = new List<TestRun>();
            foreach (var file in Directory.EnumerateFiles(dir, "*.trx", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                TrxFile trx;
                try { trx = Trx.Read(file); }
                catch (Exception e) when (e is XmlException or IOException)
                {
                    // One damaged file must not hide the rest of the night's results.
                    broken.Add((os, WholeRun, run, $"{Path.GetFileName(file)} could not be read ({e.Message})"));
                    continue;
                }
                these.AddRange(trx.Results.Select(r => new TestRun(os, trx.Project ?? "?", r.Test, run, r.Passed, r.Message, r.Stack)));
                // A test host that aborted can leave a pass and a fail behind, which reads as an
                // ordinary run unless the TRX's own verdict on the run is heard.
                if (trx.Aborted is { } why)
                    broken.Add(trx.Project is { } project ? (os, project, run, why) : (os, WholeRun, run, $"{Path.GetFileName(file)}, before any test: {why}"));
            }
            all.AddRange(these);
            projects.UnionWith(these.Select(t => t.Project));
            var exit = Path.Combine(dir, "exit");
            var code = File.Exists(exit) && int.TryParse(File.ReadAllText(exit).Trim(), CultureInfo.InvariantCulture, out var c) ? c : (int?)null;
            // The exit code is written once dotnet test returns, so a run without one was cut
            // short, and what it left may be partial.
            if (code is null)
                broken.Add((os, WholeRun, run, "no exit code recorded: the run was cut short"));
            if (these.Count == 0)
                broken.Add((os, WholeRun, run, $"no results at all (dotnet test exit {code?.ToString(CultureInfo.InvariantCulture) ?? "unknown"})"));
            else if (code is not (null or 0) && these.All(t => t.Passed))
                broken.Add((os, WholeRun, run, $"dotnet test exit {code} with no test failed (a crash, a hang or a test host aborting)"));
        }
        // A project with results in some of an OS's runs and none in another crashed or hung there.
        var runs = new SortedDictionary<(string, string), IReadOnlyList<int>>();
        foreach (var os in seen.Keys.Select(k => k.Os).Distinct())
        {
            var osRuns = seen.Where(s => s.Key.Os == os).OrderBy(s => s.Key.Run).ToList();
            foreach (var project in osRuns.SelectMany(s => s.Value).Distinct().Order(StringComparer.Ordinal))
            {
                runs[(os, project)] = osRuns.Where(s => s.Value.Contains(project)).Select(s => s.Key.Run).ToList();
                foreach (var missing in osRuns.Where(s => s.Value.Count > 0 && !s.Value.Contains(project)))
                    broken.Add((os, project, missing.Key.Run, "no results from this project in that run (its test host crashed, or a hang was ended)"));
            }
        }
        return new FlakeHunt
        {
            Results = all,
            Runs = runs,
            Broken = [.. broken.OrderBy(b => b.Item1, StringComparer.Ordinal).ThenBy(b => b.Item3).ThenBy(b => b.Item2, StringComparer.Ordinal)],
        };
    }
    public IReadOnlyList<Verdict> Classify() =>
        Results.GroupBy(r => (r.Project, r.Test))
            .Select(test => new Verdict(test.Key.Project, test.Key.Test,
                test.GroupBy(r => r.Os)
                    .OrderBy(os => os.Key, StringComparer.Ordinal)
                    .Select(os => new OsCount(os.Key, os.Count(r => !r.Passed), os.Count(), os.Where(r => !r.Passed).OrderBy(r => r.Run).FirstOrDefault()))
                    .ToList()))
            .OrderBy(v => v.Project, StringComparer.Ordinal).ThenBy(v => v.Test, StringComparer.Ordinal)
            .ToList();
}

/// <summary>How one test did on one OS: failed of runs, and its first failure there.</summary>
sealed record OsCount(string Os, int Failed, int Runs, TestRun? First)
{
    public bool Flaky => Failed > 0 && Failed < Runs;
    public bool Failing => Failed > 0 && Failed == Runs;
}

/// <summary>
/// One test across every OS. Flaky on an OS: it both passed and failed there, on the same code.
/// Failing on an OS: it failed every run there, which is a real failure, not a flake. A test can
/// be flaky on one OS and failing on another, and is then reported as both. Passing: neither.
/// </summary>
sealed record Verdict(string Project, string Test, IReadOnlyList<OsCount> ByOs)
{
    public const string Flaky = "flaky";
    public const string Failing = "failing";

    public IReadOnlyList<string> Kinds =>
        [.. new[] { (ByOs.Any(o => o.Flaky), Flaky), (ByOs.Any(o => o.Failing), Failing) }.Where(k => k.Item1).Select(k => k.Item2)];

    public bool Passing => Kinds.Count == 0;

    /// <summary>The OSes it was flaky, or failing, on.</summary>
    public IEnumerable<OsCount> On(string kind) => ByOs.Where(o => kind == Flaky ? o.Flaky : o.Failing);

    /// <summary>"Class.Method" with no namespace or theory arguments: what people put in an issue by hand.</summary>
    public string ShortName
    {
        get
        {
            var name = Test.Split('(')[0];
            var parts = name.Split('.');
            return parts.Length >= 2 ? $"{parts[^2]}.{parts[^1]}" : name;
        }
    }

    public TestRun? FirstError(string kind) => On(kind).Select(o => o.First).FirstOrDefault(f => f is not null);
}

/// <summary>A test result as a TRX file records it.</summary>
static class Trx
{
    static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    /// <summary>
    /// Passed and failed results only: a skipped or not-run test says nothing about flakiness.
    /// The project is the test assembly's name, from its definitions' codeBase (storage is
    /// lowercased): dotnet test writes one TRX per test project, named for the time, not the project. Aborted says why the run there
    /// ended early, from the TRX's own summary: an outcome other than Completed or Failed, or a
    /// message that the run was aborted (a crash, or --blame-hang ending a hang).
    /// </summary>
    // VSTest's own words ("The active test run was aborted", "Test Run Aborted"), not any text that
    // says aborted: a failing test's name is quoted in these messages too.
    static readonly Regex AbortText = new(@"(?i)\btest run (was )?aborted\b", RegexOptions.CultureInvariant);

    public static TrxFile Read(string path)
    {
        var doc = XDocument.Load(path);
        var project = doc.Descendants(Ns + "UnitTest")
            .Select(u => (string?)u.Element(Ns + "TestMethod")?.Attribute("codeBase") ?? (string?)u.Attribute("storage"))
            .Where(p => p is not null)
            .Select(p => Path.GetFileNameWithoutExtension(p!.Replace('\\', '/').Split('/')[^1]))
            .FirstOrDefault();
        var results = new List<(string, bool, string?, string?)>();
        foreach (var result in doc.Descendants(Ns + "UnitTestResult"))
        {
            var outcome = (string?)result.Attribute("outcome");
            if (outcome is not ("Passed" or "Failed"))
                continue;
            var error = result.Element(Ns + "Output")?.Element(Ns + "ErrorInfo");
            results.Add(((string?)result.Attribute("testName") ?? "?", outcome == "Passed", error?.Element(Ns + "Message")?.Value, error?.Element(Ns + "StackTrace")?.Value));
        }
        var summary = doc.Descendants(Ns + "ResultSummary").FirstOrDefault();
        var verdict = (string?)summary?.Attribute("outcome");
        var abort = summary?.Descendants(Ns + "Text").Select(t => t.Value.Trim()).FirstOrDefault(t => AbortText.IsMatch(t));
        string? aborted = abort is not null ? $"the test run was aborted: {(abort.Length > 300 ? abort[..300] + "..." : abort)}"
            : verdict is not (null or "Completed" or "Failed") ? $"the test run ended {verdict}"
            : null;
        return new TrxFile(project, results, aborted);
    }
}

// Project is null for a TRX with no test in it, such as a test host that crashed before its first.
sealed record TrxFile(string? Project, IReadOnlyList<(string Test, bool Passed, string? Message, string? Stack)> Results, string? Aborted);

/// <summary>An open issue, as <c>gh issue list --json number,title,body,labels,author</c> gives it.</summary>
sealed record OpenIssue(int Number, string Title, string Body, IReadOnlyList<string> Labels, string Author = "")
{
    public static IReadOnlyList<OpenIssue> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(i => new OpenIssue(
            i.GetProperty("number").GetInt32(),
            i.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
            i.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            i.TryGetProperty("labels", out var l) ? l.EnumerateArray().Select(x => x.GetProperty("name").GetString() ?? "").ToList() : [],
            i.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("login", out var login) ? login.GetString() ?? "" : "")).ToList();
    }
}

/// <summary>What the reporter will do on GitHub: open an issue, or comment on one (and label it).</summary>
sealed record IssueAction(string Action, string Key, int? Issue, string? Title, string Body, IReadOnlyList<string> Labels);

/// <summary>owner: the repository's owner, whose hand-filed issues the hunt may take for a test's record.</summary>
sealed record FlakeContext(string RunUrl, string Commit, DateTimeOffset Now, string? Owner = null)
{
    // The hunt's own issues are opened by GITHUB_TOKEN: gh names that author app/github-actions,
    // the REST API github-actions[bot].
    static readonly string[] Bots = ["app/github-actions", "github-actions[bot]"];

    /// <summary>
    /// Whether an issue's author may hold a test's flake record. The repository is public: anyone
    /// could open an issue titled flaky with a test's name, or paste the hunt's marker into one, and
    /// have the hunt's counts and labels go there instead (#542).
    /// </summary>
    public bool Trusts(string author) => Bots.Contains(author, StringComparer.Ordinal)
        || (!string.IsNullOrEmpty(Owner) && string.Equals(author, Owner, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Turns the hunt into issues and a summary. A flaky test gets one issue labelled flaky, found
/// again next time by a marker in its body, or, for an issue filed by hand before the hunt
/// existed, by its Class.Method in an open issue whose title says flaky; it is then commented
/// on with the new counts. Only issues by the repository's owner or the hunt itself count. A test failing every run gets an issue of its own as a bug.
/// </summary>
static class FlakeReport
{
    public const string FlakyLabel = "flaky";
    // More than this many new issues in a night is something systemic (a runner image, a broken
    // main), better read once in the run's summary than as a storm of issues.
    public const int MaxNewIssues = 10;

    public static string Marker(Verdict v, string kind) => $"<!-- flake-hunt: {kind} {v.Project} {v.Test} -->";

    public static IReadOnlyList<IssueAction> Plan(IReadOnlyList<Verdict> verdicts, IReadOnlyList<OpenIssue> open, FlakeContext context)
    {
        var actions = new List<IssueAction>();
        var created = 0;
        open = [.. open.Where(i => context.Trusts(i.Author))];
        foreach (var (v, kind) in verdicts.SelectMany(v => v.Kinds.Select(k => (v, k))))
        {
            var marker = Marker(v, kind);
            var labels = kind == Verdict.Flaky ? new[] { FlakyLabel, "area:testing" } : ["bug", "area:testing"];
            var existing = open.Where(i => i.Body.Contains(marker, StringComparison.Ordinal)).OrderBy(i => i.Number).FirstOrDefault()
                ?? (kind == Verdict.Flaky
                    // Only an issue filed by hand: one the hunt filed names its own test in its marker.
                    ? open.Where(i => !i.Body.Contains("<!-- flake-hunt:", StringComparison.Ordinal)
                            && (i.Labels.Contains(FlakyLabel) || i.Title.Contains("flaky", StringComparison.OrdinalIgnoreCase))
                            && (Names(i.Title, v.ShortName) || Names(i.Body, v.ShortName)))
                        .OrderBy(i => i.Number).FirstOrDefault()
                    : null);
            if (existing is not null)
                actions.Add(new("comment", marker, existing.Number, null, Comment(v, kind, context), labels.Where(l => !existing.Labels.Contains(l)).ToList()));
            else if (created++ < MaxNewIssues)
                actions.Add(new("create", marker, null, Title(v, kind), Body(v, kind, context), labels));
        }
        return actions;
    }

    // Class.Method whole: TrustTests.Known is not TrustTests.Known_more or OtherTrustTests.Known,
    // but may follow a namespace or carry a theory's arguments.
    static bool Names(string text, string test) => Regex.IsMatch(text, $@"(?<!\w){Regex.Escape(test)}(?!\w)");

    public static string Title(Verdict v, string kind)
    {
        var where = string.Join(", ", v.On(kind).Select(o => o.Os));
        var title = $"{(kind == Verdict.Flaky ? "Flaky" : "Failing every run")} on {where}: {v.Project.Replace("AgentBridge.", "", StringComparison.Ordinal)} {v.ShortName}";
        return title.Length <= 160 ? title : title[..157] + "...";
    }

    public static string Body(Verdict v, string kind, FlakeContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Marker(v, kind));
        sb.AppendLine(kind == Verdict.Flaky
            ? "The nightly flake hunt (quality/README.md §5) saw this test both pass and fail on the same code. Nothing was retried or skipped."
            : "The nightly flake hunt (quality/README.md §5) saw this test fail on every run on at least one OS: a real failure, not a flake.");
        sb.AppendLine();
        sb.AppendLine($"- **Project:** {v.Project}");
        sb.AppendLine($"- **Test:** `{v.Test}`");
        sb.AppendLine($"- **Commit:** {context.Commit}, [run]({context.RunUrl}), {Pacific(context.Now)}");
        sb.AppendLine();
        sb.Append(Counts(v));
        sb.AppendLine();
        sb.Append(Error(v, kind));
        sb.AppendLine();
        sb.AppendLine(kind == Verdict.Flaky
            ? "**What:** find the timing dependency or shared state, make the test deterministic without weakening it (as #449 and #496 did), and keep it failing without its behaviour. **Done when:** the hunt sees it pass every run."
            : "**What:** find why it fails on that OS, fix the product or the test without weakening it. **Done when:** the hunt sees it pass every run.");
        return Cap(sb.ToString());
    }

    public static string Comment(Verdict v, string kind, FlakeContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Seen again by the flake hunt, {Pacific(context.Now)}, at {context.Commit} ([run]({context.RunUrl})):");
        sb.AppendLine();
        sb.Append(Counts(v));
        sb.AppendLine();
        sb.Append(Error(v, kind));
        return Cap(sb.ToString());
    }

    static string Counts(Verdict v)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| OS | Failed | Runs |");
        sb.AppendLine("|---|---|---|");
        foreach (var o in v.ByOs)
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {o.Os} | {o.Failed} | {o.Runs} |");
        return sb.ToString();
    }

    static string Error(Verdict v, string kind)
    {
        if (v.FirstError(kind) is not { } first)
            return "";
        var stack = string.Join('\n', (first.Stack ?? "").Split('\n').Take(12)).TrimEnd();
        var text = $"{first.Message?.Trim()}\n{stack}".Trim();
        if (text.Length == 0)
            text = "(no message or stack recorded)";
        if (text.Length > 4000)
            text = text[..4000] + "\n...";
        // A fence longer than any run of backticks in the error, so the error can't end it.
        var fence = new string('`', Math.Max(3, Regex.Matches(text, "`+").Select(m => m.Length).DefaultIfEmpty(0).Max() + 1));
        return $"**First error** ({first.Os}, run {first.Run}):\n\n{fence}\n{text}\n{fence}\n";
    }

    static string Cap(string text)
    {
        text = text.ReplaceLineEndings("\n");
        return text.Length <= 60000 ? text : text[..60000] + "\n...";
    }

    public static string Pacific(DateTimeOffset when)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var local = TimeZoneInfo.ConvertTime(when, zone);
        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + (zone.IsDaylightSavingTime(local) ? " PDT" : " PST");
    }

    public static string Summary(FlakeHunt hunt, IReadOnlyList<Verdict> verdicts, IReadOnlyList<IssueAction> actions, bool dryRun)
    {
        var sb = new StringBuilder();
        var flaky = verdicts.Where(v => v.Kinds.Contains(Verdict.Flaky)).ToList();
        var failing = verdicts.Where(v => v.Kinds.Contains(Verdict.Failing)).ToList();
        sb.AppendLine("## Flake hunt");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{verdicts.Count} tests: **{flaky.Count} flaky**, **{failing.Count} failing every run** on some OS, {verdicts.Count(v => v.Passing)} passing. {hunt.Broken.Count} run(s) broke outside any test.");
        sb.AppendLine();
        sb.AppendLine("| OS | Project | Runs |");
        sb.AppendLine("|---|---|---|");
        foreach (var ((os, project), runs) in hunt.Runs)
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {os} | {project} | {runs.Count} |");
        void Table(string heading, IReadOnlyList<Verdict> list)
        {
            if (list.Count == 0)
                return;
            sb.AppendLine();
            sb.AppendLine($"### {heading}");
            sb.AppendLine();
            sb.AppendLine("| Project | Test | Failed of runs |");
            sb.AppendLine("|---|---|---|");
            foreach (var v in list)
                sb.AppendLine($"| {v.Project} | `{v.Test}` | {string.Join(", ", v.ByOs.Where(o => o.Failed > 0).Select(o => $"{o.Os} {o.Failed}/{o.Runs}"))} |");
        }
        Table("Flaky", flaky);
        Table("Failing every run", failing);
        if (hunt.Broken.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Runs that broke outside any test");
            sb.AppendLine();
            foreach (var (os, project, run, why) in hunt.Broken)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {os} {project} run {run}: {why}");
        }
        sb.AppendLine();
        sb.AppendLine(dryRun ? "### Issues (dry run: nothing filed)" : "### Issues");
        sb.AppendLine();
        if (actions.Count == 0)
            sb.AppendLine("None.");
        foreach (var a in actions)
            sb.AppendLine(a.Action == "create" ? $"- open: {a.Title}" : $"- comment on #{a.Issue}{(a.Labels.Count > 0 ? $", adding {string.Join(", ", a.Labels)}" : "")}");
        var unfiled = verdicts.Sum(v => v.Kinds.Count) - actions.Count;
        if (unfiled > 0)
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {unfiled} more not filed: at most {MaxNewIssues} new issues a run, so look for something systemic first.");
        return sb.ToString();
    }
}
