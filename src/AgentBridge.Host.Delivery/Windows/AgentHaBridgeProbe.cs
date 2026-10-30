using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace AgentBridge.Host.Delivery.Windows;

// Design G5: this host never types into a session while agent-ha-bridge's daemon is active
// for the same user, because two writers typing into one prompt is how answers get
// doubled or garbled. Read-only: it opens nothing it could hold, and never touches
// agent-ha-bridge's files.
//
// agent-ha-bridge's names (hooks/agent-bridge-daemon.ps1, agent-bridge-supervisor.ps1,
// bridge-install-context.ps1):
//   * the daemon holds Local\AgentBridgeDaemon_<id>, named after its scheduled task;
//   * its supervisor holds Local\CopilotBridgeSupervisor_<id>;
//   * <id> is 32 hex digits from the installation, or absent for a legacy install, whose
//     task was AgentBridgeDaemon (and CopilotBridgeDaemon before that).
// The task's own state says little (it reads Ready while the supervisor it started runs),
// so the mutexes are what count. They are found three ways, and any one is enough:
// listing this logon session's named objects for those names, opening each name derived
// from a registered task, and opening the legacy names. A task found Running counts too.
// If neither the listing nor the task query can be read, the answer is "active": not
// knowing is not permission.
[SupportedOSPlatform("windows")]
public sealed partial class AgentHaBridgeProbe : IAgentHaBridgeProbe
{
    static readonly string[] Prefixes = ["AgentBridgeDaemon", "CopilotBridgeSupervisor", "CopilotBridgeDaemon"];

    [GeneratedRegex("^(AgentBridgeDaemon|CopilotBridgeSupervisor|CopilotBridgeDaemon)(_[0-9A-Fa-f]{1,64})?$")]
    private static partial Regex OwnName();

    [GeneratedRegex("^(AgentBridgeDaemon|CopilotBridgeDaemon)(_[0-9A-Fa-f]{1,64})?$")]
    private static partial Regex TaskName();

    // Whether agent-ha-bridge's daemon itself was seen running: unlike Active, not knowing is not
    // a yes here, and nor is its supervisor or task alone (either can run while the daemon fails
    // to start). Rollback is done only once it has seen the daemon back.
    public bool Seen() => DaemonAmong(Scan().Found);

    internal static bool DaemonAmong(IEnumerable<string> found) =>
        found.Any(n => n.StartsWith("AgentBridgeDaemon", StringComparison.Ordinal) || n.StartsWith("CopilotBridgeDaemon", StringComparison.Ordinal));

