using AgentBridge.App.Core;
using AgentBridge.HostLauncher;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed partial class SlotPreparationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_production_slot_adapter_migrates_with_one_pointer_without_touching_held_legacy_files(bool sha1)
    {
        var legacy = MigrationLegacy();
        using var running = new FileStream(legacy, FileMode.Open, FileAccess.Read, FileShare.Read);
        var package = MigrationPackage("activated");
        var feedDigest = sha1 ? new PackageDigest(PackageDigest.Sha1, Convert.ToHexString(
            System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(package.File)))) : package.Digest;
        var starts = new List<string>();
        var adapter = CoordinatorMode.SlotAdapter(new(Root, Path.Combine(Root, "packages"), package.File,
            feedDigest, "AgentBridgeApp", Version, ["--background"], 1, 1),
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

    [Fact]
    public async Task The_automatic_path_retries_a_released_precommit_holder_without_losing_verified_package_custody()
    {
        var legacy = MigrationLegacy();
        var package = MigrationPackage("retry");
        var time = new RetryClock();
        var starts = 0;
        var held = true;
        var packages = Directory.CreateDirectory(Path.Combine(Root, "packages")).FullName;
        var adapter = CoordinatorMode.SlotAdapter(new(Root, packages, package.File,
            package.Digest, "AgentBridgeApp", Version, [], 1, 1),
            HostwBundleTests.Target, _ => held ? throw new IOException("fixture holder", unchecked((int)0x80070020))
                : EntryDescription, _ => null,
            _ => { starts++; return new SlotChild(); }, time);
        var run = adapter.RunAsync();
        await time.Backoff.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(PointerFile));
        Assert.Equal(0, starts);
        Assert.Throws<IOException>(() => File.OpenWrite(package.File).Dispose());

        held = false;
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(UpdateApplyEnd.Completed, (await run.WaitAsync(TimeSpan.FromSeconds(5))).End);
        await adapter.Ownership;
        Assert.Equal(1, starts);
        Assert.Equal("legacy remains runnable", File.ReadAllText(legacy));
    }

    sealed class RetryClock : FakeTimeProvider
    {
        public TaskCompletionSource Backoff { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromSeconds(1)) Backoff.TrySetResult();
            return timer;
        }
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
    public void Cancellation_at_the_publication_boundary_leaves_legacy_authoritative()
    {
        var legacy = MigrationLegacy();
        var package = MigrationPackage("cancelled");
        using var cancel = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => Migration().Activate(package.File, Version, package.Digest,
            cancel.Cancel, cancel.Token));
        Assert.False(File.Exists(PointerFile));
        Assert.Equal("legacy remains runnable", File.ReadAllText(legacy));
    }

    [Fact]
    public void Non_stopping_handoff_refuses_an_old_current_helper_before_staging()
    {
        MigrationLegacy();
        var package = MigrationPackage("handoff");
        var packages = Directory.CreateDirectory(Path.Combine(Root, "packages")).FullName;
        var cached = Path.Combine(packages, "verified.nupkg");
        File.Copy(package.File, cached);
        var helper = Path.Combine(Root, "current", "agent-bridge-hostw.exe");
        File.WriteAllBytes(helper, HostwBundleTests.Bundle());
        var folder = Path.Combine(temp, "handoff");
        var handoff = new CoordinatorHandoff(folder, HostwBundleTests.Target, _ => "fixture [coordinator-v1]");
        Assert.Throws<InvalidDataException>(() => handoff.Prepare(new(Root, packages, cached, package.Digest,
            "AgentBridgeApp", Version, [], 1, 1), helper, requireSlotApply: true));
        Assert.False(Directory.Exists(folder));
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
