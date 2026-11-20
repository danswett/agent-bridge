using System.Security.Cryptography;

namespace AgentBridge.App.Core;

// An in-place install that could not go ahead, or was undone; its message names why.
public sealed class InPlaceApplyException(string message, Exception? inner = null) : IOException(message, inner);

// #3343: Velopack installs by renaming current aside, which NTFS refuses while any file beneath
// it is open. On 2026-11-19 Windows' telemetry service (DiagTrack, SYSTEM) held
// current\mscordaccore.dll for 20+ hours, so every update failed until a reboot, and nothing
// without elevation can make it let go. The new version's copy was byte-identical, so leaving the
// held file where it was gave the same tree. This does that, and only that: never elevating, never
// writing a held file, and failing before anything changes when a held file is not the same.
public static class InPlaceApply
{
    // apply: the existing (Velopack) install, true when it took. held: the files in current that
    // something outside holds, asked only once that install has been refused. staged: the new
    // version's files, on current's volume. backup: where replaced files wait until it is done.
    // True when installed in place, false when the existing install took; anything else throws.
    public static bool Install(Func<bool> apply, Func<IReadOnlyList<string>> held, string staged, string current, string backup, Action<string>? log = null)
    {
        if (apply()) { return false; }
        var holders = held();
        if (holders.Count == 0)
        {
            throw new InPlaceApplyException("the install failed and nothing outside holds a file in current, so it is not retried in place; nothing was installed");
        }
        Mirror(staged, current, backup, holders, log);
        return true;
    }

    static void Mirror(string staged, string current, string backup, IReadOnlyList<string> held, Action<string>? log)
    {
        var next = Files(staged);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? kept = null;
        foreach (var file in held)
        {
            var rel = Path.GetRelativePath(current, file);
            keep.Add(rel);
            // Chief, Nov 19: one the new version drops stays, warned of; a later apply removes it
            // once nothing holds it. One it has, unreadable to compare, is not the same.
            if (!next.Contains(rel)) { log?.Invoke($"update: current{Path.DirectorySeparatorChar}{rel} is held by another program and is not in the new version; it stays until nothing holds it"); }
            else if (!Same(Path.Combine(staged, rel), file))
            {
                throw new InPlaceApplyException($"current{Path.DirectorySeparatorChar}{rel} is held by another program and is not the same in the new version; nothing was installed");
            }
        }
        if (Directory.Exists(backup)) { kept = Recover(current, backup, log); }
        var moved = new List<string>();
        var written = new List<string>();
        var created = new List<string>();
        var removed = new List<string>();
        try
        {
            foreach (var rel in Files(current).Where(r => !keep.Contains(r)))
            {
                var to = Path.Combine(backup, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(Path.Combine(current, rel), to);
                moved.Add(rel);
            }
            // A folder left empty is in the way of a file of the same name in the new version.
            foreach (var dir in Directory.EnumerateDirectories(current, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList())
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) { Directory.Delete(dir); removed.Add(dir); }
            }
            foreach (var rel in next.Where(r => !keep.Contains(r)).Order(StringComparer.OrdinalIgnoreCase))
            {
                var to = Path.Combine(current, rel);
                var parent = Path.GetDirectoryName(to)!;
                var missing = new List<string>();
                for (var d = parent; d.Length > current.Length && !Directory.Exists(d); d = Path.GetDirectoryName(d)!) { missing.Add(d); }
                Directory.CreateDirectory(parent);
                created.AddRange(missing);
                // Listed before the copy, so a copy that fails part way is removed with the rest.
                written.Add(rel);
                File.Copy(Path.Combine(staged, rel), to);
            }
            if (next.FirstOrDefault(rel => !Same(Path.Combine(staged, rel), Path.Combine(current, rel))) is { } bad)
            {
                throw new IOException($"current{Path.DirectorySeparatorChar}{bad} is not the new version's bytes after copying");
            }
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            var back = Restore(current, backup, written, moved, created, removed);
            var earlier = kept is null ? "" : $"; an earlier install's backup, which may hold the only intact old files, is kept at {kept}";
            throw new InPlaceApplyException(back
                ? $"the in-place install failed and the old version was put back; nothing was installed{earlier}: {e.Message}"
                : $"the in-place install failed and current is partly updated; what could not be put back is in {backup}{earlier}: {e.Message}", e);
        }
        try { DeleteTree(backup); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // A backup an earlier install left (a crash, or a final delete that failed) blocked every later
    // one. Where every file in it is the same in current it is only a leftover; where one is not, it may
    // hold the only copy (an interrupted copy truncates current's: r4639323971), so it is kept beside it, never deleted.
    static string? Recover(string current, string backup, Action<string>? log)
    {
        try
        {
            if (Files(backup).All(rel => Same(Path.Combine(backup, rel), Path.Combine(current, rel))))
            {
                DeleteTree(backup);
                log?.Invoke($"update: removed an earlier install's leftover backup at {backup}; every file in it is also in current");
                return null;
            }
            var kept = $"{backup}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            Directory.Move(backup, kept);
            log?.Invoke($"update: an earlier install's backup holds files that are not the same in current; it was kept at {kept}");
            return kept;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InPlaceApplyException($"an earlier install's backup at {backup} could not be cleared ({e.Message}); nothing was installed", e);
        }
    }

    // Undo in reverse: the new files and folders out, the old ones back. False when any could not be.
    static bool Restore(string current, string backup, List<string> written, List<string> moved, List<string> created, List<string> removed)
    {
        var whole = true;
        void Try(Action undo)
        {
            try { undo(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { whole = false; }
        }
        // A copy keeps the new version's read-only attribute, which File.Delete refuses.
        foreach (var to in written.Select(rel => Path.Combine(current, rel)).Where(File.Exists))
        {
            Try(() => { File.SetAttributes(to, FileAttributes.Normal); File.Delete(to); });
        }
        // Deepest first, and only empty: a folder the new version made where a file was.
        foreach (var dir in created.Distinct().OrderByDescending(d => d.Length).Where(Directory.Exists))
        {
            Try(() => { if (!Directory.EnumerateFileSystemEntries(dir).Any()) { Directory.Delete(dir); } });
        }
        foreach (var dir in removed) { Try(() => Directory.CreateDirectory(dir)); }
        foreach (var rel in moved)
        {
            var to = Path.Combine(current, rel);
            Try(() => { Directory.CreateDirectory(Path.GetDirectoryName(to)!); File.Move(Path.Combine(backup, rel), to); });
        }
        if (whole)
        {
            try { DeleteTree(backup); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return whole;
    }

    // Only for files nothing needs any more: Directory.Delete refuses a read-only one.
    internal static void DeleteTree(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) { File.SetAttributes(file, FileAttributes.Normal); }
        Directory.Delete(dir, recursive: true);
    }

    static HashSet<string> Files(string dir) =>
        new(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f)), StringComparer.OrdinalIgnoreCase);

    // The updater's own check (PackageDigest), with the staged file's SHA-256 as the digest.
    static bool Same(string staged, string installed)
    {
        try
        {
            using var read = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new PackageDigest(PackageDigest.Sha256, Convert.ToHexString(SHA256.HashData(read))).Matches(installed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
