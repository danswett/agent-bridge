using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentBridge.Quality;

internal sealed record DeletedLine(string File, int OldLine, string Text);

internal sealed record DeletedTest(string Name, string File, int Line);

/// <summary>What one other merged pull request (Pr 0: a commit with no "(#N)" subject) loses to this one.</summary>
internal sealed record Removal(int Pr, string Subject, IReadOnlyList<string> Files, int Lines, IReadOnlyList<DeletedTest> Tests);

/// <summary>What the guard found, and what it could not check (each note says why), so a bounded check is never mistaken for a complete one.</summary>
internal sealed record Report(IReadOnlyList<Removal> Removals, IReadOnlyList<string> Notes);

/// <summary>
/// The silent-revert guard (#4465): PR #3968's merge of main reverted #4126, code and tests together, and CI stayed green. For every line a pull request deletes against its merge base this says which
/// MERGED pull request last changed it (git blame at the merge base, the PR from the squash subject's trailing "(#N)") and names the deleted test methods. It only warns.
/// It runs on every pull request's critical path, so it is BOUNDED (#4490): a line last changed before a shallow boundary is "not checked", never blamed on the boundary commit, and a hard time box
/// and line budget stop it and say what it left.
/// </summary>
internal static partial class SilentRevert
{
    /// <summary>The hard caps; the step's own timeout is only the backstop behind them.</summary>
    public sealed record Limits(int MaxSeconds = 90, int MaxLines = 20000);

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+\d+(?:,\d+)? @@")]
    private static partial Regex HunkHeader();
    [GeneratedRegex(@"^([0-9a-f]{40}) \d+ (\d+)")]
    private static partial Regex BlameHeader();
    [GeneratedRegex(@"\(#(\d+)\)\s*$")]
    private static partial Regex TrailingPr();
    [GeneratedRegex(@"^\s*(?:public|internal)\s+(?:async\s+)?(?:Task|ValueTask|void)\s+(\w+)\s*[(<]")]
    private static partial Regex TestMethod();

