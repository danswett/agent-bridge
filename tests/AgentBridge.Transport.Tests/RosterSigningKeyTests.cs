using System.Numerics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using AgentBridge.Roster;
using Xunit;

namespace AgentBridge.Transport.Tests;

// A person's device's roster signing key (#55 R1d): in the TPM where it can sign with P-256, else
// DPAPI, and its signatures what the roster accepts. Every test works in its own temporary root.
// The TPM test runs only on a machine with such a TPM (GitHub's runners have none); see
// docs/protocol/devices.md, "Signing keys", for checking it by hand.
public sealed class RosterSigningKeyTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"agent-bridge-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (OperatingSystem.IsWindows() && Directory.Exists(root))
        {
            // A TPM key lives outside the folder; deleting the folder alone would leak it.
            foreach (var folder in Directory.GetDirectories(root)) { RosterSigningKeys.Delete(folder); }
        }
        try { Directory.Delete(root, true); } catch (DirectoryNotFoundException) { }
    }

    // Signed many times, so a high S from the platform is seen and lowered.
    static void SignsAsTheRosterRequires(RosterSigningKey key)
    {
        for (var i = 0; i < 64; i++)
        {
            var message = Entry.SignedBy(System.Text.Encoding.UTF8.GetBytes($"{{\"n\":{i}}}"));
            var der = key.Sign(message);
            P256.CheckRosterSignature(der, "sig");
            Assert.True(P256.Verify(key.Public, message, der), $"signature {i} does not verify");
        }
    }

    [WindowsFact("DPAPI exists only on Windows")]
    [SupportedOSPlatform("windows")]
    public void Concurrent_first_uses_all_get_the_one_key_the_first_made()
    {
        var folder = Path.Combine(root, "race");
        var keys = Enumerable.Range(0, 8).Select(_ => Task.Run(() => { using var k = RosterSigningKeys.LoadOrCreate(folder, KeyStorage.Dpapi); return k.Public; })).ToArray();
        Task.WaitAll(keys);
        using var after = RosterSigningKeys.LoadOrCreate(folder);
        Assert.All(keys, k => Assert.Equal(after.Public, k.Result));
    }

    [WindowsFact("DPAPI exists only on Windows")]
    [SupportedOSPlatform("windows")]
    public void A_DPAPI_signing_key_is_encrypted_on_disk_signs_as_the_roster_requires_and_reloads_as_itself()
    {
        var folder = Path.Combine(root, "dpapi");
        using var made = RosterSigningKeys.LoadOrCreate(folder, KeyStorage.Dpapi);
        using var again = RosterSigningKeys.LoadOrCreate(folder);

        Assert.Equal(KeyStorage.Dpapi, made.Storage);
        Assert.Equal(made.Public, again.Public);
        Assert.True(P256.IsPoint(made.Public));
        SignsAsTheRosterRequires(made);
        Assert.True(P256.Verify(made.Public, [1, 2, 3], again.Sign([1, 2, 3])));
        var onDisk = File.ReadAllBytes(Path.Combine(folder, "signing-key.dpapi"));
        Assert.True(onDisk.AsSpan().IndexOf(made.SoftwareD) < 0, "the private key is on disk in the clear");

        onDisk[^1] ^= 1;
        File.WriteAllBytes(Path.Combine(folder, "signing-key.dpapi"), onDisk);
        Assert.ThrowsAny<CryptographicException>(() => RosterSigningKeys.LoadOrCreate(folder));
    }

    [WindowsFact("the device key and the signing key are kept only on Windows here")]
    [SupportedOSPlatform("windows")]
    public void The_signing_key_is_its_own_key_beside_the_device_key_and_forgetting_it_removes_its_files()
    {
        var folder = Path.Combine(root, "both");
        var device = DeviceKeys.LoadOrCreate(folder, KeyStorage.Dpapi);
        using var sign = RosterSigningKeys.LoadOrCreate(folder, KeyStorage.Dpapi);

        Assert.NotEqual(device.Key.Public, sign.Public);
        RosterSigningKeys.Delete(folder);
        Assert.False(RosterSigningKeys.Exists(folder));
        Assert.False(File.Exists(Path.Combine(folder, "signing-key.dpapi")));
        Assert.True(DeviceKeys.Exists(folder));
        DeviceKeys.Delete(folder);
    }

    [TpmSigningFact]
    [SupportedOSPlatform("windows")]
    public void A_TPM_signing_key_cannot_be_exported_signs_as_the_roster_requires_and_is_deleted_from_the_TPM_itself()
    {
        var folder = Path.Combine(root, "tpm");
        using var made = RosterSigningKeys.LoadOrCreate(folder, KeyStorage.Tpm);
        using var again = RosterSigningKeys.LoadOrCreate(folder);

        Assert.Equal(KeyStorage.Tpm, made.Storage);
        Assert.Equal(made.Public, again.Public);
        Assert.False(made.IsExportable);
        Assert.Null(made.SoftwareD);
        SignsAsTheRosterRequires(again);
        Assert.False(File.Exists(Path.Combine(folder, "signing-key.dpapi")));

        var name = made.TpmKeyName!;
        RosterSigningKeys.Delete(folder);
        Assert.False(CngKey.Exists(name, new CngProvider(TpmP256.ProviderName)));
    }

    static readonly BigInteger N = BigInteger.Parse("0ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551", System.Globalization.NumberStyles.HexNumber);

    static byte[] Raw(BigInteger r, BigInteger s) => [.. r.ToByteArray(true, true).Reverse().Concat(new byte[32]).Take(32).Reverse(), .. s.ToByteArray(true, true).Reverse().Concat(new byte[32]).Take(32).Reverse()];

    [Fact]
    public void A_platform_signature_becomes_the_same_minimal_low_s_der_the_roster_library_makes()
    {
        var rng = new Random(55);
        for (var i = 0; i < 200; i++)
        {
            var raw = new byte[64];
            rng.NextBytes(raw);
            raw[0] &= 0x7f;
            // Even: a low S. Odd: the top bit set, so S is over n/2 and is lowered.
            raw[32] = (byte)(i % 2 == 0 ? raw[32] & 0x7f : (raw[32] | 0x80) & 0xfe);
            if (i % 50 == 0) { raw.AsSpan(0, 31).Clear(); raw[31] |= 1; }
            Assert.Equal(P256.FromRaw(raw), RosterSignature.FromP1363(raw));
        }
        Assert.Equal(P256.FromRaw(Raw(1, N - 1)), RosterSignature.FromP1363(Raw(1, N - 1)));
        Assert.Throws<CryptographicException>(() => RosterSignature.FromP1363(Raw(0, 1)));
        Assert.Throws<CryptographicException>(() => RosterSignature.FromP1363(Raw(1, N)));
        Assert.Throws<CryptographicException>(() => RosterSignature.FromP1363(new byte[63]));
    }
}

public sealed class TpmSigningFactAttribute : FactAttribute
{
    public TpmSigningFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || !TpmP256.IsAvailable(out _, "ECDSA_P256")) { Skip = "this machine has no TPM that can sign with a P-256 key (GitHub's runners have none)"; }
    }
}
