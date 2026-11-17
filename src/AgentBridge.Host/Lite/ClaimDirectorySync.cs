using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentBridge.Host.Lite;

// A flushed file alone does not make its rename or a newly created directory durable.
internal static class ClaimDirectorySync
{
    internal static readonly AsyncLocal<Action<string>?> BeforeFlush = new();

    public static void Flush(string path)
    {
        BeforeFlush.Value?.Invoke(path);
        if (OperatingSystem.IsWindows())
        {
            using var handle = CreateFileW(path, 0x40000000, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle.IsInvalid || !FlushFileBuffers(handle)) { throw Failure(path); }
            return;
        }
        var fd = open(path, 0);
        if (fd < 0) { throw Failure(path); }
        try { if (fsync(fd) != 0) { throw Failure(path); } }
        finally { close(fd); }
    }

    static IOException Failure(string path) => new($"Could not flush reply-claim directory '{path}'.", new Win32Exception(Marshal.GetLastPInvokeError()));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FlushFileBuffers(SafeFileHandle handle);
    [DllImport("libc", SetLastError = true)] static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] static extern int fsync(int fd);
    [DllImport("libc", SetLastError = true)] static extern int close(int fd);
}
