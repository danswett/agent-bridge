using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentBridge.Host.Delivery.Launch;

// What one git command came to.
public sealed record GitResult(bool Ran, int ExitCode, string Output, string Error)
{
    public bool Ok => Ran && ExitCode == 0;
    public string Said => (Error.Trim().Length > 0 ? Error : Output).Trim() is { Length: > 0 } s ? Limit(s) : $"git exited {ExitCode}";

    static string Limit(string s) => s.Length > 300 ? s[..300] + "…" : s;
}

// git, run windowless with its output read, never waiting on a prompt nobody can answer.
public static class Git
{
    public static GitResult Run(string directory, IReadOnlyList<string> arguments, TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(directory);
        foreach (var a in arguments) { start.ArgumentList.Add(a); }
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "never";
        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) { start.Environment[name] = value; }
        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException("git did not start"); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return new GitResult(false, -1, "", $"git could not be run: {e.Message}"); }
        using (process)
        {
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout ?? TimeSpan.FromSeconds(30)))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                return new GitResult(true, -1, "", $"git {arguments.FirstOrDefault()} did not finish in time");
            }
            return new GitResult(true, process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        }
    }
}

// A new worktree, or why there is none. Detail says anything worth logging (a fetch that failed,
// the base used); Problem is the refusal.
public sealed record WorktreeResult(string? Path, string? Worktree, string? Problem, string Detail);

// Worktree isolation for launched sessions, as agent-ha-bridge does it
// (hooks/session-launch.ps1, New-BridgeSessionWorktree and its helpers), with this host's own
// marker so that neither product ever counts or claims the other's trees:
//   * a workspace marked isolate starts each new session in a git worktree of its own under the
//     worktree root (~/repos/wt), named <repository>-<yyyyMMdd-HHmmss>, detached at a freshly
//     fetched base (origin's default branch, else origin/main, origin/master, else HEAD);
//   * it fails closed: no isolation, no launch, and never a fallback to the checkout itself;
//   * a per-repository cap counts only worktrees carrying this host's marker;
//   * one worktree operation per repository at a time, across processes (a named mutex);
//   * the worktree is locked (git worktree lock) from creation until its session registers,
//     so nothing that respects git's locks can remove it while the agent is starting;
//   * before each isolated launch, and before the cap is counted, finished worktrees this
//     host made for that repository are removed (Reclaim), by agent-ha-bridge's rules
//     (Remove-BridgeFinishedWorktree); everything else stays until a person removes it.
public static class Worktrees
{
    // Written in the worktree's own git administration folder, never in its files, so it can
    // neither be committed nor deleted by a clean of the working tree. agent-ha-bridge's is
    // agent-bridge-created; this host's is its own.
    public const string Marker = "agent-bridge-host-created";
    public const string ReservationPrefix = "agent-bridge-host-launch:";

