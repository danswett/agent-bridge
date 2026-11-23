using System.Diagnostics;
using System.Security.Cryptography;
using AgentBridge.App.Core;
using Xunit;

public sealed partial class SlotPreparationTests
{
    SlotPreparation.Prepared Prepare(string? version = null, PackageDigest? digest = null) =>
        SlotPreparation.Prepare(Root, version ?? Version, Package, digest ?? Digest, static v => v == " v0.2.0+build " ? Version : v);

    [Theory]
    [InlineData("../escape")]
    [InlineData(@"..\escape")]
    [InlineData("two/parts")]
    [InlineData(@"two\parts")]
    [InlineData("..")]
    [InlineData("0.2..0")]
    [InlineData("/absolute")]
    [InlineData(@"C:\absolute")]
    [InlineData(@"\\server\share")]
    [InlineData("0.2.0:stream")]
    [InlineData("0.0.0")]
    [InlineData("0.3.0")]
    public void Invalid_or_mismatched_release_identities_are_rejected_before_writing(string version)
    {
        MakePackage();
        Assert.Throws<InvalidDataException>(() => Prepare(version));
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public void A_package_with_the_wrong_digest_never_becomes_a_slot()
    {
        MakePackage();
        Assert.Throws<PackageMismatchException>(() => Prepare(digest: new(PackageDigest.Sha256, new string('0', 64))));
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public void Preparation_copies_the_entire_payload_without_touching_activation_or_legacy_current()
    {
        MakePackage(manifestVersion: " v0.2.0+build ");
        var current = Directory.CreateDirectory(Path.Combine(Root, "current")).FullName;
        var legacy = Path.Combine(current, "running.exe");
        var pointer = Path.Combine(Root, "active-slot.json");
        File.WriteAllText(legacy, "running legacy");
        File.WriteAllText(pointer, "existing pointer");
        using var pinLegacy = new FileStream(legacy, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var pinPointer = new FileStream(pointer, FileMode.Open, FileAccess.Read, FileShare.Read);
        var prepared = Prepare();
        Assert.Equal(Path.Combine("slots", Path.GetFileName(Slot)), prepared.RelativePath);
        Assert.Equal(Version, prepared.Version);
        Assert.Equal(Digest, prepared.Digest);
        Assert.Equal("AgentBridgeApp/ v0.2.0+build ", prepared.ManifestIdentity);
        Assert.Equal(Required.Length + 1, Directory.GetFiles(Slot, "*", SearchOption.AllDirectories).Length);
        Assert.Equal("verified Assets/nested/resource.dat", File.ReadAllText(Path.Combine(Slot, "Assets", "nested", "resource.dat")));
        Assert.Equal(prepared, Prepare());
        Assert.Equal("existing pointer", File.ReadAllText(pointer));
        Assert.Equal("running legacy", File.ReadAllText(legacy));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Existing_slots_with_different_or_extra_bytes_are_never_repaired_in_place(bool extra)
    {
        MakePackage();
        Prepare();
        var changed = Path.Combine(Slot, extra ? "unexpected.dll" : "AgentBridge.exe");
        File.WriteAllText(changed, "do not replace");
        Assert.Throws<InvalidDataException>(() => Prepare());
        Assert.Equal("do not replace", File.ReadAllText(changed));
        Assert.False(File.Exists(Path.Combine(Root, "active-slot.json")));
        Assert.False(Directory.Exists(Path.Combine(Root, "current")));
    }

    [Fact]
    public void The_existing_SHA1_feed_policy_is_preserved_in_the_receipt()
    {
        MakePackage();
        var digest = new PackageDigest(PackageDigest.Sha1, Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(Package))));
        var receipt = Prepare(digest: digest);
        Assert.Equal(digest, receipt.Digest);
        Assert.EndsWith(digest.Hex.ToLowerInvariant(), receipt.RelativePath);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("slots")]
    [InlineData("slot")]
    [InlineData("payload")]
    [Trait("Category", "Process")]
    public void Reparse_points_in_install_paths_or_existing_payloads_are_refused(string where)
    {
        MakePackage();
        var outside = Directory.CreateDirectory(Path.Combine(temp, "outside")).FullName;
        var link = where switch
        {
            "root" => Root, "slots" => Path.Combine(Root, "slots"), "slot" => Slot,
            _ => Path.Combine(Slot, "Assets"),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (OperatingSystem.IsWindows())
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, outside])
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }
        else { Directory.CreateSymbolicLink(link, outside); }
        try
        {
            Assert.Throws<InvalidDataException>(() => Prepare());
            Assert.Empty(Directory.GetFileSystemEntries(outside));
        }
        finally { Directory.Delete(link); }
    }
}
