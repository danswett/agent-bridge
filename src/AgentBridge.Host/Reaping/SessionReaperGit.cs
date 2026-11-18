using System.Text.RegularExpressions;
using AgentBridge.Host.Core.Usage;

namespace AgentBridge.Host.Reaping;

public sealed class SessionReaperGit(ICommandRunner runner, Action<string> log)
{
    readonly Dictionary<string, (string Branch, string Sha)> remotes = new(StringComparer.OrdinalIgnoreCase);
    public void Reset() => remotes.Clear();
    public async Task<ReaperGit?> ReadAsync(string path, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(Path.Combine(path, ".git"))) { throw new InvalidDataException("not isolated"); }
            async Task<string> Git(params string[] args)
            {
                ct.ThrowIfCancellationRequested();
                var result = await runner.RunAsync("git", ["--no-optional-locks", "-c", "core.fsmonitor=false",
                    "-c", "core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"), "-c", "gc.auto=0", "-C", path, .. args],
                    TimeSpan.FromSeconds(15), ct);
                if (!result.Ran || result.TimedOut || result.ExitCode != 0) { throw new IOException("git proof unavailable"); }
                return result.Output.TrimEnd('\r', '\n');
            }
            var admin = await Git("rev-parse", "--absolute-git-dir");
            if (Directory.GetFileSystemEntries(admin).Select(Path.GetFileName).Any(name =>
                name is "MERGE_HEAD" or "REBASE_HEAD" or "CHERRY_PICK_HEAD" or "REVERT_HEAD" or "BISECT_LOG" or "rebase-apply" or "rebase-merge" or "sequencer" or "index.lock" or "HEAD.lock" or "locked"))
                throw new InvalidDataException("git operation");
            var branch = await Git("symbolic-ref", "--quiet", "--short", "HEAD");
            if (!Regex.IsMatch(branch, @"\A[A-Za-z0-9][A-Za-z0-9._/-]*\z")) { throw new InvalidDataException("branch"); }
            var head = await Git("rev-parse", "--verify", "HEAD");
            if (await Git("status", "--porcelain=v1", "--untracked-files=all", "--ignored=matching", "--ignore-submodules=none") != ""
                || await Git("stash", "list", "--format=%H") != "") { throw new InvalidDataException("dirty or stashed"); }
            if (!remotes.TryGetValue(path, out var remote))
            {
                await Git("fetch", "--no-auto-maintenance", "--no-tags", "--no-recurse-submodules", "--no-write-fetch-head",
                    "origin", $"refs/heads/{branch}:refs/remotes/origin/{branch}");
                remote = (branch, await Git("rev-parse", "--verify", $"refs/remotes/origin/{branch}"));
                remotes.Add(path, remote);
            }
            if (remote.Branch != branch) { throw new InvalidDataException("branch changed"); }
            await Git("merge-base", "--is-ancestor", head, remote.Sha);
            if (await Git("rev-parse", "--verify", "HEAD") != head || await Git("symbolic-ref", "--quiet", "--short", "HEAD") != branch)
                throw new InvalidDataException("git changed");
            return new(true, true, true, false, head, remote.Sha, 0, 0);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        { log($"session reaper git proof unavailable ({e.GetType().Name}); KEEP"); return null; }
    }
}
