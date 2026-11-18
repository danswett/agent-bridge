using AgentBridge.App.Core;
using Xunit;

public sealed partial class SlotPreparationTests
{
    [Fact]
    public void Review_probe_rollback_recovers_when_current_payload_is_corrupt()
    {
        var store = new SlotPointer(Root);
        var previous = PointerTarget("previous");
        store.Publish(previous);
        var current = PointerTarget("current");
        store.Publish(current);
        File.WriteAllText(Path.Combine(Root, current.Slot.RelativePath, "AgentBridge.exe"), "corrupt");
        store.Rollback();
        using var restored = store.Read()!;
        Assert.Equal(previous, restored.Target);
    }

    [Fact]
    public void A_healthy_publish_can_replace_a_corrupt_current_without_resolving_its_payload()
    {
        var store = new SlotPointer(Root);
        store.Publish(PointerTarget("previous"));
        var current = PointerTarget("current");
        store.Publish(current);
        File.WriteAllText(Path.Combine(Root, current.Slot.RelativePath, "AgentBridge.exe"), "corrupt");
        Assert.Throws<InvalidDataException>(() => store.Read());
        var healthy = PointerTarget("healthy");
        store.Publish(healthy);
        using var restored = store.Read()!;
        Assert.Equal(healthy, restored.Target);
        Assert.Equal("corrupt", File.ReadAllText(Path.Combine(Root, current.Slot.RelativePath, "AgentBridge.exe")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovery_faults_keep_the_original_pointer_even_when_current_payload_is_corrupt(bool rollback)
    {
        var store = new SlotPointer(Root);
        store.Publish(PointerTarget("previous"));
        var current = PointerTarget("current");
        store.Publish(current);
        File.WriteAllText(Path.Combine(Root, current.Slot.RelativePath, "AgentBridge.exe"), "corrupt");
        var original = File.ReadAllBytes(PointerFile);
        var healthy = PointerTarget("healthy");
        Assert.Throws<IOException>(() =>
        {
            if (rollback) { store.Rollback(() => throw new IOException("injected recovery fault")); }
            else { store.Publish(healthy, () => throw new IOException("injected recovery fault")); }
        });
        Assert.Equal(original, File.ReadAllBytes(PointerFile));
        Assert.Throws<InvalidDataException>(() => store.Read());
    }
}
