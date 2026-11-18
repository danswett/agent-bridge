using AgentBridge.App.Core;
using AgentBridge.HostLauncher;
using Xunit;

public sealed partial class SlotPreparationTests
{
    [Fact]
    public async Task The_production_slot_adapter_migrates_with_one_pointer_without_touching_held_legacy_files()
    {
        var legacy = MigrationLegacy();
        using var running = new FileStream(legacy, FileMode.Open, FileAccess.Read, FileShare.Read);
        var package = MigrationPackage("activated");
        var starts = new List<string>();
        var adapter = CoordinatorMode.SlotAdapter(new(Root, Path.Combine(Root, "packages"), package.File,
            package.Digest, "AgentBridgeApp", Version, ["--background"], 1, 1),
            HostwBundleTests.Target, _ => EntryDescription, _ => null,
            info => { starts.Add(info.FileName); return new SlotChild(); });

        Assert.Equal(UpdateApplyEnd.Completed, (await adapter.RunAsync()).End);
        await adapter.Ownership;

        using var active = new SlotPointer(Root).Read()!;
        Assert.Equal(active.EntryPoint("AgentBridge.exe"), Assert.Single(starts));
        Assert.Equal(package.Digest, active.Target.Slot.Digest);
        Assert.Equal("legacy remains runnable", File.ReadAllText(legacy));
        Assert.Equal(PointerFile, adapter.ApplyPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_failed_activation_commit_preserves_the_previous_authority_and_legacy(bool replacing)
    {
        var legacy = MigrationLegacy();
        var first = MigrationPackage("first");
        if (replacing) { Migration().Activate(first.File, Version, first.Digest); }
        var before = File.Exists(PointerFile) ? File.ReadAllBytes(PointerFile) : null;
        var next = MigrationPackage("next", compressed: true);

        Assert.Throws<IOException>(() => Migration().Activate(next.File, Version, next.Digest,
            () => throw new IOException("injected publication failure")));

        Assert.Equal(before, File.Exists(PointerFile) ? File.ReadAllBytes(PointerFile) : null);
        Assert.Equal("legacy remains runnable", File.ReadAllText(legacy));
        Assert.Empty(Directory.GetFiles(Root, ".active-slot-*.tmp"));
    }

    [Fact]
    public void A_busy_package_writer_prevents_migration_before_any_root_alias_is_installed()
    {
        MigrationLegacy();
        var package = MigrationPackage("busy");
        Directory.CreateDirectory(Path.Combine(Root, "packages"));
        using var writer = File.Open(Path.Combine(Root, "packages", ".velopack_lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => Migration().Activate(package.File, Version, package.Digest));
        Assert.False(File.Exists(PointerFile));
        Assert.False(File.Exists(Path.Combine(Root, "stable-entry.json")));
    }

    [Fact]
    public void Entry_only_targets_cannot_restore_the_native_apply_contract_after_cutover()
    {
        MigrationLegacy();
        var package = MigrationPackage("old-protocol");
        Assert.Throws<InvalidDataException>(() => Migration("fixture [slot-entry-v1] [coordinator-v1]")
            .Activate(package.File, Version, package.Digest));
        Assert.False(File.Exists(PointerFile));
        Assert.Equal("legacy execution stub", File.ReadAllText(Path.Combine(Root, "AgentBridge.exe")));
    }
}
