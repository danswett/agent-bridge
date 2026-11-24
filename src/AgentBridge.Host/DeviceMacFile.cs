using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Transport;

namespace AgentBridge.Host;

internal static class DeviceMacFile
{
    internal static bool IsPrivate(string path)
    {
        if (!OperatingSystem.IsWindows())
        { return (File.GetUnixFileMode(path) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) == 0; }
        var me = WindowsIdentity.GetCurrent().User!;
        var acl = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var mine = false;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (!me.Equals(rule.IdentityReference)) { return false; }
            mine |= rule.AccessControlType == AccessControlType.Allow && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl);
        }
        return acl.AreAccessRulesProtected && me.Equals(acl.GetOwner(typeof(SecurityIdentifier))) && mine;
    }

    // Self-ECDH also works with non-exportable device keys; callers must use distinct domains.
    internal static byte[] Mac(byte[] body, P256KeyPair? key, ReadOnlySpan<byte> domain)
    {
        if (key is null) { throw new CryptographicException("no host key"); }
        var shared = key.Dh(key.Public);
        byte[]? derived = null;
        try
        {
            derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, domain.ToArray());
            return HMACSHA256.HashData(derived, body);
        }
        finally { CryptographicOperations.ZeroMemory(shared); if (derived is not null) { CryptographicOperations.ZeroMemory(derived); } }
    }

    internal static void Write(string path, byte[] bytes, Action<string> log)
    {
        PrivateFolder.Ensure(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = PrivateFolder.CreateNew(temp))
            {
                if (OperatingSystem.IsWindows()) { AgentAuditLog.NarrowFileWindows(temp); }
                stream.Write(bytes); stream.Flush(true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log("authenticated store temporary file could not be removed"); }
        }
    }
}
