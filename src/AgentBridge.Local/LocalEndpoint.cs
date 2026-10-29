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

    public static LocalEndpoint For(string profileFolder)
    {
        var folder = Path.GetFullPath(profileFolder);
        if (!OperatingSystem.IsWindows()) { return new(folder, Path.Combine(folder, "host.sock")); }
        // Pipe names are one namespace for the whole machine, so the user's SID is part of the
        // hash: two people with the same profile name never share a name. Nobody else can use
        // it anyway (the DACL, FirstPipeInstance and the client's owner check), so the hash only
        // keeps names apart; it is not a secret.
        var user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{user}|{folder.ToLowerInvariant()}"));
        return new(folder, $"AgentBridge.Host.{Convert.ToHexString(hash, 0, 8).ToLowerInvariant()}");
    }
}
