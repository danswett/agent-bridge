using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;
using Xunit;

public sealed partial class ReplyTests
{
    string InboxPath => Path.Combine(folder, "ha-connector.json.inbox");

    [Fact]
    public async Task Concurrent_senders_retrying_one_rid_queue_and_deliver_it_only_once()
    {
        await using var c = await Ready();
        var first = ha.SendFromCard(Node, "once", rid: "durable-concurrent", sender: "A");
        var second = ha.SendFromCard(Node, "once", rid: "durable-concurrent", sender: "B");
        await Task.WhenAll(Task.Run(() => c.Observe(first)), Task.Run(() => c.Observe(second)));
        Assert.Single(JsonNode.Parse(File.ReadAllText(InboxPath))!["Messages"]!.AsArray());
        for (var n = 0; n < 4; n++) { await Pass(c); await c.DeliveredAsync(); }

        Assert.Equal("once", Assert.Single(replied).Text);
    }

    [Fact]
    public async Task Observed_replies_survive_restart_before_a_pass_and_drain_beyond_the_old_waiting_limit_in_FIFO_order()
    {
        await using (var first = await Ready())
        {
            for (var n = 0; n < 20; n++)
            {
                first.Observe(ha.SendFromCard(Node, $"message {n}", rid: $"durable-{n}", sender: $"sender-{n % 2}"));
            }
            Assert.Empty(replied);
        }
        await using var restarted = Connector();
        await Pass(restarted);
        for (var n = 0; n < 22; n++)
        {
            await Pass(restarted);
            await restarted.DeliveredAsync();
        }

        Assert.Equal(Enumerable.Range(0, 20).Select(n => $"message {n}"), replied.Select(r => r.Text));
        Assert.Null(new DurableInbox(InboxPath).Peek(Node));
    }

    [Fact]
    public async Task Two_senders_are_stored_and_acknowledged_while_the_first_delivery_is_still_running()
    {
        var release = new TaskCompletionSource<ReplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        outcome = r =>
        {
            reached.TrySetResult();
            return r.Text == "first" ? release.Task : Task.FromResult(new ReplyResult(AnswerOutcome.Delivered));
        };
        await using var c = await Ready();
        var mark = ha.Log.Count;
        try
        {
            c.Observe(ha.SendFromCard(Node, "first", rid: "durable-a", sender: "A"));
            await Pass(c);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Run(() => c.Observe(ha.SendFromCard(Node, "second", rid: "durable-b", sender: "B")));
            await Pass(c);

            Assert.Equal("durable-b", Ack.GetProperty("rid").GetString());
            Assert.Equal("queued", Ack.GetProperty("outcome").GetString());
            Assert.Equal("first", Assert.Single(replied).Text);
            Assert.NotNull(new DurableInbox(InboxPath).Peek(Node));
        }
        finally { release.TrySetResult(new ReplyResult(AnswerOutcome.Delivered)); }
        for (var n = 0; n < 4; n++) { await c.DeliveredAsync(); await Pass(c); }

        Assert.Equal(["first", "second"], replied.Select(r => r.Text));
        var acknowledgements = NotesSince(mark).Select(n => n.Attributes)
            .Where(a => a.TryGetProperty("reply_ack", out _)).Select(a => a.GetProperty("reply_ack")).ToList();
        foreach (var rid in new[] { "durable-a", "durable-b" })
        {
            Assert.Contains(acknowledgements, a => a.GetProperty("rid").GetString() == rid && a.GetProperty("outcome").GetString() == "queued");
            Assert.Contains(acknowledgements, a => a.GetProperty("rid").GetString() == rid && a.GetProperty("outcome").GetString() == "delivered");
        }
    }

    [Theory]
    [InlineData(AnswerOutcome.Delivered, "delivered")]
    [InlineData(AnswerOutcome.Unconfirmed, "unconfirmed")]
    public async Task A_completed_rid_is_not_delivered_again_after_restart(AnswerOutcome result, string expected)
    {
        outcome = _ => Task.FromResult(new ReplyResult(result));
        await using (var first = await Ready())
        {
            await Do(first, ha.SendFromCard(Node, "once", rid: "durable-once"));
        }
        await using var restarted = Connector();
        await Pass(restarted);
        await Do(restarted, ha.SendFromCard(Node, "different retry words", rid: "durable-once"));

        Assert.Equal("once", Assert.Single(replied).Text);
        Assert.Equal(expected, Ack.GetProperty("outcome").GetString());
        Assert.True(Ack.GetProperty("duplicate").GetBoolean());
        Assert.DoesNotContain("once", File.ReadAllText(InboxPath).Replace("durable-once", ""));
    }

    [Fact]
    public async Task A_crash_during_dispatch_is_reported_unconfirmed_without_retyping_and_later_queued_replies_survive()
    {
        await using (var first = await Ready())
        {
            first.Observe(ha.SendFromCard(Node, "possibly typed", rid: "durable-crash"));
            first.Observe(ha.SendFromCard(Node, "still queued", rid: "durable-next"));
        }
        var snapshot = JsonNode.Parse(File.ReadAllText(InboxPath))!;
        snapshot["Messages"]![0]!["StartedAt"] = time.GetUtcNow().ToString("O");
        File.WriteAllText(InboxPath, snapshot.ToJsonString());
        await using var restarted = Connector();
        var mark = ha.Log.Count;
        for (var n = 0; n < 5; n++) { await Pass(restarted); await restarted.DeliveredAsync(); }

        Assert.Equal("still queued", Assert.Single(replied).Text);
        Assert.Contains(NotesSince(mark), n => n.Attributes.TryGetProperty("reply_ack", out var a) &&
            a.GetProperty("rid").GetString() == "durable-crash" && a.GetProperty("outcome").GetString() == "unconfirmed");
        Assert.Contains(log, m => m.Contains("interrupted", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{broken", typeof(System.Text.Json.JsonException))]
    [InlineData("""{"Version":1,"Messages":[{"Node":"n","Rid":"r","Sender":"s","Payload":"body","Outcome":"delivered"}]}""", typeof(InvalidDataException))]
    public async Task A_store_failure_never_accepts_or_delivers_the_reply(string snapshot, Type error)
    {
        await using var c = await Ready();
        File.WriteAllText(InboxPath, snapshot);
        var hit = ha.SendFromCard(Node, "not accepted", rid: "durable-error");

        Assert.Throws(error, () => c.Observe(hit));
        Assert.Empty(replied);
        Assert.False(Detail.TryGetProperty("reply_ack", out _));
    }
}
