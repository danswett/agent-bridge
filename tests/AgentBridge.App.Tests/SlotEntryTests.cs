using AgentBridge.App.Core;
using AgentBridge.HostLauncher;
using Xunit;

public sealed partial class SlotPreparationTests
{
    [Theory]
    [InlineData("AgentBridge.exe")]
    [InlineData("agent-bridge-host.exe")]
    [InlineData("agent-bridge-hostw.exe")]
    public void Stable_entry_uses_legacy_current_until_the_pointer_commit(string entry)
    {
        LegacyFile();
        File.WriteAllText(Path.Combine(Root, "current", entry), "trusted legacy");
        using var launch = SlotEntry.Resolve(Root, entry);
        Assert.Equal(Path.Combine(Root, "current", entry), launch.Executable);
        Assert.Null(launch.Target);
        Assert.False(File.Exists(PointerFile));
    }

    [Fact]
    public void Stable_entry_resolves_all_entry_points_from_one_verified_generation()
    {
        var target = PointerTarget("new");
        new SlotPointer(Root).Publish(target);
        using var launch = SlotEntry.Resolve(Root, "AgentBridge.exe");
        Assert.Equal(target, launch.Target);
        Assert.Equal(Path.Combine(Root, target.Slot.RelativePath, "AgentBridge.exe"), launch.Executable);
        new SlotPointer(Root).Publish(PointerTarget("next"));
        Assert.Equal(target, launch.Target);
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<IOException>(() => File.WriteAllText(launch.Executable, "changed"));
        }
    }

    [Fact]
    public void Invalid_active_pointer_is_not_a_legacy_launch_fallback()
    {
        LegacyFile();
        File.WriteAllText(PointerFile, "{}");
        Assert.Throws<InvalidDataException>(() => SlotEntry.Resolve(Root, "AgentBridge.exe"));
        Assert.Throws<InvalidDataException>(() => SlotEntry.Resolve(Root, "../outside.exe"));
    }

    [Fact]
    public void Legacy_and_slot_payloads_register_the_same_stable_launcher_only_after_it_is_installed()
    {
        LegacyFile();
        File.WriteAllText(Path.Combine(Root, "Update.exe"), "fixture");
        var target = PointerTarget("new");
        var legacy = new InstallLayout(Path.Combine(Root, "current"), temp, "fixture");
        var slot = new InstallLayout(Path.Combine(Root, target.Slot.RelativePath), temp, "fixture");
        Assert.Equal(Path.Combine(legacy.AppFolder, "agent-bridge-hostw.exe"), legacy.LauncherExe);
        File.WriteAllText(Path.Combine(Root, "agent-bridge-hostw.exe"), "installed stable helper");
        Assert.Equal(Path.Combine(Root, "agent-bridge-hostw.exe"), legacy.LauncherExe);
        Assert.Equal(legacy.LauncherExe, slot.LauncherExe);
        Assert.Null(SlotEntry.RootForPayload(temp));
    }

    [Theory]
    [InlineData("AgentBridge.exe", "AgentBridge.exe")]
    [InlineData("agent-bridge-hostw.exe", "agent-bridge-hostw.exe")]
    public async Task Root_bootstrap_forwards_arguments_and_keeps_the_selected_generation(string image, string entry)
    {
        LegacyFile();
        File.WriteAllText(Path.Combine(Root, "Update.exe"), "fixture");
        var store = new SlotPointer(Root);
        var target = PointerTarget("old");
        store.Publish(target);
        var called = false;
        var code = await StableEntryMode.RunAsync(Path.Combine(Root, image), ["serve", "--profile", "fixture"], info =>
        {
            called = true;
            Assert.Equal(Path.Combine(Root, target.Slot.RelativePath, entry), info.FileName);
            Assert.Equal(Path.GetDirectoryName(info.FileName), info.WorkingDirectory);
            Assert.Equal(new[] { "serve", "--profile", "fixture" }, info.ArgumentList);
            Assert.False(info.UseShellExecute);
            store.Publish(PointerTarget("next"));
            if (OperatingSystem.IsWindows())
            {
                Assert.Throws<IOException>(() => File.WriteAllText(info.FileName, "changed"));
            }
            return Task.FromResult(17);
        });
        Assert.True(called);
        Assert.Equal(17, code);
    }

    [Fact]
    public async Task Ordinary_payload_helper_does_not_redirect_or_start_another_process()
    {
        LegacyFile();
        File.WriteAllText(Path.Combine(Root, "Update.exe"), "fixture");
        Assert.Null(await StableEntryMode.RunAsync(Path.Combine(Root, "current", "agent-bridge-hostw.exe"), [],
            _ => throw new InvalidOperationException("must not start")));
    }

    [Theory]
    [InlineData("fixture host launcher [coordinator-v1]")]
    [InlineData("fixture host launcher [slot-entry-v2] [coordinator-v1]")]
    public void An_older_helper_cannot_be_used_as_a_stable_bootstrap(string description)
    {
        using var bytes = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => CoordinatorImage.RequireSlotEntryV1(bytes, _ => description));
    }
}
