using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AgentBridge.Quality;

/// <summary>One test's result in one run: run is the run's number on that OS.</summary>
sealed record TestRun(string Os, string Project, string Test, int Run, bool Passed, string? Message, string? Stack);

/// <summary>
/// What the flake hunt saw (quality/README.md §5): each test project run N times on each OS,
/// each run leaving <c>&lt;results&gt;/&lt;os&gt;/&lt;project&gt;/run-&lt;i&gt;.trx</c>, and
/// <c>run-&lt;i&gt;.exit</c> holding dotnet test's exit code.
/// </summary>
sealed class FlakeHunt
{
    public required IReadOnlyList<TestRun> Results { get; init; }

    /// <summary>Each OS and project, with the runs started there.</summary>
    public required IReadOnlyDictionary<(string Os, string Project), IReadOnlyList<int>> Runs { get; init; }

    /// <summary>Runs whose dotnet test failed with no test failed in their results, or left none: a crash or a hang.</summary>
    public required IReadOnlyList<(string Os, string Project, int Run, string Why)> Broken { get; init; }

    static readonly Regex RunFile = new(@"^run-(\d+)\.(trx|exit)$", RegexOptions.CultureInvariant);

    public static FlakeHunt Load(string results)
    {
        if (!Directory.Exists(results))
            throw new IOException($"{results} does not exist.");
        var all = new List<TestRun>();
        var runs = new SortedDictionary<(string, string), IReadOnlyList<int>>();
        var broken = new List<(string, string, int, string)>();
        // Found wherever they are below results, so the artifacts can be downloaded merged or
        // each to a folder of its own: a folder of runs is a project's, and its parent an OS.
        var folders = Directory.EnumerateDirectories(results, "*", SearchOption.AllDirectories)
            .Where(d => Directory.EnumerateFiles(d).Any(f => RunFile.IsMatch(Path.GetFileName(f))))
            .Order(StringComparer.Ordinal);
        foreach (var projectDir in folders)
        {
            var os = Path.GetFileName(Path.GetDirectoryName(projectDir))!;
            var project = Path.GetFileName(projectDir);
            var files = Directory.EnumerateFiles(projectDir)
                .Select(f => (Path: f, Match: RunFile.Match(Path.GetFileName(f))))
                .Where(f => f.Match.Success)
                .ToList();
            var numbers = files.Select(f => int.Parse(f.Match.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().Order().ToList();
            runs[(os, project)] = numbers;
            foreach (var run in numbers)
            {
                var trx = Path.Combine(projectDir, $"run-{run}.trx");
                var exit = Path.Combine(projectDir, $"run-{run}.exit");
                var these = File.Exists(trx) ? Trx.Read(trx).Select(r => new TestRun(os, project, r.Test, run, r.Passed, r.Message, r.Stack)).ToList() : [];
                all.AddRange(these);
                var code = File.Exists(exit) && int.TryParse(File.ReadAllText(exit).Trim(), CultureInfo.InvariantCulture, out var c) ? c : (int?)null;
                if (!File.Exists(trx))
                    broken.Add((os, project, run, $"no results (dotnet test exit {code?.ToString(CultureInfo.InvariantCulture) ?? "unknown"})"));
                else if (code is not (null or 0) && these.All(t => t.Passed))
                    broken.Add((os, project, run, $"dotnet test exit {code} with no test failed (a crash, a hang or the test host aborting)"));
            }
        }
        return new FlakeHunt { Results = all, Runs = runs, Broken = broken };
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

    /// <summary>Passed and failed results only: a skipped or not-run test says nothing about flakiness.</summary>
    public static IEnumerable<(string Test, bool Passed, string? Message, string? Stack)> Read(string path)
    {
        var doc = XDocument.Load(path);
        foreach (var result in doc.Descendants(Ns + "UnitTestResult"))
        {
            var outcome = (string?)result.Attribute("outcome");
            if (outcome is not ("Passed" or "Failed"))
                continue;
            var error = result.Element(Ns + "Output")?.Element(Ns + "ErrorInfo");
            yield return ((string?)result.Attribute("testName") ?? "?", outcome == "Passed",
                error?.Element(Ns + "Message")?.Value, error?.Element(Ns + "StackTrace")?.Value);
        }
    }
}

/// <summary>An open issue, as <c>gh issue list --json number,title,body,labels</c> gives it.</summary>
sealed record OpenIssue(int Number, string Title, string Body, IReadOnlyList<string> Labels)
{
    public static IReadOnlyList<OpenIssue> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(i => new OpenIssue(
            i.GetProperty("number").GetInt32(),
            i.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
            i.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            i.TryGetProperty("labels", out var l) ? l.EnumerateArray().Select(x => x.GetProperty("name").GetString() ?? "").ToList() : [])).ToList();
    }
}

/// <summary>What the reporter will do on GitHub: open an issue, or comment on one (and label it).</summary>
sealed record IssueAction(string Action, string Key, int? Issue, string? Title, string Body, IReadOnlyList<string> Labels);

sealed record FlakeContext(string RunUrl, string Commit, DateTimeOffset Now);

/// <summary>
/// Turns the hunt into issues and a summary. A flaky test gets one issue labelled flaky, found
/// again next time by a marker in its body, or, for an issue filed by hand before the hunt
/// existed, by its Class.Method in an open issue whose title says flaky; it is then commented
/// on with the new counts. A test failing every run gets an issue of its own as a bug.
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
        foreach (var (v, kind) in verdicts.SelectMany(v => v.Kinds.Select(k => (v, k))))
        {
            var marker = Marker(v, kind);
            var labels = kind == Verdict.Flaky ? new[] { FlakyLabel, "area:testing" } : ["bug", "area:testing"];
            var existing = open.Where(i => i.Body.Contains(marker, StringComparison.Ordinal)).OrderBy(i => i.Number).FirstOrDefault()
                ?? (kind == Verdict.Flaky
                    ? open.Where(i => (i.Labels.Contains(FlakyLabel) || i.Title.Contains("flaky", StringComparison.OrdinalIgnoreCase))
                            && (i.Title.Contains(v.ShortName, StringComparison.Ordinal) || i.Body.Contains(v.ShortName, StringComparison.Ordinal)))
                        .OrderBy(i => i.Number).FirstOrDefault()
                    : null);
            if (existing is not null)
                actions.Add(new("comment", marker, existing.Number, null, Comment(v, kind, context), labels.Where(l => !existing.Labels.Contains(l)).ToList()));
            else if (created++ < MaxNewIssues)
                actions.Add(new("create", marker, null, Title(v, kind), Body(v, kind, context), labels));
        }
        return actions;
    }

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
