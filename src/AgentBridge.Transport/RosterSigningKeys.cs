// A person's device's roster signing key (design §3.2, `sign`; #55 R1d), kept where it cannot be
// copied, as its ECDH device key is (DeviceKeys):
//
//   TPM    Windows' Platform Crypto Provider, an ECDSA P-256 key. The private key never leaves
//          the TPM; only its name is on disk.
//   DPAPI  No TPM that can sign with P-256: a software key, encrypted to the Windows user.
//
// It is a separate key from the device's ECDH key: the roster names both (`dh` and `sign`), and a
// key used for one never signs or agrees for the other. Elsewhere there is no store yet (the
// Apple client's Secure Enclave comes with A1, and a host has no signing key at all).
//
// A roster signature is minimal DER with a low S (§3.2): CNG and .NET sign in IEEE P1363 (r ‖ s),
// and either S of a pair verifies, so S is replaced by n − S when it is the high one.

using System.Numerics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentBridge.Transport;

public sealed class RosterSigningKey : IDisposable
{
    readonly ECDsa key;

    internal RosterSigningKey(ECDsa key, KeyStorage storage, string? tpmKeyName)
    {
        this.key = key;
        Storage = storage;
        TpmKeyName = tpmKeyName;
        var q = key.ExportParameters(false).Q;
        Public = [0x04, .. q.X!, .. q.Y!];
    }

    public KeyStorage Storage { get; }
    public string? TpmKeyName { get; }
    // The uncompressed point, as the roster's `sign` carries it.
    public byte[] Public { get; }

    // ECDSA over SHA-256(message), as minimal low-S DER: an entry's `sig` over F(label, body), a
    // veto's or a person command's.
    public byte[] Sign(byte[] message) => RosterSignature.FromP1363(key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    // The private scalar of a DPAPI key, for tests that check it is not on disk in the clear.
    internal byte[]? SoftwareD => Storage == KeyStorage.Dpapi ? key.ExportParameters(true).D : null;

    public bool IsExportable => !(OperatingSystem.IsWindows() && key is ECDsaCng cng && cng.Key.ExportPolicy == CngExportPolicies.None);

    public void Dispose() => key.Dispose();
}

public static class RosterSigningKeys
{
    const string RefFile = "signing-key.json";
    const string DpapiFile = "signing-key.dpapi";
    static readonly byte[] DpapiEntropy = "agent-bridge-roster-signing-key-v1"u8.ToArray();

    sealed record KeyRef(string Storage, string? Name);

    // Opens the signing key a profile folder already has, or creates one: in the TPM when it can
    // sign with P-256, else DPAPI. As with the device key, a key never moves between storages: a
    // TPM key that can no longer be opened is an error, and the device must be enrolled again.
    [SupportedOSPlatform("windows")]
    public static RosterSigningKey LoadOrCreate(string folder, KeyStorage? prefer = null)
    {
        Directory.CreateDirectory(folder);
        var refPath = Path.Combine(folder, RefFile);
        if (File.Exists(refPath))
        {
            var r = JsonSerializer.Deserialize<KeyRef>(File.ReadAllText(refPath)) ?? throw new InvalidDataException(RefFile);
            return r.Storage switch
            {
                "tpm" => new(new ECDsaCng(CngKey.Open(r.Name!, TpmP256Signing.Provider)), KeyStorage.Tpm, r.Name),
                "dpapi" => new(LoadDpapi(folder), KeyStorage.Dpapi, null),
                _ => throw new PlatformNotSupportedException($"a {r.Storage} signing key cannot be opened here"),
            };
        }

        var storage = prefer ?? (TpmP256.IsAvailable(out _, TpmP256Signing.Algorithm) ? KeyStorage.Tpm : KeyStorage.Dpapi);
        switch (storage)
        {
            case KeyStorage.Tpm:
                {
                    var name = $"agent-bridge-roster-sign-{Guid.NewGuid():N}";
                    var made = new RosterSigningKey(new ECDsaCng(TpmP256Signing.Create(name)), KeyStorage.Tpm, name);
                    File.WriteAllText(refPath, JsonSerializer.Serialize(new KeyRef("tpm", name)));
                    return made;
                }
            case KeyStorage.Dpapi:
                {
                    using var e = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                    var pkcs8 = e.ExportPkcs8PrivateKey();
                    try { File.WriteAllBytes(Path.Combine(folder, DpapiFile), ProtectedData.Protect(pkcs8, DpapiEntropy, DataProtectionScope.CurrentUser)); }
                    finally { CryptographicOperations.ZeroMemory(pkcs8); }
                    File.WriteAllText(refPath, JsonSerializer.Serialize(new KeyRef("dpapi", null)));
                    return new(LoadDpapi(folder), KeyStorage.Dpapi, null);
                }
            default:
                throw new PlatformNotSupportedException($"{storage} signing keys are not available");
        }
    }

