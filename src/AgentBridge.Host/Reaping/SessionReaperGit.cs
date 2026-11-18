using System.Text.RegularExpressions;
using AgentBridge.Host.Core.Usage;

namespace AgentBridge.Host.Reaping;

public sealed class SessionReaperGit(ICommandRunner runner, Action<string> log)
{
    const string OriginProofs = "refs/slipgates/session-reaper/origin/";
    readonly Dictionary<string, bool> fetched = new(StringComparer.OrdinalIgnoreCase);
    public void Reset() => fetched.Clear();
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
            if (!fetched.TryGetValue(path, out var succeeded))
            {
                fetched.Add(path, false);
                await Git("fetch", "--atomic", "--prune", "--no-auto-maintenance", "--no-tags", "--no-recurse-submodules",
                    "--no-write-fetch-head", "--refmap=", "origin", $"+refs/heads/*:{OriginProofs}*");
                fetched[path] = true;
            }
            else if (!succeeded) { throw new IOException("origin fetch failed this cycle"); }
            var branches = await Git("for-each-ref", "--contains=" + head, "--format=%(refname)%09%(symref)", OriginProofs);
            var containing = branches.Split('\n').Select(line => line.TrimEnd('\r').Split('\t'))
                .Where(parts => parts.Length == 2 && parts[1] == ""
                    && parts[0].StartsWith(OriginProofs, StringComparison.Ordinal))
                .Select(parts => parts[0]).FirstOrDefault();
            if (containing is null) { throw new InvalidDataException("no containing origin branch"); }
            var remote = await Git("rev-parse", "--verify", containing);
            await Git("merge-base", "--is-ancestor", head, remote);
            if (await Git("rev-parse", "--verify", "HEAD") != head || await Git("symbolic-ref", "--quiet", "--short", "HEAD") != branch)
                throw new InvalidDataException("git changed");
            return new(true, true, true, false, head, remote, 0, 0);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        { log($"session reaper git proof unavailable ({e.GetType().Name}); KEEP"); return null; }
    }
}
