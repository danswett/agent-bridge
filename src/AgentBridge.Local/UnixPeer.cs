using System.Net.Sockets;
using System.Runtime.InteropServices;
using AgentBridge.Protocol;

namespace AgentBridge.Local;

// #4414: a host on a Unix socket must run as this user. The socket's mode keeps another user from writing to it, but a process of another user could still have
// listened where this one connects (a path it owns, a swapped folder). The kernel names the peer's user on the connected socket (Linux SO_PEERCRED, macOS
// LOCAL_PEERCRED); a platform with neither, or a read that fails, is refused with its reason rather than trusted on the path's mode alone.
internal static class UnixPeer
{
    // No arguments and no marshalling, so a plain import (a LibraryImport would need this assembly to allow unsafe code).
#pragma warning disable SYSLIB1054
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUid();
#pragma warning restore SYSLIB1054

    // The peer's uid, or null when this platform has no way to ask or the read failed.
    public static uint? PeerUid(Socket socket)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var ucred = new byte[12]; // struct ucred { pid_t pid; uid_t uid; gid_t gid; }
                return socket.GetRawSocketOption(1 /* SOL_SOCKET */, 17 /* SO_PEERCRED */, ucred) == ucred.Length ? BitConverter.ToUInt32(ucred, 4) : null;
            }
            if (OperatingSystem.IsMacOS())
            {
                var xucred = new byte[76]; // struct xucred { u_int cr_version (0); uid_t cr_uid; short cr_ngroups; gid_t cr_groups[16]; }
                return socket.GetRawSocketOption(0 /* SOL_LOCAL */, 1 /* LOCAL_PEERCRED */, xucred) >= 8 && BitConverter.ToUInt32(xucred, 0) == 0 ? BitConverter.ToUInt32(xucred, 4) : null;
            }
        }
        catch (Exception e) when (e is SocketException or PlatformNotSupportedException or ArgumentException) { }
        return null;
    }

    public static uint CurrentUid() => GetEffectiveUid();

    // Throws LocalRefusedException, with the reason, unless the peer is this user.
    public static void Require(uint? peerUid, uint ownUid)
    {
        if (peerUid is not { } peer)
        {
            throw new LocalRefusedException($"this platform cannot say which user the host's socket belongs to, so the {Product.HostName} is not trusted on the socket's mode alone");
        }
        if (peer != ownUid)
        {
            throw new LocalRefusedException($"the {Product.HostName}'s socket is served by another user (uid {peer}, not this user's {ownUid})");
        }
    }
}
