using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentBridge.Quality;

/// <summary>
/// A pull request as the weekly report reads it: open now, or opened, merged or closed in the
/// window. ReadyAt is when it last left draft, null when it never was one. The rest is read only
/// for open pull requests: GitHub's mergeable verdict, unresolved review threads, and the
/// merge-rule status on its head.
/// </summary>
sealed record ReportPr(int Number, string State, bool IsDraft, DateTimeOffset CreatedAt, DateTimeOffset? ReadyAt, DateTimeOffset? MergedAt, DateTimeOffset? ClosedAt,
    string HeadRef, string BaseRef, string? Mergeable = null, int UnresolvedThreads = 0, string? MergeRuleState = null, string? MergeRuleDescription = null, bool CrossRepository = false)
{
    public bool Open => State == "OPEN";
    public bool Merged => State == "MERGED" && MergedAt is not null;
}

/// <summary>One of the flake hunt's counts tables on a flaky issue: its body or a comment.</summary>
sealed record FlakeTally(DateTimeOffset At, IReadOnlyList<(string Os, int Failed, int Runs)> ByOs)
{
    public int Failed => ByOs.Sum(o => o.Failed);
}

/// <summary>
/// A flaky issue: open now, or closed in the window. Test is "Class.Method" when the hunt filed it.
/// Partial: it has more comments than were read, so its oldest tallies are missing from the total.
/// </summary>
sealed record FlakyIssue(int Number, string Title, string? Test, bool Open, DateTimeOffset CreatedAt, DateTimeOffset? ClosedAt, IReadOnlyList<FlakeTally> Tallies, bool Partial = false);

/// <summary>
/// One of main's CI runs: its latest attempt's conclusion, and for a run attempted more than once,
/// whether its first attempt failed.
/// </summary>
sealed record MainRun(long Id, string Sha, DateTimeOffset CreatedAt, string? Conclusion, int Attempt, bool FirstAttemptFailed)
{
    /// <summary>Red, then green on a rerun of the same commit: nothing changed but luck.</summary>
    public bool Flake => Conclusion == "success" && Attempt > 1 && FirstAttemptFailed;
    /// <summary>Still red after any reruns: broken, or a flake no one reran.</summary>
    public bool Break => Conclusion is "failure" or "timed_out" or "startup_failure";
}

/// <summary>A completed pull request run of ci.yml: its wall time and each job's.</summary>
sealed record PrCiRun(long Id, TimeSpan Wall, IReadOnlyList<(string Name, TimeSpan Duration)> Jobs);

/// <summary>A test that failed in one CI job, read from the job's log.</summary>
sealed record CiTestFailure(string Test, bool OnMain);

/// <summary>Everything the weekly report says, read from GitHub; Render turns it into Markdown.</summary>
sealed record ReportFacts
{
    public required DateTimeOffset Now { get; init; }
    public int Days { get; init; } = 7;
    public string Base { get; init; } = "main";
    public required IReadOnlyList<ReportPr> PullRequests { get; init; }
    public required IReadOnlyList<FlakyIssue> FlakyIssues { get; init; }
    public required IReadOnlyList<MainRun> MainRuns { get; init; }
    /// <summary>Main's newest completed CI run's verdict: true red, false green, null unknown.</summary>
    public bool? MainRedNow { get; init; }
    public required IReadOnlyList<PrCiRun> PrRuns { get; init; }
    public required IReadOnlyList<CiTestFailure> TestFailures { get; init; }
    public int LogsRead { get; init; }
    /// <summary>Logs asked for that couldn't be read (expired, or gh refused): said, so a gap isn't read as no failures.</summary>
    public int LogsUnread { get; init; }
    public int FailedTestJobs { get; init; }
    public string? RunUrl { get; init; }
    public string? Cost { get; init; }

    public DateTimeOffset Since => Now.AddDays(-Days);
}

