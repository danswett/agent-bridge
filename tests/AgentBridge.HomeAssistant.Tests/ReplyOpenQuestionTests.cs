using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;
using Xunit;

// #1634: a message sent while a question card is open was dropped with no word to the sender. It is
// now refused loudly with the sender's own request id, and never queued: the sender retries.
public sealed partial class ReplyTests
{
    static readonly Field Choice = new()
    {
        Name = "release", Title = "Release", Type = FieldType.Choice, Required = true,
        Options = [new Option("cut_now", "Cut now"), new Option("wait", "Wait")],
    };

    static Question Pick() => new()
    {
        Id = "q1", Kind = QuestionKind.Form, Message = "Cut the release?", AskedAt = FakeSessions.T0, Fields = [Choice],
    };

    async Task<HaConnector> Asking()
    {
        var c = await Ready();
        Idle(question: Pick());
        await Pass(c);
        return c;
    }

    async Task CardCloses(HaConnector c)
    {
        Idle(turn: 2);
        await Pass(c);
        await Pass(c, 20);
        await c.DeliveredAsync();
        await Pass(c);
    }

    [Fact]
    public async Task Three_rapid_sends_while_a_question_is_open_are_each_refused_to_their_sender_and_none_is_lost_silently()
    {
        await using var c = await Asking();

        foreach (var n in new[] { 1, 2, 3 })
        {
            await Do(c, ha.SendFromCard(Node, "message " + n, rid: "r-" + n, sender: "SG Builder"));
            Assert.Equal("Reply NOT sent", Activity);
            Assert.Equal("r-" + n, Ack.GetProperty("rid").GetString());
            Assert.Equal("refused", Ack.GetProperty("outcome").GetString());
        }

        Assert.Contains(log, m => m.Contains("refused a reply", StringComparison.Ordinal) && m.Contains("SG Builder", StringComparison.Ordinal));
        await CardCloses(c);
        Assert.Empty(replied);
        Assert.Empty(answered);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("other device")]
    public async Task A_refused_message_is_not_typed_later_and_a_retry_after_the_card_closes_is_delivered_once(string closedBy)
    {
        await using var c = await Asking();
        await Do(c, ha.SendFromCard(Node, "first", rid: "r-1"));
        Assert.Equal("refused", Ack.GetProperty("outcome").GetString());

        if (closedBy == "timeout") { await Pass(c, 3600); }
        // Otherwise answered on another device: the session moves on without this message.
        await CardCloses(c);
        Assert.Empty(replied);

        await Do(c, ha.SendFromCard(Node, "first", rid: "r-1"));
        Assert.Equal("first", Assert.Single(replied).Text);
        Assert.Equal("delivered", Ack.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_session_that_ends_with_a_refused_message_never_receives_it()
    {
        await using var c = await Asking();
        await Do(c, ha.SendFromCard(Node, "too late", rid: "r-end"));
        sessions.End(A);
        await Pass(c, 20);
        await c.DeliveredAsync();
        Assert.Empty(replied);
        Assert.Empty(answered);
    }

    [Fact]
    public async Task Three_rapid_replies_with_no_question_are_each_delivered_once_in_order()
    {
        await using var c = await Ready();
        foreach (var n in new[] { 1, 2, 3 })
        {
            await Do(c, ha.SendFromCard(Node, "message " + n, rid: "r-" + n));
            Assert.Equal("r-" + n, Ack.GetProperty("rid").GetString());
            Assert.Equal("delivered", Ack.GetProperty("outcome").GetString());
        }
        Assert.Equal(["message 1", "message 2", "message 3"], replied.Select(r => r.Text));
    }
}
