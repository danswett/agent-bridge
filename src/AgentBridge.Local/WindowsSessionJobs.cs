using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace AgentBridge.Local;

// The Windows session job (design/agent-principals.md §3.4): one job per launch root, held by the host as an open handle while the
// credential lives. UNNAMED (a named job vanishes when the last handle closes, and anything of the user can open or squat a name), so
// nothing else can open, join or trust it; only inherited membership gets in. Its one limit is permission to break away (never
// kill-on-close); nested jobs count. Contains believes a job only with the recorded root (pid and start time) in it. After a restart the
// host enrols live roots again in new jobs: later processes are members, shells started before keep the ancestor rule until relaunched.[SupportedOSPlatform("windows")]
public sealed class WindowsSessionJobs : ISessionJobs
{
    const uint ProcessTerminate = 0x0001, ProcessSetQuota = 0x0100, ProcessQueryLimitedInformation = 0x1000;
    const uint JobObjectLimitBreakawayOk = 0x0800, StillActive = 259, HandleFlagInherit = 1;
    const int JobObjectExtendedLimitInformation = 9;
    static readonly TimeSpan StartSlack = TimeSpan.FromSeconds(1);

    readonly object gate = new();
    readonly Dictionary<string, Handle> held = new(StringComparer.Ordinal);

    public void Enroll(string key, int processId, DateTimeOffset startedAt)
    {
        lock (gate) { if (held.ContainsKey(key)) { throw new InvalidOperationException("this launch already has a session job"); } }
        var job = new Handle(CreateJobObjectW(0, null));
        if (job.IsInvalid) { var error = Marshal.GetLastWin32Error(); job.Dispose(); throw new Win32Exception(error); }
        try
        {
            var limit = new ExtendedLimit { LimitFlags = JobObjectLimitBreakawayOk };
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limit, (uint)Marshal.SizeOf<ExtendedLimit>()))
            { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            using var process = Open(processId, startedAt, ProcessSetQuota | ProcessTerminate | ProcessQueryLimitedInformation)
                ?? throw new InvalidOperationException("the root process is not the one that was started");
            if (!AssignProcessToJobObject(job, process)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            lock (gate)
            {
                if (!held.TryAdd(key, job)) { throw new InvalidOperationException("this launch already has a session job"); }
            }
        }
        catch (Exception)
        {
            job.Dispose();
            throw;
        }
    }

    public bool Contains(string key, int rootPid, DateTimeOffset rootStartedAt, int processId, DateTimeOffset? startedAt)
    {
        if (startedAt is not { } caller || processId <= 0 || rootPid <= 0) { return false; }
        // Referenced under the lock: a Release while this is asked cannot close the handle, or let its value be recycled, until it is answered.
        Handle? job;
        var added = false;
        lock (gate)
        {
            if (!held.TryGetValue(key, out job)) { return false; }
            job.DangerousAddRef(ref added);
        }
        try
        {
            AfterLookup?.Invoke(key);
            return In(job, rootPid, rootStartedAt) && In(job, processId, caller);
        }
        finally { if (added) { job.DangerousRelease(); } }
    }

    // For tests: runs between finding the job and using it.
    internal Action<string>? AfterLookup { get; set; }

    // For tests: whether the job's handle can be inherited by a child process; null when there is no job.
    internal bool? IsInheritable(string key)
    { Handle? job; lock (gate) { held.TryGetValue(key, out job); } return job is not null && GetHandleInformation(job, out var flags) ? (flags & HandleFlagInherit) != 0 : null; }

    public void Release(string key)
    {
        Handle? job;
        lock (gate) { held.Remove(key, out job); }
        // The job lives on while its processes do: closing the handle ends nothing (no kill-on-close).
        job?.Dispose();
    }

    // For tests: the limit flags the job was given, or null when there is none.
    internal uint? LimitFlags(string key)
    {
        Handle? job;
        lock (gate) { held.TryGetValue(key, out job); }
        if (job is null) { return null; }
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<ExtendedLimit>());
        try { return QueryInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)Marshal.SizeOf<ExtendedLimit>(), out _) ? (uint)Marshal.ReadInt32(buffer, 16) : null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // A recycled id, or a process that is gone, is "no".
    static bool In(Handle job, int processId, DateTimeOffset startedAt)
    {
        using var process = Open(processId, startedAt, ProcessQueryLimitedInformation);
        return process is not null && IsProcessInJob(process, job, out var inside) && inside;
    }

    static Handle? Open(int processId, DateTimeOffset startedAt, uint access)
    {
        var process = new Handle(OpenProcess(access, false, (uint)processId));
        if (process.IsInvalid) { process.Dispose(); return null; }
        // A process that has exited (its object may live on while someone holds a handle) is gone.
        if (GetExitCodeProcess(process, out var exit) && exit == StillActive && GetProcessTimes(process, out var created, out _, out _, out _)
            && (DateTimeOffset.FromFileTime(created).ToUniversalTime() - startedAt).Duration() <= StartSlack)
        { return process; }
        process.Dispose();
        return null;
    }

    sealed class Handle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public Handle(nint value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() => CloseHandle(handle);
        public static implicit operator nint(Handle h) => h.DangerousGetHandle();
    }

    // JOBOBJECT_EXTENDED_LIMIT_INFORMATION as 64-bit Windows lays it out (the host is 64-bit only): 144 bytes, LimitFlags at 16. The
    // break-away flags are valid only in the extended class.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    struct ExtendedLimit
    {
        [FieldOffset(16)] public uint LimitFlags;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetInformationJobObject(nint job, int informationClass, ref ExtendedLimit information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryInformationJobObject(nint job, int informationClass, nint information, uint length, out uint returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetExitCodeProcess(nint process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetProcessTimes(nint process, out long created, out long exited, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetHandleInformation(nint handle, out uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(nint handle);
}
