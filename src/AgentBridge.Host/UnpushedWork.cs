using System.Diagnostics;

namespace AgentBridge.Host;

// Whether ending a session in this folder could lose work (#2741): uncommitted or untracked files, a stash, a commit
// on any local branch that no remote has, or a local branch with no upstream. Fails closed: a folder git cannot
// read, or a git that does not answer in time, counts as pending. An open pull request is GitHub's to say and the
// host knows nothing of it, so that part stays with the caller's policy.
internal static class UnpushedWork
{
    public static bool Pending(string folder)
    {
        try
        {
            // The repository's own config must not hide work: untracked files and submodule changes always count.
            if (Run(folder, "-c", "status.showUntrackedFiles=all", "status", "--porcelain", "--untracked-files=all", "--ignore-submodules=none") is not { Length: 0 }) { return true; }
            if (Run(folder, "stash", "list") is not { Length: 0 }) { return true; }
            if (Run(folder, "for-each-ref", "--format=%(upstream)|%(upstream:track)", "refs/heads") is not { } branches) { return true; }
            foreach (var branch in branches.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = branch.Trim().Split('|');
                if (parts[0].Length == 0 || parts.Length > 1 && (parts[1].Contains("ahead") || parts[1].Contains("gone"))) { return true; }
            }
            // A detached HEAD has no branch row above: its commits still need an upstream to be safe.
            return Run(folder, "rev-list", "--count", "@{upstream}..HEAD") != "0";
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
    }

    // null for any failure, so a failed read is never taken for "nothing pending".
    static string? Run(string folder, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        // The folder is the agent's own: no config-run programs, no index lock taken from its git, no prompt.
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var a in new[] { "-c", "core.fsmonitor=false", "-c", "core.hooksPath=NUL" }) { info.ArgumentList.Add(a); }
        foreach (var a in args) { info.ArgumentList.Add(a); }
        using var git = Process.Start(info) ?? throw new InvalidOperationException("git did not start");
        // Both pipes are drained as git writes, so a full pipe cannot stall it.
        var output = git.StandardOutput.ReadToEndAsync();
        var errors = git.StandardError.ReadToEndAsync();
        if (!git.WaitForExit(5_000)) { git.Kill(true); return null; }
        errors.GetAwaiter().GetResult();
        return git.ExitCode == 0 ? output.GetAwaiter().GetResult().Trim() : null;
    }
}
