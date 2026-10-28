// Spike S6: a device's long-term P-256 key, kept where it cannot be copied.
//
//   TPM    Windows' Platform Crypto Provider. The private key never leaves the TPM; the
//          file on disk is a TPM-wrapped blob that only this TPM can load.
//   DPAPI  No usable TPM: a software key, encrypted to the Windows user (CurrentUser).
//   File   Elsewhere (macOS uses the Secure Enclave or Keychain in P5; not this spike).
//
// The protocol needs exactly three things from the key: its public key, and raw ECDH
// (the X coordinate) for the Noise static DH and the relay's proof of possession.
// P256KeyPair.FromAgreement delegates DH, so nothing above it changes.
//
// .NET's own wrappers cannot be used with the TPM provider: ECDiffieHellmanCng hands it
// a peer key held by the software provider (NTE_INVALID_HANDLE), and CngKey.Import into
// it passes flags it refuses (NTE_BAD_FLAGS). So agreement goes straight to NCrypt.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using S2.Noise;

namespace S6;

public enum KeyStorage { Tpm, Dpapi, File }

public sealed record DeviceKey(P256KeyPair Key, KeyStorage Storage, string? TpmKeyName);

public static class DeviceKeys
{
    const string RefFile = "device-key.json";
    const string DpapiFile = "device-key.dpapi";
    static readonly byte[] DpapiEntropy = "agent-bridge-device-key-v1"u8.ToArray();

    sealed record KeyRef(string Storage, string? Name);

    // Opens the key a profile folder already has, or creates one: in the TPM when there
    // is one, else DPAPI. A key never moves between storages: a TPM key that can no
    // longer be opened (TPM cleared, disk moved to another machine) is an error, and the
    // device must be paired again, not quietly given a weaker key.
    public static DeviceKey LoadOrCreate(string folder, KeyStorage? prefer = null)
    {
        Directory.CreateDirectory(folder);
        var refPath = Path.Combine(folder, RefFile);
        if (File.Exists(refPath))
        {
            var r = JsonSerializer.Deserialize<KeyRef>(File.ReadAllText(refPath)) ?? throw new InvalidDataException(RefFile);
            return r.Storage switch
            {
                "tpm" when OperatingSystem.IsWindows() => new(TpmP256.Open(r.Name!).ToKeyPair(), KeyStorage.Tpm, r.Name),
                "dpapi" when OperatingSystem.IsWindows() => new(LoadDpapi(folder), KeyStorage.Dpapi, null),
                _ => throw new PlatformNotSupportedException($"a {r.Storage} key cannot be opened here"),
            };
        }

        var storage = prefer ?? (OperatingSystem.IsWindows() ? (TpmP256.IsAvailable(out _) ? KeyStorage.Tpm : KeyStorage.Dpapi) : KeyStorage.File);
        switch (storage)
        {
            case KeyStorage.Tpm when OperatingSystem.IsWindows():
                {
                    var name = $"agent-bridge-device-{Guid.NewGuid():N}";
                    var made = new DeviceKey(TpmP256.Create(name).ToKeyPair(), KeyStorage.Tpm, name);
                    File.WriteAllText(refPath, JsonSerializer.Serialize(new KeyRef("tpm", name)));
                    return made;
                }
            case KeyStorage.Dpapi when OperatingSystem.IsWindows():
                {
                    using var e = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                    var pkcs8 = e.ExportPkcs8PrivateKey();
                    try { File.WriteAllBytes(Path.Combine(folder, DpapiFile), ProtectedData.Protect(pkcs8, DpapiEntropy, DataProtectionScope.CurrentUser)); }
                    finally { CryptographicOperations.ZeroMemory(pkcs8); }
                    File.WriteAllText(refPath, JsonSerializer.Serialize(new KeyRef("dpapi", null)));
                    return new(LoadDpapi(folder), KeyStorage.Dpapi, null);
                }
            default:
                throw new PlatformNotSupportedException($"{storage} keys are not available on this platform");
        }
    }

    public static bool Exists(string folder) => File.Exists(Path.Combine(folder, RefFile));

    // Forgetting a device (sign-out, uninstall): the TPM key is deleted, not just its file.
    public static void Delete(string folder)
    {
        var refPath = Path.Combine(folder, RefFile);
        if (!File.Exists(refPath)) { return; }
        var r = JsonSerializer.Deserialize<KeyRef>(File.ReadAllText(refPath));
        if (r?.Storage == "tpm" && OperatingSystem.IsWindows()) { TpmP256.DeleteIfExists(r.Name!); }
        File.Delete(Path.Combine(folder, DpapiFile));
        File.Delete(refPath);
    }

    [SupportedOSPlatform("windows")]
    static P256KeyPair LoadDpapi(string folder)
    {
        var pkcs8 = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(folder, DpapiFile)), DpapiEntropy, DataProtectionScope.CurrentUser);
        try
        {
            using var e = ECDiffieHellman.Create();
            e.ImportPkcs8PrivateKey(pkcs8, out _);
            var p = e.ExportParameters(true);
            return P256KeyPair.FromParameters(p.D!, p.Q.X!, p.Q.Y!);
        }
        finally { CryptographicOperations.ZeroMemory(pkcs8); }
    }
}

