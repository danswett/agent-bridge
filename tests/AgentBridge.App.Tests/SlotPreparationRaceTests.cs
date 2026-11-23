using AgentBridge.App.Core;
using Xunit;

public sealed partial class SlotPreparationTests
{
    [Fact]
    public async Task Eight_concurrent_preparations_validate_the_winner_and_return_the_same_receipt()
    {
        MakePackage();
        var digest = Digest;
        var legacy = Path.Combine(Directory.CreateDirectory(Path.Combine(Root, "current")).FullName, "running.exe");
        var pointer = Path.Combine(Root, "active-slot.json");
        File.WriteAllText(legacy, "running legacy");
        File.WriteAllText(pointer, "existing pointer");
        using var pinLegacy = new FileStream(legacy, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var pinPointer = new FileStream(pointer, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var ready = new Barrier(8);
        var callers = Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(
            () => SlotPreparation.Prepare(Root, Version, Package, digest, version =>
            {
                if (!ready.SignalAndWait(TimeSpan.FromSeconds(30))) { throw new TimeoutException("Preparation callers did not rendezvous."); }
                return version;
            }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        var receipts = await Task.WhenAll(callers);
        Assert.All(receipts, receipt => Assert.Equal(receipts[0], receipt));
        Assert.Equal(receipts[0], Prepare());
        Assert.Single(Directory.GetDirectories(Path.Combine(Root, "slots")));
        Assert.Equal("existing pointer", File.ReadAllText(pointer));
        Assert.Equal("running legacy", File.ReadAllText(legacy));
    }

    [Fact]
    public void An_existing_slot_with_an_extra_empty_directory_is_refused_without_repair()
    {
        MakePackage();
        Prepare();
        var extra = Directory.CreateDirectory(Path.Combine(Slot, "Assets", "unexpected")).FullName;
        Assert.Throws<InvalidDataException>(() => Prepare());
        Assert.True(Directory.Exists(extra));
        Assert.False(File.Exists(Path.Combine(Root, "active-slot.json")));
        Assert.False(Directory.Exists(Path.Combine(Root, "current")));
    }
}
