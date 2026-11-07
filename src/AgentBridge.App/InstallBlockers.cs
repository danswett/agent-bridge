using System.Runtime.InteropServices;
using System.Text;
using Velopack.Locators;

namespace AgentBridge.App;

// What outside the installation holds a file in its current folder, which Update.exe has to move
// aside to install an update (#1092): on 2026-11-06 the Windows telemetry service (DiagTrack)
// held current\mscordaccore.dll, the move failed with error 5, and the retries stopped the host
// every three minutes. Asked through the Restart Manager, best effort: names and process ids only,
// and never an exception. Processes running from the installation itself are not blockers: the
// update stops them (the host is stopped for it, and Update.exe ends the rest of the package).
// Unelevated, Restart Manager still lists a service's process; only its path can't be read, and
// a process whose path can't be read is counted as outside.
static class InstallBlockers
{
    const int MoreData = 234;
    const int Batch = 200;
    const int MostNamed = 5;

    // The processes outside the installation holding files in it, as "name (service, pid n)", or
    // null when there are none or it can't be told.
    public static string? Find()
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !VelopackLocator.IsCurrentSet) { return null; }
            if (VelopackLocator.Current.RootAppDir is not { Length: > 0 } root) { return null; }
            var current = Path.Combine(root, "current");
            if (!Directory.Exists(current)) { return null; }
            var files = Directory.EnumerateFiles(current, "*", SearchOption.AllDirectories).ToList();
            var own = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            var found = Held(files).Where(p => p.Pid != Environment.ProcessId && !RunsFrom(p.Pid, own)).ToList();
            if (found.Count == 0) { return null; }
            var names = found.Take(MostNamed).Select(p => p.Service.Length > 0 ? $"{p.Name} ({p.Service}, pid {p.Pid})" : $"{p.Name} (pid {p.Pid})");
            return string.Join(", ", names) + (found.Count > MostNamed ? $" and {found.Count - MostNamed} more" : "");
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException)) { return null; }
    }

    static bool RunsFrom(int pid, string root)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.MainModule?.FileName is { } path && path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException or NotSupportedException) { return false; }
    }

    static List<(int Pid, string Name, string Service)> Held(List<string> files)
    {
        var found = new List<(int, string, string)>();
        var key = new StringBuilder(33);
        if (RmStartSession(out var session, 0, key) != 0) { return found; }
        try
        {
            for (var i = 0; i < files.Count; i += Batch)
            {
                var part = files.Skip(i).Take(Batch).ToArray();
                if (RmRegisterResources(session, (uint)part.Length, part, 0, null, 0, null) != 0) { return found; }
            }
            uint have = 0;
            RmProcessInfo[] info = [];
            while (true)
            {
                var result = RmGetList(session, out var needed, ref have, info, out _);
                if (result == 0) { break; }
                if (result != MoreData) { return found; }
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
