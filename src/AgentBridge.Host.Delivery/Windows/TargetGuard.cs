using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AgentBridge.Host.Delivery.Windows;

// Refuses a session that runs elevated. Windows would refuse the attach anyway when the
// host is not elevated itself, but a host that is must still not type into an
// administrator's session from a phone, and saying so up front names the reason.
// Only PROCESS_QUERY_LIMITED_INFORMATION and TOKEN_QUERY are asked for, which agent-ha-bridge
// found granted against an elevated copilot.exe from a non-elevated process of the same user.
[SupportedOSPlatform("windows")]
public sealed class TargetGuard : ITargetGuard
{
    const int High = 0x3000;

    public string? Refuse(int processId)
    {
        var level = IntegrityLevel(processId);
        if (level < 0) { return "the host could not check whether the session runs as administrator; answer at the machine"; }
        if (level >= High) { return "the session runs as administrator, and the host does not type into elevated sessions; answer at the machine"; }
        return null;
    }

    // The mandatory integrity level (0x2000 medium, 0x3000 high), or -1 when unreadable.
    internal static long IntegrityLevel(int processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process == IntPtr.Zero) { return -1; }
        var token = IntPtr.Zero;
        var buffer = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out token)) { return -1; }
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var length);
            if (length <= 0) { return -1; }
            buffer = Marshal.AllocHGlobal(length);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out _)) { return -1; }
            // TOKEN_MANDATORY_LABEL starts with the label's SID, whose last sub-authority is
            // the level itself.
            var sid = Marshal.ReadIntPtr(buffer);
            int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            if (count < 1) { return -1; }
            return (uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
        }
        finally
        {
            if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); }
            if (token != IntPtr.Zero) { CloseHandle(token); }
            CloseHandle(process);
        }
    }

    const uint ProcessQueryLimitedInformation = 0x1000;
    const uint TokenQuery = 0x0008;
    const int TokenIntegrityLevel = 25;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, int length, out int returned);

    [DllImport("advapi32.dll")]
    static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}
