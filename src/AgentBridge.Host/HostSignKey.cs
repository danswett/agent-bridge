using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// Custody only: no caller enrolls this key or enables roster v2 by creating it.
[SupportedOSPlatform("windows")]
internal sealed class HostSignKey : IDisposable
{
    const string Unavailable = "hostSign custody is incomplete, damaged or inaccessible; the existing identity is not replaced";
    readonly RosterSigningKey key;

    HostSignKey(RosterSigningKey key) => this.key = key;

    public byte[] PublicPoint => key.Public.ToArray();
    public KeyStorage Storage => key.Storage;
    public byte[] Sign(byte[] message) => key.Sign(message);
    public void Dispose() => key.Dispose();

    public static HostSignKey LoadOrCreate(string profile, KeyStorage? prefer = null)
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("hostSign custody is implemented only on Windows"); }
        RosterSigningKey? opened = null;
        try
        {
            var folder = PrivateFolder.Ensure(Path.Combine(profile, "host-sign"), "the hostSign folder", "no hostSign key is opened or created");
            using var guard = AcquireGate(Path.Combine(folder, "host-sign.lock"));
            var commit = Path.Combine(folder, "host-sign.public");
            var committed = Present(commit);
            var reference = Present(Path.Combine(folder, "signing-key.json"));
            var software = Present(Path.Combine(folder, "signing-key.dpapi"));
            // The shared store creates when its reference is missing. Never let a torn write
            // or a missing reference silently turn a previously committed identity into a new one.
            if (committed ? !reference : reference || software) { throw new InvalidDataException(Unavailable); }
            byte[]? expected = null;
            if (committed)
            {
                if (new FileInfo(commit).Length != 65) { throw new InvalidDataException(Unavailable); }
                expected = File.ReadAllBytes(commit);
                if (!AgentBridge.Roster.P256.IsPoint(expected)) { throw new InvalidDataException(Unavailable); }
            }
            opened = RosterSigningKeys.LoadOrCreate(folder, prefer);
            if (expected is not null)
            {
                if (!expected.AsSpan().SequenceEqual(opened.Public) || (opened.Storage == KeyStorage.Dpapi) != software)
                { throw new InvalidDataException(Unavailable); }
            }
            else
            {
                // Only a completed, flushed record makes a key available to a caller. A crash
                // before this finishes leaves refused state, never an identity to auto-repair.
                using var record = PrivateFolder.CreateNew(commit);
                record.Write(opened.Public);
                record.Flush(true);
            }
            var result = new HostSignKey(opened);
            opened = null;
            return result;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
            or CryptographicException or JsonException or ArgumentException or PlatformNotSupportedException)
        {
            // Provider and deserializer exceptions may echo file contents; do not retain them
            // as an inner exception that a caller could log.
            throw new InvalidDataException(Unavailable);
        }
        finally { opened?.Dispose(); }
    }

    static bool Present(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            { throw new InvalidDataException(Unavailable); }
            return true;
        }
        catch (FileNotFoundException) { return false; }
    }

    static FileStream AcquireGate(string path)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33 && elapsed.Elapsed < TimeSpan.FromSeconds(30))
            { Thread.Sleep(10); }
        }
    }
}
