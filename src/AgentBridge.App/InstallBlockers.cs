using System.Runtime.InteropServices;
using System.Text;
using AgentBridge.App.Core;
using Velopack.Locators;

namespace AgentBridge.App;

// What outside the installation holds a file in its current folder, which Update.exe has to move
// aside to install an update (#1092): on 2026-11-06 the Windows telemetry service (DiagTrack)
// held current\mscordaccore.dll, the move failed with error 5, and the retries stopped the host
// every three minutes. Asked through the Restart Manager, best effort: names and process ids only,
// and never an exception. Processes running from the installation itself are not blockers: the
// update stops them (the host is stopped for it, and Update.exe ends the rest of the package).
// Unelevated, Restart Manager still lists a service's process; only its path can't be read by
// MainModule, so it is read with QueryFullProcessImageName. A positive service identity still
// counts if that too fails; an unidentified holder remains unknown, never clear (#2345).
static class InstallBlockers
{
    const int MoreData = 234;
    const int Batch = 200;
    const int MostNamed = 5;
    static readonly object scan = new();

    // The processes outside the installation holding files in it, as "name (service, pid n)", or
    // null only when the completed scan found none; a failed scan is explicitly unknown.
    public static string? Find()
    {
        if (!Monitor.TryEnter(scan)) { return InstallationFiles.Unknown; }
        try
        {
            if (!OperatingSystem.IsWindows()) { return null; }
            if (!VelopackLocator.IsCurrentSet) { return InstallationFiles.Unknown; }
            if (VelopackLocator.Current.RootAppDir is not { Length: > 0 } root) { return InstallationFiles.Unknown; }
            var current = Path.Combine(root, "current");
            if (!Directory.Exists(current)) { return InstallationFiles.Unknown; }
            var files = Directory.EnumerateFiles(current, "*", SearchOption.AllDirectories).ToList();
            var own = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            var held = Held(files).Select(p => (p.Pid, p.Name, p.Service, Image: ImagePath(p.Pid))).ToList();
            var found = held.Where(p => InstallHolder.Blocks(p.Pid, Environment.ProcessId, p.Image, own, p.Service)).ToList();
            if (found.Count == 0)
            {
                return held.Any(p => p.Pid != Environment.ProcessId && string.IsNullOrWhiteSpace(p.Image) && string.IsNullOrWhiteSpace(p.Service))
                    ? InstallationFiles.Unknown : null;
            }
            var names = found.Take(MostNamed).Select(p => p.Service.Length > 0 ? $"{p.Name} ({p.Service}, pid {p.Pid})" : $"{p.Name} (pid {p.Pid})");
            return string.Join(", ", names) + (found.Count > MostNamed ? $" and {found.Count - MostNamed} more" : "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
            or InvalidOperationException or ArgumentException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            AppLog.Write($"installation file holders could not be checked: {e.Message}");
            return InstallationFiles.Unknown;
        }
        finally { Monitor.Exit(scan); }
    }

    // OpenProcess with limited query rights works for elevated and other users' processes, which
    // Process.MainModule does not; null when the path still can't be read.
    static string? ImagePath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) { return null; }
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);

    static List<(int Pid, string Name, string Service)> Held(List<string> files)
    {
        var found = new List<(int, string, string)>();
        var key = new StringBuilder(33);
        var started = RmStartSession(out var session, 0, key);
        if (started != 0) { throw new System.ComponentModel.Win32Exception(started); }
        try
        {
            for (var i = 0; i < files.Count; i += Batch)
            {
                var part = files.Skip(i).Take(Batch).ToArray();
                var registered = RmRegisterResources(session, (uint)part.Length, part, 0, null, 0, null);
                if (registered != 0) { throw new System.ComponentModel.Win32Exception(registered); }
            }
            uint have = 0;
            RmProcessInfo[] info = [];
            var attempts = 0;
            while (true)
            {
                var result = RmGetList(session, out var needed, ref have, info, out _);
                if (result == 0) { break; }
                if (result != MoreData) { throw new System.ComponentModel.Win32Exception(result); }
                if (++attempts > 5) { throw new InvalidOperationException("the Windows file-holder list kept changing"); }
                info = new RmProcessInfo[needed];
                have = needed;
            }
            foreach (var p in info.Take((int)have))
            {
                if (!found.Any(f => f.Item1 == p.Process.ProcessId)) { found.Add((p.Process.ProcessId, p.AppName, p.ServiceShortName)); }
            }
            return found;
        }
        finally { RmEndSession(session); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmStartSession(out uint session, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(uint session, uint files, string[] names, uint applications, RmUniqueProcess[]? apps, uint services, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[] info, out uint reasons);

    [DllImport("rstrtmgr.dll")]
    static extern int RmEndSession(uint session);
}