    public static bool Exists(string folder) => File.Exists(Path.Combine(folder, RefFile));

    // Forgetting the device: a TPM key is deleted from the TPM, not just its file.
    public static void Delete(string folder)
    {
        var refPath = Path.Combine(folder, RefFile);
        if (!File.Exists(refPath)) { return; }
        var r = JsonSerializer.Deserialize<KeyRef>(File.ReadAllText(refPath));
        if (r?.Storage == "tpm" && OperatingSystem.IsWindows() && CngKey.Exists(r.Name!, TpmP256Signing.Provider))
        {
            using var k = CngKey.Open(r.Name!, TpmP256Signing.Provider);
            k.Delete();
        }
        File.Delete(Path.Combine(folder, DpapiFile));
        File.Delete(refPath);
    }

    [SupportedOSPlatform("windows")]
    static ECDsa LoadDpapi(string folder)
    {
        var pkcs8 = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(folder, DpapiFile)), DpapiEntropy, DataProtectionScope.CurrentUser);
        try
        {
            var e = ECDsa.Create();
            e.ImportPkcs8PrivateKey(pkcs8, out _);
            if (e.KeySize != 256) { e.Dispose(); throw new CryptographicException("the signing key is not P-256"); }
            return e;
        }
        finally { CryptographicOperations.ZeroMemory(pkcs8); }
    }
}

// The TPM side: an ECDSA P-256 key in the Platform Crypto Provider. Signing needs no peer key, so
// .NET's ECDsaCng works with it, unlike ECDH (DeviceKeys).
[SupportedOSPlatform("windows")]
static class TpmP256Signing
{
    public const string Algorithm = "ECDSA_P256";
    public static readonly CngProvider Provider = new(TpmP256.ProviderName);

    // A per-user key that cannot be exported.
    public static CngKey Create(string name) => CngKey.Create(CngAlgorithm.ECDsaP256, name, new CngKeyCreationParameters
    {
        Provider = Provider,
        ExportPolicy = CngExportPolicies.None,
        KeyCreationOptions = CngKeyCreationOptions.None,
    });
}

// IEEE P1363 r ‖ s as the minimal low-S DER a roster signature is (the same rule as the roster
// library's P256.FromRaw, which this assembly does not reference).
public static class RosterSignature
{
    static readonly BigInteger N = BigInteger.Parse("0ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551", System.Globalization.NumberStyles.HexNumber);
    static readonly BigInteger HalfN = N >> 1;

    public static byte[] FromP1363(ReadOnlySpan<byte> raw)
    {
        if (raw.Length != 64) { throw new CryptographicException("a P-256 signature is 64 bytes"); }
        var r = new BigInteger(raw[..32], isUnsigned: true, isBigEndian: true);
        var s = new BigInteger(raw[32..], isUnsigned: true, isBigEndian: true);
        if (r.IsZero || s.IsZero || r >= N || s >= N) { throw new CryptographicException("r or s out of range"); }
        if (s > HalfN) { s = N - s; }
        var rb = Integer(r);
        var sb = Integer(s);
        return [0x30, (byte)(4 + rb.Length + sb.Length), 0x02, (byte)rb.Length, .. rb, 0x02, (byte)sb.Length, .. sb];
    }

    // A DER INTEGER's content: big-endian, minimal, with a 0 first when the top bit is set.
    static byte[] Integer(BigInteger v) => v.ToByteArray(isUnsigned: false, isBigEndian: true);
}
