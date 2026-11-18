using System.IO.Compression;
using AgentBridge.App.Core;
using Xunit;

public sealed partial class SlotPreparationTests
{
    const string EntryDescription = "fixture helper [slot-apply-v1] [slot-entry-v1] [coordinator-v1]";
    SlotMigration Migration(string? description = EntryDescription) =>
        new(Root, HostwBundleTests.Target, _ => description);

    (string File, PackageDigest Digest) MigrationPackage(string name, bool compressed = false)
    {
        if (File.Exists(Package)) { File.Delete(Package); }
        MakePackage(extra: $"lib/app/{name}.txt");
        using (var zip = ZipFile.Open(Package, ZipArchiveMode.Update))
        {
            foreach (var suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json", ".exe" })
            { zip.GetEntry("lib/app/agent-bridge-hostw" + suffix)!.Delete(); }
            using var output = zip.CreateEntry("lib/app/agent-bridge-hostw.exe").Open();
            output.Write(HostwBundleTests.Bundle(compressed: compressed));
        }
        var digest = Digest;
        var file = Path.Combine(temp, name + ".nupkg");
        File.Copy(Package, file);
        return (file, digest);
    }

    string MigrationLegacy()
    {
        var legacy = LegacyFile();
        File.WriteAllText(Path.Combine(Root, "Update.exe"), "fixture");
        File.WriteAllText(Path.Combine(Root, "AgentBridge.exe"), "legacy execution stub");
        return legacy;
    }

    [Fact]
    public void Migration_preparation_installs_verified_aliases_but_never_activates_or_moves_legacy()
    {
        var legacy = MigrationLegacy();
        using var running = new FileStream(legacy, FileMode.Open, FileAccess.Read, FileShare.Read);
        var package = MigrationPackage("new");
        var target = Migration().Prepare(package.File, Version, package.Digest);
        Assert.False(File.Exists(PointerFile));
        Assert.Equal("legacy remains runnable", File.ReadAllText(legacy));
        using var launch = SlotEntry.Resolve(Root, "AgentBridge.exe");
        Assert.Equal(legacy, launch.Executable);
        var source = File.ReadAllBytes(Path.Combine(Root, target.Slot.RelativePath, "agent-bridge-hostw.exe"));
        Assert.Equal(source, File.ReadAllBytes(Path.Combine(Root, "agent-bridge-hostw.exe")));
        Assert.Equal(source, File.ReadAllBytes(Path.Combine(Root, "AgentBridge.exe")));
        Assert.True(File.Exists(Path.Combine(Root, target.Package)));
    }

    [Fact]
    public void Bootstrap_provenance_is_immutable_and_idempotent_even_when_root_aliases_are_held()
    {
        MigrationLegacy();
        var old = MigrationPackage("old");
        var first = Migration().Prepare(old.File, Version, old.Digest);
        var ledger = File.ReadAllBytes(Path.Combine(Root, "stable-entry.json"));
        var alias = File.ReadAllBytes(Path.Combine(Root, "AgentBridge.exe"));
        using var app = new FileStream(Path.Combine(Root, "AgentBridge.exe"), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var host = new FileStream(Path.Combine(Root, "agent-bridge-hostw.exe"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var newer = MigrationPackage("new", compressed: true);
        var second = Migration().Prepare(newer.File, Version, newer.Digest);
        Assert.NotEqual(first, second);
        Assert.Equal(second, Migration().Prepare(newer.File, Version, newer.Digest));
        Assert.Equal(ledger, File.ReadAllBytes(Path.Combine(Root, "stable-entry.json")));
        Assert.Equal(alias, File.ReadAllBytes(Path.Combine(Root, "AgentBridge.exe")));
        Assert.False(File.Exists(PointerFile));
    }

    [Theory]
    [InlineData("ledger")]
    [InlineData("host-alias")]
    [InlineData("app-alias")]
    public void Interrupted_bootstrap_preparation_recovers_without_publishing_a_pointer(string step)
    {
        var legacy = MigrationLegacy();
        var package = MigrationPackage("new");
        Assert.Throws<IOException>(() => Migration().Prepare(package.File, Version, package.Digest,
            at => { if (at == step) { throw new IOException("injected interruption"); } }));
        Assert.False(File.Exists(PointerFile));
        using (var launch = SlotEntry.Resolve(Root, "AgentBridge.exe")) { Assert.Equal(legacy, launch.Executable); }
        Migration().Prepare(package.File, Version, package.Digest);
        Assert.Equal("legacy remains runnable", File.ReadAllText(legacy));
        Assert.False(File.Exists(PointerFile));
    }

    [Fact]
    public void Unverified_packages_never_install_a_root_bootstrap()
    {
        MigrationLegacy();
        var package = MigrationPackage("new");
        File.AppendAllText(package.File, "changed");
        Assert.Throws<PackageMismatchException>(() => Migration().Prepare(package.File, Version, package.Digest));
        Assert.False(File.Exists(Path.Combine(Root, "stable-entry.json")));
        Assert.False(File.Exists(Path.Combine(Root, "agent-bridge-hostw.exe")));
        Assert.Equal("legacy execution stub", File.ReadAllText(Path.Combine(Root, "AgentBridge.exe")));
    }

    [Fact]
    public void An_unsupported_helper_is_not_installed_as_a_bootstrap()
    {
        MigrationLegacy();
        var package = MigrationPackage("new");
        Assert.Throws<InvalidDataException>(() => Migration("fixture [coordinator-v1]").Prepare(package.File, Version, package.Digest));
        Assert.False(File.Exists(Path.Combine(Root, "stable-entry.json")));
        Assert.False(File.Exists(PointerFile));
    }

    [Fact]
    public void An_unexpected_existing_root_helper_is_refused_not_overwritten()
    {
        MigrationLegacy();
        File.WriteAllText(Path.Combine(Root, "agent-bridge-hostw.exe"), "unexpected existing helper");
        var package = MigrationPackage("new");
        Assert.Throws<InvalidDataException>(() => Migration().Prepare(package.File, Version, package.Digest));
        Assert.Equal("unexpected existing helper", File.ReadAllText(Path.Combine(Root, "agent-bridge-hostw.exe")));
        Assert.False(File.Exists(PointerFile));
    }
}
