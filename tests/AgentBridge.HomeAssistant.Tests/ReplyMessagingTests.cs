using System.Text.Json;
using AgentBridge.Protocol;
using Xunit;

public sealed partial class ReplyTests
{
    [Fact]
    public async Task Messaging_distinct_task_rids_survive_two_senders_using_the_same_sensor_stamp()
    {
        await using var c = await Ready();
        const string Stamp = "2026-11-16T20:30:00Z";
        c.Observe(ha.SendFromCard(Node, "from A", at: Stamp, rid: "task-a", sender: "sender-a"));
        c.Observe(ha.SendFromCard(Node, "from B", at: Stamp, rid: "task-b", sender: "sender-b"));

        for (var n = 0; n < 6; n++)
        {
            await Pass(c);
            await c.DeliveredAsync();
        }

        Assert.Equal(["from A", "from B"], replied.Select(r => r.Text));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(18)]
    public async Task Messaging_each_watched_reply_is_delivered_or_explicitly_refused_at_the_waiting_limit(int waiting)
    {
        var release = new TaskCompletionSource<ReplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        outcome = r => r.Text == "message 0" ? release.Task : Task.FromResult(new ReplyResult(AnswerOutcome.Delivered));
        await using var c = await Ready();
        var mark = ha.Log.Count;
        try
        {
            c.Observe(ha.SendFromCard(Node, "message 0", rid: "task-0", sender: "sender-a"));
            await Pass(c);
            for (var n = 1; n <= waiting; n++)
            {
                c.Observe(ha.SendFromCard(Node, "message " + n, rid: "task-" + n, sender: n % 2 == 0 ? "sender-a" : "sender-b"));
            }
            await Pass(c);
        }
        finally { release.TrySetResult(new ReplyResult(AnswerOutcome.Delivered)); }
        await c.DeliveredAsync();
        for (var n = 0; n < waiting + 3; n++)
        {
            await Pass(c);
            await c.DeliveredAsync();
        }

        var acks = NotesSince(mark).Select(n => n.Attributes)
            .Where(a => a.TryGetProperty("reply_ack", out _))
            .Select(a => a.GetProperty("reply_ack"))
            .GroupBy(a => a.GetProperty("rid").GetString()!)
            .ToDictionary(g => g.Key, g => g.Last().GetProperty("outcome").GetString());
        for (var n = 0; n <= waiting; n++)
        {
            Assert.True(acks.TryGetValue("task-" + n, out var result), $"task-{n} was neither delivered nor explicitly refused");
            Assert.Contains(result, new[] { "delivered", "refused" });
            Assert.Equal(result == "delivered" ? 1 : 0, replied.Count(r => r.Text == "message " + n));
        }
    }

    [Theory]
    [InlineData(49, true)]
    [InlineData(50, false)]
    public async Task Messaging_the_fifty_rid_history_boundary_affects_replay_not_the_first_delivery(int laterReplies, bool duplicate)
    {
        await using var c = await Ready();
        await Do(c, ha.SendFromCard(Node, "original", rid: "task-original", sender: "sender-a"));
        for (var n = 0; n < laterReplies; n++)
        {
            await Do(c, ha.SendFromCard(Node, "later " + n, rid: "later-" + n, sender: "sender-b"));
        }
        Assert.Equal(laterReplies + 1, replied.Count);

        await Do(c, ha.SendFromCard(Node, "original", rid: "task-original", sender: "sender-a"));

        Assert.Equal(laterReplies + (duplicate ? 1 : 2), replied.Count);
        Assert.Equal(duplicate, Ack.TryGetProperty("duplicate", out var repeated) && repeated.GetBoolean());
        Assert.Equal("task-original", Ack.GetProperty("rid").GetString());
    }

    [Fact]
    public async Task Messaging_the_task_rid_and_sender_reach_the_host_delivery_with_the_reply()
    {
        await using var c = await Ready();

        await Do(c, ha.SendFromCard(Node, "message", rid: "task-original", sender: "sender-a"));

        var delivered = JsonSerializer.SerializeToElement(Assert.Single(replied), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(delivered.TryGetProperty("rid", out var rid), "Host delivery lost the task RID");
        Assert.Equal("task-original", rid.GetString());
        Assert.True(delivered.TryGetProperty("sender", out var sender), "Host delivery lost the sender");
        Assert.Equal("sender-a", sender.GetString());
        Assert.Equal("task-original", Ack.GetProperty("rid").GetString());
        Assert.Equal("sender-a", Ack.GetProperty("sender").GetString());
    }
}
