using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace AgentBridge.Local;

// One process in a chain of ancestors: its id, when it started (which tells it from a later
// process given the same id), and the full path of its executable, each null when the OS would
// not say.
public sealed record PeerProcess(int Id, DateTimeOffset? StartedAt, string? Image);

// A process and its ancestors as far as they could be read. Complete is false when the walk
// stopped at a process it could not read, rather than at the top of the tree, a parent that
// has gone, or one given a reused id: then an agent session may sit above what was read, and
// whatever only a person may do is refused (fails closed). An agent found in what was read
// still counts.
public sealed record ProcessAncestry(IReadOnlyList<PeerProcess> Processes, bool Complete)
{
    public static readonly ProcessAncestry Unknown = new([], false);
}

// A process and the processes that started it, nearest first: who is really on the other end
// of the pipe, and what it is running inside. Only reads (query-limited handles, /proc, ps).
public static class ProcessChain
{
    const int MaxDepth = 32;

    public static IReadOnlyList<PeerProcess> Of(int processId) => Read(processId).Processes;

    public static ProcessAncestry Read(int processId)
    {
        try
        {
            if (OperatingSystem.IsWindows()) { return Windows(processId); }
            if (OperatingSystem.IsLinux()) { return Linux(processId); }
            if (OperatingSystem.IsMacOS()) { return Mac(processId); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or FormatException) { }
        return ProcessAncestry.Unknown;
    }

    // One process as a whole-table listing gives it: its parent, start and image.
    internal sealed record Listed(int Parent, DateTimeOffset? StartedAt, string? Image);

    // Walks a table of every process from one to the top. Each step's parent must be in the
    // table and have started no later than its child (a second of slack): one that has gone, or
    // whose id was given to a later process, ends the chain there, which is its natural end.
    // Stopping at MaxDepth with a parent still to read is not: an agent may be above it.
    internal static ProcessAncestry Walk(int processId, IReadOnlyDictionary<int, Listed> table)
    {
        if (!table.TryGetValue(processId, out var self)) { return ProcessAncestry.Unknown; }
        var chain = new List<PeerProcess> { new(processId, self.StartedAt, self.Image) };
        var (current, entry) = (processId, self);
        while (chain.Count < MaxDepth && entry.Parent > 0 && entry.Parent != current && table.TryGetValue(entry.Parent, out var parent))
        {
            if (entry.StartedAt is { } c && parent.StartedAt is { } p && p > c + TimeSpan.FromSeconds(1)) { break; }
            chain.Add(new PeerProcess(entry.Parent, parent.StartedAt, parent.Image));
            (current, entry) = (entry.Parent, parent);
        }
        var cut = chain.Count >= MaxDepth && entry.Parent > 0 && entry.Parent != current && table.ContainsKey(entry.Parent);
        return new ProcessAncestry(chain, !cut);
    }

    // A Toolhelp snapshot gives every process's parent and executable name in one read; the
    // full path and start time come from a query-limited handle, and a process that will not
    // open keeps its name from the snapshot, so it can still be recognised.
    [SupportedOSPlatform("windows")]
    static ProcessAncestry Windows(int processId)
    {
        var snapshot = Snapshot();
        if (snapshot is null) { return ProcessAncestry.Unknown; }
        var table = new Dictionary<int, Listed>();
        // Only the walk's own steps are opened, and one beyond, so Walk can tell a chain cut at
        // MaxDepth from one that ends there.
        var current = processId;
        for (var depth = 0; depth <= MaxDepth && snapshot.TryGetValue(current, out var entry) && !table.ContainsKey(current); depth++)
        {
            var (started, image) = Open(current);
            table[current] = new Listed(entry.Parent, started, image ?? entry.Name);
            current = entry.Parent;
        }
        return Walk(processId, table);
    }

    [SupportedOSPlatform("windows")]
    static Dictionary<int, (int Parent, string Name)>? Snapshot()
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == -1) { return null; }
        try
        {
            var map = new Dictionary<int, (int, string)>();
            var entry = new ProcessEntry32 { Size = Marshal.SizeOf<ProcessEntry32>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                map[(int)entry.ProcessId] = ((int)entry.ParentProcessId, entry.ExeFile);
            }
            return map;
        }
        finally { CloseHandle(snapshot); }
    }

    [SupportedOSPlatform("windows")]
    static (DateTimeOffset? Started, string? Image) Open(int processId)
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
    // may itself hold spaces and parentheses. A process that has gone ends the chain; one whose
    // stat cannot be read (hidepid, say) stops it with what was read so far, incomplete. The
    // command name stands in for an executable path the kernel will not show (another user's).
    static ProcessAncestry Linux(int processId)
    {
        var chain = new List<PeerProcess>();
        var current = processId;
        DateTimeOffset? childStarted = null;
        while (chain.Count < MaxDepth && current > 0)
        {
            string stat;
            try { stat = File.ReadAllText($"/proc/{current}/stat"); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return new ProcessAncestry(chain, chain.Count > 0); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new ProcessAncestry(chain, false); }
            var open = stat.IndexOf('(');
            var close = stat.LastIndexOf(')');
            if (open < 0 || close < open) { return new ProcessAncestry(chain, false); }
            var fields = stat[(close + 2)..].Split(' ');
            if (fields.Length < 2 || !int.TryParse(fields[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parent))
            {
                return new ProcessAncestry(chain, false);
            }
            DateTimeOffset? started = null;
            try { using var p = System.Diagnostics.Process.GetProcessById(current); started = new DateTimeOffset(p.StartTime.ToUniversalTime(), TimeSpan.Zero); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { }
            if (chain.Count > 0 && childStarted is { } c && started is { } s && s > c + TimeSpan.FromSeconds(1)) { break; }
            string? image = null;
            try { image = File.ResolveLinkTarget($"/proc/{current}/exe", returnFinalTarget: true)?.FullName; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            chain.Add(new PeerProcess(current, started, image ?? stat[(open + 1)..close]));
            if (parent == current) { break; }
            (current, childStarted) = (parent, started);
        }
        // Cut at MaxDepth with an ancestor still unread (current is it).
        return new ProcessAncestry(chain, !(chain.Count >= MaxDepth && current > 0));
    }

    // macOS has no /proc. ps lists every process, the user's and others', with its parent, start
    // and executable in one call; it is what ProcessInspector already reads command lines with.
    static ProcessAncestry Mac(int processId)
    {
        var start = new System.Diagnostics.ProcessStartInfo("/bin/ps", ["-axww", "-o", "pid=,ppid=,lstart=,comm="])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // lstart in the C locale's fixed form, whatever the user's.
        start.Environment["LC_ALL"] = "C";
        using var ps = System.Diagnostics.Process.Start(start);
        if (ps is null) { return ProcessAncestry.Unknown; }
        var text = ps.StandardOutput.ReadToEnd();
        if (!ps.WaitForExit(5000) || ps.ExitCode != 0) { return ProcessAncestry.Unknown; }
        return Walk(processId, ParsePs(text));
    }

    // `ps -o pid=,ppid=,lstart=,comm=` in the C locale: "  412     1 Thu Oct 29 17:05:16 2026     /usr/sbin/sshd".
    // lstart is local time. A line that does not read is left out, which ends a walk there.
    internal static Dictionary<int, Listed> ParsePs(string text)
    {
        var table = new Dictionary<int, Listed>();
        foreach (var line in text.Split('\n'))
        {
            var t = line.TrimEnd('\r').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 8) { continue; }
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            if (!int.TryParse(t[0], System.Globalization.NumberStyles.None, invariant, out var pid) || !int.TryParse(t[1], System.Globalization.NumberStyles.None, invariant, out var parent)) { continue; }
            DateTimeOffset? started = DateTime.TryParseExact($"{t[3]} {t[4]} {t[6]} {t[5]}", "MMM d yyyy HH:mm:ss", invariant, System.Globalization.DateTimeStyles.AssumeLocal, out var local)
                ? new DateTimeOffset(local).ToUniversalTime()
                : null;
            // comm is the rest of the line, and may hold spaces ("/Applications/Visual Studio Code.app/...").
            var comm = string.Join(' ', t[7..]);
            table[pid] = new Listed(parent, started, comm);
        }
        return table;
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
