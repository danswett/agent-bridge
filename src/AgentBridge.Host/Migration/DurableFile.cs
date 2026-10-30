using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentBridge.Host.Migration;

// How migrate and rollback write files, so that neither a crash nor an agent writing at the same
// time costs anyone their settings.
//
// * Durable: each write is flushed to the disk (Flush(true)) before it is renamed into place, so
//   a power cut cannot leave a settings file, a snapshot or migration.json renamed but zero-filled.
// * Through links, as agent-ha-bridge's Set-Content wrote: a symbolic link (a dotfiles checkout,
//   say) is resolved and its target written, so the link stays a link. A file with more than one
//   hard link is refused (migrate refuses it in preflight). Any other file is replaced keeping its
//   ACL and attributes (File.Replace).
// * Checked: a settings file is re-read immediately before it is written, and refused
//   (ChangedMeanwhileException) unless it is still what the change was planned from. An agent
//   already on its new hook keeps running and may write its settings at any moment.
public static class DurableFile
{
    public sealed class ChangedMeanwhileException(string path)
        : IOException($"{path} changed while it was being rewritten (an agent may have written it); it was left as it is");

    // A new file of this product's own (a snapshot, a record): written whole, flushed, renamed.
    public static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Flushed(partial, bytes);
            File.Move(partial, path, overwrite: true);
        }
        finally { Delete(partial); }
    }

    // Replaces a file another program owns, if it still hashes to expected (null: if it is still
    // absent).
    public static void Replace(string path, byte[] bytes, string? expected)
    {
        Check(path, expected);
        var target = Target(path);
        // A rename would leave the other names on the old contents, and rewriting in place could
        // leave every name truncated if cut short. Migrate refuses such a file in preflight; one
        // made since is left for the person, its contents in the snapshot.
        if (LinkCount(target) > 1) { throw new IOException($"{path} has another hard link, so it is not rewritten; restore it by hand"); }
        var directory = Path.GetDirectoryName(target)!;
        if (!File.Exists(target))
        {
            Directory.CreateDirectory(directory);
            var staged = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
            try
            {
                Flushed(staged, bytes);
                BeforeCommit.Value?.Invoke(target);
                // Never over a file created since the check.
                try { File.Move(staged, target, overwrite: false); }
                catch (IOException) when (File.Exists(target)) { throw new ChangedMeanwhileException(path); }
            }
            finally { Delete(staged); }
            return;
        }
        var partial = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        var displaced = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.old");
        // Deleted only once known to be what was planned from: otherwise it may be the only copy
        // of a save that landed meanwhile.
        var planned = false;
        try
        {
            Flushed(partial, bytes);
            BeforeCommit.Value?.Invoke(target);
            // What this replaces is kept aside and checked: a save that landed after the check
            // above is put back, rather than lost under this write.
            File.Replace(partial, target, displaced, ignoreMetadataErrors: true);
            planned = MigrationRecord.Hash(displaced) == expected;
            if (!planned)
            {
                AfterReplace.Value?.Invoke(target);
                try { File.Replace(displaced, target, destinationBackupFileName: null, ignoreMetadataErrors: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    throw new IOException($"{path} changed while it was being rewritten, and the save that landed could not be put back ({e.Message}); it is in {displaced}", e);
                }
                throw new ChangedMeanwhileException(path);
            }
        }
        finally
        {
            Delete(partial);
            if (planned) { Delete(displaced); }
        }
    }

    // Deletes a file another program owns, if it still hashes to expected. A link is removed,
    // not its target, as Remove-Item does. Moved aside first and checked, as Replace does.
    public static void Remove(string path, string? expected)
    {
        Check(path, expected);
        if (!File.Exists(path) && new FileInfo(path).LinkTarget is null) { return; }
        var displaced = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.old");
        BeforeCommit.Value?.Invoke(path);
        File.Move(path, displaced);
        if (MigrationRecord.Hash(displaced) != expected)
        {
            if (File.Exists(path)) { throw new IOException($"{path} changed while it was being removed, and was written again since; the save that landed is in {displaced}"); }
            File.Move(displaced, path);
            throw new ChangedMeanwhileException(path);
        }
        Delete(displaced);
    }

    // For tests: runs between the check and the commit, as a save landing there would; and before
    // a save found displaced is put back, as a reader holding the file would.
    internal static readonly AsyncLocal<Action<string>?> BeforeCommit = new();
    internal static readonly AsyncLocal<Action<string>?> AfterReplace = new();

    static void Check(string path, string? expected)
    {
        if (MigrationRecord.Hash(path) != expected) { throw new ChangedMeanwhileException(path); }
    }

    // The file a write to path lands in: the final target of a symbolic link, or path itself.
    public static string Target(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is null) { return path; }
        return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
    }

    public static bool HardLinked(string path) => File.Exists(path) && LinkCount(Target(path)) > 1;

    // How a file is linked, for the record and the report, or null for a plain file.
    public static string? Link(string path)
    {
        if (!File.Exists(path)) { return null; }
        if (new FileInfo(path).LinkTarget is { } target) { return "symlink:" + target; }
        return LinkCount(path) > 1 ? "hardlink" : null;
    }

    static void Flushed(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    static void Delete(string path)
    {
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // The file's hard links; 1 where it cannot be read (and off Windows, where migration does not
    // run).
    static int LinkCount(string path)
    {
        if (!OperatingSystem.IsWindows()) { return 1; }
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(handle, out var info) ? (int)info.NumberOfLinks : 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 1; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FileInformation
    {
        public uint FileAttributes;
        // Three FILETIMEs, each two DWORDs (4-byte aligned, unlike a long).
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
