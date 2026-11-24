using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentBridge.App.Core;

/// <summary>
/// A minidump of this process with <c>MiniDumpNormal</c>: thread stacks and the module list only, no heap, so it holds far less of what the app
/// was working on than a full dump (a stack can still hold a value in a local: the dump stays in the owner's own data folder and goes nowhere).
/// Written to <c>%LOCALAPPDATA%\AgentBridge\dumps</c> as <c>AgentBridge-hang-yyyyMMdd-HHmmss-pid.dmp</c>; only the newest three are kept.
/// </summary>
public sealed class WindowsMiniDump(string folder, TimeProvider time, int keep = 3, Func<long>? freeBytes = null) : IHangDumpWriter
{
    const int MiniDumpNormal = 0x00000000;
    const string Prefix = "AgentBridge-hang-";

    /// <summary>A disk with less free than this gets no dump: a few megabytes are not worth the last of it.</summary>
    public const long LeastFree = 1L << 30;

    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool MiniDumpWriteDump(IntPtr process, uint processId, SafeFileHandle file, int dumpType, IntPtr exception, IntPtr user, IntPtr callback);

    public string? Write()
    {
        if (!OperatingSystem.IsWindows()) { return null; }
        Directory.CreateDirectory(folder);
        var free = (freeBytes ?? FreeOnTheDumpDrive)();
        if (free < LeastFree) { throw new HangDumpSkipped($"only {Math.Max(0, free) >> 20} MB of disk is free and a dump needs 1 GB"); }
        using var process = Process.GetCurrentProcess();
        var path = Path.Combine(folder, $"{Prefix}{time.GetUtcNow():yyyyMMdd-HHmmss}-{process.Id}.dmp");
        bool written;
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            written = MiniDumpWriteDump(process.Handle, (uint)process.Id, file.SafeFileHandle, MiniDumpNormal, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
        if (!written)
        {
            try { File.Delete(path); } catch (IOException) { }
            return null;
        }
        Prune();
        return path;
    }

    long FreeOnTheDumpDrive() => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;

    void Prune()
    {
        try
        {
            foreach (var old in Directory.EnumerateFiles(folder, Prefix + "*.dmp").OrderByDescending(f => f, StringComparer.Ordinal).Skip(keep))
            {
                File.Delete(old);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
