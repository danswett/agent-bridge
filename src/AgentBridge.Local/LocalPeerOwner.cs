using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AgentBridge.Local;

// On Unix a client that names its own socket path must not talk to a socket another user made there. Linux reports the peer's user id on the connected socket
// (SO_PEERCRED), which must be this user's. Elsewhere the socket file itself must not be writable by group or others, the same 0700-folder trust the
// derived path relies on. Windows needs neither: the pipe client opens with CurrentUserOnly.
public static class LocalPeerOwner
{
    const int SolSocket = 1, SoPeerCred = 17;

    public static bool IsCurrentUser(uint peerUid, uint currentUid) => peerUid == currentUid;

    internal static void Verify(Socket connected, string path)
    {
        if (OperatingSystem.IsLinux())
        {
            Span<byte> credentials = stackalloc byte[12]; // struct ucred { pid, uid, gid }
            connected.GetRawSocketOption(SolSocket, SoPeerCred, credentials);
            if (!IsCurrentUser(BitConverter.ToUInt32(credentials[4..8]), GetEuid())) { throw new LocalRefusedException("the socket belongs to another user"); }
            return;
        }
        if (OperatingSystem.IsWindows()) { return; }
        if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            throw new LocalRefusedException("the socket can be written by other users");
        }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    static extern uint GetEuid();
}
