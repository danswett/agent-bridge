using AgentBridge.HomeAssistant;
using Xunit;

public sealed class DurableInboxTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ab-inbox-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset Now = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);
    string Pathname => Path.Combine(folder, "inbox.json");

    public DurableInboxTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Fact]
    public void Restart_replays_each_nodes_undelivered_messages_in_fifo_order()
    {
        var inbox = new DurableInbox(Pathname);
        inbox.Enqueue("one", "a", "sender", "first", Now);
        inbox.Enqueue("two", "x", "sender", "other", Now);
        inbox.Enqueue("one", "b", "sender", "second", Now);

        inbox = new DurableInbox(Pathname);
        Assert.Equal("a", inbox.Peek("one")!.Rid);
        Assert.Equal("x", inbox.Peek("two")!.Rid);
        Assert.Throws<InvalidOperationException>(() => inbox.Deliver("one", "b", Now));
        inbox.Deliver("one", "a", Now.AddSeconds(1));
        inbox = new DurableInbox(Pathname);
        Assert.Equal("b", inbox.Peek("one")!.Rid);
        inbox.Deliver("one", "b", Now.AddSeconds(2));
        Assert.Null(new DurableInbox(Pathname).Peek("one"));
        Assert.Equal("x", inbox.Peek("two")!.Rid);
    }

    [Fact]
    public void Retried_rids_return_the_original_ack_after_restart_and_more_than_fifty_deliveries()
    {
        var inbox = new DurableInbox(Pathname);
        var first = inbox.Enqueue("one", "a", "sender", "first", Now);
        Assert.Equal(first, inbox.Enqueue("one", "a", "sender", "first", Now.AddHours(1)));
        var delivered = inbox.Deliver("one", "a", Now.AddMinutes(1));
        for (var n = 0; n < 55; n++)
        {
            var rid = "r" + n;
            inbox.Enqueue("one", rid, "sender", "next", Now);
            inbox.Deliver("one", rid, Now.AddMinutes(2));
        }
        inbox = new DurableInbox(Pathname);
        Assert.Equal(delivered, inbox.Enqueue("one", "a", "sender", "first", Now.AddHours(25)));
        Assert.Equal(delivered, inbox.Deliver("one", "a", Now.AddHours(25)));
        Assert.Null(inbox.Peek("one"));
        Assert.Throws<InvalidOperationException>(() => inbox.Enqueue("one", "a", "sender", "changed", Now));
        Assert.Equal("a", inbox.Enqueue("two", "a", "sender", "independent", Now).Rid);
    }

    [Fact]
    public async Task Concurrent_instances_keep_all_enqueues()
    {
        var first = new DurableInbox(Pathname);
        var second = new DurableInbox(Pathname);
        await Task.WhenAll(Enumerable.Range(0, 24).Select(n => Task.Run(() =>
            (n % 2 == 0 ? first : second).Enqueue("one", "r" + n, "sender", "message", Now))));
        var inbox = new DurableInbox(Pathname);
        var seen = new HashSet<string>();
        while (inbox.Peek("one") is { } item)
        {
            Assert.True(seen.Add(item.Rid));
            inbox.Deliver("one", item.Rid, Now);
        }
        Assert.Equal(24, seen.Count);
    }

    [Fact]
    public void Corrupt_storage_fails_closed_instead_of_discarding_accepted_messages()
    {
        File.WriteAllText(Pathname, "{broken");
        var inbox = new DurableInbox(Pathname);
        Assert.ThrowsAny<Exception>(() => inbox.Enqueue("one", "a", "sender", "message", Now));
        Assert.Equal("{broken", File.ReadAllText(Pathname));
    }

    [Fact]
    public void An_unavailable_store_never_confirms_a_new_message()
    {
        var inbox = new DurableInbox(Pathname);
        inbox.Enqueue("one", "a", "sender", "first", Now);
        using (File.Open(Pathname, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => inbox.Enqueue("one", "b", "sender", "second", Now));
        }
        Assert.Equal("a", new DurableInbox(Pathname).Peek("one")!.Rid);
        inbox.Deliver("one", "a", Now);
        Assert.Null(inbox.Peek("one"));
    }
}
