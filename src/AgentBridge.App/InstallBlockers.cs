using System.Runtime.InteropServices;
using System.Text;
using AgentBridge.App.Core;
using Velopack.Locators;

namespace AgentBridge.App;

// What outside the installation holds a file in its current folder, which Update.exe has to move
// aside to install an update (#1092): on 2026-11-06 the Windows telemetry service (DiagTrack)
// held current\mscordaccore.dll, the move failed with error 5, and the retries stopped the host
// every three minutes. Asked through the Restart Manager: process identities and confirmed files,
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
    internal readonly record struct Holder(int Pid, long Started, string Name, string Service, string? Image);

    // The processes outside the installation holding files in it, with their confirmed file, or
    // null only when the completed scan found none; a failed scan is explicitly unknown.
    public static string? Find()
    {
        if (!Monitor.TryEnter(scan)) { return InstallationFiles.Unknown; }
        try
        {
            if (!OperatingSystem.IsWindows()) { return null; }
            if (!VelopackLocator.IsCurrentSet) { return InstallationFiles.Unknown; }
            if (VelopackLocator.Current.RootAppDir is not { Length: > 0 } root) { return InstallationFiles.Unknown; }
            return Find(root, () => InstallationFiles.LockedImages(Path.Combine(root, "current")).ToList(), Held);
        }
        catch (InvalidOperationException e)
        {
            AppLog.Write($"installation file holders could not be checked: {e.Message}");
            return InstallationFiles.Unknown;
        }
        finally { Monitor.Exit(scan); }
    }

    internal static string? Find(string root, Func<List<string>> installationFiles, Func<List<string>, List<Holder>> query)
    {
        try
        {
            var files = installationFiles();
            if (files.Count == 0) { return null; }
            var own = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var held = query(files);
            var found = held.Where(p => InstallHolder.Blocks(p.Pid, Environment.ProcessId, p.Image, own, p.Service)).ToList();
            if (found.Count == 0)
            {
                return held.Any(p => p.Pid != Environment.ProcessId && string.IsNullOrWhiteSpace(p.Image) && string.IsNullOrWhiteSpace(p.Service))
                    ? InstallationFiles.Unknown : null;
            }
            var names = new List<string>();
            var unconfirmed = 0;
            foreach (var p in found.Take(MostNamed))
            {
                // One holder that releases or can't be re-queried must not hide another's confirmed file.
                string? file = null;
                try { file = HeldFile(files, p, query); }
                catch (Exception e) when (Unreadable(e)) { AppLog.Write($"installation file holder {p.Name} (pid {p.Pid}) could not be confirmed: {e.Message}"); }
                if (file is null) { unconfirmed++; continue; }
                names.Add($"{p.Name} ({(p.Service.Length > 0 ? p.Service + ", " : "")}pid {p.Pid}) holding {Path.GetRelativePath(root, file)}");
            }
            if (names.Count == 0) { return InstallationFiles.Unknown; }
            return string.Join(", ", names) + (found.Count > MostNamed ? $" and {found.Count - MostNamed} more" : "")
                + (unconfirmed > 0 ? $"; {InstallationFiles.Unknown} for {unconfirmed} other" : "");
        }
        catch (Exception e) when (Unreadable(e))
        {
            AppLog.Write($"installation file holders could not be checked: {e.Message}");
            return InstallationFiles.Unknown;
        }
    }

    static bool Unreadable(Exception e) => e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
        or InvalidOperationException or ArgumentException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException;

    // OpenProcess with limited query rights works for elevated and other users' processes, which
    // Process.MainModule does not; null when the path still can't be read.
    static string? ImagePath(int pid, long started)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) { return null; }
        try
        {
            if (!GetProcessTimes(handle, out var created, out _, out _, out _) || Stamp(created) != started) { return null; }
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    const uint ProcessQueryLimitedInformation = 0x1000;

    // The aggregate RM result has no resource association. Bisect only for confirmed external
    // holders, then verify the single file: listing every registered file would falsely blame it.
    static string? HeldFile(List<string> files, Holder holder, Func<List<string>, List<Holder>> query)
    {
        bool Matches(Holder p) => p.Pid == holder.Pid && p.Started == holder.Started;
        if (files.Count == 1) { return query(files).Any(Matches) ? files[0] : null; }
        var half = files.Count / 2;
        var first = files.Take(half).ToList();
        var matches = query(first).Any(Matches);
        return HeldFile(matches ? first : files.Skip(half).ToList(), holder, query);
    }

    static long Stamp(System.Runtime.InteropServices.ComTypes.FILETIME time) => ((long)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetProcessTimes(IntPtr process, out System.Runtime.InteropServices.ComTypes.FILETIME created,
        out System.Runtime.InteropServices.ComTypes.FILETIME exited, out System.Runtime.InteropServices.ComTypes.FILETIME kernel,
        out System.Runtime.InteropServices.ComTypes.FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);

    static List<Holder> Held(List<string> files)
    {
        var found = new List<Holder>();
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
                var startedAt = Stamp(p.Process.ProcessStartTime);
                if (!found.Any(f => f.Pid == p.Process.ProcessId && f.Started == startedAt))
                {
                    found.Add(new(p.Process.ProcessId, startedAt, p.AppName, p.ServiceShortName, ImagePath(p.Process.ProcessId, startedAt)));
                }
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
