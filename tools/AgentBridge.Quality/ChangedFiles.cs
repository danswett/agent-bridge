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
        AddPaths(files, Git(repoRoot, "diff", "-z", "--name-only", "--no-renames", mergeBase, head ?? "HEAD"));
        if (head is null)
        {
            AddPaths(files, Git(repoRoot, "diff", "-z", "--name-only", "--no-renames", "HEAD"));
            // The index too: a change staged and then undone in the working tree is still to be committed.
            AddPaths(files, Git(repoRoot, "diff", "-z", "--cached", "--name-only", "--no-renames", "HEAD"));
            AddPaths(files, Git(repoRoot, "ls-files", "-z", "--others", "--exclude-standard"));
        }
        return (files.ToList(), mergeBase);
    }

    public static IReadOnlyList<string> Tracked(string repoRoot)
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        AddPaths(files, Git(repoRoot, "ls-files", "-z"));
        return files.ToList();
    }

    public static string RepoRoot(string from) => Git(from, "rev-parse", "--show-toplevel").Trim();

    // NUL-separated (-z), as git writes every path verbatim: one-per-line output quotes a path
    // holding ", \, a tab or another control character, and the quoted form ("tools/x/a\"b.cs")
    // matched none of the catalog's globs (#523's review).
    static void AddPaths(SortedSet<string> into, string output)
    {
        foreach (var path in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            into.Add(RepoPath.Normalize(path));
    }

    internal static string Git(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // git writes paths as UTF-8 bytes; the console's code page would garble them on Windows.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
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