    /// <summary>The deleted lines of a <c>git diff --unified=0</c>, with their line numbers in the OLD file.</summary>
    public static IReadOnlyList<DeletedLine> DeletedLines(string diff)
    {
        var lines = new List<DeletedLine>();
        string? file = null;
        var (next, remaining) = (0, 0);
        foreach (var line in diff.Split('\n'))
        {
            // The hunk header says how many old lines follow, so a deleted line that itself starts with "-- " is not mistaken for the next file's "--- ".
            if (remaining > 0 && line.StartsWith('-'))
            {
                lines.Add(new DeletedLine(file!, next++, line[1..].TrimEnd('\r')));
                remaining--;
            }
            else if (remaining == 0 && line.StartsWith("--- ", StringComparison.Ordinal)) { file = line.StartsWith("--- a/", StringComparison.Ordinal) ? line[6..] : null; }
            else if (file is not null && HunkHeader().Match(line) is { Success: true } hunk)
            {
                next = int.Parse(hunk.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                remaining = hunk.Groups[2].Success ? int.Parse(hunk.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
            }
        }
        return lines;
    }

    /// <summary>The pull request a squash-merge commit came from: the trailing "(#N)" of its subject.</summary>
    public static int? PrOf(string subject) => TrailingPr().Match(subject) is { Success: true } m ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>Deleted test methods: in a test file, a public method declaration right after a deleted [Fact] or [Theory] line.</summary>
    public static IReadOnlyList<DeletedTest> DeletedTests(IEnumerable<DeletedLine> deleted)
    {
        var tests = new List<DeletedTest>();
        DeletedLine? previous = null;
        foreach (var line in deleted)
        {
            var inTests = line.File.Contains("/tests/", StringComparison.Ordinal) || line.File.StartsWith("tests/", StringComparison.Ordinal) || line.File.EndsWith("Tests.cs", StringComparison.Ordinal);
            if (inTests && previous is { } p && p.File == line.File && p.OldLine == line.OldLine - 1 && (p.Text.Contains("[Fact", StringComparison.Ordinal) || p.Text.Contains("[Theory", StringComparison.Ordinal))
                && TestMethod().Match(line.Text) is { Success: true } m)
            {
                tests.Add(new DeletedTest(m.Groups[1].Value, line.File, line.OldLine));
            }
            previous = line;
        }
        return tests;
    }

    public static IReadOnlyList<Removal> Find(string repo, string baseRef, string head, int? ownPr) => Check(repo, baseRef, head, ownPr).Removals;

    public static Report Check(string repo, string baseRef, string head, int? ownPr, Limits? limits = null)
    {
        limits ??= new Limits();
        var clock = Stopwatch.StartNew();
        var mergeBase = ChangedFiles.Git(repo, "merge-base", baseRef, head).Trim();
        // blame calls the edge of the history it can see a "boundary": a shallow clone's grafted commit (lines last changed before it are unknown), but also a repository's real root commit (known).
        var shallow = ChangedFiles.Git(repo, "rev-parse", "--is-shallow-repository").Trim() == "true";
        var deleted = DeletedLines(ChangedFiles.Git(repo, "diff", "--unified=0", "--no-color", "--no-renames", mergeBase, head));
        var (owner, notes) = (new Dictionary<(string File, int Line), (int Pr, string Subject)>(), new List<string>());
        int checkedLines = 0, older = 0;
        string? stopped = null;
        foreach (var file in deleted.GroupBy(d => d.File).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (clock.Elapsed.TotalSeconds >= limits.MaxSeconds) { stopped = $"stopped at the {limits.MaxSeconds} s time box"; break; }
            if (checkedLines >= limits.MaxLines) { stopped = $"stopped at the {limits.MaxLines}-line budget"; break; }
            var take = file.Take(limits.MaxLines - checkedLines).ToList();
            checkedLines += take.Count;
            var args = new List<string> { "blame", "--porcelain" };
            foreach (var d in take) { args.Add("-L"); args.Add($"{d.OldLine},{d.OldLine}"); }
            args.AddRange([mergeBase, "--", file.Key]);
            var (byLine, subjects, boundary) = ParseBlame(ChangedFiles.Git(repo, [.. args]));
            foreach (var d in take)
            {
                if (!byLine.TryGetValue(d.OldLine, out var sha)) { continue; }
                if (shallow && boundary.Contains(sha)) { older++; continue; }
                var subject = subjects.GetValueOrDefault(sha, "");
                owner[(d.File, d.OldLine)] = (PrOf(subject) ?? 0, subject);
            }
        }
        if (stopped is not null || checkedLines < deleted.Count) { notes.Add($"{stopped ?? "stopped early"}: checked {checkedLines} of {deleted.Count} deleted lines; the rest are not checked"); }
        if (older > 0) { notes.Add($"{older} deleted lines were last changed before the fetched history (the shallow boundary), so no pull request is named for them: not checked"); }
        var tests = DeletedTests(deleted).ToList();
        var removals = deleted.Where(d => owner.TryGetValue((d.File, d.OldLine), out var o) && (ownPr is null || o.Pr != ownPr)).GroupBy(d => owner[(d.File, d.OldLine)].Pr).OrderBy(g => g.Key)
            .Select(g => new Removal(g.Key, owner[(g.First().File, g.First().OldLine)].Subject, [.. g.Select(d => d.File).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], g.Count(),
                [.. tests.Where(t => owner.TryGetValue((t.File, t.Line), out var o) && o.Pr == g.Key)])).ToList();
        return new Report(removals, notes);
    }

    static (Dictionary<int, string> ByLine, Dictionary<string, string> Subjects, HashSet<string> Boundary) ParseBlame(string porcelain)
    {
        var (byLine, subjects, boundary, current) = (new Dictionary<int, string>(), new Dictionary<string, string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), "");
        foreach (var line in porcelain.Split('\n'))
        {
            if (BlameHeader().Match(line) is { Success: true } h) { current = h.Groups[1].Value; byLine[int.Parse(h.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)] = current; }
            else if (line.StartsWith("summary ", StringComparison.Ordinal)) { subjects[current] = line[8..]; }
            else if (line == "boundary") { boundary.Add(current); }
        }
        return (byLine, subjects, boundary);
    }

    public static string Render(IReadOnlyList<Removal> removals) => Render(new Report(removals, []));

    public static string Render(Report report)
    {
        var text = new StringBuilder();
        if (report.Removals.Count == 0) { text.Append("No lines removed that another merged PR last changed (silent-revert guard, #4465).\n"); }
        else
        {
            text.Append("### This PR removes lines other merged PRs last changed (silent-revert guard, #4465)\n\n");
            foreach (var r in report.Removals)
            {
                var who = r.Pr > 0 ? $"#{r.Pr}" : "a commit with no PR number";
                var tests = r.Tests.Count > 0 ? $"; deleted tests: {string.Join(", ", r.Tests.Select(t => t.Name))}" : "";
                text.Append($"- this PR removes lines from {who} (\"{r.Subject}\"): {r.Lines} lines in {string.Join(", ", r.Files)}{tests}\n");
            }
        }
        foreach (var note in report.Notes) { text.Append($"- not checked: {note}\n"); }
        foreach (var r in report.Removals)
        {
            text.Append($"::warning title=silent-revert::this PR removes lines from {(r.Pr > 0 ? "#" + r.Pr : "a commit with no PR number")}: {r.Lines} lines in {r.Files.Count} files{(r.Tests.Count > 0 ? "; deleted tests " + string.Join(", ", r.Tests.Select(t => t.Name)) : "")}\n");
        }
        foreach (var note in report.Notes) { text.Append($"::notice title=silent-revert::not checked: {note}\n"); }
        return text.ToString().TrimEnd();
    }

    public static int Run(Dictionary<string, string> options, TextWriter output)
    {
        var repo = options.GetValueOrDefault("repo") ?? ChangedFiles.RepoRoot(Directory.GetCurrentDirectory());
        var baseRef = options.GetValueOrDefault("base") ?? throw new ArgumentException("silent-revert needs --base.");
        int? ownPr = options.TryGetValue("pr", out var pr) ? int.Parse(pr, System.Globalization.CultureInfo.InvariantCulture) : null;
        var defaults = new Limits();
        var limits = new Limits(
            options.TryGetValue("max-seconds", out var s) ? int.Parse(s, System.Globalization.CultureInfo.InvariantCulture) : defaults.MaxSeconds,
            options.TryGetValue("max-lines", out var n) ? int.Parse(n, System.Globalization.CultureInfo.InvariantCulture) : defaults.MaxLines);
        var text = Render(Check(repo, baseRef, options.GetValueOrDefault("head") ?? "HEAD", ownPr, limits));
        output.WriteLine(text);
        // The ::warning and ::notice lines are for the log; in the job summary they would show as literal text.
        if (options.TryGetValue("summary", out var summary)) { File.AppendAllText(summary, string.Join('\n', text.Split('\n').Where(l => !l.StartsWith("::", StringComparison.Ordinal))) + "\n"); }
        return 0;
    }
}
