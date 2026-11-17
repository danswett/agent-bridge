using AgentBridge.Protocol;
using Xunit;

public sealed partial class ReplyTests
{
    [Fact]
    public async Task Reply_write_is_refused_with_a_clear_error_while_the_slot_is_occupied()
    {
        await using var c = await Ready();
        var release = new TaskCompletionSource<ReplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        outcome = _ => release.Task;
        try
        {
            c.Observe(ha.SendFromCard(Node, "first message", driver: "agent", rid: "slot-first", sender: "sender-a"));
            await Pass(c);
            c.Observe(ha.SendFromCard(Node, "second message", driver: "agent", rid: "slot-second", sender: "sender-b"));
            await Pass(c);

            Assert.True(Detail.TryGetProperty("reply_ack", out var ack),
                "The second sender must receive a refusal, not silence, while the slot is occupied.");
            Assert.Equal("slot-second", ack.GetProperty("rid").GetString());
            Assert.Equal("refused", ack.GetProperty("outcome").GetString());
            Assert.Contains("occupied", Detail.GetProperty("error").GetString()!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            release.TrySetResult(new ReplyResult(AnswerOutcome.Delivered));
            await c.DeliveredAsync();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Two_senders_to_one_session_never_swap_or_lose_a_message(bool watchConnected)
    {
        await using var c = await Ready();
        var mark = ha.Log.Count;
        var first = ha.SendFromCard(Node, "first message", driver: "agent", rid: "race-first", sender: "sender-a");
        var second = ha.SendFromCard(Node, "second message", driver: "agent", rid: "race-second", sender: "sender-b");
        if (watchConnected)
        {
            c.Observe(first);
            c.Observe(second);
        }
        // With the watch disconnected, the next sweep can read only the latest slot.
        await Pass(c, seconds: 20);
        await c.DeliveredAsync();
        await Pass(c);
        await c.DeliveredAsync();
        await Pass(c);

        Assert.Contains(replied, r => r.Text == "second message");
        var acks = NotesSince(mark).Select(n => n.Attributes)
            .Where(a => a.TryGetProperty("reply_ack", out _))
            .Select(a => a.GetProperty("reply_ack")).ToList();
        foreach (var (rid, text) in new[] { ("race-first", "first message"), ("race-second", "second message") })
        {
            Assert.Contains(acks, a => a.GetProperty("rid").GetString() == rid
                && a.GetProperty("outcome").GetString() is "delivered" or "refused");
            if (acks.Any(a => a.GetProperty("rid").GetString() == rid && a.GetProperty("outcome").GetString() == "delivered"))
            {
                Assert.Single(replied, r => r.Text == text);
            }
        }
    }
}
