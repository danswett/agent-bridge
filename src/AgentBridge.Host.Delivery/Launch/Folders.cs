namespace AgentBridge.Host.Delivery.Launch;

// Folders: which are approved for a launch, as agent-ha-bridge decides it
// (hooks/session-launch.ps1, Test-BridgeWorkspacePathApproved and Test-BridgeSystemDirectory).
// Paths are compared physically, every link resolved, so a junction inside an approved folder
// cannot carry a launch somewhere that is not.
public static class Folders
{
    static readonly StringComparison Case = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // An absolute path with a leading ~ expanded and . and .. resolved, or null for a path that
    // is not absolute: a relative one would mean wherever the host happened to start.
    public static string? Expand(string path)
    {
        path = path.Trim();
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "");
        }
        try
        {
            if (!Path.IsPathFullyQualified(path)) { return null; }
            return Trim(Path.GetFullPath(path));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public static string DefaultLabel(string full)
    {
        var leaf = Path.GetFileName(Trim(full));
        return string.IsNullOrEmpty(leaf) ? full : leaf;
    }

    // A folder no session should be launched in. A console opened from the Start menu starts in
    // System32, so that is a routine working directory for a session nobody meant to point
    // anywhere in particular; Windows, Program Files, ProgramData, temp and bare drive roots
    // are excluded for the same reason.
    public static bool IsSystemDirectory(string path)
    {
        string full;
        try { full = Trim(Path.GetFullPath(path)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return true; }
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(full) || string.Equals(full, Trim(root ?? ""), Case)) { return true; }
        var excluded = new List<string?>
        {
            Environment.GetEnvironmentVariable("WINDIR"),
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetEnvironmentVariable("ProgramData"),
            Environment.GetEnvironmentVariable("TEMP"),
        };
        if (!OperatingSystem.IsWindows()) { excluded.AddRange(["/System", "/Library", "/usr", "/bin", "/sbin", "/private", "/Applications", "/opt", "/var", "/etc"]); }
        foreach (var candidate in excluded)
        {
            if (string.IsNullOrWhiteSpace(candidate)) { continue; }
            string prefix;
            try { prefix = Trim(Path.GetFullPath(candidate)); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (IsAtOrUnder(full, prefix)) { return true; }
        }
        return false;
    }

    // The folder as it really is on disk, every component's link (junction, symlink) resolved,
    // or null when it does not exist, cannot be read, or took too long (see Resolve).
    public static string? Physical(string path) => Resolve(path).Path;

    // Physical, with the reason when there is none. It never waits for ever: a link to an offline network share can stall the file system
    // call, so the work is given ResolveBound and a path that stalled is not asked again for StalledFor. With localOnly (an approval), a link whose
    // target is not on a local fixed drive is refused before the target is touched.
    public static FolderResolved Resolve(string path, bool localOnly = false, TimeSpan? timeout = null)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return new(null, FolderProblem.Missing); }
        return Bounded(full, () => Walk(full, 0, localOnly), timeout ?? ResolveBound, out var resolved) ? resolved : new(null, FolderProblem.TimedOut);
    }

    static readonly TimeSpan ResolveBound = TimeSpan.FromSeconds(5);
    static readonly TimeSpan StalledFor = TimeSpan.FromSeconds(30);
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> stalled = new(StringComparer.OrdinalIgnoreCase);

    internal static void ForgetStalled() => stalled.Clear();

    // Runs work and gives up after timeout, remembering that key stalled so the next asks for it, or for anything below it, within StalledFor do
    // not wait again. The stalled call is left to finish (or not) on its own thread, which is its own rather than the pool's so a stall cannot
    // starve it, and the mark is dropped as soon as that call does finish: a disk that was only slow is asked again.
    internal static bool Bounded<T>(string key, Func<T> work, TimeSpan timeout, out T? result)
    {
        result = default;
        var now = Environment.TickCount64;
        foreach (var (marked, until) in stalled)
        {
            if (now < until && IsAtOrUnder(key, marked)) { return false; }
        }
        var task = Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        if (task.Wait(timeout)) { result = task.Result; return true; }
        if (stalled.Count > 256) { stalled.Clear(); }
        stalled[key] = Environment.TickCount64 + (long)StalledFor.TotalMilliseconds;
        task.ContinueWith(_ => stalled.TryRemove(key, out long _), TaskScheduler.Default);
        return false;
    }

    // A fixed drive of this machine (Windows): not a network share (UNC or mapped), a removable or optical drive, or one that is not there.
    public static bool IsLocalFixed(string path)
    {
        if (!OperatingSystem.IsWindows()) { return true; }
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) { return false; }
        try { return new DriveInfo(Path.GetPathRoot(path) ?? "").DriveType == DriveType.Fixed; }
        catch (Exception e) when (e is ArgumentException or IOException) { return false; }
    }

    static FolderResolved Walk(string full, int depth, bool localOnly)
    {
        // Bounded, so a link cycle cannot hold a launch for ever.
        if (depth > 32) { return new(null, FolderProblem.Missing); }
        try
        {
            if (localOnly && !IsLocalFixed(full)) { return new(null, FolderProblem.NotLocal); }
            var root = Path.GetPathRoot(full)!;
            var current = root;
            foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                var next = Path.Combine(current, part);
                if (new DirectoryInfo(next).LinkTarget is { } target)
                {
                    // The target may run through links of its own.
                    var targetFull = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(current, target));
                    if (localOnly && !IsLocalFixed(targetFull)) { return new(null, FolderProblem.NotLocal); }
                    var resolved = Walk(targetFull, depth + 1, localOnly);
                    if (resolved.Path is null) { return resolved; }
                    next = resolved.Path;
                }
                current = next;
            }
            return Directory.Exists(current) ? new(Trim(current), FolderProblem.None) : new(null, FolderProblem.Missing);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return new(null, FolderProblem.Missing); }
    }

    public static bool IsAtOrUnder(string path, string root)
    {
        path = Trim(path);
        root = Trim(root);
        if (string.Equals(path, root, Case)) { return true; }
        var sep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(sep, Case);
    }

    public static bool Same(string a, string b) => string.Equals(Trim(a), Trim(b), Case);

    // The approved workspace holding this folder, physically; null when none does. The first
    // configured one wins, so a folder in two keeps the label a person listed first.
    public static WorkspaceConfig? Containing(IReadOnlyList<WorkspaceConfig> workspaces, string folder)
    {
        var physical = Physical(folder);
        if (physical is null) { return null; }
        foreach (var w in workspaces)
        {
            if (Physical(w.Path) is { } root && IsAtOrUnder(physical, root)) { return w; }
        }
        return null;
    }

    // The approved workspace that is this folder, physically; null when none is.
    public static WorkspaceConfig? Exactly(IReadOnlyList<WorkspaceConfig> workspaces, string folder)
    {
        var physical = Physical(folder);
        if (physical is null) { return null; }
        return workspaces.FirstOrDefault(w => Physical(w.Path) is { } root && Same(physical, root));
    }

    static string Trim(string path)
    {
        var root = Path.GetPathRoot(path) ?? "";
        return path.Length > root.Length ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
    }
}

public enum FolderProblem { None, Missing, NotLocal, TimedOut }

public readonly record struct FolderResolved(string? Path, FolderProblem Problem);

