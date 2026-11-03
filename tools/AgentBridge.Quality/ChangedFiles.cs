using System.Diagnostics;

namespace AgentBridge.Quality;

/// <summary>What changed, as git sees it (quality/README.md §4.1).</summary>
static class ChangedFiles
{
    /// <summary>
    /// The files changed since the merge base with <paramref name="baseRef"/>, plus, when
    /// <paramref name="head"/> is the working tree (null), staged, unstaged and untracked files, so
    /// ring 0 tests what's on disk and not just what's committed.
    /// </summary>
    public static (IReadOnlyList<string> Files, string MergeBase) Since(string repoRoot, string baseRef, string? head = null)
    {
        var mergeBase = Git(repoRoot, "merge-base", baseRef, head ?? "HEAD").Trim();
        var files = new SortedSet<string>(StringComparer.Ordinal);
        // --no-renames lists a moved file at both paths, so the suites of the old home run too.
        AddLines(files, Git(repoRoot, "diff", "--name-only", "--no-renames", mergeBase, head ?? "HEAD"));
        if (head is null)
        {
            AddLines(files, Git(repoRoot, "diff", "--name-only", "--no-renames", "HEAD"));
            AddLines(files, Git(repoRoot, "ls-files", "--others", "--exclude-standard"));
        }
        return (files.ToList(), mergeBase);
    }

    public static IReadOnlyList<string> Tracked(string repoRoot)
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        AddLines(files, Git(repoRoot, "ls-files"));
        return files.ToList();
    }

    public static string RepoRoot(string from) => Git(from, "rev-parse", "--show-toplevel").Trim();

    static void AddLines(SortedSet<string> into, string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            into.Add(RepoPath.Normalize(line));
    }

    internal static string Git(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Paths with non-ASCII characters come back quoted and escaped otherwise.
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.quotePath=false");
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({process.ExitCode}): {stderr.Result.Trim()}");
        return stdout;
    }
}
