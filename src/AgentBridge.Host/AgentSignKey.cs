using System.Security.Cryptography;
using AgentBridge.Host.Delivery.Staging;

namespace AgentBridge.Host;

// The per-host P-256 ECDSA key an AP5-lite envelope is signed with (#728, slice L2). It is made
// only when a caller asks and the mode is on: never at install, never while the mode is off. The
// private half stays inside this class: it lives in the host's private folder, DPAPI-protected
// (CurrentUser) on Windows and a 0600 file elsewhere, and is never logged or returned. A key file
// that is present but cannot be read or does not match its own recorded public point is refused,
// not replaced (a truncated file included: the owner deletes it to start over): a new key would
// be an identity no peer has pinned, made silently. The file is written whole to a temp file and
// moved in without overwrite, so a crash or a concurrent first caller never leaves a partial key.
public sealed class AgentSignKey : IDisposable
{
    public const string FolderName = "agent-sign";
    public const string FileName = "agent-sign.key";
    static readonly byte[] Entropy = "agent-bridge-agent-sign-key-v1"u8.ToArray();

    readonly ECDsa key;

    AgentSignKey(ECDsa key, byte[] point)
    {
        this.key = key;
        PublicPoint = point;
    }

    // The uncompressed point: 0x04, X, Y.
    public byte[] PublicPoint { get; }

    // P-256 ECDSA/SHA-256 as DER: the shape of the envelope library's IEnvelopeSigner.
    public byte[] Sign(ReadOnlySpan<byte> message) => key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    public static string PathOf(string profileFolder) => Path.Combine(profileFolder, FolderName, FileName);

    // The existing key, or null when none was ever made. Never makes one.
    public static AgentSignKey? Open(string profileFolder)
    {
        var path = PathOf(profileFolder);
        if (!File.Exists(path)) { return null; }
        return Read(path);
    }

    // The existing key, or a new one. Refused while the mode is off.
    public static AgentSignKey OpenOrCreate(string profileFolder, bool modeOn)
    {
        if (!modeOn) { throw new InvalidOperationException("own-account messaging is off, so no agent signing key is made"); }
        if (Open(profileFolder) is { } existing) { return existing; }
        var folder = PrivateFolder.Ensure(Path.Combine(profileFolder, FolderName), "the agent signing key folder", "no agent signing key is made");
        var final = Path.Combine(folder, FileName);
        // File.Move(overwrite: false) is not exclusive off Windows (it checks, then renames), so two
        // first callers, in one process or two, could each move a different key in and the second
        // would replace the first. An exclusive lock on a side file, held across the check and the
        // move, lets one make the key and the others read it.
        using (AcquireGate(Path.Combine(folder, FileName + ".lock")))
        {
            if (!File.Exists(final))
            {
                using var fresh = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var point = Point(fresh);
                var pkcs8 = fresh.ExportPkcs8PrivateKey();
                var temp = Path.Combine(folder, $"{FileName}.{Guid.NewGuid():N}.tmp");
                byte[] content = [.. point, .. pkcs8];
                try
                {
                    // Written whole to a temp file and moved in without overwrite, so the final path
                    // never holds a partial key.
                    using (var stream = PrivateFolder.CreateNew(temp))
                    {
                        stream.Write(OperatingSystem.IsWindows() ? Protect(content) : content);
                        stream.Flush(true);
                    }
                    File.Move(temp, final, overwrite: false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(content);
                    CryptographicOperations.ZeroMemory(pkcs8);
                    try { File.Delete(temp); } catch (IOException) { }
                }
            }
        }
        return Read(final);
    }

    // The OS drops the lock if its holder dies, so a crash never leaves it stuck.
    static FileStream AcquireGate(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(10); }
        }
    }

    static AgentSignKey Read(string path)
    {
        byte[]? content = null;
        try
        {
            content = ReadWithRetry(path);
            if (OperatingSystem.IsWindows()) { content = Unprotect(content); }
            if (content.Length < 66 || content[0] != 0x04) { throw new InvalidDataException("short"); }
            var ecdsa = ECDsa.Create();
            try
            {
                ecdsa.ImportPkcs8PrivateKey(content.AsSpan(65), out _);
                var point = Point(ecdsa);
                if (ecdsa.KeySize != 256 || !point.AsSpan().SequenceEqual(content.AsSpan(0, 65))) { throw new InvalidDataException("mismatch"); }
                return new AgentSignKey(ecdsa, point);
            }
            catch { ecdsa.Dispose(); throw; }
        }
        catch (Exception e) when (e is CryptographicException or InvalidDataException or ArgumentException)
        {
            throw new InvalidDataException("the agent signing key file is damaged or was changed; it is left as it is and no new key is made");
        }
        finally { if (content is not null) { CryptographicOperations.ZeroMemory(content); } }
    }

    // Windows can report a sharing violation while a just-moved file settles; that is an IOException,
    // retried briefly. A file that reads but is damaged is not retried (InvalidDataException).
    static byte[] ReadWithRetry(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return File.ReadAllBytes(path); }
            catch (IOException) when (attempt < 20 && File.Exists(path)) { Thread.Sleep(10 * (attempt + 1)); }
        }
    }

    static byte[] Point(ECDsa e)
    {
        var q = e.ExportParameters(false).Q;
        return [0x04, .. q.X!, .. q.Y!];
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static byte[] Protect(byte[] b) => ProtectedData.Protect(b, Entropy, DataProtectionScope.CurrentUser);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static byte[] Unprotect(byte[] b) => ProtectedData.Unprotect(b, Entropy, DataProtectionScope.CurrentUser);

    public void Dispose() => key.Dispose();
}
