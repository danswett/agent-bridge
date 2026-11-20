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
        if (Directory.Exists(backup)) { throw new InPlaceApplyException($"an earlier install's backup is still at {backup}; nothing was installed"); }
        var moved = new List<string>();
        var written = new List<string>();
        try
        {
            foreach (var rel in Files(current).Where(r => !keep.Contains(r)))
            {
                var to = Path.Combine(backup, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(Path.Combine(current, rel), to);
                moved.Add(rel);
            }
            foreach (var rel in next.Where(r => !keep.Contains(r)))
            {
                var to = Path.Combine(current, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(Path.Combine(staged, rel), to);
                written.Add(rel);
            }
            if (next.FirstOrDefault(rel => !Same(Path.Combine(staged, rel), Path.Combine(current, rel))) is { } bad)
            {
                throw new IOException($"current{Path.DirectorySeparatorChar}{bad} is not the new version's bytes after copying");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var back = Restore(current, backup, written, moved);
            throw new InPlaceApplyException(back
                ? $"the in-place install failed and the old version was put back; nothing was installed: {e.Message}"
                : $"the in-place install failed and the old version could not all be put back (what is missing is in {backup}); nothing was installed: {e.Message}", e);
        }
        try { Directory.Delete(backup, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // Undo in reverse: the new files out, the old ones back. False when any could not be.
    static bool Restore(string current, string backup, List<string> written, List<string> moved)
    {
        var whole = true;
        foreach (var rel in written)
        {
            try { File.Delete(Path.Combine(current, rel)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { whole = false; }
        }
        foreach (var rel in moved)
        {
            try { File.Move(Path.Combine(backup, rel), Path.Combine(current, rel)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { whole = false; }
        }
        if (whole)
        {
            try { Directory.Delete(backup, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return whole;
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
