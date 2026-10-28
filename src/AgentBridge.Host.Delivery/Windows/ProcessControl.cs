using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AgentBridge.Host.Delivery.Windows;

// Opens a session's process for ending it, and holds it open while it is ended. A held
// handle is what makes ending safe against pid reuse: Windows does not give the id to a new
// process while a handle to the old one is open, so everything done through it reaches the
// process whose start time was checked, whatever happens to the id meanwhile.
//
// Refuses what agent-ha-bridge would not end and Windows might: an elevated session (the host
// does not act on an administrator's process from a phone, even when it could), another
// user's, the host itself, and anything whose start time or owner cannot be read.
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessControl : IProcessControl
{
    public HeldOpen Open(int processId, DateTimeOffset startedAt)
    {
        if (processId <= 0) { return HeldOpen.Refused("the session's process is not known; end it at the machine"); }
        if (processId == Environment.ProcessId) { return HeldOpen.Refused("the session's process is this host; end it at the machine"); }
        var handle = OpenProcess(Synchronize | ProcessTerminate | ProcessQueryLimitedInformation, false, processId);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            // ERROR_INVALID_PARAMETER: no process has that id any more.
            if (error == 87) { return HeldOpen.Exited(); }
            return HeldOpen.Refused(error == 5
                ? "Windows does not let the host end this session (it runs as administrator or as another user); end it at the machine"
                : $"the host could not open the session's process (error {error}); end it at the machine");
        }
        try
        {
            if (WaitForSingleObject(handle, 0) == WaitObject0) { handle.Dispose(); return HeldOpen.Exited(); }
            if (!GetProcessTimes(handle, out var created, out _, out _, out _))
            {
                handle.Dispose();
                return HeldOpen.Refused("the host could not read when the session's process started, so it cannot tell it from another; end it at the machine");
            }
            // The inspector read the same creation time through Process.StartTime; anything
            // more than rounding apart is another process that has taken the id.
            if (Math.Abs((DateTimeOffset.FromFileTime(created).UtcTicks - startedAt.UtcTicks)) > TimeSpan.TicksPerMillisecond)
            {
                handle.Dispose();
                return HeldOpen.Exited();
            }
            var level = TargetGuard.IntegrityLevel(processId);
            if (level < 0) { handle.Dispose(); return HeldOpen.Refused("the host could not check whether the session runs as administrator; end it at the machine"); }
            if (level >= 0x3000) { handle.Dispose(); return HeldOpen.Refused("the session runs as administrator, and the host does not end elevated sessions; end it at the machine"); }
            var owner = Owner(handle);
            var me = WindowsIdentity.GetCurrent().User;
            if (owner is null || me is null) { handle.Dispose(); return HeldOpen.Refused("the host could not read who runs the session; end it at the machine"); }
            if (owner != me) { handle.Dispose(); return HeldOpen.Refused("the session runs as another user; end it at the machine"); }
            return HeldOpen.Of(new Held(handle, processId));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    static SecurityIdentifier? Owner(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token)) { return null; }
        var buffer = IntPtr.Zero;
        try
        {
            GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var length);
            if (length <= 0) { return null; }
            buffer = Marshal.AllocHGlobal(length);
            if (!GetTokenInformation(token, TokenUser, buffer, length, out _)) { return null; }
            // TOKEN_USER starts with a SID_AND_ATTRIBUTES, whose first member is the SID.
            return new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
        }
        finally
        {
            if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); }
            token.Dispose();
        }
    }

    sealed class Held(SafeProcessHandle handle, int id) : IHeldProcess
    {
        public int Id => id;

        public async Task<bool> WaitForExitAsync(TimeSpan timeout)
        {
            var started = Environment.TickCount64;
            while (true)
            {
                if (WaitForSingleObject(handle, 0) == WaitObject0) { return true; }
                if (Environment.TickCount64 - started >= timeout.TotalMilliseconds) { return false; }
                await Task.Delay(100);
            }
        }

        public string? Terminate()
        {
            if (TerminateProcess(handle, 1)) { return null; }
            var error = Marshal.GetLastWin32Error();
            // ERROR_ACCESS_DENIED is also what terminating a process that is already exiting
            // returns; the wait that follows tells the two apart.
            return $"the host could not terminate the session's process ({new Win32Exception(error).Message})";
        }

        public void Dispose() => handle.Dispose();
    }

    const int Synchronize = 0x00100000;
    const int ProcessTerminate = 0x0001;
    const int ProcessQueryLimitedInformation = 0x1000;
    const uint TokenQuery = 0x0008;
    const int TokenUser = 1;
    const uint WaitObject0 = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeProcessHandle OpenProcess(int access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr information, int length, out int returned);
}
