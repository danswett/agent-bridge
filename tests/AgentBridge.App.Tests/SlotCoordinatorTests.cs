using System.Diagnostics;
using AgentBridge.App.Core;
using AgentBridge.HostLauncher;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed partial class SlotPreparationTests
{
    [Fact]
    public async Task Managed_apply_uses_a_fresh_pinned_pointer_and_never_starts_Update_exe()
    {
        var legacy = LegacyFile();
        var target = PointerTarget("managed");
        var starts = new List<ProcessStartInfo>();
        var package = Path.Combine(Root, target.Package);
        var adapter = new UpdateCoordinatorAdapter(new(Root, Path.Combine(Root, "packages"), package,
            target.Slot.Digest, "AgentBridgeApp", target.Slot.Version, ["--profile", "two words"], 1, 1),
            _ => null, info =>
            {
                starts.Add(info);
                Assert.Throws<IOException>(() => File.OpenWrite(info.FileName).Dispose());
                return new SlotChild();
            }, slotApply: (request, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                Assert.Throws<IOException>(() => File.OpenWrite(request.Package).Dispose());
                new SlotPointer(request.Root).Publish(target);
                return Task.CompletedTask;
            });

        var result = await adapter.RunAsync();
        await adapter.Ownership;

        Assert.Equal(UpdateApplyEnd.Completed, result.End);
        Assert.Null(adapter.ApplyProcessId);
        Assert.Equal(PointerFile, adapter.ApplyPath);
        var start = Assert.Single(starts);
        Assert.Equal(Path.Combine(Root, target.Slot.RelativePath, "AgentBridge.exe"), start.FileName);
        Assert.Equal(Path.GetDirectoryName(start.FileName), start.WorkingDirectory);
        Assert.Equal(["--profile", "two words"], start.ArgumentList);
        Assert.Equal("legacy remains runnable", File.ReadAllText(legacy));
    }

    [Fact]
    public async Task An_equal_version_from_a_different_package_cannot_authorize_managed_respawn()
    {
        LegacyFile();
        var requested = PointerTarget("requested");
        var different = PointerTarget("different");
        var starts = 0;
        var adapter = new UpdateCoordinatorAdapter(new(Root, Path.Combine(Root, "packages"),
            Path.Combine(Root, requested.Package), requested.Slot.Digest, "AgentBridgeApp", requested.Slot.Version, [], 1, 1),
            _ => null, _ => { starts++; return new SlotChild(); },
            slotApply: (_, _) => { new SlotPointer(Root).Publish(different); return Task.CompletedTask; });

        var result = await adapter.RunAsync();

        await Assert.ThrowsAsync<PackageMismatchException>(() => adapter.Ownership);
        Assert.Equal(UpdateApplyEnd.Unknown, result.End);
        Assert.Equal("active-version", result.Step);
        Assert.Equal(nameof(PackageMismatchException), result.ErrorType);
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task A_pointer_republished_mid_respawn_keeps_the_confirmed_generation()
    {
        LegacyFile();
        var target = PointerTarget("confirmed");
        var next = PointerTarget("superseding");
        string? executable = null;
        var adapter = new UpdateCoordinatorAdapter(new(Root, Path.Combine(Root, "packages"),
            Path.Combine(Root, target.Package), target.Slot.Digest, "AgentBridgeApp", target.Slot.Version, [], 1, 1),
            _ => null, info => { executable = info.FileName; return new SlotChild(); },
            new RepublishClock(() => new SlotPointer(Root).Publish(next)),
            slotApply: (_, _) => { new SlotPointer(Root).Publish(target); return Task.CompletedTask; });
        Assert.Equal(UpdateApplyEnd.Completed, (await adapter.RunAsync()).End);
        await adapter.Ownership;
        Assert.Equal(Path.Combine(Root, target.Slot.RelativePath, "AgentBridge.exe"), executable);
        using var active = new SlotPointer(Root).Read()!;
        Assert.Equal(next, active.Target);
    }

    sealed class RepublishClock(Action publish) : FakeTimeProvider
    {
        int deadlines;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (++deadlines == 2) { publish(); }
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    [Fact]
    public async Task A_failed_managed_activation_is_reported_without_native_fallback_or_respawn()
    {
        var legacy = LegacyFile();
        var target = PointerTarget("refused");
        var adapter = new UpdateCoordinatorAdapter(new(Root, Path.Combine(Root, "packages"),
            Path.Combine(Root, target.Package), target.Slot.Digest, "AgentBridgeApp", target.Slot.Version, [], 1, 1),
            _ => null, _ => throw new InvalidOperationException("must not start"),
            slotApply: (_, _) => throw new IOException("injected activation refusal"));

        var result = await adapter.RunAsync();
        await Assert.ThrowsAsync<IOException>(() => adapter.Ownership);

        Assert.Equal(UpdateApplyEnd.Unknown, result.End);
        Assert.Equal("slot-activation", result.Step);
        Assert.Equal(nameof(IOException), result.ErrorType);
        Assert.False(File.Exists(PointerFile));
        Assert.Equal("legacy remains runnable", File.ReadAllText(legacy));
    }

    sealed class SlotChild : IUpdateCoordinatorProcess
    {
        public int Id => 2;
        public long StartedUtcTicks => 1;
        public int ExitCode => 0;
        public Task WaitForExitAsync(CancellationToken cancel) => Task.CompletedTask;
        public void Dispose() { }
    }
}
