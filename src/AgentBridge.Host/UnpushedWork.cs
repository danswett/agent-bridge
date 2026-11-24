using System.Diagnostics;

namespace AgentBridge.Host;

// Whether ending a session in this folder could lose work (#2741): uncommitted or untracked files, a stash, a commit
// on any local branch that no remote has, or a local branch with no upstream. Fails closed: a folder git cannot
// read, or a git that does not answer in time, counts as pending. An open pull request is GitHub's to say and the
// host knows nothing of it, so that part stays with the caller's policy.
internal static class UnpushedWork
{
    // One deadline over the whole check, every git call and every pipe read: the fleet.end lock is held while it runs.
    static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    public static bool Pending(string folder)
    {
        using var deadline = new CancellationTokenSource(Bound);
        var ct = deadline.Token;
        try
        {
            // The repository's own config must not hide work: untracked files and submodule changes always count.
            if (Run(folder, ct, "-c", "status.showUntrackedFiles=all", "status", "--porcelain", "--untracked-files=all", "--ignore-submodules=none") is not { Length: 0 }) { return true; }
            if (Run(folder, ct, "stash", "list") is not { Length: 0 }) { return true; }
            if (Run(folder, ct, "for-each-ref", "--format=%(upstream)|%(upstream:track)", "refs/heads") is not { } branches) { return true; }
            foreach (var branch in branches.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = branch.Trim().Split('|');
                if (parts[0].Length == 0 || parts.Length > 1 && (parts[1].Contains("ahead") || parts[1].Contains("gone"))) { return true; }
            }
            // A detached HEAD has no branch row above: its commits still need an upstream to be safe.
            return Run(folder, ct, "rev-list", "--count", "@{upstream}..HEAD") != "0";
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
    }

    // null for any failure or for the deadline, so a failed or slow read is never taken for "nothing pending".
    static string? Run(string folder, CancellationToken deadline, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        // The folder is the agent's own: no config-run programs, no index lock taken from its git, no prompt.
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var a in new[] { "-c", "core.fsmonitor=false", "-c", "core.hooksPath=NUL" }) { info.ArgumentList.Add(a); }
        foreach (var a in args) { info.ArgumentList.Add(a); }
        using var git = Process.Start(info) ?? throw new InvalidOperationException("git did not start");
        // git's exit and both pipes under the one deadline: a descendant that outlives git holding a pipe (a clean filter that
        // backgrounds a process) must not hold the caller past it. Past it the git tree is killed and the answer is none.
        var output = git.StandardOutput.ReadToEndAsync();
        var all = Task.WhenAll(output, git.StandardError.ReadToEndAsync(), git.WaitForExitAsync());
        try { all.Wait(deadline); }
        catch (Exception e) when (e is OperationCanceledException or AggregateException)
        {
            try { git.Kill(true); } catch (InvalidOperationException) { /* already gone */ }
            return null;
        }
        return git.ExitCode == 0 ? output.Result.Trim() : null;
    }
}
