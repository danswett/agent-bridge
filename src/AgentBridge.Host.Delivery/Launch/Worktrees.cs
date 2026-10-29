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
    public static GitResult Run(string directory, IReadOnlyList<string> arguments, TimeSpan? timeout = null)
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
//     so nothing that respects git's locks can remove it while the agent is starting.
// There is no reclaimer: worktrees this host creates stay until a person removes them.
public static class Worktrees
{
    // Written in the worktree's own git administration folder, never in its files, so it can
    // neither be committed nor deleted by a clean of the working tree. agent-ha-bridge's is
    // agent-bridge-created; this host's is its own.
    public const string Marker = "agent-bridge-host-created";
    public const string ReservationPrefix = "agent-bridge-host-launch:";

    public static WorktreeResult Create(string workspaceDirectory, LaunchConfig config, Func<DateTime>? now = null)
    {
        now ??= () => DateTime.Now;
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
        var fetch = Git.Run(repository, ["fetch", "origin", "--quiet"], TimeSpan.FromSeconds(60));
        if (!fetch.Ok) { detail.Add($"origin fetch failed ({fetch.Said}), so the existing local base was used"); }
        var baseRef = BaseRef(repository);
        if (baseRef is null) { return Refused("The repository has no readable base commit for an isolated worktree."); }

        var registered = Registered(repository);
        if (registered is null) { return Refused("The repository's worktrees cannot be listed, so the cap cannot be checked."); }
        var managed = registered.Where(IsManaged).ToList();
        if (config.WorktreeLimit > 0 && managed.Count >= config.WorktreeLimit)
        {
            return Refused($"{managed.Count} worktrees this host created already exist for this repository (limit {config.WorktreeLimit}); finish them and remove them at the machine, or raise launch.worktrees.limit.");
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
        return new WorktreeResult(launchIn, target, null, string.Join("; ", detail));
    }

    // Whether a folder lies in a worktree this host created for one of the isolated workspaces,
    // at or below the same subdirectory the workspace names in its repository: where a session
    // launched there runs, and so where it may be resumed. Returns that workspace and the
    // worktree's top folder.
    public static (WorkspaceConfig Workspace, string Worktree)? Managed(IReadOnlyList<WorkspaceConfig> workspaces, string folder)
    {
        if (!workspaces.Any(w => w.Isolate)) { return null; }
        var physical = Folders.Physical(folder);
        if (physical is null) { return null; }
        var top = Git.Run(physical, ["rev-parse", "--show-toplevel"]);
        if (!top.Ok || Folders.Physical(top.Output.Trim()) is not { } worktree || !IsManaged(worktree)) { return null; }
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

    // This host's marker in the worktree's administration folder. The primary checkout has a
    // .git folder, not a file, so it never qualifies.
    public static bool IsManaged(string worktree) => AdminFolder(worktree) is { } admin && File.Exists(Path.Combine(admin, Marker));

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
