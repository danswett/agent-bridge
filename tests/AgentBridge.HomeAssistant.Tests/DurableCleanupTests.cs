using AgentBridge.HomeAssistant;
using Xunit;

public sealed class DurableCleanupTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ab-cleanup-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-17T13:30:00-08:00");
    static readonly InboxCleanupFence Fence = new("token", 7, 1, "host-1", "process-1");
    string Pathname => Path.Combine(folder, "inbox.json");
    public DurableCleanupTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, recursive: true);

    DurableInbox Started()
    {
        var inbox = new DurableInbox(Pathname);
        inbox.Enqueue("node", "first", "sender", "private", Now);
        inbox.Enqueue("node", "second", "sender", "later", Now.AddSeconds(1));
        inbox.Start("node", "first", Now, Fence.HostRequest, Fence.Incarnation);
        return inbox;
    }

    [Theory]
    [InlineData("completion-before-clear")]
    [InlineData(" ")]
    public void Reserving_a_clear_cannot_also_assert_its_completion(string receipt)
    {
        var inbox = Started();
        Assert.Throws<ArgumentException>(() => inbox.ConsumeCleanup("node", "first", Fence with { Receipt = receipt }));
        Assert.Null(new DurableInbox(Pathname).Peek("node")!.ConsumedTokens);
    }

    [Fact]
    public void A_lost_clear_receipt_never_grants_a_second_mutation_after_restart()
    {
        var inbox = Started();
        Assert.True(inbox.ConsumeCleanup("node", "first", Fence));
        inbox = new DurableInbox(Pathname);
        Assert.False(inbox.ConsumeCleanup("node", "first", Fence));
        Assert.Null(Assert.Single(inbox.Peek("node")!.ConsumedTokens!).Receipt);
        Assert.NotNull(inbox.Peek("node")!.StartedAt);
    }
}
