using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Roster;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Xunit;

namespace AgentBridge.App.Tests;

// The person's capSign key (docs/protocol/devices.md, "Signer and enrollment" and "Custody"):
// made once in the platform store the roster signing key uses, never re-made locally. DPAPI is
// forced so the tests can look for the private scalar; the TPM tier is the same code path.
[SupportedOSPlatform("windows")]
public sealed class CapSignKeysTests : IDisposable
{
    readonly string profile = Path.Combine(Path.GetTempPath(), $"agent-bridge-capsign-{Guid.NewGuid():N}");
    string Folder => Path.Combine(profile, "cap-sign");
    CapSignKeys Keys => new(profile, KeyStorage.Dpapi);

    public void Dispose()
    {
        if (OperatingSystem.IsWindows()) { RosterSigningKeys.Delete(Folder); RosterSigningKeys.Delete(profile); }
        try { Directory.Delete(profile, true); } catch (DirectoryNotFoundException) { }
    }

    byte[] PrivateScalar()
    {
        var pkcs8 = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(Folder, "signing-key.dpapi")), "agent-bridge-roster-signing-key-v1"u8.ToArray(), DataProtectionScope.CurrentUser);
        using var e = ECDsa.Create();
        e.ImportPkcs8PrivateKey(pkcs8, out _);
        return e.ExportParameters(true).D!;
    }

    [WindowsFact("capSign custody is Windows-only here")]
    public void The_first_need_creates_the_key_and_a_second_call_returns_the_same_public_key()
    {
        using var made = Keys.OpenOrCreate();
        using var again = Keys.OpenOrCreate(enrolled: made.PublicPoint);

        Assert.True(P256.IsPoint(made.PublicPoint));
        Assert.Equal(made.PublicPoint, again.PublicPoint);
        Assert.Equal("p256-sha256-der", made.Algorithm);
        Assert.Equal("dpapi", made.Tier);
    }

    [WindowsFact("capSign custody is Windows-only here")]
    public void A_capSign_signature_is_low_S_DER_that_verifies_with_its_public_key()
    {
        using var key = Keys.OpenOrCreate();
        for (var i = 0; i < 32; i++)
        {
            var message = Encoding.UTF8.GetBytes($"capSign {i}");
            var der = key.Sign(message);
            P256.CheckRosterSignature(der, "sig");
            Assert.True(P256.Verify(key.PublicPoint, message, der));
        }
    }

    [WindowsFact("capSign custody is Windows-only here")]
    public void The_capSign_key_is_distinct_from_the_profile_generic_sign_key()
    {
        using var sign = RosterSigningKeys.LoadOrCreate(profile, KeyStorage.Dpapi);
        using var cap = Keys.OpenOrCreate();
        Assert.NotEqual(sign.Public, cap.PublicPoint);
    }

    [WindowsFact("capSign custody is Windows-only here")]
    public void No_private_material_appears_in_serialized_state_files_in_the_clear_or_failure_text()
    {
        using var key = Keys.OpenOrCreate();
        var d = PrivateScalar();
        var serialized = JsonSerializer.Serialize<object>(key) + key;
        Assert.DoesNotContain(Convert.ToHexString(d), serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(d), serialized);
        foreach (var file in Directory.GetFiles(Folder))
        { Assert.True(File.ReadAllBytes(file).AsSpan().IndexOf(d) < 0, $"{Path.GetFileName(file)} holds the private key in the clear"); }

        File.WriteAllBytes(Path.Combine(Folder, "signing-key.dpapi"), [1, 2, 3]);
        var refused = Assert.Throws<CapSignUnavailableException>(() => Keys.OpenOrCreate());
        Assert.Equal("no-capsign", refused.Message);
        Assert.Null(refused.InnerException);
    }

    [WindowsFact("capSign custody is Windows-only here")]
    public void A_missing_store_entry_is_a_plain_failure_and_is_never_regenerated()
    {
        byte[] committed;
        using (var made = Keys.OpenOrCreate()) { committed = made.PublicPoint; }
        File.Delete(Path.Combine(Folder, "signing-key.json"));

        Assert.Throws<CapSignUnavailableException>(() => Keys.OpenOrCreate());
        Assert.Throws<CapSignUnavailableException>(() => Keys.OpenOrCreate());
        Assert.False(File.Exists(Path.Combine(Folder, "signing-key.json")));
        Assert.Equal(committed, File.ReadAllBytes(Path.Combine(Folder, "cap-sign.public")));
    }

    [WindowsFact("capSign custody is Windows-only here")]
    public void A_cleared_profile_that_the_roster_still_names_reports_no_capSign_and_creates_nothing()
    {
        byte[] enrolled;
        using (var made = Keys.OpenOrCreate()) { enrolled = made.PublicPoint; }
        RosterSigningKeys.Delete(Folder);
        Directory.Delete(Folder, true);

        Assert.Throws<CapSignUnavailableException>(() => Keys.OpenOrCreate(enrolled));
        Assert.False(File.Exists(Path.Combine(Folder, "signing-key.json")));
        Assert.False(File.Exists(Path.Combine(Folder, "cap-sign.public")));
    }

    [WindowsFact("capSign custody is Windows-only here")]
    public void A_key_that_differs_from_the_enrolled_one_is_refused()
    {
        using var made = Keys.OpenOrCreate();
        var other = made.PublicPoint.ToArray();
        other[^1] ^= 1;
        Assert.Throws<CapSignUnavailableException>(() => Keys.OpenOrCreate(other));
    }
}
