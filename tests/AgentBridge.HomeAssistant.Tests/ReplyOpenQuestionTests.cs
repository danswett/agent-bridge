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

    [Theory]
    [InlineData("q1", "agent")]
    [InlineData(null, "human")]
    [InlineData(null, "agent")]
    public async Task A_stale_instance_at_an_open_question_is_refused_before_answer_or_reply_routing(string? decisionId, string driver)
    {
        Idle();
        await using var c = ConnectorWithInstance("host-run-2");
        await Pass(c);
        Idle(question: Pick() with { Fields = [] });
        await Pass(c);

        await Do(c, ha.SendFromCard(Node, "stale answer", decisionId: decisionId, driver: driver,
            rid: "r-stale-question", expectedInstance: "host-run-1"));

        Assert.Empty(answered);
        Assert.Empty(replied);
        Assert.Equal("instance-mismatch", Ack.GetProperty("outcome").GetString());
        Assert.Equal("r-stale-question", Ack.GetProperty("rid").GetString());
        await Pass(c, 20);
        Assert.Empty(answered);
        await CardCloses(c);
        Assert.Empty(replied);
    }

    [Theory]
    [InlineData(false, null, "human")]
    [InlineData(true, "q1", "agent")]
    [InlineData(true, null, "human")]
    public async Task A_matching_instance_is_delivered_through_the_idle_or_question_path(bool question, string? decisionId, string driver)
    {
        Idle();
        await using var c = ConnectorWithInstance("host-run-2");
        await Pass(c);
        if (question)
        {
            Idle(question: Pick() with { Fields = [] });
            await Pass(c);
        }

        await Do(c, ha.SendFromCard(Node, "current answer", decisionId: decisionId, driver: driver,
            rid: "r-current-instance", expectedInstance: "host-run-2"));

        if (question)
        {
            Assert.Equal("current answer", Assert.Single(answered).Text);
            Assert.Empty(replied);
        }
        else
        {
            Assert.Equal("current answer", Assert.Single(replied).Text);
            Assert.Equal("delivered", Ack.GetProperty("outcome").GetString());
            Assert.Empty(answered);
        }
    }

    [Fact]
    public async Task A_stale_payload_arriving_between_question_reads_is_refused_without_blocking_a_later_answer()
    {
        Idle();
        await using var c = ConnectorWithInstance("host-run-2");
        await Pass(c);
        Idle(question: Pick() with { Fields = [] });
        await Pass(c);
        ha.AfterRead = entity =>
        {
            if (entity != SessionEntities.ReplyPayload(Node)) { return; }
            ha.AfterRead = null;
            ha.SendFromCard(Node, "stale between reads", decisionId: "q1", driver: "agent",
                rid: "r-between-reads", expectedInstance: "host-run-1");
        };

        await Pass(c, 20);

        Assert.Empty(answered);
        Assert.Empty(replied);
        Assert.Equal("instance-mismatch", Ack.GetProperty("outcome").GetString());
        Assert.Equal("r-between-reads", Ack.GetProperty("rid").GetString());
        await Do(c, ha.SendFromCard(Node, "fresh answer", decisionId: "q1", driver: "agent",
            expectedInstance: "host-run-2"));
        Assert.Equal("fresh answer", Assert.Single(answered).Text);
        Assert.Empty(replied);
    }

    [Fact]
    public async Task A_stale_reply_queued_when_a_question_opens_keeps_its_instance_mismatch_refusal()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ReplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        outcome = _ => { reached.TrySetResult(); return release.Task; };
        Idle();
        await using var c = ConnectorWithInstance("host-run-2");
        await Pass(c);
        c.Observe(ha.SendFromCard(Node, "first", expectedInstance: "host-run-2"));
        await Pass(c);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            c.Observe(ha.SendFromCard(Node, "stale queued reply", driver: "agent",
                rid: "r-queued-instance", expectedInstance: "host-run-1"));
            await Pass(c);
            Assert.Equal("first", Assert.Single(replied).Text);

            Idle(question: Pick());
            await Pass(c);

            Assert.Equal("instance-mismatch", Ack.GetProperty("outcome").GetString());
            Assert.Equal("r-queued-instance", Ack.GetProperty("rid").GetString());
            Assert.Contains("instance", Detail.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Empty(answered);
            Assert.Equal("first", Assert.Single(replied).Text);
        }
        finally
        {
            release.TrySetResult(new ReplyResult(AnswerOutcome.Delivered));
            await c.DeliveredAsync();
        }
        await CardCloses(c);
        Assert.Equal("first", Assert.Single(replied).Text);
    }

    [Fact]
    public async Task An_untagged_agent_payload_swapped_between_question_reads_is_refused_not_typed()
    {
        Idle();
        await using var c = ConnectorWithInstance("host-run-2");
        await Pass(c);
        Idle(question: Pick() with { Fields = [] });
        await Pass(c);
        ha.AfterRead = entity =>
        {
            if (entity != SessionEntities.ReplyPayload(Node)) { return; }
            ha.AfterRead = null;
            ha.SendFromCard(Node, "agent reply not an answer", driver: "agent",
                rid: "r-untagged-swap", expectedInstance: "host-run-2");
        };

        await Pass(c, 20);

        Assert.Empty(answered);
        Assert.Empty(replied);
        Assert.Equal("refused", Ack.GetProperty("outcome").GetString());
        Assert.Equal("r-untagged-swap", Ack.GetProperty("rid").GetString());
        await Pass(c, 20);
        Assert.Empty(answered);
        await CardCloses(c);
        Assert.Empty(replied);
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

    [Fact]
    public async Task A_split_payload_at_an_open_question_is_not_refused_using_the_previous_messages_identity()
    {
        await using var c = await Ready();
        var previous = ha.SendFromCard(Node, "previous message", driver: "agent");
        await Do(c, previous);
        replied.Clear();
        Idle(question: Pick());
        await Pass(c);
        var current = ha.SendFromCard(Node, "current message", driver: "agent", rid: "r-current");
        var split = current with { State = current.State with { Attributes = previous.State.Attributes } };
        ha.States[current.EntityId] = split.State;

        await Do(c, split);

        Assert.False(Detail.TryGetProperty("reply_ack", out _));
        ha.States[current.EntityId] = current.State;
        await Do(c, current);
        Assert.True(Detail.TryGetProperty("reply_ack", out _), "The complete payload must receive its own refusal acknowledgement.");
        Assert.Equal("r-current", Ack.GetProperty("rid").GetString());
        Assert.Equal("refused", Ack.GetProperty("outcome").GetString());
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