    public static WorktreeResult Create(string workspaceDirectory, LaunchConfig config, Func<DateTime>? now = null, IWorktreeUsage? usage = null, Action<string>? log = null)
    {
        now ??= () => DateTime.Now;
        log ??= _ => { };
        var top = Git.Run(workspaceDirectory, ["rev-parse", "--show-toplevel"]);
        if (!top.Ok) { return Refused($"Cannot establish the repository for isolation: {top.Said}"); }
        var repository = Folders.Physical(top.Output.Trim());
        var directory = Folders.Physical(workspaceDirectory);
        if (repository is null || directory is null) { return Refused("Cannot establish the repository for isolation: its folder cannot be read."); }
        if (!Folders.IsAtOrUnder(directory, repository)) { return Refused("The configured workspace is outside its repository root."); }
        var relative = Path.GetRelativePath(repository, directory);
        if (relative == ".") { relative = ""; }

        var root = Folders.Expand(config.WorktreeRoot);
        if (root is null) { return Refused("The configured worktree root is empty or invalid; correct launch.worktrees.root."); }

        using var operation = Enter(repository, out var busy);
        if (operation is null) { return Refused(busy!); }

        var detail = new List<string>();
        // Finished worktrees go before the cap is judged, so sessions that ended days ago do not
        // refuse isolation. Best effort, as agent-ha-bridge's: what cannot be cleared stays, and
        // the launch goes on to the cap. Before the fetch, as there: the base it judges "ahead"
        // against is the one the worktrees were made from or older, never newer.
        var removed = ReclaimHeld(repository, config.WorktreeIdleHours, usage, log, () => DateTimeOffset.Now);
        if (removed > 0) { detail.Add($"removed {removed} finished"); }

        var fetch = Git.Run(repository, ["fetch", "origin", "--quiet"], TimeSpan.FromSeconds(60));
        if (!fetch.Ok) { detail.Add($"origin fetch failed ({fetch.Said}), so the existing local base was used"); }
        var baseRef = BaseRef(repository);
        if (baseRef is null) { return Refused("The repository has no readable base commit for an isolated worktree."); }

        var registered = Registered(repository);
        if (registered is null) { return Refused("The repository's worktrees cannot be listed, so the cap cannot be checked."); }
        var managed = registered.Where(IsManaged).ToList();
        if (config.WorktreeLimit > 0 && managed.Count >= config.WorktreeLimit)
        {
            return Refused($"{managed.Count} worktrees this host created already exist for this repository (limit {config.WorktreeLimit}), after removing finished ones; a worktree is removed once it is clean, detached, unused and idle for launch.worktrees.idleHours, so finish some, remove them at the machine, or raise launch.worktrees.limit.");
        }

        var leaf = Path.GetFileName(repository) is { Length: > 0 } l ? l : "repo";
        var stamp = now().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string? target = null;
        for (var i = 0; i <= 50 && target is null; i++)
        {
            var candidate = Path.Combine(root, i == 0 ? $"{leaf}-{stamp}" : $"{leaf}-{stamp}-{i}");
            if (Directory.Exists(candidate) || File.Exists(candidate) || registered.Any(r => Folders.Same(r, candidate))) { continue; }
            target = candidate;
        }
        if (target is null) { return Refused("No unused worktree name is available; existing directories were preserved."); }

        try { Directory.CreateDirectory(root); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Refused($"The worktree root cannot be created: {e.Message}"); }
        var add = Git.Run(repository, ["worktree", "add", "--detach", target, baseRef], TimeSpan.FromMinutes(5));
        if (!add.Ok || !Directory.Exists(target)) { return Refused($"Could not create an isolated worktree: {add.Said}"); }
        detail.Add($"created {target} at {baseRef}");

        if (AdminFolder(target) is not { } admin) { detail.Add("its git administration folder cannot be found, so it carries no marker"); }
        else
        {
            try { File.WriteAllText(Path.Combine(admin, Marker), DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { detail.Add($"its marker could not be written ({e.Message}), so it is not counted against the cap"); }
        }

        var launchIn = relative.Length == 0 ? target : Path.GetFullPath(Path.Combine(target, relative));
        if (!Folders.IsAtOrUnder(launchIn, target)) { return new WorktreeResult(null, target, "The approved subdirectory escapes the isolated worktree; launch was refused.", string.Join("; ", detail)); }
        if (!Directory.Exists(launchIn)) { return new WorktreeResult(null, target, "The approved subdirectory is absent from the isolated base; the new worktree was retained without launching.", string.Join("; ", detail)); }
        // Where it really is: the base may hold a link at that path, which Directory.Exists
        // follows, out of the worktree into the checkout or anywhere else.
        if (Folders.Physical(launchIn) is not { } physical || Folders.Physical(target) is not { } physicalTarget || !Folders.IsAtOrUnder(physical, physicalTarget))
        {
            return new WorktreeResult(null, target, "The approved subdirectory is a link out of the isolated worktree in its base; launch was refused.", string.Join("; ", detail));
        }
        return new WorktreeResult(launchIn, target, null, string.Join("; ", detail));
    }

    // Whether a folder lies in a worktree made for one of the isolated workspaces, by this host
    // or by agent-ha-bridge (its marker, agent-bridge-created), at or below the same
    // subdirectory the workspace names in its repository: where a session launched there runs,
    // and so where it may be resumed. agent-ha-bridge's own are included because it offers its
    // sessions there for resume, and a machine moved off it would otherwise lose them; they are
    // still never counted, locked or removed here (IsManaged). Returns that workspace and the
    // worktree's top folder.
    public static (WorkspaceConfig Workspace, string Worktree)? Managed(IReadOnlyList<WorkspaceConfig> workspaces, string folder)
    {
        if (!workspaces.Any(w => w.Isolate)) { return null; }
        var physical = Folders.Physical(folder);
        if (physical is null) { return null; }
        var top = Git.Run(physical, ["rev-parse", "--show-toplevel"]);
        if (!top.Ok || Folders.Physical(top.Output.Trim()) is not { } worktree || !(IsManaged(worktree) || IsAgentHaBridges(worktree))) { return null; }
        var common = CommonDir(worktree);
        if (common is null) { return null; }
        foreach (var w in workspaces.Where(w => w.Isolate))
        {
            var wsTop = Git.Run(w.Path, ["rev-parse", "--show-toplevel"]);
            if (!wsTop.Ok || Folders.Physical(wsTop.Output.Trim()) is not { } repository || Folders.Physical(w.Path) is not { } wsDir) { continue; }
            if (CommonDir(repository) is not { } wsCommon || !Folders.Same(wsCommon, common)) { continue; }
            var relative = Path.GetRelativePath(repository, wsDir);
            var scope = relative == "." ? worktree : Path.GetFullPath(Path.Combine(worktree, relative));
            if (Directory.Exists(scope) && Folders.IsAtOrUnder(physical, scope)) { return (w, worktree); }
        }
        return null;
    }

    // Locks the worktree until its session registers, with a reason that names this launch,
    // or says why it could not. A worktree already locked by anyone is left as it is.
    public static (string? Token, string? Problem) Reserve(string worktree)
    {
        if (!IsManaged(worktree)) { return (null, null); }
        if (AdminFolder(worktree) is { } admin && File.Exists(Path.Combine(admin, "locked"))) { return (null, null); }
        var token = ReservationPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var locked = Git.Run(worktree, ["worktree", "lock", "--reason", token, worktree]);
        return locked.Ok ? (token, null) : (null, $"Cannot protect the worktree while its session starts: {locked.Said}");
    }

    // Unlocks it, only if the lock is still exactly this launch's.
    public static bool Release(string worktree, string token)
    {
        if (!token.StartsWith(ReservationPrefix, StringComparison.Ordinal) || AdminFolder(worktree) is not { } admin) { return false; }
        string reason;
        try { reason = File.ReadAllText(Path.Combine(admin, "locked")).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        if (!string.Equals(reason, token, StringComparison.Ordinal)) { return false; }
        return Git.Run(worktree, ["worktree", "unlock", worktree]).Ok;
    }

    // ---- the reclaimer: agent-ha-bridge's Remove-BridgeFinishedWorktree and what it calls ----

    // Build outputs a finished worktree may hold and still be removed: agent-ha-bridge's
    // $script:BridgeWorktreeBuildOutputs, exactly. Without them, following that repository's
    // documented test procedure (build the native hook) kept every worktree for ever, the cap
    // filled and launches there were refused (#143). They are exact file paths, never a
    // pattern or a folder: a folder would mean a recursive delete, which this never does. They
    // are reproducible only because a tree reaches removal with its tracked files unmodified,
    // so the hook's source is exactly what is committed. agent-bridge itself names none: its
    // build output is bin/ and obj/ folders, which retain a worktree like any other ignored data.
    internal static readonly IReadOnlyList<string> BuildOutputs = ["hook/agent-bridge-hook", "hook/agent-bridge-hook.exe"];

    // Removes the finished worktrees this host made for a repository, and returns how many.
    // Each one kept is logged with its reason, by folder name only.
    public static int Reclaim(string repositoryDirectory, LaunchConfig config, IWorktreeUsage? usage, Action<string> log, Func<DateTimeOffset>? now = null)
    {
        var top = Git.Run(repositoryDirectory, ["rev-parse", "--show-toplevel"]);
        if (!top.Ok || Folders.Physical(top.Output.Trim()) is not { } repository) { log("worktrees were retained: the repository cannot be established"); return 0; }
        using var operation = Enter(repository, out var busy);
        if (operation is null) { log($"worktrees were retained: {busy}"); return 0; }
        return ReclaimHeld(repository, config.WorktreeIdleHours, usage, log, now ?? (() => DateTimeOffset.Now));
    }

    // With the repository's worktree mutex held. Enumerates only that repository's own
    // registered worktrees, never the shared root, and of those only the ones carrying this
    // host's marker: agent-ha-bridge's, a person's, and any made before the marker existed are
    // not this host's to remove.
    static int ReclaimHeld(string repository, double idleHours, IWorktreeUsage? usage, Action<string> log, Func<DateTimeOffset> now)
    {
        if (CommonDir(repository) is not { } common) { log("worktrees were retained: the repository's git folder cannot be read"); return 0; }
        // As a commit id, resolved here in the repository: the fallback base is HEAD, and read
        // inside a worktree that would be the worktree's own, so HEAD..HEAD found nothing ahead
        // and a worktree holding the only copy of a commit was removed. (agent-ha-bridge passes
        // the name through, and has that gap without an origin.)
        if (BaseRef(repository) is not { } baseName
            || Git.Run(repository, ["rev-parse", "--verify", "--quiet", baseName + "^{commit}"]) is not { Ok: true } resolved
            || resolved.Output.Trim() is not { Length: > 0 } baseRef)
        {
            log("worktrees were retained: the repository has no readable base to compare them with");
            return 0;
        }
        if (Registered(repository) is not { } registered) { log("worktrees were retained: the repository's worktrees cannot be listed"); return 0; }
        var mine = registered.Where(IsManaged).ToList();
        if (mine.Count == 0) { return 0; }
        var snapshot = usage?.Read() ?? WorktreeUsage.Unknown("this host has no way to read it");
        if (!snapshot.Known)
        {
            log($"{mine.Count} worktree(s) were retained: whether a session is using them cannot be established ({snapshot.Detail})");
            return 0;
        }

        var removed = 0;
        foreach (var worktree in mine)
        {
            var name = Path.GetFileName(worktree);
            var why = Retained(worktree, common, baseRef, idleHours, snapshot, now) ?? Remove(worktree, baseRef, idleHours, usage!, now);
            if (why is null) { removed++; log($"removed finished worktree {name}"); }
            else { log($"retained worktree {name}: {why}"); }
        }
        return removed;
    }

    static string? Retained(string worktree, string common, string baseRef, double idleHours, WorktreeUsage usage, Func<DateTimeOffset> now)
    {
        if (usage.InUse(worktree)) { return "a live session is working in it"; }
        if (CommonDir(worktree) is not { } own || !Folders.Same(own, common)) { return "which repository it belongs to is uncertain"; }
        if (AdminFolder(worktree) is not { } admin) { return "its git administration folder cannot be found"; }
        if (File.Exists(Path.Combine(admin, "locked"))) { return "it is locked (a launch still starting, or someone's lock)"; }
        return Unfinished(worktree, baseRef, idleHours, now);
    }

    // Why a worktree is not finished, or null when it holds nothing that is not already in
    // the repository and is old enough: agent-ha-bridge's Test-BridgeWorktreeFinished. A
    // session that has only just started is clean and detached too, hence the age, read from
    // the marker this host wrote, which nothing that only reads the tree can disturb.
    static string? Unfinished(string worktree, string baseRef, double idleHours, Func<DateTimeOffset> now)
    {
        if (!Directory.Exists(worktree)) { return "its folder is missing"; }

        // Git can report clean ordinary entries while a folder in the way is a junction, or
        // while assume-unchanged hides a missing file. Neither authorizes removal.
        if (TrackedPaths(worktree) is not { } tracked || !NoLinks(worktree, [".git", .. tracked])
            || tracked.Any(p => !File.Exists(Path.Combine(worktree, p))))
        {
            return "it holds a link, a submodule, a conflict, or a tracked file that is missing or unreadable";
        }

        // --no-optional-locks: asking must not itself write to the index.
        var status = Git.Run(worktree, ["--no-optional-locks", "status", "--porcelain", "--untracked-files=all", "--ignored=matching"]);
        if (!status.Ok) { return "its git state cannot be read"; }
        foreach (var raw in status.Output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) { continue; }
            if (!line.StartsWith("!! ", StringComparison.Ordinal)) { return "it has uncommitted or untracked files"; }
            if (!IsBuildOutput(line[3..])) { return "it has ignored files, which may be someone's local data"; }
        }

        var branch = Git.Run(worktree, ["symbolic-ref", "--quiet", "HEAD"]);
        if (branch.Ok) { return "a branch is checked out in it"; }
        if (!branch.Ran || branch.ExitCode != 1) { return "its git state cannot be read"; }

        var ahead = Git.Run(worktree, ["rev-list", "--count", $"{baseRef}..HEAD"]);
        if (!ahead.Ok) { return "its git state cannot be read"; }
        if (ahead.Output.Trim() != "0") { return "it has commits that are not in the base"; }

        if (CreatedAt(worktree) is not { } created) { return "its ownership marker has no readable time"; }
        if ((now() - created).TotalHours < idleHours) { return "it is younger than launch.worktrees.idleHours"; }
        return null;
    }

    // Removes a finished worktree without ever deleting recursively: agent-ha-bridge's
    // Remove-BridgeWorktreeFiles. Even a non-forced `git worktree remove` deletes ignored
    // files recursively, so it is not used. Instead, with git's own HEAD and index locks held
    // so nothing else works in the tree meanwhile: everything is checked again, the clean
    // tracked files are removed through a private index (git rm refuses a modified one), the
    // named build outputs by exact path, then each folder with a non-recursive delete, which
    // fails on anything that arrived since. On any failure the .git pointer is put back and git
    // restores only the tracked files that are missing, never overwriting one. The worktree's
    // administration folder is left to git's own maintenance: a repository-wide prune would
    // also retire other owners' missing trees. Returns why it stayed, or null once it is gone.
    static string? Remove(string worktree, string baseRef, double idleHours, IWorktreeUsage usage, Func<DateTimeOffset> now)
    {
        if (AdminFolder(worktree) is not { } admin) { return "its git administration folder cannot be found"; }
        if (File.Exists(Path.Combine(admin, "locked"))) { return "it is locked (a launch still starting, or someone's lock)"; }
        var owned = new List<(FileStream Stream, string Path)>();
        var token = Guid.NewGuid().ToString("N");
        var privateIndex = Path.Combine(admin, $"agent-bridge-host-cleanup-{token}.index");
        var dotGit = Path.Combine(worktree, ".git");
        var staged = Path.Combine(Path.GetDirectoryName(worktree)!, $".agent-bridge-host-cleanup-{token}");
        var withPrivateIndex = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = privateIndex };
        IReadOnlyList<string> tracked = [];
        bool removedTracked = false, removedDirectory = false;
        var after = new List<string>();
        string? why;
        try
        {
            foreach (var name in new[] { "HEAD.lock", "index.lock" })
            {
                var path = Path.Combine(admin, name);
                owned.Add((new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None), path));
            }
            why = Attempt();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            why = owned.Count < 2 ? "git is working in it" : $"its removal stopped without deleting anything recursively ({e.GetType().Name})";
        }
        finally
        {
            if (!removedDirectory)
            {
                if (File.Exists(staged))
                {
                    try { File.Move(staged, dotGit); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { after.Add($"its .git pointer was left beside it and needs restoring by hand ({e.GetType().Name})"); }
                }
                if (removedTracked && File.Exists(dotGit))
                {
                    // Without --force, checkout-index restores missing files only: a file a
                    // person wrote meanwhile is never overwritten to make this look clean.
                    var restored = NoLinks(worktree, [".git", .. tracked]) && Git.Run(worktree, ["checkout-index", "--all"]).Ok;
                    after.Add(restored ? "the tracked files it had removed were restored" : "some tracked files were not restored, and existing files were preserved");
                }
            }
            foreach (var leftover in removedDirectory ? new[] { privateIndex, staged } : new[] { privateIndex })
            {
                try { if (File.Exists(leftover)) { File.Delete(leftover); } }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            foreach (var (stream, path) in owned)
            {
                stream.Dispose();
                try { File.Delete(path); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { after.Add($"a cleanup lock of its own could not be removed ({e.GetType().Name})"); }
            }
        }
        return why is null ? null : string.Join("; ", [why, .. after]);

        string? Attempt()
        {
            if (Unfinished(worktree, baseRef, idleHours, now) is { } unfinished) { return unfinished; }
            var again = usage.Read();
            if (!again.Known) { return "whether a session is using it cannot be established"; }
            if (again.InUse(worktree)) { return "a live session is working in it"; }

            tracked = TrackedPaths(worktree) ?? throw new InvalidOperationException("tracked files changed");
            var folders = new HashSet<string>(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            void Parents(string file)
            {
                for (var parent = Path.GetDirectoryName(file); parent is not null && Below(parent, worktree); parent = Path.GetDirectoryName(parent)) { folders.Add(parent); }
            }
            foreach (var relative in tracked) { Parents(Path.GetFullPath(Path.Combine(worktree, relative))); }
            if (!Git.Run(worktree, ["read-tree", "HEAD"], environment: withPrivateIndex).Ok) { throw new InvalidOperationException("read-tree"); }
            if (tracked.Count > 0)
            {
                if (!NoLinks(worktree, [".git", .. tracked])) { throw new InvalidOperationException("links"); }
                removedTracked = true;
                if (!Git.Run(worktree, ["rm", "-r", "--quiet", "--", "."], environment: withPrivateIndex).Ok) { throw new InvalidOperationException("git rm"); }
            }
            // After the tracked files and before the folders, so hook/ can empty. Unfinished
            // has established that these are the only ignored entries; each is checked again,
            // since this is the step that deletes. A link here would delete what it points at.
            foreach (var relative in BuildOutputs)
            {
                var output = Path.GetFullPath(Path.Combine(worktree, relative));
                if (!Below(output, worktree) || !File.Exists(output)) { continue; }
                if (!NoLinks(worktree, [relative])) { throw new InvalidOperationException("a linked build output"); }
                File.Delete(output);
                Parents(output);
            }
            foreach (var folder in folders.OrderByDescending(f => f.Length))
            {
                if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: false); }
            }
            File.Move(dotGit, staged);
            Directory.Delete(worktree, recursive: false);
            removedDirectory = true;
            return null;
        }
    }

    static bool IsBuildOutput(string entry)
    {
        // A path git had to quote has characters none of these have; decoding its escapes to
        // compare is a way to get it subtly wrong, so it is not one, and the tree stays.
        if (entry.StartsWith('"')) { return false; }
        var relative = entry.Trim().Replace('\\', '/');
        return relative.Length > 0 && BuildOutputs.Contains(relative, StringComparer.Ordinal);
    }

    // Every tracked path, relative, or null when any entry is not an ordinary file at stage 0
    // (a link, a submodule, a conflict) or would lie outside the worktree.
    static List<string>? TrackedPaths(string worktree)
    {
        var entries = Git.Run(worktree, ["ls-files", "--stage", "-z"]);
        if (!entries.Ok) { return null; }
        var paths = new List<string>();
        foreach (var entry in entries.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = System.Text.RegularExpressions.Regex.Match(entry, @"^100(?:644|755) [a-f0-9]+ 0\t(.+)$", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!m.Success) { return null; }
            var relative = m.Groups[1].Value;
            try { if (!Below(Path.GetFullPath(Path.Combine(worktree, relative)), worktree)) { return null; } }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
            paths.Add(relative);
        }
        return paths;
    }

    // agent-ha-bridge's Assert-BridgeInstallPayload: no component of the root or of any of
    // these paths below it is a link (junction, symlink), and nor is anything directly in a
    // folder one of them names. A component that does not exist is passed over.
    static bool NoLinks(string root, IReadOnlyList<string> relatives)
    {
        try
        {
            if (IsLink(root)) { return false; }
            foreach (var relative in relatives)
            {
                var parts = relative.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
                if (Path.IsPathRooted(relative) || parts.Contains("..")) { return false; }
                var path = root;
                FileSystemInfo? last = null;
                foreach (var part in parts)
                {
                    path = Path.Combine(path, part);
                    last = Directory.Exists(path) ? new DirectoryInfo(path) : File.Exists(path) ? new FileInfo(path) : null;
                    if (last is null) { var info = new FileInfo(path); if (info.LinkTarget is not null) { return false; } continue; }
                    if (last.Attributes.HasFlag(FileAttributes.ReparsePoint)) { return false; }
                }
                if (last is DirectoryInfo folder && folder.EnumerateFileSystemInfos().Any(c => c.Attributes.HasFlag(FileAttributes.ReparsePoint))) { return false; }
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    static bool Below(string path, string root) => !Folders.Same(path, root) && Folders.IsAtOrUnder(path, root);

    // When this host made it, from its marker. Unreadable or malformed cannot prove it old.
    static DateTimeOffset? CreatedAt(string worktree)
    {
        if (AdminFolder(worktree) is not { } admin) { return null; }
        try
        {
            var raw = File.ReadAllText(Path.Combine(admin, Marker)).Trim();
            return DateTimeOffset.TryParseExact(raw, "o", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // This host's marker in the worktree's administration folder. The primary checkout has a
    // .git folder, not a file, so it never qualifies.
    public static bool IsManaged(string worktree) => AdminFolder(worktree) is { } admin && File.Exists(Path.Combine(admin, Marker));

    // agent-ha-bridge's marker (Get-BridgeWorktreeMarkerPath).
    public const string AgentHaBridgeMarker = "agent-bridge-created";

    public static bool IsAgentHaBridges(string worktree) => AdminFolder(worktree) is { } admin && File.Exists(Path.Combine(admin, AgentHaBridgeMarker));

    // A linked worktree's .git is a file, "gitdir: <its administration folder>".
    internal static string? AdminFolder(string worktree)
    {
        var dotGit = Path.Combine(worktree, ".git");
        try
        {
            if (!File.Exists(dotGit)) { return null; }
            var line = File.ReadLines(dotGit).FirstOrDefault()?.Trim();
            if (line is null || !line.StartsWith("gitdir:", StringComparison.Ordinal)) { return null; }
            var path = line["gitdir:".Length..].Trim();
            var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(worktree, path));
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    static string? CommonDir(string directory)
    {
        var r = Git.Run(directory, ["rev-parse", "--path-format=absolute", "--git-common-dir"]);
        return r.Ok ? Folders.Physical(r.Output.Trim()) : null;
    }

    static string? BaseRef(string repository)
    {
        var head = Git.Run(repository, ["symbolic-ref", "--quiet", "refs/remotes/origin/HEAD"]);
        var candidates = new List<string>();
        if (head.Ok && head.Output.Trim() is { } r && r.StartsWith("refs/remotes/origin/", StringComparison.Ordinal)) { candidates.Add("origin/" + r["refs/remotes/origin/".Length..]); }
        candidates.AddRange(["origin/main", "origin/master", "HEAD"]);
        foreach (var c in candidates)
        {
            if (Git.Run(repository, ["rev-parse", "--verify", "--quiet", c + "^{commit}"]).Ok) { return c; }
        }
        return null;
    }

    static List<string>? Registered(string repository)
    {
        var list = Git.Run(repository, ["worktree", "list", "--porcelain", "-z"]);
        if (!list.Ok) { return null; }
        return [.. list.Output.Split('\0').Where(l => l.StartsWith("worktree ", StringComparison.Ordinal)).Select(l => Path.GetFullPath(l["worktree ".Length..]))];
    }

    // One worktree operation per repository at a time, across every process of this user. The
    // repository is named by its common git folder, so a worktree and its main checkout agree.
    static IDisposable? Enter(string repository, out string? problem)
    {
        problem = null;
        var common = CommonDir(repository);
        if (common is null) { problem = "Cannot establish the repository's git folder for isolation."; return null; }
        var key = OperatingSystem.IsWindows() ? common.ToLowerInvariant() : common;
        var name = $"Local\\AgentBridgeHostWorktree_{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))}";
        var mutex = new Mutex(false, OperatingSystem.IsWindows() ? name : name.Replace('\\', '_'));
        bool taken;
        try { taken = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { taken = true; }
        if (!taken)
        {
            mutex.Dispose();
            problem = "Another worktree operation is in progress; retry the launch after it finishes.";
            return null;
        }
        return new HeldMutex(mutex);
    }

    // Released on dispose, on the thread that took it: Create runs synchronously throughout.
    sealed class HeldMutex(Mutex inner) : IDisposable
    {
        public void Dispose()
        {
            try { inner.ReleaseMutex(); } catch (ApplicationException) { }
            inner.Dispose();
        }
    }

    static WorktreeResult Refused(string problem) => new(null, null, problem, "");
}
