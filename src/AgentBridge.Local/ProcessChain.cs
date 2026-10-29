using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace AgentBridge.Local;

// One process in a chain of ancestors: its id, when it started (which tells it from a later
// process given the same id), and the full path of its executable, each null when the OS would
// not say.
public sealed record PeerProcess(int Id, DateTimeOffset? StartedAt, string? Image);

// A process and the processes that started it, nearest first: who is really on the other end
// of the pipe, and what it is running inside. Only reads (query-limited handles).
public static class ProcessChain
{
    const int MaxDepth = 32;

    // Empty when the process cannot be read at all, or on a platform this cannot trace yet.
    public static IReadOnlyList<PeerProcess> Of(int processId)
    {
        try
        {
            if (OperatingSystem.IsWindows()) { return Windows(processId); }
            if (OperatingSystem.IsLinux()) { return Linux(processId); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or FormatException) { }
        return [];
    }

    [SupportedOSPlatform("windows")]
    static List<PeerProcess> Windows(int processId)
    {
        var parents = Parents();
        var chain = new List<PeerProcess>();
        if (parents is null) { return chain; }
        var current = processId;
        var (started, image) = Read(current);
        if (started is null && image is null && !parents.ContainsKey(current)) { return chain; }
        chain.Add(new PeerProcess(current, started, image));
        while (chain.Count < MaxDepth && parents.TryGetValue(current, out var parent) && parent != 0 && parent != current)
        {
            var (parentStarted, parentImage) = Read(parent);
            // The parent's id may since have been given to another process; a parent cannot
            // have started after its child, so the chain ends there. One whose start cannot be
            // read is kept: a link that cannot be disproved is not dropped.
            if (started is { } c && parentStarted is { } p && p > c + TimeSpan.FromSeconds(1)) { break; }
            if (!parents.ContainsKey(parent) && parentStarted is null) { break; }
            chain.Add(new PeerProcess(parent, parentStarted, parentImage));
            (current, started) = (parent, parentStarted);
        }
        return chain;
    }

    [SupportedOSPlatform("windows")]
    static Dictionary<int, int>? Parents()
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == -1) { return null; }
        try
        {
            var map = new Dictionary<int, int>();
            var entry = new ProcessEntry32 { Size = Marshal.SizeOf<ProcessEntry32>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                map[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            }
            return map;
        }
        finally { CloseHandle(snapshot); }
    }

    [SupportedOSPlatform("windows")]
    static (DateTimeOffset? Started, string? Image) Read(int processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process == 0) { return (null, null); }
        try
        {
            DateTimeOffset? started = GetProcessTimes(process, out var created, out _, out _, out _) ? DateTimeOffset.FromFileTime(created).ToUniversalTime() : null;
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            var image = QueryFullProcessImageNameW(process, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
            return (started, image);
        }
        finally { CloseHandle(process); }
    }

    // /proc/<pid>/stat: the parent is the fourth field, after the command in parentheses, which
    // may itself hold spaces and parentheses.
    static List<PeerProcess> Linux(int processId)
    {
        var chain = new List<PeerProcess>();
        var current = processId;
        DateTimeOffset? childStarted = null;
        while (chain.Count < MaxDepth && current > 0)
        {
            var stat = File.ReadAllText($"/proc/{current}/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            var parent = int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture);
            DateTimeOffset? started = null;
            try { using var p = System.Diagnostics.Process.GetProcessById(current); started = new DateTimeOffset(p.StartTime.ToUniversalTime(), TimeSpan.Zero); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { }
            if (chain.Count > 0 && childStarted is { } c && started is { } s && s > c + TimeSpan.FromSeconds(1)) { break; }
            string? image = null;
            try { image = File.ResolveLinkTarget($"/proc/{current}/exe", returnFinalTarget: true)?.FullName; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            chain.Add(new PeerProcess(current, started, image));
            if (parent == current) { break; }
            (current, childStarted) = (parent, started);
        }
        return chain;
    }

    const uint ProcessQueryLimitedInformation = 0x1000;
    const uint Th32csSnapProcess = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry32
    {
        public int Size;
        public uint Usage, ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool Process32FirstW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool Process32NextW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nint OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageNameW(nint process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(nint handle);
}
