using System.Text;
using System.Text.RegularExpressions;

namespace AgentBridge.Quality;

internal sealed record DeletedLine(string File, int OldLine, string Text);

internal sealed record DeletedTest(string Name, string File, int Line);

/// <summary>What one other merged pull request (Pr 0: a commit with no "(#N)" subject) loses to this one.</summary>
internal sealed record Removal(int Pr, string Subject, IReadOnlyList<string> Files, int Lines, IReadOnlyList<DeletedTest> Tests);

/// <summary>
/// The silent-revert guard (#4465). PR #3968's merge of main reverted #4126, code and tests together, and CI stayed green because the tests went with the code. For every line a pull request deletes
/// relative to its merge base, this says which MERGED pull request last changed it (git blame at the merge base, the pull request taken from the squash subject's trailing "(#N)"), and names
/// the deleted test methods. It only warns: the point is that a reader looks, and every reader does not have to find it by comparing with main.
/// </summary>
internal static partial class SilentRevert
{
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

    public static IReadOnlyList<Removal> Find(string repo, string baseRef, string head, int? ownPr)
    {
        var mergeBase = ChangedFiles.Git(repo, "merge-base", baseRef, head).Trim();
        var deleted = DeletedLines(ChangedFiles.Git(repo, "diff", "--unified=0", "--no-color", "--no-renames", mergeBase, head));
        var owner = new Dictionary<(string File, int Line), (int Pr, string Subject)>();
        foreach (var file in deleted.GroupBy(d => d.File))
        {
            var args = new List<string> { "blame", "--porcelain" };
            foreach (var d in file) { args.Add("-L"); args.Add($"{d.OldLine},{d.OldLine}"); }
            args.AddRange([mergeBase, "--", file.Key]);
            var (byLine, subjects) = ParseBlame(ChangedFiles.Git(repo, [.. args]));
            foreach (var d in file)
            {
                if (!byLine.TryGetValue(d.OldLine, out var sha)) { continue; }
                var subject = subjects.GetValueOrDefault(sha, "");
                owner[(d.File, d.OldLine)] = (PrOf(subject) ?? 0, subject);
            }
        }
        var tests = DeletedTests(deleted).ToList();
        return [.. deleted.Where(d => owner.TryGetValue((d.File, d.OldLine), out var o) && (ownPr is null || o.Pr != ownPr)).GroupBy(d => owner[(d.File, d.OldLine)].Pr).OrderBy(g => g.Key)
            .Select(g => new Removal(g.Key, owner[(g.First().File, g.First().OldLine)].Subject, [.. g.Select(d => d.File).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], g.Count(),
                [.. tests.Where(t => owner.TryGetValue((t.File, t.Line), out var o) && o.Pr == g.Key)]))];
    }

    static (Dictionary<int, string> ByLine, Dictionary<string, string> Subjects) ParseBlame(string porcelain)
    {
        var (byLine, subjects, current) = (new Dictionary<int, string>(), new Dictionary<string, string>(StringComparer.Ordinal), "");
        foreach (var line in porcelain.Split('\n'))
        {
            if (BlameHeader().Match(line) is { Success: true } h) { current = h.Groups[1].Value; byLine[int.Parse(h.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)] = current; }
            else if (line.StartsWith("summary ", StringComparison.Ordinal)) { subjects[current] = line[8..]; }
        }
        return (byLine, subjects);
    }

    public static string Render(IReadOnlyList<Removal> removals)
    {
        if (removals.Count == 0) { return "No lines removed that another merged PR last changed (silent-revert guard, #4465)."; }
        var text = new StringBuilder("### This PR removes lines other merged PRs last changed (silent-revert guard, #4465)\n\n");
        foreach (var r in removals)
        {
            var who = r.Pr > 0 ? $"#{r.Pr}" : "a commit with no PR number";
            var tests = r.Tests.Count > 0 ? $"; deleted tests: {string.Join(", ", r.Tests.Select(t => t.Name))}" : "";
            text.Append($"- this PR removes lines from {who} (\"{r.Subject}\"): {r.Lines} lines in {string.Join(", ", r.Files)}{tests}\n");
        }
        foreach (var r in removals)
        {
            text.Append($"::warning title=silent-revert::this PR removes lines from {(r.Pr > 0 ? "#" + r.Pr : "a commit with no PR number")}: {r.Lines} lines in {r.Files.Count} files{(r.Tests.Count > 0 ? "; deleted tests " + string.Join(", ", r.Tests.Select(t => t.Name)) : "")}\n");
        }
        return text.ToString().TrimEnd();
    }

    public static int Run(Dictionary<string, string> options, TextWriter output)
    {
        var repo = options.GetValueOrDefault("repo") ?? ChangedFiles.RepoRoot(Directory.GetCurrentDirectory());
        var baseRef = options.GetValueOrDefault("base") ?? throw new ArgumentException("silent-revert needs --base.");
        int? ownPr = options.TryGetValue("pr", out var pr) ? int.Parse(pr, System.Globalization.CultureInfo.InvariantCulture) : null;
        var text = Render(Find(repo, baseRef, options.GetValueOrDefault("head") ?? "HEAD", ownPr));
        output.WriteLine(text);
        // The ::warning lines are for the log; in the job summary they would show as literal text.
        if (options.TryGetValue("summary", out var summary)) { File.AppendAllText(summary, string.Join('\n', text.Split('\n').Where(l => !l.StartsWith("::", StringComparison.Ordinal))) + "\n"); }
        return 0;
    }
}