[SupportedOSPlatform("windows")]
public sealed class TpmP256 : IDisposable
{
    public const string ProviderName = "Microsoft Platform Crypto Provider";
    static readonly CngProvider Provider = new(ProviderName);

    readonly CngKey key;
    readonly SafeNCryptProviderHandle provider;
    readonly Lock gate = new();
    public string Name { get; }
    public byte[] Public { get; }
    public CngKey CngKey => key;

    TpmP256(CngKey key)
    {
        this.key = key;
        Name = key.KeyName!;
        Check(NCryptOpenStorageProvider(out provider, ProviderName, 0), "open provider");
        // BCRYPT_ECCPUBLIC_BLOB: magic, cbKey, X, Y.
        var blob = key.Export(CngKeyBlobFormat.EccPublicBlob);
        var pub = new byte[NoiseConst.PubLen];
        pub[0] = 0x04;
        blob.AsSpan(8, 64).CopyTo(pub.AsSpan(1));
        Public = pub;
    }

    // "TPM-Version:2.0 -Level:0-Revision:1.59-VendorID:'MSFT'-..." on a Hyper-V virtual
    // TPM, for example. False when there is no usable TPM.
    public static bool IsAvailable(out string? platform)
    {
        platform = null;
        if (NCryptOpenStorageProvider(out var p, ProviderName, 0) != 0) { return false; }
        using (p)
        {
            var buf = new byte[512];
            if (NCryptGetProperty(p, "PCP_PLATFORM_TYPE", buf, buf.Length, out var n, 0) != 0) { return false; }
            platform = System.Text.Encoding.Unicode.GetString(buf, 0, n).TrimEnd('\0');
            return true;
        }
    }

    // A per-user key (not a machine key), so another user on the machine cannot use it.
    public static TpmP256 Create(string name) => new(CngKey.Create(CngAlgorithm.ECDiffieHellmanP256, name, new CngKeyCreationParameters
    {
        Provider = Provider,
        ExportPolicy = CngExportPolicies.None,
        KeyCreationOptions = CngKeyCreationOptions.None,
    }));

    public static TpmP256 Open(string name) => new(CngKey.Open(name, Provider));

    public static bool Exists(string name) => CngKey.Exists(name, Provider);

    public static void DeleteIfExists(string name)
    {
        if (Exists(name)) { using var k = CngKey.Open(name, Provider); k.Delete(); }
    }

    public P256KeyPair ToKeyPair() => P256KeyPair.FromAgreement(Public, Agree, this);

    // Raw ECDH in the TPM. Callers reach this through P256KeyPair.Dh, which has already
    // refused off-curve points.
    public byte[] Agree(byte[] peerPublic)
    {
        var blob = new byte[8 + 64];
        BitConverter.GetBytes(0x314B4345).CopyTo(blob, 0); // ECK1: ECDH public, P-256
        BitConverter.GetBytes(32).CopyTo(blob, 4);
        peerPublic.AsSpan(1, 64).CopyTo(blob.AsSpan(8));
        lock (gate)
        {
            Check(NCryptImportKey(provider, IntPtr.Zero, "ECCPUBLICBLOB", IntPtr.Zero, out var peer, blob, blob.Length, 0), "import peer key");
            using (peer)
            {
                Check(NCryptSecretAgreement(key.Handle, peer, out var secret, 0), "secret agreement");
                using (secret)
                {
                    // BCRYPT_KDF_RAW_SECRET ("TRUNCATE"): the shared X coordinate itself, which
                    // CNG returns little-endian. Noise and WebCrypto use it big-endian.
                    Check(NCryptDeriveKey(secret, "TRUNCATE", IntPtr.Zero, null, 0, out var n, 0), "derive (size)");
                    var z = new byte[n];
                    Check(NCryptDeriveKey(secret, "TRUNCATE", IntPtr.Zero, z, n, out n, 0), "derive");
                    Array.Reverse(z, 0, n);
                    return z[..n];
                }
            }
        }
    }

    public void Delete() => key.Delete();

    public void Dispose()
    {
        key.Dispose();
        provider.Dispose();
    }

    static void Check(int status, string what)
    {
        if (status != 0) { throw new CryptographicException($"TPM {what} failed: 0x{status:X8}") { HResult = status }; }
    }

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    static extern int NCryptOpenStorageProvider(out SafeNCryptProviderHandle provider, string name, int flags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    static extern int NCryptGetProperty(SafeNCryptProviderHandle obj, string property, byte[] output, int size, out int result, int flags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    static extern int NCryptImportKey(SafeNCryptProviderHandle provider, IntPtr importKey, string blobType, IntPtr parameters, out SafeNCryptKeyHandle key, byte[] blob, int size, int flags);

    [DllImport("ncrypt.dll")]
    static extern int NCryptSecretAgreement(SafeNCryptKeyHandle privateKey, SafeNCryptKeyHandle publicKey, out SafeNCryptSecretHandle secret, int flags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    static extern int NCryptDeriveKey(SafeNCryptSecretHandle secret, string kdf, IntPtr parameters, byte[]? output, int size, out int result, int flags);
}
