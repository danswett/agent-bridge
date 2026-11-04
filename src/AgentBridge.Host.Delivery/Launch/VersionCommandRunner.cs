using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Host.Core.Usage;
using Microsoft.Win32.SafeHandles;

namespace AgentBridge.Host.Delivery.Launch;

// The job is assigned atomically at creation: even an immediately exiting wrapper cannot
// leave descendants behind. Ordinary launch/session processes must not use this runner.
public sealed class VersionCommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("Bounded CLI version checks are currently supported on Windows only."); }
        cancellationToken.ThrowIfCancellationRequested();
        var line = WindowsCommandLine.For(program, arguments, out var problem) ?? throw new ArgumentException(problem, nameof(program));
        using var launch = ProcessLaunch.HoldInheritableHandles();
        using var job = CreateJobObjectW(0, null);
        if (job.IsInvalid) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        var limits = new JobLimits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>())) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        var security = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
        if (!CreatePipe(out var outputRead, out var outputWrite, ref security, 0)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        using var stdoutRead = outputRead;
        using var stdoutWrite = outputWrite;
        if (!CreatePipe(out var errorRead, out var errorWrite, ref security, 0)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        using var stderrRead = errorRead;
        using var stderrWrite = errorWrite;
        if (!SetHandleInformation(stdoutRead, 1, 0) || !SetHandleInformation(stderrRead, 1, 0)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        if (!CreatePipe(out var inputRead, out var inputWrite, ref security, 0)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        using var stdinRead = inputRead;
        inputWrite.Dispose();
        var info = new StartupInfoEx
        {
            Startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfoEx>(),
                Flags = 0x100,
                Input = stdinRead.DangerousGetHandle(),
                Output = stdoutWrite.DangerousGetHandle(),
                Error = stderrWrite.DangerousGetHandle(),
            },
        };
        nuint size = 0;
        InitializeProcThreadAttributeList(0, 2, 0, ref size);
        if (size == 0) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        var storage = Marshal.AllocHGlobal(checked((int)size + 4 * IntPtr.Size));
        ProcessInformation started;
        try
        {
            if (!InitializeProcThreadAttributeList(storage, 2, 0, ref size)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            try
            {
                var handles = storage + checked((int)size);
                Marshal.WriteIntPtr(handles, stdinRead.DangerousGetHandle());
                Marshal.WriteIntPtr(handles, IntPtr.Size, stdoutWrite.DangerousGetHandle());
                Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, stderrWrite.DangerousGetHandle());
                var jobs = handles + 3 * IntPtr.Size;
                Marshal.WriteIntPtr(jobs, job.DangerousGetHandle());
                if (!UpdateProcThreadAttribute(storage, 0, 0x20002, handles, (nuint)(3 * IntPtr.Size), 0, 0)
                    || !UpdateProcThreadAttribute(storage, 0, 0x2000D, jobs, (nuint)IntPtr.Size, 0, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                info.Attributes = storage;
                if (!CreateProcessW(line.Application, new StringBuilder(line.CommandLine), 0, 0, true,
                    0x08080000, 0, null, ref info, out started))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally { DeleteProcThreadAttributeList(storage); }
        }
        finally { Marshal.FreeHGlobal(storage); }
        using var process = new SafeProcessHandle(started.Process, true);
        using var thread = new SafeWaitHandle(started.Thread, true);
        stdoutWrite.Dispose();
        stderrWrite.Dispose();
        stdinRead.Dispose();
        launch.Dispose();
        using var output = new StreamReader(new FileStream(stdoutRead, FileAccess.Read), Encoding.UTF8);
        using var errors = new StreamReader(new FileStream(stderrRead, FileAccess.Read), Encoding.UTF8);
        var text = output.ReadToEndAsync();
        var discarded = errors.ReadToEndAsync();
        using var exited = new ProcessWait(process);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(exited, (_, _) => completion.TrySetResult(), null, Timeout.Infinite, true);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await Task.WhenAll(completion.Task, text, discarded).WaitAsync(limit.Token);
            if (!GetExitCodeProcess(process, out var exitCode)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            return new CommandResult(true, false, unchecked((int)exitCode), await text);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new CommandResult(true, true, -1, "");
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                if (!TerminateJobObject(job, 1)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                await Task.WhenAll(completion.Task, text, discarded).WaitAsync(cleanup.Token);
                while (true)
                {
                    if (!QueryInformationJobObject(job, 1, out var accounting, (uint)Marshal.SizeOf<JobAccounting>(), 0)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                    if (accounting.ActiveProcesses == 0) { break; }
                    await Task.Delay(10, cleanup.Token);
                }
            }
            catch (OperationCanceledException e) when (cleanup.IsCancellationRequested)
            {
                throw new IOException("The version probe did not finish cleanup after its process job was terminated.", e);
            }
            finally { registration.Unregister(null); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes { public int Size; public nint Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize;
        public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfoEx { public StartupInfo Startup; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    struct JobLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)]
    struct JobAccounting
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    sealed class JobHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    sealed class ProcessWait : WaitHandle
    {
        public ProcessWait(SafeProcessHandle process) => SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), false);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern JobHandle CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetInformationJobObject(JobHandle job, int informationClass, ref JobLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool TerminateJobObject(JobHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryInformationJobObject(JobHandle job, int informationClass, out JobAccounting information, uint length, nint returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32.dll")]
    static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateProcessW(string application, StringBuilder command, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string? directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(nint handle);
}
