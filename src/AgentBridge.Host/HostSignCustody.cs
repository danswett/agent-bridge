using System.Security.Cryptography;
using AgentBridge.Local;
using AgentBridge.Roster;
using AgentBridge.Transport;

namespace AgentBridge.Host;

internal static class HostSignCustody
{
    public static HostSigningStatus Read(string profile)
    {
        if (!OperatingSystem.IsWindows()) { return new("missing-key"); }
        try
        {
            // Do not retain a key or successful result: loss must be visible on the next read.
            using var key = HostSignKey.OpenExisting(profile);
            if (key.Storage is not (KeyStorage.Tpm or KeyStorage.Dpapi)) { return new("missing-key"); }
            return new("available", key.Storage == KeyStorage.Tpm ? "tpm" : "dpapi",
                Codec.Base64Url(SHA256.HashData(key.PublicPoint)));
        }
        catch (InvalidDataException e) when (e.Message == "missing-key")
        {
            return new("missing-key");
        }
    }
}
