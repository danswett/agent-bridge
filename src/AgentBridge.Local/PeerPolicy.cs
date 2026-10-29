using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace AgentBridge.Local;

// Who is on the other end of a pipe connection: the client process, its user and whether it
// runs elevated. The host serves only its own user at its own elevation.
public sealed record PipePeer(int ProcessId, string? User, bool? Elevated, string? ProcessName);

public static class PeerPolicy
{
    // Whether this process runs elevated (as administrator under UAC). False off Windows.
    public static bool SelfElevated() => OperatingSystem.IsWindows() && WindowsPeers.CurrentElevated();

    // Null when the peer may use the host; otherwise why not, which the client is told.
    // Not knowing who it is counts as a refusal: the check fails closed.
    public static string? Refusal(PipePeer peer, string selfUser, bool selfElevated)
    {
        if (peer.User is null || peer.Elevated is null) { return "the host could not verify the connecting process"; }
        if (!string.Equals(peer.User, selfUser, StringComparison.OrdinalIgnoreCase)) { return "the host serves only the user it runs as"; }
        if (peer.Elevated != selfElevated)
        {
            return selfElevated
                ? "the host is running as administrator and the app is not; start the host as the signed-in user"
                : "the app is running as administrator and the host is not; start the app without administrator rights";
        }
        return null;
    }
}

[SupportedOSPlatform("windows")]
static class WindowsPeers
{
    public static string CurrentUser() => WindowsIdentity.GetCurrent().User!.Value;

    public static bool CurrentElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return IsElevated(identity.AccessToken.DangerousGetHandle()) ?? false;
    }

    public static PipePeer Of(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)) { return new PipePeer(0, null, null, null); }
        string? name = null;
        try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); name = p.ProcessName; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { }
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) { return new PipePeer((int)pid, null, null, name); }
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token)) { return new PipePeer((int)pid, null, null, name); }
            try
            {
                using var identity = new WindowsIdentity(token);
                return new PipePeer((int)pid, identity.User?.Value, IsElevated(token), name);
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    static bool? IsElevated(IntPtr token)
    {
        var size = Marshal.SizeOf<int>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, TokenElevation, buffer, size, out _)) { return null; }
            return Marshal.ReadInt32(buffer) != 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    const uint ProcessQueryLimitedInformation = 0x1000;
    const uint TokenQuery = 0x0008;
    const int TokenElevation = 20;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}
