using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace AgentBridge.Host.Core.Processes;

// Whether a process is known to be another Windows user's. Another user's agents are listed by
// name like this user's own, but nothing of them can be read: no command line, no start time.
// Taken for this user's, they refused every migrate (#140), and one with no start time held
// discovery, and with it every resume and worktree reclaim, for as long as it ran (#143).
//
// Windows does not tell a standard user who owns a process it may not open (the terminal
// services list leaves the SID out), so this goes by what it does say. This user may open every
// process of its own for query-limited information, elevated ones and those in its other
// sessions included; a refusal therefore means another account. A refusal in this session is
// not taken as one: a process run as another user from here (runas) still has this desktop.
// Anything else (a refusal of another kind, the session unknown) is not known.
public static class ProcessOwnership
{
    [SupportedOSPlatform("windows")]
    public static bool AnotherUsers(int pid)
    {
        int? openError;
        using (var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid))
        {
            openError = handle.IsInvalid ? Marshal.GetLastPInvokeError() : null;
        }
        if (openError is null) { return false; }
        int? session = null;
        var gone = false;
        try
        {
            using var process = Process.GetProcessById(pid);
            session = process.SessionId;
        }
        catch (ArgumentException) { gone = true; }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        using var self = Process.GetCurrentProcess();
        return AnotherUsers(openError, session, gone, self.SessionId);
    }

    // openError is null when the open succeeded; session is null when it could not be read; gone
    // when the process has exited since (not running, so not one to wait for).
    public static bool AnotherUsers(int? openError, int? session, bool gone, int ownSession) =>
        openError == ErrorAccessDenied && (gone || session is { } s && s != ownSession);

    const int ProcessQueryLimitedInformation = 0x1000;
    const int ErrorAccessDenied = 5;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeProcessHandle OpenProcess(int access, bool inherit, int pid);
}