/// <summary>
/// The weekly quality report (quality/README.md §5), posted on the quality epic each Monday so
/// flakes get fixed rather than re-run, and so the owner can see what slows merging: flakes,
/// throughput, where open pull requests are stuck, and CI's wall time. Plain short tables, read
/// on a phone.
/// </summary>
static class WeeklyReport
{
    const int MaxRows = 10;

    public static string Render(ReportFacts f)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Weekly quality report: {FlakeReport.Pacific(f.Since)} to {FlakeReport.Pacific(f.Now)}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"_The past {f.Days} days, from `weekly-report.yml`{(f.RunUrl is null ? "" : $" ([run]({f.RunUrl}))")}; quality/README.md §5._");
        sb.AppendLine();
        Flakes(sb, f);
        Throughput(sb, f);
        Stuck(sb, f);
        CiHealth(sb, f);
        if (f.Cost is not null)
            sb.AppendLine(CultureInfo.InvariantCulture, $"_Read with {f.Cost}._");
        return sb.ToString().ReplaceLineEndings("\n");
    }

    static void Flakes(StringBuilder sb, ReportFacts f)
    {
        sb.AppendLine("### 1. Flakes");
        sb.AppendLine();
        var open = f.FlakyIssues.Where(i => i.Open)
            .Select(i => (Issue: i, Week: i.Tallies.Where(t => t.At >= f.Since).Sum(t => t.Failed), All: i.Tallies.Sum(t => t.Failed)))
            .OrderByDescending(x => x.Week).ThenByDescending(x => x.All).ThenBy(x => x.Issue.Number).ToList();
        var created = f.FlakyIssues.Where(i => i.CreatedAt >= f.Since).Select(i => i.Number).Order().ToList();
        var fixedThisWeek = f.FlakyIssues.Where(i => !i.Open && i.ClosedAt >= f.Since).Select(i => i.Number).Order().ToList();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Open `flaky` issues: **{open.Count}**. New this week: **{created.Count}**{Numbers(created)}. Fixed (closed) this week: **{fixedThisWeek.Count}**{Numbers(fixedThisWeek)}.");
        sb.AppendLine();
        if (open.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| Issue | Test | Failed ({f.Days} d / all) | OS | Last seen |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var (issue, week, all) in open.Take(MaxRows))
            {
                var failing = issue.Tallies.Where(t => t.Failed > 0).ToList();
                var oses = failing.SelectMany(t => t.ByOs).Where(o => o.Failed > 0).Select(o => o.Os).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
                var last = failing.Count > 0 ? FlakeReport.Pacific(failing.Max(t => t.At)) : "-";
                sb.AppendLine(CultureInfo.InvariantCulture, $"| #{issue.Number} | {Cell(issue.Test ?? issue.Title)} | {week} / {(issue.Partial ? "≥" : "")}{all} | {Cell(string.Join(", ", oses).DefaultIfBlank("-"))} | {last} |");
            }
            More(sb, open.Count);
            sb.AppendLine();
        }

        var runs = f.MainRuns.Where(r => r.CreatedAt >= f.Since).ToList();
        var flakes = runs.Count(r => r.Flake);
        var breaks = runs.Count(r => r.Break);
        // A run still red whose next finished run (the next commit) went green was most likely a
        // flake no one reran; a real break stays red across commits until it's fixed.
        var finished = f.MainRuns.Where(r => r.Conclusion is not (null or "cancelled" or "skipped")).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToList();
        var alone = finished.Zip(finished.Skip(1)).Count(p => p.First.Break && p.First.CreatedAt >= f.Since && !p.Second.Break);
        sb.AppendLine(CultureInfo.InvariantCulture, $"Main's CI ({f.Base}, push), the runs started this week:");
        sb.AppendLine();
        sb.AppendLine("| Runs | Red | Flakes (green on a rerun, same commit) | Still red | Still red, next commit green | Main now |");
        sb.AppendLine("|---|---|---|---|---|---|");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| {runs.Count} | {flakes + breaks} | {flakes} | {breaks} | {alone} | {f.MainRedNow switch { true => "red", false => "green", null => "unknown" }} |");
        sb.AppendLine();

        // CI's logs say which tests failed on main and pull requests; the hunt's tallies say which
        // failed under its repeated runs. Joined on Class.Method, which both name a test by.
        var tests = new Dictionary<string, (int Main, int Prs, int Hunt)>(StringComparer.Ordinal);
        foreach (var failure in f.TestFailures)
        {
            var name = ShortName(failure.Test);
            var t = tests.GetValueOrDefault(name);
            tests[name] = failure.OnMain ? t with { Main = t.Main + 1 } : t with { Prs = t.Prs + 1 };
        }
        foreach (var issue in f.FlakyIssues.Where(i => i.Test is not null))
        {
            var week = issue.Tallies.Where(t => t.At >= f.Since).Sum(t => t.Failed);
            if (week == 0)
                continue;
            var name = ShortName(issue.Test!);
            var t = tests.GetValueOrDefault(name);
            tests[name] = t with { Hunt = t.Hunt + week };
        }
        var top = tests.OrderByDescending(t => t.Value.Main + t.Value.Prs).ThenByDescending(t => t.Value.Hunt).ThenBy(t => t.Key, StringComparer.Ordinal).Take(5).ToList();
        sb.AppendLine(CultureInfo.InvariantCulture, $"The tests that failed most: CI's logs of {f.LogsRead} of the {f.FailedTestJobs} failed test job(s) found (main's and pull requests', newest first){(f.LogsUnread > 0 ? $"; **{f.LogsUnread} more couldn't be read**" : "")}, and the flake hunt's tallies this week.");
        sb.AppendLine();
        if (top.Count == 0)
            sb.AppendLine("_No test failure found._");
        else
        {
            sb.AppendLine("| Test | Main | PRs | Flake hunt |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var (name, t) in top)
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {Cell(name)} | {t.Main} | {t.Prs} | {t.Hunt} |");
        }
        sb.AppendLine();
    }

    static void Throughput(StringBuilder sb, ReportFacts f)
    {
        sb.AppendLine("### 2. Throughput");
        sb.AppendLine();
        var prs = f.PullRequests;
        var open = prs.Where(p => p.Open).ToList();
        var merged = prs.Where(p => p.Merged && p.MergedAt >= f.Since).ToList();
        sb.AppendLine("| Open now | Opened | Merged | Closed unmerged |");
        sb.AppendLine("|---|---|---|---|");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| {open.Count} | {prs.Count(p => p.CreatedAt >= f.Since)} | {merged.Count} | {prs.Count(p => p.State == "CLOSED" && p.ClosedAt >= f.Since)} |");
        sb.AppendLine();
        if (merged.Count > 0)
        {
            var fromOpen = merged.Select(p => p.MergedAt!.Value - p.CreatedAt).ToList();
            var fromReady = merged.Select(p => p.MergedAt!.Value - (p.ReadyAt ?? p.CreatedAt)).ToList();
            sb.AppendLine(CultureInfo.InvariantCulture, $"| To merge ({merged.Count} merged) | Median | 90th percentile |");
            sb.AppendLine("|---|---|---|");
            sb.AppendLine(CultureInfo.InvariantCulture, $"| From open | {Duration(Percentile(fromOpen, 50))} | {Duration(Percentile(fromOpen, 90))} |");
            sb.AppendLine(CultureInfo.InvariantCulture, $"| From ready (not draft) | {Duration(Percentile(fromReady, 50))} | {Duration(Percentile(fromReady, 90))} |");
            sb.AppendLine();
        }
        var old = open.Where(p => f.Now - p.CreatedAt > TimeSpan.FromDays(7)).OrderBy(p => p.CreatedAt).ThenBy(p => p.Number).ToList();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Open more than 7 days: **{old.Count}**{(old.Count == 0 ? "." : ":")}");
        if (old.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("| PR | Age | Stuck on |");
            sb.AppendLine("|---|---|---|");
            foreach (var p in old.Take(MaxRows))
                sb.AppendLine(CultureInfo.InvariantCulture, $"| #{p.Number} | {Duration(f.Now - p.CreatedAt)} | {Cell(StuckOn(p, f.Base, f.MainRedNow))} |");
            More(sb, old.Count);
        }
        sb.AppendLine();
    }

    static void Stuck(StringBuilder sb, ReportFacts f)
    {
        sb.AppendLine("### 3. Where open PRs are stuck");
        sb.AppendLine();
        var open = f.PullRequests.Where(p => p.Open).ToList();
        if (open.Count == 0)
            sb.AppendLine("_No open pull request._");
        else
        {
            sb.AppendLine("| Stuck on (merge-rule's first missing item) | PRs |");
            sb.AppendLine("|---|---|");
            foreach (var g in open.GroupBy(p => StuckOn(p, f.Base, f.MainRedNow)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {Cell(g.Key)} | {g.Count()} |");
        }
        sb.AppendLine();

        // Prefixes with merges this week and none open still say how fast that kind of work lands.
        var depth = StackDepths(open, f.Base);
        var merged = f.PullRequests.Where(p => p.Merged && p.MergedAt >= f.Since).ToList();
        var prefixes = open.Select(p => Prefix(p.HeadRef)).Concat(merged.Select(p => Prefix(p.HeadRef))).Distinct(StringComparer.Ordinal);
        var rows = prefixes.Select(prefix =>
        {
            var mine = open.Where(p => Prefix(p.HeadRef) == prefix).ToList();
            var landed = merged.Where(p => Prefix(p.HeadRef) == prefix).Select(p => p.MergedAt!.Value - p.CreatedAt).ToList();
            return (Prefix: prefix, Open: mine.Count, Stacked: mine.Count(p => p.BaseRef != f.Base), Deepest: mine.Count == 0 ? 0 : mine.Max(p => depth[p.Number]),
                Merged: landed.Count, Median: landed.Count == 0 ? "-" : Duration(Percentile(landed, 50)));
        }).OrderByDescending(r => r.Open).ThenByDescending(r => r.Merged).ThenBy(r => r.Prefix, StringComparer.Ordinal).ToList();
        if (rows.Count == 0)
            return;
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Branch prefix | Open | Stacked | Deepest stack | Merged ({f.Days} d) | Median open → merge |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in rows.Take(MaxRows))
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {Cell(r.Prefix)} | {r.Open} | {r.Stacked} | {r.Deepest} | {r.Merged} | {r.Median} |");
        More(sb, rows.Count);
        sb.AppendLine();
    }

    static void CiHealth(StringBuilder sb, ReportFacts f)
    {
        sb.AppendLine("### 4. CI health");
        sb.AppendLine();
        if (f.PrRuns.Count == 0)
        {
            sb.AppendLine("_No completed pull request run of ci.yml found._");
            sb.AppendLine();
            return;
        }
        var walls = f.PrRuns.Select(r => r.Wall).ToList();
        sb.AppendLine(CultureInfo.InvariantCulture, $"The last {f.PrRuns.Count} completed pull request runs of ci.yml (passed or failed; drafts run a fast subset): median wall time **{Duration(Percentile(walls, 50))}**, 90th percentile {Duration(Percentile(walls, 90))}.");
        sb.AppendLine();
        var jobs = f.PrRuns.SelectMany(r => r.Jobs).GroupBy(j => j.Name, StringComparer.Ordinal)
            .Select(g => (Name: g.Key, Durations: g.Select(j => j.Duration).ToList()))
            .Select(j => (j.Name, Median: Percentile(j.Durations, 50), P90: Percentile(j.Durations, 90), Runs: j.Durations.Count))
            .OrderByDescending(j => j.Median).ThenBy(j => j.Name, StringComparer.Ordinal).Take(3).ToList();
        if (jobs.Count > 0)
        {
            sb.AppendLine("| Slowest jobs | Median | 90th percentile | Runs |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var j in jobs)
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {Cell(j.Name)} | {Duration(j.Median)} | {Duration(j.P90)} | {j.Runs} |");
            sb.AppendLine();
        }
    }

    /// <summary>
    /// What an open pull request waits on: the first item merge-rule's description names (it
    /// lists what a person must act on before what's on its way); then, for one merge-rule
    /// passed, what merge-rule doesn't judge: conflicts, unresolved threads, a red main.
    /// </summary>
    public static string StuckOn(ReportPr pr, string baseBranch, bool? mainRed)
    {
        if (pr.BaseRef != baseBranch)
            return "stacked on another branch";
        if (pr.MergeRuleState is null)
            return pr.IsDraft ? "draft" : "no merge-rule status";
        if (pr.MergeRuleState != "success")
            return Category(pr.MergeRuleDescription ?? "");
        if (pr.Mergeable == "CONFLICTING")
            return "conflicts";
        if (pr.UnresolvedThreads > 0)
            return "unresolved threads";
        if (mainRed == true)
            return "main red";
        return "ready: waiting to be merged";
    }

    static readonly Regex Combined = new(@"^#\d+:\s*", RegexOptions.CultureInvariant);

    /// <summary>merge-rule's description (MergeRule.Evaluate) reduced to its first item's kind.</summary>
    public static string Category(string description)
    {
        var first = Combined.Replace(description, "").Split("; ")[0].Trim().ToLowerInvariant();
        return first switch
        {
            "" => "merge-rule gave no reason",
            _ when first.StartsWith("draft", StringComparison.Ordinal) => "draft",
            _ when first.StartsWith("held by", StringComparison.Ordinal) || first.StartsWith("it has more labels", StringComparison.Ordinal) => "held",
            _ when first.Contains("independent review", StringComparison.Ordinal) => "independent review",
            _ when first.StartsWith("waiting for a copilot review", StringComparison.Ordinal) => "Copilot review",
            _ when first.Contains("codeql", StringComparison.Ordinal) || first.StartsWith("code scanning", StringComparison.Ordinal) => "CodeQL",
            _ when first.StartsWith("check ", StringComparison.Ordinal) && first.Contains(" ended ", StringComparison.Ordinal) => "a red check",
            _ when first.StartsWith("waiting for ", StringComparison.Ordinal) => "checks running",
            _ when first.StartsWith("its checks and files", StringComparison.Ordinal) => "checks not read yet",
            _ when first.StartsWith("couldn't read", StringComparison.Ordinal) => "merge-rule couldn't read it",
            _ => "other: " + (first.Length <= 40 ? first : first[..39] + "…"),
        };
    }

    /// <summary>The head branch's prefix through its first slash ("codex/"), which names the session or kind of work.</summary>
    public static string Prefix(string headRef)
    {
        var slash = headRef.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? headRef[..(slash + 1)] : "(none)";
    }

    /// <summary>
    /// How deep each open pull request stacks: 1 for one into the base branch, or onto a branch
    /// with no open pull request; one more than its parent's for one onto another's head. Only a
    /// head in this repository can be a parent: a fork's branch of the same name is another branch.
    /// </summary>
    public static Dictionary<int, int> StackDepths(IReadOnlyList<ReportPr> open, string baseBranch)
    {
        var byHead = open.Where(p => !p.CrossRepository).GroupBy(p => p.HeadRef, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.OrderBy(p => p.Number).First(), StringComparer.Ordinal);
        var depths = new Dictionary<int, int>();
        foreach (var pr in open)
        {
            var depth = 1;
            var seen = new HashSet<int> { pr.Number };
            var at = pr;
            // A cycle of bases can't happen on GitHub, but a loop here would hang the report.
            while (at.BaseRef != baseBranch && byHead.TryGetValue(at.BaseRef, out var parent) && seen.Add(parent.Number))
            {
                depth++;
                at = parent;
            }
            depths[pr.Number] = depth;
        }
        return depths;
    }

    /// <summary>Nearest rank: the smallest value with at least p% of them at or below it.</summary>
    public static TimeSpan Percentile(IReadOnlyList<TimeSpan> values, int p)
    {
        if (values.Count == 0)
            return TimeSpan.Zero;
        var sorted = values.Order().ToList();
        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }

    public static string Duration(TimeSpan t) =>
        t.TotalMinutes < 60 ? $"{Math.Round(t.TotalMinutes).ToString(CultureInfo.InvariantCulture)} min"
        : t.TotalHours < 48 ? $"{t.TotalHours.ToString("0.0", CultureInfo.InvariantCulture)} h"
        : $"{t.TotalDays.ToString("0.0", CultureInfo.InvariantCulture)} d";

    /// <summary>"Class.Method", with no namespace or theory arguments, as Verdict.ShortName.</summary>
    public static string ShortName(string test)
    {
        var name = test.Split('(')[0].Trim();
        var parts = name.Split('.');
        return parts.Length >= 2 ? $"{parts[^2]}.{parts[^1]}" : name;
    }

    // dotnet test's console logger: "  Failed Class.Method [16 s]", after the runner's timestamp.
    // "Failed!  - Failed: 1, ..." is the project's summary, not a test.
    static readonly Regex FailedLine = new(@"^(?:\S+Z\s)?\s*Failed (?<test>\S.*?) \[[^\]]*\]\s*$", RegexOptions.CultureInvariant | RegexOptions.Multiline);
    // The runner's colour codes, which gh keeps in a log: around a line they'd hide it from the above.
    static readonly Regex Ansi = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.CultureInvariant);

    /// <summary>The tests a CI job's log says failed, each once.</summary>
    public static IReadOnlyList<string> FailedTests(string log) =>
        FailedLine.Matches(Ansi.Replace(log, "").ReplaceLineEndings("\n")).Select(m => m.Groups["test"].Value.Trim()).Distinct(StringComparer.Ordinal).ToList();

    // FlakeReport.Counts: "| OS | Failed | Runs |", then a row per OS.
    static readonly Regex TallyRow = new(@"^\|\s*(?<os>[A-Za-z0-9._-]+)\s*\|\s*(?<failed>\d+)\s*\|\s*(?<runs>\d+)\s*\|\s*$", RegexOptions.CultureInvariant | RegexOptions.Multiline);
    static readonly Regex HuntMarker = new(@"<!-- flake-hunt: flaky \S+ (?<test>.+?) -->", RegexOptions.CultureInvariant);

    /// <summary>The hunt's counts table in an issue's body or comment, or null when there's none.</summary>
    public static FlakeTally? Tally(string text, DateTimeOffset at)
    {
        var rows = TallyRow.Matches(text.ReplaceLineEndings("\n"))
            .Select(m => (m.Groups["os"].Value, int.Parse(m.Groups["failed"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["runs"].Value, CultureInfo.InvariantCulture)))
            .ToList();
        return rows.Count == 0 ? null : new FlakeTally(at, rows);
    }

    /// <summary>The test a flaky issue is for, as "Class.Method", from the hunt's marker; null for one filed by hand.</summary>
    public static string? MarkedTest(string body) =>
        HuntMarker.Match(body) is { Success: true } m ? ShortName(m.Groups["test"].Value) : null;

    static string Numbers(IReadOnlyList<int> numbers) =>
        numbers.Count == 0 ? "" : $" ({string.Join(", ", numbers.Take(MaxRows).Select(n => $"#{n.ToString(CultureInfo.InvariantCulture)}"))}{(numbers.Count > MaxRows ? ", …" : "")})";

    // After a blank line: a line of text straight after a table would be read as one more row.
    static void More(StringBuilder sb, int count)
    {
        if (count <= MaxRows)
            return;
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"_… and {count - MaxRows} more._");
    }

    // Test, branch, job and issue names are others' text, posted on an issue: in a code span an
    // @mention pings no one and Markdown does nothing, and a pipe or newline can't break the table.
    static string Cell(string text) =>
        "`" + text.ReplaceLineEndings(" ").Replace("`", "'", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal) + "`";

    static string DefaultIfBlank(this string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s;
}
