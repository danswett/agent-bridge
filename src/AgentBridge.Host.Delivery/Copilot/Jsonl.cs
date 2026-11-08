using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace AgentBridge.Host.Delivery.Copilot;

internal delegate void JsonlLine(ReadOnlySpan<byte> line, long start);

// Reads a transcript a line at a time without parsing it: callers look for a few markers in
// the bytes and parse only the lines that carry them, so a transcript of hundreds of
// megabytes costs a scan, not a parse. Only reads (FileShare.ReadWrite | Delete: the CLI
// keeps writing).
internal static class Jsonl
{
    const int MaxLineBytes = 32 << 20;

    public static long Length(string path)
    {
        try { return new FileInfo(path).Length; }
        // A file deleted while another handle is still open is, on Windows, access denied (not
        // gone) until that handle closes; it reads as empty, and Lines then says it cannot be read.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    // Every complete line from `from`, with the offset it starts at; a line longer than any
    // event is skipped. A window starting inside a line (skipFirst) skips that partial line.
    // Returns the offset just after the last complete line, so a trailing line still being
    // written is read whole next time; null when the file cannot be opened or read. With a
    // mark, only the file it was taken of is read on, and the mark moves to what was read: a
    // file that is not that one any more reads nothing, null, with the mark Replaced.
    public static long? Lines(string path, long from, bool skipFirst, JsonlLine line, JsonlMark? mark = null)
    {
        using var stream = Open(path);
        if (stream is null) { return null; }
        // A read that fails once the file is open is a file that cannot be read, as one that
        // cannot be opened is, not an exception for the caller (#510 review, Copilot 4479319354).
        try
        {
            if (mark is not null && !mark.Holds(stream)) { return null; }
            var to = Read(stream, from, skipFirst, line);
            mark?.Take(stream, to);
            return to;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static long Read(FileStream stream, long from, bool skipFirst, JsonlLine line)
    {
        stream.Seek(from, SeekOrigin.Begin);
        var skipping = skipFirst && from > 0;
        var buffer = new List<byte>(4096);
        var oversized = false;
        var chunk = new byte[64 * 1024];
        long position = from, start = from, scannedTo = from;
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var b = chunk[i];
                position++;
                if (b != (byte)'\n')
                {
                    if (!skipping && !oversized)
                    {
                        if (buffer.Count >= MaxLineBytes) { oversized = true; buffer.Clear(); }
                        else { buffer.Add(b); }
                    }
                    continue;
                }
                if (skipping) { skipping = false; }
                else if (!oversized) { line(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer), start); }
                buffer.Clear();
                oversized = false;
                scannedTo = position;
                start = position;
            }
        }
        return scannedTo;
    }

    internal static FileStream? Open(string path)
    {
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // A record's own "timestamp" (ISO 8601, UTC: Copilot, Claude Code and Codex all write one at
    // the top of each line), or null. For timings only.
    public static DateTimeOffset? Timestamp(System.Text.Json.JsonElement record) =>
        record.ValueKind == System.Text.Json.JsonValueKind.Object
        && record.TryGetProperty("timestamp", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String
        && DateTimeOffset.TryParse(t.GetString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var at)
            ? at : null;
}

// Which file a transcript read on from where an earlier read stopped is, so a file put in its
// place is never read as more of it (#510 review, Copilot 4484702591). Copilot only appends, so
// the watches before each write of an answer read only what was added; one checking only that
// the file was no shorter took a replacement of the same length, another call's transcript among
// them, for the one read, kept the question it had found asking, and let the rest of the keys go
// to whatever that file's session showed. Taken of an open file at the offset read to: its
// identity (the volume serial and file index on Windows, the device and inode on Linux), its
// first bytes and the bytes just before that offset, each window up to 4 KiB, so a check costs
// two small reads however long the transcript. A file of another identity, shorter than that
// offset, or with other bytes in either window is not the one read: Replaced, for good, until
// Reset. Rewritten in place, with the same identity and length, and changed only between those
// windows, it is not told apart: Copilot rewrites no transcript, and a session started again
// starts its own.
internal sealed class JsonlMark
{
    const int Window = 4096;
    bool taken;
    string? id;
    long at;
    byte[] head = [];
    byte[] edge = [];

    public bool Replaced { get; private set; }

    // A mark of the file as it is now, up to `at`, for a watch reading on from where another
    // read stopped. One that cannot be taken (the file missing, or shorter than that) is
    // Replaced: what was read there is not there to read on from.
    public static JsonlMark Of(string path, long at)
    {
        var mark = new JsonlMark();
        using var stream = Jsonl.Open(path);
        try
        {
            if (stream is not null && stream.Length >= at) { mark.Take(stream, at); return mark; }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        (mark.taken, mark.Replaced) = (true, true);
        return mark;
    }

    // Forgets the file, and that it was replaced: the next read is a first one.
    public void Reset() => (taken, Replaced, id, at, head, edge) = (false, false, null, 0, [], []);

    // Whether the open file is the one marked; nothing marked yet holds.
    internal bool Holds(FileStream stream)
    {
        if (!taken) { return true; }
        if (!Replaced)
        {
            try
            {
                Replaced = stream.Length < at || Identity(stream.SafeFileHandle) != id
                    || !Hash(stream, 0, Math.Min(Window, at)).AsSpan().SequenceEqual(head)
                    || !Hash(stream, Math.Max(0, at - Window), at).AsSpan().SequenceEqual(edge);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Replaced = true; }
        }
        return !Replaced;
    }

    internal void Take(FileStream stream, long to)
    {
        (id, at) = (Identity(stream.SafeFileHandle), to);
        head = Hash(stream, 0, Math.Min(Window, to));
        edge = Hash(stream, Math.Max(0, to - Window), to);
        taken = true;
    }

    static byte[] Hash(FileStream stream, long from, long to)
    {
        var bytes = new byte[to - from];
        stream.Seek(from, SeekOrigin.Begin);
        stream.ReadExactly(bytes);
        return SHA256.HashData(bytes);
    }

    // The file's identity, or null where it cannot be read, which then matches only another
    // that cannot be read either, and the windows decide.
    static string? Identity(SafeFileHandle handle)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return GetFileInformationByHandle(handle, out var info)
                    ? $"{info.VolumeSerialNumber:x8}:{info.FileIndexHigh:x8}{info.FileIndexLow:x8}" : null;
            }
            if (!OperatingSystem.IsLinux()) { return null; }
            // Linux's struct stat (x64 and arm64 alike) begins with a 64-bit st_dev and st_ino.
            var stat = new byte[256];
            if (FStat((int)handle.DangerousGetHandle(), stat) != 0) { return null; }
            return $"{BitConverter.ToUInt64(stat, 0):x}:{BitConverter.ToUInt64(stat, 8):x}";
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { return null; }
    }

    // BY_HANDLE_FILE_INFORMATION: each FILETIME is two DWORDs, so nothing in it is padded.
    [StructLayout(LayoutKind.Sequential)]
    struct FileInformation
    {
        public uint FileAttributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    static extern int FStat(int descriptor, byte[] stat);
}
