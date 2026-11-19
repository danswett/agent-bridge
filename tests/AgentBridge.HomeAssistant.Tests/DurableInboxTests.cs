using AgentBridge.HomeAssistant;
using System.Text.Json;
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
        Assert.Empty(inbox.Enqueue("one", "a", "sender", "changed", Now).Payload);
        Assert.Equal("a", inbox.Enqueue("two", "a", "sender", "independent", Now).Rid);
    }

    [Fact]
    public void A_restamped_retry_returns_the_original_message()
    {
        var inbox = new DurableInbox(Pathname);
        var original = inbox.Enqueue("one", "a", "sender", "first", Now);

        Assert.Equal(original, inbox.Enqueue("one", "a", "new sender", "restamped", Now.AddMinutes(1)));
    }

    [Fact]
    public void Delivered_payloads_are_removed_without_losing_persisted_rid_deduplication()
    {
        var inbox = new DurableInbox(Pathname);
        var original = inbox.Enqueue("one", "a", "sender", "private payload", Now);
        var delivered = inbox.Deliver("one", "a", Now.AddMinutes(1));

        Assert.Empty(delivered.Payload);
        Assert.DoesNotContain("private payload", File.ReadAllText(Pathname));

        var retry = new DurableInbox(Pathname).Enqueue("one", "a", "sender", "restamped", Now.AddHours(1));
        Assert.Equal(original.Node, retry.Node);
        Assert.Equal(original.Rid, retry.Rid);
        Assert.Equal(original.Sender, retry.Sender);
        Assert.Equal(original.QueuedAt, retry.QueuedAt);
        Assert.Equal(delivered.DeliveredAt, retry.DeliveredAt);
        Assert.Empty(retry.Payload);
    }

    [Theory]
    [InlineData("peek")]
    [InlineData("enqueue")]
    [InlineData("deliver")]
    public void Legacy_delivered_payloads_are_scrubbed_without_blocking_the_store(string operation)
    {
        File.WriteAllText(Pathname, """
            {"Version":1,"Messages":[
              {"Node":"one","Rid":"done","Sender":"sender","Payload":"secret","QueuedAt":"2026-11-16T00:00:00Z","DeliveredAt":"2026-11-16T00:01:00Z"},
              {"Node":"one","Rid":"a","Sender":"sender","Payload":"first","QueuedAt":"2026-11-16T00:02:00Z","DeliveredAt":null},
              {"Node":"one","Rid":"b","Sender":"sender","Payload":"second","QueuedAt":"2026-11-16T00:03:00Z","DeliveredAt":null}]}
            """);
        var inbox = new DurableInbox(Pathname);
        if (operation == "peek") { Assert.Equal("a", inbox.Peek("one")!.Rid); }
        else if (operation == "enqueue") { Assert.Equal("done", inbox.Enqueue("one", "done", "other", "retry", Now).Rid); }
        else { Assert.NotNull(inbox.Deliver("one", "done", Now).DeliveredAt); }

        Assert.DoesNotContain("secret", File.ReadAllText(Pathname));
        inbox = new DurableInbox(Pathname);
        var retry = inbox.Enqueue("one", "done", "other", "retry", Now);
        Assert.Empty(retry.Payload);
        Assert.Equal(Now.AddMinutes(1), retry.DeliveredAt);
        Assert.Equal("sender", retry.Sender);
        Assert.Equal("a", inbox.Peek("one")!.Rid);
        inbox.Deliver("one", "a", Now);
        Assert.Equal("b", inbox.Peek("one")!.Rid);
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_delivered_rid_is_deduplicated_only_until_capacity_prunes_it(bool totalLimit)
    {
        var messages = Enumerable.Range(0, totalLimit ? 1000 : 100)
            .Select(n => new InboxMessage(totalLimit ? "node" + n / 100 : "node0", "r" + n,
                "sender", "", Now, Now.AddMinutes(1))).ToList();
        File.WriteAllText(Pathname, JsonSerializer.Serialize(new { Version = 1, Messages = messages }));
        var inbox = new DurableInbox(Pathname);
        Assert.NotNull(inbox.Enqueue("node0", "r0", "sender", "retry", Now).DeliveredAt);
        inbox.Enqueue(totalLimit ? "new-node" : "node0", "new", "sender", "new", Now);

        inbox = new DurableInbox(Pathname);
        Assert.NotNull(inbox.Enqueue("node0", "r1", "sender", "retry", Now).DeliveredAt);
        Assert.Null(inbox.Enqueue("node0", "r0", "sender", "retry", Now).DeliveredAt);
    }

    [Fact]
    public void Legacy_messages_without_rid_or_sender_get_defaults()
    {
        var inbox = new DurableInbox(Pathname);

        var first = inbox.Enqueue("one", null!, null!, "first", Now);
        var second = inbox.Enqueue("one", null!, null!, "second", Now);

        Assert.NotEmpty(first.Rid);
        Assert.NotEmpty(second.Rid);
        Assert.NotEqual(first.Rid, second.Rid);
        Assert.Equal("legacy", first.Sender);
        Assert.Equal("legacy", second.Sender);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Crash_leftovers_are_removed_before_reading_and_delivering(bool partial, bool differentCase)
    {
        var inbox = new DurableInbox(Pathname);
        inbox.Enqueue("one", "a", "sender", "secret", Now);
        var snapshot = File.ReadAllText(Pathname);
        var leftover = Pathname + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var unrelated = Path.Combine(folder, "other.json." + Guid.NewGuid().ToString("N") + ".tmp");
        var lookalike = Pathname + ".not-an-inbox-temp.tmp";
        File.WriteAllText(leftover, partial ? snapshot[..^5] : snapshot);
        File.WriteAllText(unrelated, "unrelated");
        File.WriteAllText(lookalike, "unrelated");

        inbox = new DurableInbox(OperatingSystem.IsWindows() && differentCase ? Path.Combine(folder, "INBOX.JSON") : Pathname);
        Assert.Equal("a", inbox.Peek("one")!.Rid);
        Assert.False(File.Exists(leftover));
        File.WriteAllText(leftover, partial ? snapshot[..^5] : snapshot);
        inbox.Deliver("one", "a", Now);
        Assert.False(File.Exists(leftover));
        Assert.DoesNotContain("secret", File.ReadAllText(Pathname));
        Assert.Equal("unrelated", File.ReadAllText(unrelated));
        Assert.Equal("unrelated", File.ReadAllText(lookalike));
        Assert.Null(new DurableInbox(Pathname).Peek("one"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Full_queues_persist_refused_rids_without_displacing_accepted_replies(bool totalLimit)
    {
        var messages = Enumerable.Range(0, totalLimit ? 1000 : 100)
            .Select(n => new InboxMessage(totalLimit ? "node" + n / 100 : "node0", "r" + n,
                "sender", "message", Now, null)).ToList();
        File.WriteAllText(Pathname, JsonSerializer.Serialize(new { Version = 1, Messages = messages }));
        var inbox = new DurableInbox(Pathname);
        var node = totalLimit ? "overflow" : "node0";
        var refused = inbox.Enqueue(node, "overflow", "sender", "private overflow", Now);

        Assert.Equal("refused", refused.Outcome);
        Assert.NotNull(refused.DeliveredAt);
        Assert.Empty(refused.Payload);
        Assert.DoesNotContain("private overflow", File.ReadAllText(Pathname));
        var saved = JsonDocument.Parse(File.ReadAllText(Pathname)).RootElement.GetProperty("Messages")
            .Deserialize<List<InboxMessage>>()!;
        Assert.All(messages, m => Assert.Contains(m, saved));
        inbox.Deliver("node0", "r0", Now);
        inbox = new DurableInbox(Pathname);
        Assert.Equal(refused, inbox.Enqueue(node, "overflow", "changed", "retry", Now.AddMinutes(1)));
        Assert.Null(inbox.Enqueue(node, "new", "sender", "new", Now).DeliveredAt);
        Assert.Equal("r1", inbox.Peek("node0")!.Rid);
    }

    [Fact]
    public void Absence_requires_three_consecutive_passes_and_presence_resets_the_guard()
    {
        var inbox = new DurableInbox(Pathname);
        inbox.Enqueue("one", "a", "sender", "pending", Now);
        inbox.Enqueue("two", "b", "sender", "delivered", Now);
        inbox.Deliver("two", "b", Now);
        IReadOnlySet<string> both = new HashSet<string> { "one", "two" };
        IReadOnlySet<string> none = new HashSet<string>();

        inbox.ForgetExcept(none);
        inbox.ForgetExcept(none);
        Assert.NotNull(inbox.Peek("one"));
        Assert.NotNull(inbox.Find("two", "b"));
        inbox.ForgetExcept(both);
        inbox.ForgetExcept(none);
        inbox.ForgetExcept(none);
        Assert.NotNull(inbox.Peek("one"));
        Assert.NotNull(inbox.Find("two", "b"));
        inbox.ForgetExcept(none);
        Assert.Null(inbox.Peek("one"));
        Assert.Null(inbox.Find("two", "b"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Migration_preserves_v1_and_the_v2_fence_refuses_missing_or_rolled_back_state(bool rollback)
    {
        var legacy = """{"Version":1,"Messages":[]}""";
        File.WriteAllText(Pathname, legacy);
        var inbox = new DurableInbox(Pathname);
        inbox.Enqueue("one", "a", "sender", "pending", Now);
        Assert.Equal(legacy, File.ReadAllText(Pathname + ".v1"));
        Assert.True(File.Exists(Pathname + ".v2"));
        var snapshot = JsonDocument.Parse(File.ReadAllText(Pathname)).RootElement;
        Assert.Equal(2, snapshot.GetProperty("Version").GetInt32());
        Assert.Empty(snapshot.GetProperty("Retired").EnumerateArray());
        if (rollback) { File.WriteAllText(Pathname, legacy); }
        else { File.Delete(Pathname); }

        if (rollback) { Assert.Throws<InvalidDataException>(() => new DurableInbox(Pathname).Peek("one")); }
        else { Assert.Throws<FileNotFoundException>(() => new DurableInbox(Pathname).Peek("one")); }
    }

    [Theory]
    [InlineData("""{"Version":2,"Messages":[],"Retired":[{"Node":"one","At":"2026-11-16T00:00:00Z","Resolution":0}]}""", typeof(InvalidDataException))]
    [InlineData("""{"Version":2,"Messages":[{"Node":"one","Rid":"a","Sender":"s","Payload":"p","Attempt":1}],"Retired":[]}""", typeof(JsonException))]
    public void The_C1_reader_never_discards_C2_recovery_or_retirement_data(string snapshot, Type error)
    {
        File.WriteAllText(Pathname, snapshot);

        Assert.Throws(error, () => new DurableInbox(Pathname).Enqueue("one", "b", "sender", "new", Now));
        Assert.Equal(snapshot, File.ReadAllText(Pathname));
        Assert.False(File.Exists(Pathname + ".v2"));
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
