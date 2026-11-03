using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace AgentBridge.HostLauncher;

// The host this launcher starts ends with the launcher (#420). Ending the launcher (the app's
// Stop after its start timeout, through the logon task or the launcher's pid) used to end only
// the launcher: a host still starting ran on, unwatched, believing a launcher would restart it,
// so its next host.restart (exit 75) or crash brought nothing back. In a job that is killed when
// its last handle closes, which only this process holds, the host goes the moment the launcher
// does, however it ends.
//
// Only the host: SILENT_BREAKAWAY_OK leaves everything the host starts (agent sessions, above
// all) out of the job, so a host ended with its launcher never takes a person's agents with it.
[SupportedOSPlatform("windows")]
public sealed class HostJob : IDisposable
{
    const int ExtendedLimitInformation = 9;
    const uint KillOnJobClose = 0x2000;
    const uint SilentBreakawayOk = 0x1000;

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

    public void Add(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.SafeHandle)) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
    }

    // Closing the job's only handle ends what is in it.
    public void Dispose() => handle.Dispose();

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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimit info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
