using System.Runtime.InteropServices;

namespace AgentBridge.Hooks;

// The hook's process ancestry, nearest first, recorded while those processes are certain to be
// alive: the owning agent process is found from it later, when the shells in between have
// exited. As agent-bridge-hook's parentChain (ancestry_windows.go, walk.go).
public static partial class Ancestry
{
    // The chain from this process's parent upwards.
    public static int[] OfParent(int max = SpoolContract.MaxAncestors)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Process by process: opening twelve processes costs microseconds, where a
                // snapshot of every process on a busy machine measured 30-45 ms, most of the
                // hook's whole budget. The snapshot is kept for when this process's own parent
                // cannot be asked.
                if (WindowsParent(Environment.ProcessId) is { } direct && direct > 0)
                {
                    return Walk(direct, max, WindowsParent);
                }
                var parents = WindowsParents();
                return parents.TryGetValue(Environment.ProcessId, out var parent) && parent > 0
                    ? Walk(parent, max, p => parents.TryGetValue(p, out var q) ? q : null)
                    : [];
            }
            if (OperatingSystem.IsLinux())
            {
                return LinuxParent(Environment.ProcessId) is { } parent && parent > 0 ? Walk(parent, max, LinuxParent) : [];
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ExternalException or DllNotFoundException or EntryPointNotFoundException) { }
        // macOS: none yet (the hook's caller is still recorded by the agent's own hook payload
        // where it has one). The host ships on Windows first; see docs/host-hooks.md.
        return [];
    }

    // Follows parent links from pid, for at most max processes. Stops at a process it cannot
    // look up, at the root, and at a loop.
    public static int[] Walk(int pid, int max, Func<int, int?> parentOf)
    {
        var chain = new List<int>();
        var seen = new HashSet<int>();
        var current = pid;
        while (chain.Count < max && current > 0 && seen.Add(current))
        {
            chain.Add(current);
            if (parentOf(current) is not { } parent || parent == current) { break; }
            current = parent;
        }
        return [.. chain];
    }

    static int? LinuxParent(int pid)
    {
        string stat;
        try { stat = File.ReadAllText($"/proc/{pid}/stat"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        // The name, in parentheses, may itself hold spaces; the fields after it do not.
        var close = stat.LastIndexOf(')');
        if (close < 0) { return null; }
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 2 && int.TryParse(fields[1], out var parent) ? parent : null;
    }

    // The parent a process was started by, or null when it has gone or cannot be asked.
    static unsafe int? WindowsParent(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, (uint)pid);
        if (handle == 0) { return null; }
        try
        {
            ProcessBasicInformation info;
            var status = NtQueryInformationProcess(handle, 0, &info, (uint)sizeof(ProcessBasicInformation), out _);
            return status == 0 ? (int)info.InheritedFromUniqueProcessId : null;
        }
        finally { CloseHandle(handle); }
    }

    static Dictionary<int, int> WindowsParents()
    {
        var parents = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle) { return parents; }
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (var ok = Process32First(snapshot, ref entry); ok; ok = Process32Next(snapshot, ref entry))
            {
                parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            }
        }
        finally { CloseHandle(snapshot); }
        return parents;
    }

    const uint SnapProcess = 0x00000002;
    const uint QueryLimitedInformation = 0x1000;
    static readonly nint InvalidHandle = -1;

    // PROCESS_BASIC_INFORMATION, every field pointer-sized as it is laid out on 64 and 32 bits.
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQueryInformationProcess(nint process, int informationClass, void* information, uint length, out uint returned);

    // PROCESSENTRY32W, blittable: the name is UTF-16 code units this never reads.
    [StructLayout(LayoutKind.Sequential)]
    unsafe struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriClassBase;
        public uint Flags;
        public fixed ushort ExeFile[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32First(nint snapshot, ref ProcessEntry32 entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32Next(nint snapshot, ref ProcessEntry32 entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