    (SortedSet<string> Found, bool Known) Scan()
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var listed = NamedMutexes(found);
        var tasks = Tasks();
        foreach (var name in Prefixes) { Probe(name, found); }
        foreach (var (task, running) in tasks ?? [])
        {
            var id = task.Contains('_') ? task[task.IndexOf('_')..] : "";
            Probe("AgentBridgeDaemon" + id, found);
            Probe("CopilotBridgeSupervisor" + id, found);
            if (running) { found.Add($"task {task} (running)"); }
        }
        return (found, listed || tasks is not null);
    }

    public string? Active()
    {
        var (found, known) = Scan();
        if (found.Count > 0)
        {
            return $"agent-ha-bridge is running for this user ({string.Join(", ", found)}); answer at the machine or through it";
        }
        if (!known)
        {
            return "whether agent-ha-bridge is running for this user could not be checked; answer at the machine";
        }
        return null;
    }

    static void Probe(string name, SortedSet<string> found)
    {
        try
        {
            if (Mutex.TryOpenExisting(@"Local\" + name, out var mutex)) { mutex.Dispose(); found.Add(name); }
        }
        // It exists, under an owner that will not share it: still a running daemon.
        catch (UnauthorizedAccessException) { found.Add(name); }
        catch (Exception e) when (e is IOException or WaitHandleCannotBeOpenedException) { }
    }

    // The mutexes in this logon session's Local namespace (\Sessions\<n>\BaseNamedObjects),
    // listed rather than guessed, so an installation id this host cannot derive is found.
    internal static bool NamedMutexes(ISet<string> found)
    {
        try
        {
            if (!ProcessIdToSessionId((uint)Environment.ProcessId, out var session)) { return false; }
            var directory = session == 0 ? @"\BaseNamedObjects" : $@"\Sessions\{session}\BaseNamedObjects";
            foreach (var (name, type) in ObjectDirectory(directory))
            {
                if (type == "Mutant" && OwnName().IsMatch(name)) { found.Add(name); }
            }
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or ExternalException or DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    // Registered scheduled tasks named like agent-ha-bridge's daemon, with whether each is
    // running now; null when the scheduler could not be asked.
    internal static List<(string Name, bool Running)>? Tasks()
    {
        object? service = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) { return null; }
            service = Activator.CreateInstance(type);
            if (service is null) { return null; }
            Call(service, "Connect");
            var folder = Call(service, "GetFolder", @"\")!;
            // TASK_ENUM_HIDDEN: a hidden task is still the daemon's.
            var tasks = Call(folder, "GetTasks", 1)!;
            var count = (int)Get(tasks, "Count")!;
            var list = new List<(string, bool)>();
            for (var i = 1; i <= count; i++)
            {
                var task = Get(tasks, "Item", i)!;
                var name = (string)Get(task, "Name")!;
                // TASK_STATE_RUNNING
                if (TaskName().IsMatch(name)) { list.Add((name, (int)Get(task, "State")! == 4)); }
                Marshal.ReleaseComObject(task);
            }
            Marshal.ReleaseComObject(tasks);
            Marshal.ReleaseComObject(folder);
            return list;
        }
        catch (Exception e) when (e is COMException or TargetInvocationException or InvalidCastException or MissingMethodException or NullReferenceException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (service is not null && Marshal.IsComObject(service)) { Marshal.ReleaseComObject(service); }
        }
    }

    static object? Call(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);

    static object? Get(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, args);

    static IEnumerable<(string Name, string Type)> ObjectDirectory(string path)
    {
        var entries = new List<(string, string)>();
        var nameBuffer = Marshal.StringToHGlobalUni(path);
        var unicode = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var handle = IntPtr.Zero;
        var buffer = IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(new UnicodeString { Length = (ushort)(path.Length * 2), MaximumLength = (ushort)(path.Length * 2 + 2), Buffer = nameBuffer }, unicode, false);
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), ObjectName = unicode };
            var status = NtOpenDirectoryObject(out handle, DirectoryQuery, ref attributes);
            if (status != 0) { throw new InvalidOperationException($"NtOpenDirectoryObject 0x{status:X8}"); }
            const int Size = 256 * 1024;
            buffer = Marshal.AllocHGlobal(Size);
            uint context = 0;
            var restart = true;
            while (true)
            {
                status = NtQueryDirectoryObject(handle, buffer, Size, false, restart, ref context, out _);
                restart = false;
                if (status == NoMoreEntries) { break; }
                if (status is not (0 or MoreEntries)) { throw new InvalidOperationException($"NtQueryDirectoryObject 0x{status:X8}"); }
                var stride = Marshal.SizeOf<DirectoryInformation>();
                for (var at = buffer; ; at += stride)
                {
                    var entry = Marshal.PtrToStructure<DirectoryInformation>(at);
                    if (entry.Name.Buffer == IntPtr.Zero) { break; }
                    entries.Add((Marshal.PtrToStringUni(entry.Name.Buffer, entry.Name.Length / 2), Marshal.PtrToStringUni(entry.TypeName.Buffer, entry.TypeName.Length / 2)));
                }
                if (status == 0) { break; }
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); }
            if (handle != IntPtr.Zero) { NtClose(handle); }
            Marshal.FreeHGlobal(unicode);
            Marshal.FreeHGlobal(nameBuffer);
        }
        return entries;
    }

    const uint DirectoryQuery = 0x0001;
    const int MoreEntries = 0x00000105;
    const int NoMoreEntries = unchecked((int)0x8000001A);

    [StructLayout(LayoutKind.Sequential)]
    struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DirectoryInformation
    {
        public UnicodeString Name;
        public UnicodeString TypeName;
    }

    [DllImport("ntdll.dll")]
    static extern int NtOpenDirectoryObject(out IntPtr handle, uint access, ref ObjectAttributes attributes);

    [DllImport("ntdll.dll")]
    static extern int NtQueryDirectoryObject(IntPtr handle, IntPtr buffer, int length, [MarshalAs(UnmanagedType.U1)] bool single, [MarshalAs(UnmanagedType.U1)] bool restart, ref uint context, out uint returned);

    [DllImport("ntdll.dll")]
    static extern int NtClose(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
}
