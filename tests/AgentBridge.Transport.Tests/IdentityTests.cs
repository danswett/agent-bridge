using System.Runtime.Versioning;
using System.Security.Cryptography;
using Xunit;

namespace AgentBridge.Transport.Tests;

// Profiles and device keys (spike S6). Every test works in its own temporary root, never
// the real profile folder.
public sealed class IdentityTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"agent-bridge-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (OperatingSystem.IsWindows() && Directory.Exists(root))
        {
            // A TPM key lives outside the folder; deleting the folder alone would leak it.
            foreach (var folder in Directory.GetDirectories(root)) { DeviceKeys.Delete(folder); }
        }
        try { Directory.Delete(root, true); } catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void The_default_identity_folder_is_agent_bridges_own_and_never_agent_ha_bridges()
    {
        var expected = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentBridge")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agent-bridge");

        Assert.Equal(expected, Identity.DefaultRoot);
        Assert.DoesNotContain("ha-bridge", Identity.DefaultRoot, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:")]
    public void A_profile_name_that_is_a_path_is_refused_and_nothing_is_written(string profile)
    {
        Assert.Throws<ArgumentException>(() => Identity.Load(profile, KeyStorage.File, root));

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void A_profile_keeps_its_key_and_account_across_loads()
    {
        var first = Identity.Load("p1", KeyStorage.File, root);
        var account = new SignInResult("token", "1", "octo");
        first.Save(account);

        var again = Identity.Load("p1", null, root);
        var other = Identity.Load("p2", KeyStorage.File, root);

        Assert.Equal(first.Key.Public, again.Key.Public);
        Assert.Equal(KeyStorage.File, again.Storage);
        Assert.Equal(account, again.Account);
        Assert.Equal(Path.Combine(root, "p1"), again.Folder);
        Assert.NotEqual(first.Key.Public, other.Key.Public);
        Assert.Null(other.Account);
    }

    [NotWindowsFact("Windows keeps the key in the TPM or DPAPI, under the user's own %LOCALAPPDATA%")]
    public void A_new_profile_off_Windows_keeps_its_key_and_token_where_only_the_owner_can_read_them()
    {
        if (OperatingSystem.IsWindows()) { return; }
        var id = Identity.Load("p", null, root);
        id.Save(new SignInResult("token", "1", "octo"));

        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(KeyStorage.File, id.Storage);
        Assert.Equal(ownerOnly, File.GetUnixFileMode(Path.Combine(id.Folder, "key.json")));
        Assert.Equal(ownerOnly, File.GetUnixFileMode(Path.Combine(id.Folder, "account.json")));
        Assert.Equal(ownerOnly | UnixFileMode.UserExecute, File.GetUnixFileMode(id.Folder));
    }

    [NotWindowsFact("Windows has both; the DPAPI and TPM tests cover them")]
    public void A_device_key_protection_asked_for_by_name_where_it_does_not_exist_is_refused_not_quietly_stored_in_plaintext()
    {
        foreach (var storage in new[] { KeyStorage.Tpm, KeyStorage.Dpapi })
        {
            var profile = $"asked-{storage}";

            Assert.Throws<PlatformNotSupportedException>(() => Identity.Load(profile, storage, root));

            Assert.False(File.Exists(Path.Combine(root, profile, "key.json")));
        }
    }

    [WindowsFact("DPAPI exists only on Windows")]
    [SupportedOSPlatform("windows")]
    public void A_DPAPI_key_is_encrypted_on_disk_the_same_key_on_reload_and_a_tampered_file_is_refused()
    {
        var folder = Path.Combine(root, "dpapi");
        var made = DeviceKeys.LoadOrCreate(folder, KeyStorage.Dpapi);
        var again = DeviceKeys.LoadOrCreate(folder);
        using var peer = P256KeyPair.Generate();

        Assert.Equal(KeyStorage.Dpapi, made.Storage);
        Assert.Equal(made.Key.Public, again.Key.Public);
        Assert.Equal(peer.Dh(made.Key.Public), again.Key.Dh(peer.Public));
        var (d, _, _) = made.Key.Export();
        var onDisk = File.ReadAllBytes(Path.Combine(folder, "device-key.dpapi"));
        Assert.True(onDisk.AsSpan().IndexOf(d) < 0, "the private key is on disk in the clear");

        onDisk[^1] ^= 1;
        File.WriteAllBytes(Path.Combine(folder, "device-key.dpapi"), onDisk);
        Assert.ThrowsAny<CryptographicException>(() => DeviceKeys.LoadOrCreate(folder));
    }

    [WindowsFact("the choice is between the TPM and DPAPI, which exist only on Windows")]
    [SupportedOSPlatform("windows")]
    public void A_new_profile_on_Windows_chooses_the_TPM_when_there_is_one_and_DPAPI_otherwise()
    {
        var id = Identity.Load("default-choice", null, root);

        Assert.Equal(TpmP256.IsAvailable(out _) ? KeyStorage.Tpm : KeyStorage.Dpapi, id.Storage);
        Assert.False(File.Exists(Path.Combine(id.Folder, "key.json")));
    }

    [TpmFact]
    [SupportedOSPlatform("windows")]
    public void A_TPM_key_reopens_as_the_same_key_agrees_like_software_ECDH_and_is_deleted_from_the_TPM_itself()
    {
        var folder = Path.Combine(root, "tpm");
        var made = DeviceKeys.LoadOrCreate(folder, KeyStorage.Tpm);
        var again = DeviceKeys.LoadOrCreate(folder);

        Assert.Equal(KeyStorage.Tpm, made.Storage);
        Assert.Equal(made.Key.Public, again.Key.Public);
        Assert.False(made.Key.IsExportable);
        for (int i = 0; i < 5; i++)
        {
            using var peer = P256KeyPair.Generate();
            Assert.Equal(peer.Dh(made.Key.Public), again.Key.Dh(peer.Public));
        }

        DeviceKeys.Delete(folder);

        Assert.False(TpmP256.Exists(made.TpmKeyName!));
    }
}
