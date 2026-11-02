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

    // agent-ha-bridge's Dev Box keep-awake: its task, AgentBridgeDevBoxKeepAwake_<id> (or without
    // the id on a pre-metadata install), and the mutex of the same name each pass holds.
    [GeneratedRegex("^AgentBridgeDevBoxKeepAwake(_[0-9A-Fa-f]{1,64})?$")]
    private static partial Regex KeepAwakeName();

    // Why agent-ha-bridge's own Dev Box keep-awake owns this Dev Box's schedule now, or null when
    // this host may: any of its tasks enabled or running, or a pass of it holding its mutex. Two
    // writers to one schedule is the double-writer problem G5 exists for, so a host beside
    // agent-ha-bridge (read-only or not) stands back while it is on; migrate disables the task and
    // the host takes over, and rollback enables it again. Not being able to read the scheduler is
    // not permission.
    public string? KeepAwakeActive()
    {
        var tasks = KeepAwakeTasks();
        var passes = new SortedSet<string>(StringComparer.Ordinal);
        NamedMutexes(passes, KeepAwakeName());
        Probe("AgentBridgeDevBoxKeepAwake", passes);
        foreach (var (name, _, _) in tasks ?? []) { Probe(name, passes); }
        return KeepAwakeReason(tasks, passes);
    }

    internal static string? KeepAwakeReason(IReadOnlyList<(string Name, bool Enabled, bool Running)>? tasks, IEnumerable<string> passes)
    {
        var owning = (tasks ?? []).Where(t => t.Enabled || t.Running).Select(t => $"task {t.Name} ({(t.Running ? "running" : "enabled")})")
            .Concat(passes.Select(p => $"{p} (a pass is running)")).Distinct().ToList();
        if (owning.Count > 0)
        {
            return $"agent-ha-bridge's own Dev Box keep-awake is on here ({string.Join(", ", owning)}), so it keeps this Dev Box awake and this host stands back";
        }
        return tasks is null ? "whether agent-ha-bridge's own Dev Box keep-awake is on could not be checked, so this host stands back rather than be a second writer to the schedule" : null;
    }

    // For as long as a pass of this host's runs, the mutex each of agent-ha-bridge's keep-awake
    // passes takes (Local\<its task name>) for every keep-awake task registered here, so that a
    // pass of it started meanwhile (a rollback re-enables and starts its task) finds one running
    // and exits without a cloud call, as its own second pass would. Null when a pass of it holds
    // one now, or the scheduler cannot be read.
    public IDisposable? HoldKeepAwake() =>
        KeepAwakeTasks() is { } tasks ? HoldMutexes([.. tasks.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase)]) : null;

    // A mutex belongs to the thread that took it and a pass awaits across threads, so they are
    // taken, held and let go on a thread of their own.
    internal static IDisposable? HoldMutexes(IReadOnlyList<string> names)
    {
        if (names.Count == 0) { return new Held(null, null); }
        var taken = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var held = new List<Mutex>();
            try
            {
                foreach (var name in names)
                {
                    Mutex mutex;
                    // One that exists under an owner that will not share it is a pass of someone's.
                    try { mutex = new Mutex(false, @"Local\" + name); }
                    catch (Exception e) when (e is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException) { taken.TrySetResult(false); return; }
                    bool got;
                    try { got = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { got = true; }
                    if (!got) { mutex.Dispose(); taken.TrySetResult(false); return; }
                    held.Add(mutex);
                }
                taken.TrySetResult(true);
                release.Wait();
            }
            catch (Exception e) { taken.TrySetException(e); }
            finally
            {
                foreach (var mutex in held)
                {
                    mutex.ReleaseMutex();
                    mutex.Dispose();
                }
            }
        }) { IsBackground = true, Name = "keep-awake mutexes" };
        thread.Start();
        bool ok;
        try { ok = taken.Task.GetAwaiter().GetResult(); }
        catch
        {
            thread.Join();
            release.Dispose();
            throw;
        }
        if (ok) { return new Held(release, thread); }
        thread.Join();
        release.Dispose();
        return null;
    }

    sealed class Held(ManualResetEventSlim? release, Thread? thread) : IDisposable
    {
        int disposed;

        public void Dispose()
        {
            if (release is null || Interlocked.Exchange(ref disposed, 1) == 1) { return; }
            release.Set();
            thread!.Join();
            release.Dispose();
        }
    }

    // Whether that installation's daemon itself was seen running: unlike Active, not knowing is not
    // a yes here, and nor is its supervisor or task alone (either can run while the daemon fails
    // to start), nor another installation's daemon. Rollback is done only once it has seen this
    // installation's back; Active stays user-wide, for G5.
    public bool Seen(string installationId)
    {
        var found = Scan().Found;
        Probe("AgentBridgeDaemon_" + installationId, found);
        return DaemonAmong(found, installationId);
    }

    // Whether that installation's supervisor holds its mutex. Not being able to tell is no: the
    // answer only decides whether rollback starts the task again, and a second supervisor exits
    // at once on the first one's mutex.
    public bool SupervisorSeen(string installationId)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        NamedMutexes(found);
        Probe("CopilotBridgeSupervisor_" + installationId, found);
        return found.Contains("CopilotBridgeSupervisor_" + installationId, StringComparer.OrdinalIgnoreCase);
    }

    internal static bool DaemonAmong(IEnumerable<string> found, string installationId) =>
        found.Contains("AgentBridgeDaemon_" + installationId, StringComparer.OrdinalIgnoreCase);

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
    internal static bool NamedMutexes(ISet<string> found) => NamedMutexes(found, OwnName());

    internal static bool NamedMutexes(ISet<string> found, Regex names)
    {
        try
        {
            if (!ProcessIdToSessionId((uint)Environment.ProcessId, out var session)) { return false; }
            var directory = session == 0 ? @"\BaseNamedObjects" : $@"\Sessions\{session}\BaseNamedObjects";
            foreach (var (name, type) in ObjectDirectory(directory))
            {
                if (type == "Mutant" && names.IsMatch(name)) { found.Add(name); }
            }
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or ExternalException or DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    // agent-ha-bridge's keep-awake tasks, enabled and running; null when the scheduler could not
    // be asked.
    internal static List<(string Name, bool Enabled, bool Running)>? KeepAwakeTasks()
    {
        var list = new List<(string, bool, bool)>();
        return EachTask(task =>
        {
            var name = (string)Get(task, "Name")!;
            // TASK_STATE_RUNNING
            if (KeepAwakeName().IsMatch(name)) { list.Add((name, (bool)Get(task, "Enabled")!, (int)Get(task, "State")! == 4)); }
        }) ? list : null;
    }

    // Registered scheduled tasks named like agent-ha-bridge's daemon, with whether each is
    // running now; null when the scheduler could not be asked.
    internal static List<(string Name, bool Running)>? Tasks()
    {
        var list = new List<(string, bool)>();
        return EachTask(task =>
        {
            var name = (string)Get(task, "Name")!;
            // TASK_STATE_RUNNING
            if (TaskName().IsMatch(name)) { list.Add((name, (int)Get(task, "State")! == 4)); }
        }) ? list : null;
    }

    // Every task in the scheduler's root folder, hidden ones too; false when it could not be asked.
    static bool EachTask(Action<object> visit)
    {
        object? service = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) { return false; }
            service = Activator.CreateInstance(type);
            if (service is null) { return false; }
            Call(service, "Connect");
            var folder = Call(service, "GetFolder", @"\")!;
            // TASK_ENUM_HIDDEN: a hidden task is still the daemon's.
            var tasks = Call(folder, "GetTasks", 1)!;
            var count = (int)Get(tasks, "Count")!;
            for (var i = 1; i <= count; i++)
            {
                var task = Get(tasks, "Item", i)!;
                try { visit(task); }
                finally { Marshal.ReleaseComObject(task); }
            }
            Marshal.ReleaseComObject(tasks);
            Marshal.ReleaseComObject(folder);
            return true;
        }
        catch (Exception e) when (e is COMException or TargetInvocationException or InvalidCastException or MissingMethodException or NullReferenceException or UnauthorizedAccessException)
        {
            return false;
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
