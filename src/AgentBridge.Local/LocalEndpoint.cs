using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace AgentBridge.Local;

// Where a profile's host listens: a named pipe on Windows, a Unix socket in the profile
// folder elsewhere. Derived from the profile folder, so an app finds its host from the same
// --profile, and a test with a temporary root never meets a real host.
public sealed record LocalEndpoint(string Folder, string Address)
{
    public bool IsPipe => OperatingSystem.IsWindows();

    // sun_path is 104 bytes on macOS and 108 on Linux, including the terminating zero.
    const int MaxSocketPath = 103;

    public static LocalEndpoint For(string profileFolder)
    {
        var folder = Path.GetFullPath(profileFolder);
        if (!OperatingSystem.IsWindows()) { return new(folder, SocketPath(folder)); }
        // Pipe names are one namespace for the whole machine, so the user's SID is part of the
        // hash: two people with the same profile name never share a name. Nobody else can use
        // it anyway (the DACL, FirstPipeInstance and the client's owner check), so the hash only
        // keeps names apart; it is not a secret.
        var user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{user}|{folder.ToLowerInvariant()}"));
        return new(folder, $"AgentBridge.Host.{Convert.ToHexString(hash, 0, 8).ToLowerInvariant()}");
    }

    // In the profile folder (0700) when the path fits a socket address. A long home directory
    // can push it past the limit; then a short name in the user's private runtime folder
    // ($XDG_RUNTIME_DIR, or $TMPDIR on macOS, both the user's alone). If neither fits either,
    // the long path stands: the host reports it cannot listen and carries on without its local
    // API rather than not starting.
    static string SocketPath(string folder)
    {
        var inFolder = Path.Combine(folder, "host.sock");
        if (Fits(inFolder)) { return inFolder; }
        var name = $"agent-bridge-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder)), 0, 8).ToLowerInvariant()}.sock";
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } xdg && Directory.Exists(xdg) ? xdg
            : OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("TMPDIR") is { Length: > 0 } tmp && Directory.Exists(tmp) ? tmp
            : null;
        return runtime is not null && Path.Combine(runtime, name) is var shorter && Fits(shorter) ? shorter : inFolder;
    }

    internal static bool Fits(string socketPath) => Encoding.UTF8.GetByteCount(socketPath) <= MaxSocketPath;
}
