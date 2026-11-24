using System.Diagnostics;

namespace AgentBridge.Host;

// Whether ending a session in this folder could lose work (#2741): uncommitted changes, or commits no remote has.
// Fails closed: a folder git cannot read, a branch with no upstream or a git that does not answer in time counts as pending.
// An open pull request is GitHub's to say and the host knows nothing of it, so that part stays with the caller's policy.
internal static class UnpushedWork
{
    public static bool Pending(string folder)
    {
        try
        {
            // The repository's own config must not hide work: untracked files and submodule changes always count.
            if (Run(folder, "-c", "status.showUntrackedFiles=all", "status", "--porcelain", "--untracked-files=all", "--ignore-submodules=none") is not { Length: 0 }) { return true; }
            return Run(folder, "rev-list", "--count", "@{upstream}..HEAD") != "0";
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
    }

    // null for any failure, so a failed read is never taken for "nothing pending".
    static string? Run(string folder, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, CreateNoWindow = true };
        // The folder is the agent's own: no config-run programs, no index lock taken from its git, no prompt.
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var a in new[] { "-c", "core.fsmonitor=false", "-c", "core.hooksPath=NUL" }) { info.ArgumentList.Add(a); }
        foreach (var a in args) { info.ArgumentList.Add(a); }
        using var git = Process.Start(info) ?? throw new InvalidOperationException("git did not start");
        var output = git.StandardOutput.ReadToEndAsync();
        if (!git.WaitForExit(5_000)) { git.Kill(true); return null; }
        return git.ExitCode == 0 ? output.GetAwaiter().GetResult().Trim() : null;
    }
}
