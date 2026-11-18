using System.Text.Json;
using AgentBridge.App.Core;
using Xunit;

public sealed partial class SlotPreparationTests
{
    SlotPointer.Target PointerTarget(string name)
    {
        if (File.Exists(Package)) { File.Delete(Package); }
        MakePackage(extra: $"lib/app/{name}.txt");
        var package = Path.Combine(Root, "packages", name + ".nupkg");
        Directory.CreateDirectory(Path.GetDirectoryName(package)!);
        File.Copy(Package, package);
        var prepared = SlotPreparation.Prepare(Root, Version, package, Digest, v => v);
        return new(prepared, $"packages/{name}.nupkg");
    }

    string PointerFile => Path.Combine(Root, "active-slot.json");
    string LegacyFile()
    {
        var current = Directory.CreateDirectory(Path.Combine(Root, "current")).FullName;
        var file = Path.Combine(current, "AgentBridge.exe");
        File.WriteAllText(file, "legacy remains runnable");
        return file;
    }

    [Fact]
    public void Missing_pointer_does_not_activate_or_modify_legacy()
    {
        var legacy = LegacyFile();
        Assert.Null(new SlotPointer(Root).Read());
        Assert.Equal("legacy remains runnable", File.ReadAllText(legacy));
        Assert.False(File.Exists(PointerFile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_fault_after_staging_preserves_the_old_authority(bool replacing)
    {
        var legacy = LegacyFile();
        var store = new SlotPointer(Root);
        if (replacing) { store.Publish(PointerTarget("old")); }
        var before = File.Exists(PointerFile) ? File.ReadAllBytes(PointerFile) : null;
        var target = PointerTarget("new");
        Assert.Throws<IOException>(() => store.Publish(target, () => throw new IOException("injected")));
        Assert.Equal(before, File.Exists(PointerFile) ? File.ReadAllBytes(PointerFile) : null);
        Assert.Equal("legacy remains runnable", File.ReadAllText(legacy));
        Assert.Empty(Directory.GetFiles(Root, ".active-slot-*.tmp"));
    }

    [Fact]
    public void A_constructed_receipt_is_not_verification_authority()
    {
        var target = PointerTarget("new");
        var forged = target with { Slot = target.Slot with { ManifestIdentity = "different/identity" } };
        Assert.Throws<InvalidDataException>(() => new SlotPointer(Root).Publish(forged));
        Assert.False(File.Exists(PointerFile));
    }

    [Theory]
    [InlineData("AgentBridge.exe")]
    [InlineData("unexpected.dll")]
    public void Altered_or_extra_slot_bytes_are_refused_before_publication(string name)
    {
        var target = PointerTarget("new");
        File.WriteAllText(Path.Combine(Root, target.Slot.RelativePath, name), "changed after preparation");
        Assert.Throws<InvalidDataException>(() => new SlotPointer(Root).Publish(target));
        Assert.False(File.Exists(PointerFile));
    }

    [Fact]
    public void A_changed_cached_package_is_not_accepted_by_the_pointer_store()
    {
        var target = PointerTarget("new");
        File.AppendAllText(Path.Combine(Root, target.Package), "altered");
        Assert.Throws<PackageMismatchException>(() => new SlotPointer(Root).Publish(target));
        Assert.False(File.Exists(PointerFile));
    }

    [Fact]
    public void Resolution_is_read_once_and_keeps_old_slot_files_held_across_a_switch()
    {
        var store = new SlotPointer(Root);
        var old = PointerTarget("old");
        store.Publish(old);
        using var snapshot = store.Read()!;
        var oldEntry = snapshot.EntryPoint("AgentBridge.exe");
        using var running = new FileStream(oldEntry, FileMode.Open, FileAccess.Read, FileShare.Read);
        var next = PointerTarget("new");
        store.Publish(next);
        using var active = store.Read()!;
        Assert.Equal(old, snapshot.Target);
        Assert.Equal(oldEntry, snapshot.EntryPoint("AgentBridge.exe"));
        Assert.Equal(next, active.Target);
        Assert.NotEqual(oldEntry, active.EntryPoint("AgentBridge.exe"));
        Assert.Equal("verified AgentBridge.exe", File.ReadAllText(oldEntry));
        Assert.Throws<InvalidDataException>(() => snapshot.EntryPoint("../outside.exe"));
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<IOException>(() => File.WriteAllText(oldEntry, "cannot change leased bytes"));
        }
    }

    [Fact]
    public void Rollback_revalidates_and_restores_the_previous_slot_not_payload_bytes()
    {
        var store = new SlotPointer(Root);
        var old = PointerTarget("old");
        store.Publish(old);
        store.Publish(PointerTarget("new"));
        store.Rollback();
        using var active = store.Read()!;
        Assert.Equal(old, active.Target);
    }

    [Fact]
    public void Rollback_refuses_a_previous_slot_that_no_longer_verifies()
    {
        var store = new SlotPointer(Root);
        var old = PointerTarget("old");
        store.Publish(old);
        var next = PointerTarget("new");
        store.Publish(next);
        File.WriteAllText(Path.Combine(Root, old.Slot.RelativePath, "old.txt"), "changed");
        Assert.Throws<InvalidDataException>(() => store.Rollback());
        using var active = store.Read()!;
        Assert.Equal(next, active.Target);
    }

    [Fact]
    public void A_windows_replace_failure_keeps_the_existing_pointer_intact()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var store = new SlotPointer(Root);
        var old = PointerTarget("old");
        store.Publish(old);
        var next = PointerTarget("new");
        using var held = new FileStream(PointerFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Throws<IOException>(() => store.Publish(next));
        using var active = store.Read()!;
        Assert.Equal(old, active.Target);
    }

    [Fact]
    public void Corrupt_or_oversized_pointers_fail_closed_without_using_legacy()
    {
        LegacyFile();
        var store = new SlotPointer(Root);
        File.WriteAllText(PointerFile, "{}");
        Assert.Throws<InvalidDataException>(() => store.Read());
        File.WriteAllText(PointerFile, new string(' ', 8193));
        Assert.Throws<InvalidDataException>(() => store.Read());
        File.WriteAllText(PointerFile, """{"Current":null,"Current":null}""");
        Assert.Throws<JsonException>(() => store.Read());
    }

    [Theory]
    [InlineData("../outside.nupkg")]
    [InlineData("packages/../../outside.nupkg")]
    [InlineData("packages/other/name.nupkg")]
    public void Pointer_package_paths_cannot_escape_the_install_cache(string path)
    {
        var target = PointerTarget("new") with { Package = path };
        Assert.Throws<InvalidDataException>(() => new SlotPointer(Root).Publish(target));
        Assert.False(File.Exists(PointerFile));
    }
}
