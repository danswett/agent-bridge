using System.Diagnostics;
using AgentBridge.App.Core;
using AgentBridge.HostLauncher;
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
