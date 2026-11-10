using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentBridge.HostLauncher;

// The host this launcher starts ends with the launcher (#420). Ending the launcher (the app's
// Stop after its start timeout, through the logon task or the launcher's pid) used to end only
// the launcher: a host still starting ran on, unwatched, believing a launcher would restart it,
// so its next host.restart (exit 75) or crash brought nothing back. In a job that is killed when
// its last handle closes, which only this process holds (not inheritable), the host goes the
// moment the launcher does, however it ends.
//
// Created in the job, not added to it afterwards (PROC_THREAD_ATTRIBUTE_JOB_LIST): with the host
// already running before it was added, a launcher ended in between left it outside the job, the
// very orphan this prevents (#420's review).
//
// Only the host: SILENT_BREAKAWAY_OK leaves everything the host starts (agent sessions, above
// all) out of the job, so a host ended with its launcher never takes a person's agents with it.
[SupportedOSPlatform("windows")]
public sealed class HostJob : IDisposable
{
    const int ExtendedLimitInformation = 9;
    const uint KillOnJobClose = 0x2000;
    const uint SilentBreakawayOk = 0x1000;
    const uint CreateUnicodeEnvironment = 0x400;
    const uint ExtendedStartupInfoPresent = 0x80000;
    const uint CreateNoWindow = 0x08000000;
    const nint ProcThreadAttributeJobList = 0x2000D;

    readonly SafeFileHandle handle;

    HostJob(SafeFileHandle handle) => this.handle = handle;

    public static HostJob Create()
    {
        var handle = CreateJobObjectW(0, null);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = KillOnJobClose | SilentBreakawayOk } };
        if (!SetInformationJobObject(handle, ExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimit>()))
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        return new HostJob(handle);
    }

    // Starts a program in this job, with no window, this process's environment with extra over
    // it, and nothing inherited.
    public JobChild Start(string exe, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> extra, string workingDirectory)
    {
        var line = new StringBuilder(Quote(exe));
        foreach (var a in arguments) { line.Append(' ').Append(Quote(a)); }
        var command = line.Append('\0').ToString().ToCharArray();
        var environment = Marshal.StringToHGlobalUni(EnvironmentBlock(extra));
        nint size = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref size);
        var list = Marshal.AllocHGlobal(size);
        var jobs = Marshal.AllocHGlobal(IntPtr.Size);
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            Marshal.WriteIntPtr(jobs, handle.DangerousGetHandle());
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
            try
            {
                if (!UpdateProcThreadAttribute(list, 0, ProcThreadAttributeJobList, jobs, IntPtr.Size, 0, 0)) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
                var info = new StartupInfoEx { Size = Marshal.SizeOf<StartupInfoEx>(), AttributeList = list };
                if (!CreateProcessW(exe, command, 0, 0, false, CreateNoWindow | CreateUnicodeEnvironment | ExtendedStartupInfoPresent, environment, workingDirectory, ref info, out var started))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
                CloseHandle(started.Thread);
                return new JobChild(new SafeProcessHandle(started.Process, ownsHandle: true), started.ProcessId);
            }
            finally { DeleteProcThreadAttributeList(list); }
        }
        finally
        {
            if (added) { handle.DangerousRelease(); }
            Marshal.FreeHGlobal(jobs);
            Marshal.FreeHGlobal(list);
            Marshal.FreeHGlobal(environment);
        }
    }

    // Closing the job's only handle ends what is in it.
    public void Dispose() => handle.Dispose();

    // A process started in the job, by the handle that started it.
    public sealed class JobChild(SafeProcessHandle process, int id) : IDisposable
    {
        public int Id => id;

        public bool HasExited => WaitForSingleObject(process, 0) == 0;

        public async Task<int> WaitForExitAsync(CancellationToken ct = default)
        {
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var wait = new ProcessWait { SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false) };
            var registered = ThreadPool.RegisterWaitForSingleObject(wait, (_, _) => exited.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
            try
            {
                await exited.Task.WaitAsync(ct);
                return GetExitCodeProcess(process, out var code) ? unchecked((int)code) : throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            finally { registered.Unregister(wait); }
        }

        public void Dispose() => process.Dispose();
    }

    // A wait handle on a process handle someone else owns. A ManualResetEvent would create an event
    // of its own first, which setting SafeWaitHandle leaves unclosed.
    sealed class ProcessWait : WaitHandle;

    // An argument as CommandLineToArgvW and the C runtime read it back.
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) { return argument; }
        var quoted = new StringBuilder("\"");
        for (var i = 0; i < argument.Length; i++)
        {
            var slashes = 0;
            while (i < argument.Length && argument[i] == '\\') { slashes++; i++; }
            if (i == argument.Length) { quoted.Append('\\', slashes * 2); break; }
            if (argument[i] == '"') { quoted.Append('\\', slashes * 2 + 1).Append('"'); }
            else { quoted.Append('\\', slashes).Append(argument[i]); }
        }
        return quoted.Append('"').ToString();
    }

    // This process's environment with extra over it, as CreateProcess takes one: name=value
    // strings sorted by name, each ended by a NUL, then one more.
    static string EnvironmentBlock(IReadOnlyDictionary<string, string> extra)
    {
        var all = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables()) { all[(string)e.Key] = (string?)e.Value ?? ""; }
        foreach (var (name, value) in extra) { all[name] = value; }
        var block = new StringBuilder();
        foreach (var (name, value) in all) { block.Append(name).Append('=').Append(value).Append('\0'); }
        return block.Append('\0').ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfoEx
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Count;
        public nint Reserved2, StdInput, StdOutput, StdError;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public nint Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimit info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize);

    [DllImport("kernel32.dll")]
    static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateProcessW(string application, char[] commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string workingDirectory, ref StartupInfoEx info, out ProcessInformation started);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
}
