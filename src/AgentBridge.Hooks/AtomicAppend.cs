using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentBridge.Hooks;

// Appends a whole record to a file that other processes append to at the same time.
//
// Two hooks finishing together both open the log, both see the same end, and on Windows a
// FileStream opened for FileMode.Append writes at the offset it saw when it opened: the later
// write lands on top of the earlier one, and a Codex hook's unspooled record (the one approval
// reconciliation needs) can vanish. A handle with FILE_APPEND_DATA and no FILE_WRITE_DATA has
// every write placed at the end of file by the file system, which is what the Go hook's
// os.O_APPEND opens, so both hooks append the same way. .NET opens FileMode.Append without
// O_APPEND elsewhere, so the race remains on macOS and Linux, where intake does not run yet
// (docs/host-hooks.md); there the record is at least one write.
internal static partial class AtomicAppend
{
    const uint FileAppendData = 0x0004;
    const uint Synchronize = 0x0010_0000;
    const uint ShareAll = 0x1 | 0x2 | 0x4;
    const uint OpenAlways = 4;
    const uint Normal = 0x80;

    // One write of the whole record. False if it could not be written in full.
    public static bool Write(string path, ReadOnlySpan<byte> record)
    {
        using var appender = Open(path);
        return appender.Append(record);
    }

    // Open to append; each Append lands at the end of file as it is then.
    internal static Appender Open(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.ReadWrite | FileShare.Delete, BufferSize = 0 };
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            return new Appender(null, new FileStream(path, options));
        }
        var handle = CreateFile(path, FileAppendData | Synchronize, ShareAll, 0, OpenAlways, Normal, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException($"{path} could not be opened to append", error);
        }
        return new Appender(handle, null);
    }

    internal sealed class Appender(SafeFileHandle? handle, FileStream? stream) : IDisposable
    {
        public unsafe bool Append(ReadOnlySpan<byte> record)
        {
            if (stream is not null) { stream.Write(record); return true; }
            fixed (byte* bytes = record)
            {
                return WriteFile(handle!, bytes, (uint)record.Length, out var written, 0) && written == (uint)record.Length;
            }
        }

        public void Dispose()
        {
            handle?.Dispose();
            stream?.Dispose();
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool WriteFile(SafeFileHandle handle, byte* buffer, uint count, out uint written, nint overlapped);
}
