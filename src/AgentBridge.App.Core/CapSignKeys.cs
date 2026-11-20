// The person's capSign key (docs/protocol/devices.md, "Signer and enrollment" and "Custody"): a
// distinct, purpose-named P-256 statement key, never the generic `sign`, the device's DH key or a
// WebAuthn key. It lives in the same platform store as the roster signing key (RosterSigningKeys:
// TPM, else DPAPI CurrentUser) in its own `cap-sign` folder, so it is a different key and no new
// secret path exists. Callers get the public point, the algorithm, the tier and Sign only.
//
// There is no local re-key: key loss or rotation needs a person-approved enrollment of a new
// device. So a torn, damaged or missing entry, or a key the roster names that this profile no
// longer holds, is the one fixed failure "no-capsign", never a fresh key.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.Transport;

namespace AgentBridge.App.Core;

public interface ICapSignKey : IDisposable
{
    string Algorithm { get; }
    // "tpm" or "dpapi": the spec asks clients to report the custody tier.
    string Tier { get; }
    byte[] PublicPoint { get; }
    // ECDSA P-256 over SHA-256(message), minimal low-S DER.
    byte[] Sign(byte[] message);
}

public interface ICapSignKeys
{
    // Creates the key on first need. `enrolled` is the capSign point the roster names for this
    // device, if any: a profile without that exact key refuses rather than making another.
    ICapSignKey OpenOrCreate(byte[]? enrolled = null);
}

// A fixed code with no inner exception: provider and parser errors can echo paths or file bytes.
public sealed class CapSignUnavailableException() : Exception("no-capsign");

public sealed class CapSignKeys(string profile, KeyStorage? prefer = null) : ICapSignKeys
{
    public const string Algorithm = "p256-sha256-der";

    public ICapSignKey OpenOrCreate(byte[]? enrolled = null)
    {
        if (!OperatingSystem.IsWindows()) { throw new CapSignUnavailableException(); }
        RosterSigningKey? opened = null;
        try
        {
            var folder = Path.Combine(profile, "cap-sign");
            if (enrolled is not null && !Directory.Exists(folder)) { throw new CapSignUnavailableException(); }
            Directory.CreateDirectory(folder);
            using var guard = Gate(Path.Combine(folder, "cap-sign.lock"));
            var commit = Path.Combine(folder, "cap-sign.public");
            var committed = Present(commit);
            var reference = Present(Path.Combine(folder, "signing-key.json"));
            var software = Present(Path.Combine(folder, "signing-key.dpapi"));
            // The shared store creates whenever its reference is missing; never let that turn a
            // committed or enrolled identity, or a torn first write, into a new key.
            if (committed ? !reference : reference || software || enrolled is not null) { throw new CapSignUnavailableException(); }
            var expected = committed ? File.ReadAllBytes(commit) : null;
            if (expected is not null && expected.Length != 65) { throw new CapSignUnavailableException(); }
            opened = committed ? RosterSigningKeys.OpenExisting(folder) : RosterSigningKeys.LoadOrCreate(folder, prefer);
            if (expected is not null && (!expected.AsSpan().SequenceEqual(opened.Public) || (opened.Storage == KeyStorage.Dpapi) != software))
            { throw new CapSignUnavailableException(); }
            if (enrolled is not null && !enrolled.AsSpan().SequenceEqual(opened.Public)) { throw new CapSignUnavailableException(); }
            if (!committed)
            {
                // Only a flushed commit makes the key usable; a crash before it leaves refused state.
                using var record = new FileStream(commit, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                record.Write(opened.Public);
                record.Flush(true);
            }
            var result = new Key(opened);
            opened = null;
            return result;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
            or CryptographicException or JsonException or ArgumentException or PlatformNotSupportedException)
        { throw new CapSignUnavailableException(); }
        finally { opened?.Dispose(); }
    }

    static bool Present(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) { throw new CapSignUnavailableException(); }
            return true;
        }
        catch (FileNotFoundException) { return false; }
    }

    static FileStream Gate(string path)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33 && elapsed.Elapsed < TimeSpan.FromSeconds(30)) { Thread.Sleep(10); }
        }
    }

    sealed class Key(RosterSigningKey key) : ICapSignKey
    {
        public string Algorithm => CapSignKeys.Algorithm;
        public string Tier => key.Storage == KeyStorage.Tpm ? "tpm" : "dpapi";
        public byte[] PublicPoint => key.Public.ToArray();
        public byte[] Sign(byte[] message) => key.Sign(message);
        public override string ToString() => $"capSign {Tier}";
        public void Dispose() => key.Dispose();
    }
}
