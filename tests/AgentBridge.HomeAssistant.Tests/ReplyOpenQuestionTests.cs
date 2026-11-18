using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;
using System.Text.Json;
using Xunit;

// Agent messages wait for the owner question; they never become its answer.
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
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_agent_payload_and_companion_press_cannot_confirm_the_owners_completed_form(bool together)
    {
        await using var c = await Ready("agent-user");
        Idle(question: Pick());
        await Pass(c);
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut now"));
        var payload = ha.SendFromCard(Node, "agent message", driver: "agent", rid: "r-agent");
        if (together) { c.Observe(payload); }
        else { await Do(c, payload); }
        await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user"));
        Assert.Empty(answered);
        Assert.Equal("queued", Ack.GetProperty("outcome").GetString());
        await Pass(c, 20);
        Assert.Empty(answered);
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Single(answered);
        await CardCloses(c);
        Assert.Equal("agent message", Assert.Single(replied).Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_question_clear_captures_outstanding_agent_payloads_before_baselining(bool watched)
    {
        await using var c = await Asking();
        for (var n = 1; n <= (watched ? 3 : 1); n++)
        {
            var hit = ha.SendFromCard(Node, "message " + n, driver: "agent", rid: "r-" + n);
            if (watched) { c.Observe(hit); }
        }
        await CardCloses(c);
        for (var n = 0; n < 3; n++) { await c.DeliveredAsync(); await Pass(c); }
        Assert.Equal(Enumerable.Range(1, watched ? 3 : 1).Select(n => "message " + n), replied.Select(r => r.Text));
        Assert.Empty(answered);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_semantically_invalid_snapshot_refuses_queueing_without_starving_owner_answers(bool startup)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "ha-connector.json.inbox");
        if (startup) { File.WriteAllText(path, """{"Version":2,"Messages":[]}"""); }
        await using var c = await Asking();
        if (!startup) { File.WriteAllText(path, """{"Version":2,"Messages":[]}"""); }
        var mark = ha.Log.Count;
        c.Observe(ha.SendFromCard(Node, "message", driver: "agent", rid: "r-invalid"));
        c.Observe(ha.Choose(SessionEntities.Field(Node, 1), "Cut now"));
        c.Observe(ha.Press(SessionEntities.Submit(Node)));
        await Pass(c);
        Assert.Single(answered);
        Assert.Equal("refused", Ack.GetProperty("outcome").GetString());
        Assert.Contains(NotesSince(mark), n => n.Attributes.TryGetProperty("error", out var error)
            && error.GetString()!.Contains("queue unavailable", StringComparison.Ordinal));
        Assert.Empty(replied);
    }

    [Fact]
    public async Task Retirement_preserves_entries_through_discovery_holds_and_restart_then_forgets_both_pending_and_receipts()
    {
        var path = Path.Combine(folder, "ha-connector.json.inbox");
        await using (var c = await Asking())
        {
            await Do(c, ha.SendFromCard(Node, "old delivered", driver: "agent", rid: "r-done"));
            await CardCloses(c);
            Idle(question: Pick());
            await Pass(c);
            await Do(c, ha.SendFromCard(Node, "old pending", driver: "agent", rid: "r-pending"));
            sessions.Held = new DiscoveryHeld("incomplete discovery", [AgentKind.Copilot]);
            sessions.End(A);
            await Pass(c);
        }
        await using (var c = Connector())
        {
            await Pass(c);
            Assert.Equal(2, new DurableInbox(path).Read().Count);
            Idle(question: Pick());
            await Pass(c);
            Assert.Equal(2, new DurableInbox(path).Read().Count);
            sessions.Held = null;
            sessions.End(A);
            await Pass(c);
            Assert.Empty(new DurableInbox(path).Read());
        }
        replied.Clear();
        Idle();
        await using var restarted = Connector();
        await Pass(restarted);
        await Pass(restarted, 20);
        Assert.Empty(replied);
        await Do(restarted, ha.SendFromCard(Node, "new delivered", driver: "agent", rid: "r-done"));
        await Do(restarted, ha.SendFromCard(Node, "new pending", driver: "agent", rid: "r-pending"));
        Assert.Equal(["new delivered", "new pending"], replied.Select(r => r.Text));
    }

    [Fact]
    public async Task Startup_reclaims_shared_capacity_from_sessions_that_ended_while_the_connector_was_down()
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "ha-connector.json.inbox");
        var messages = Enumerable.Range(0, 1000).Select(n =>
            new InboxMessage("gone-" + n / 100, "r-" + n, "sender", "old", FakeSessions.T0, null)).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(new { Version = 1, Messages = messages }));
        await using var c = Connector();
        await Pass(c);
        var store = new DurableInbox(path);
        Assert.Empty(store.Read());
        Assert.Equal("new", store.Enqueue(A, "new", "sender", "new", FakeSessions.T0).Rid);
    }

    [Fact]
    public async Task Three_agent_messages_wait_durably_for_the_owner_answer_then_deliver_once_in_order()
    {
        await using var c = await Asking();

        foreach (var n in new[] { 1, 2, 3 })
        {
            await Do(c, ha.SendFromCard(Node, "message " + n, driver: "agent", rid: "r-" + n, sender: "SG Builder"));
            Assert.Equal("r-" + n, Ack.GetProperty("rid").GetString());
            Assert.Equal("queued", Ack.GetProperty("outcome").GetString());
            Assert.Equal(n, Detail.GetProperty("reply_queue_count").GetInt32());
        }

        Assert.Empty(replied);
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut now"));
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Single(answered);
        Assert.Empty(replied);
        await CardCloses(c);
        for (var n = 0; n < 3; n++) { await c.DeliveredAsync(); await Pass(c); }
        Assert.Equal(["message 1", "message 2", "message 3"], replied.Select(r => r.Text));
        await Do(c, ha.SendFromCard(Node, "message 1", driver: "agent", rid: "r-1"));
        Assert.Equal(3, replied.Count);
        Assert.Equal(0, Detail.GetProperty("reply_queue_count").GetInt32());
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
        Assert.True(Detail.TryGetProperty("reply_ack", out _), "The complete payload must receive its own acknowledgement.");
        Assert.Equal("r-current", Ack.GetProperty("rid").GetString());
        Assert.Equal("queued", Ack.GetProperty("outcome").GetString());
        await CardCloses(c);
        Assert.Equal("current message", Assert.Single(replied).Text);
        Assert.Empty(answered);
    }

    [Fact]
    public async Task A_full_agent_queue_refuses_without_overwriting_accepted_messages()
    {
        await using var c = await Asking();
        for (var n = 0; n < 17; n++)
        {
            await Do(c, ha.SendFromCard(Node, "message " + n, driver: "agent", rid: "r-" + n));
        }
        Assert.Equal("refused", Ack.GetProperty("outcome").GetString());
        Assert.Contains("queue full", Detail.GetProperty("error").GetString());
        Assert.Equal(16, Detail.GetProperty("reply_queue_count").GetInt32());
        await CardCloses(c);
        for (var n = 0; n < 16; n++) { await c.DeliveredAsync(); await Pass(c); }
        Assert.Equal(Enumerable.Range(0, 16).Select(n => "message " + n), replied.Select(r => r.Text));
    }

    [Fact]
    public async Task A_restart_preserves_the_queue_order_and_deduplicates_before_and_after_delivery()
    {
        await using (var c = await Asking())
        {
            for (var n = 1; n <= 3; n++)
            {
                await Do(c, ha.SendFromCard(Node, "message " + n, driver: "agent", rid: "r-" + n));
            }
        }

        await using (var c = Connector())
        {
            await Pass(c);
            await Do(c, ha.SendFromCard(Node, "duplicate", driver: "agent", rid: "r-1"));
            Assert.Equal("queued", Ack.GetProperty("outcome").GetString());
            Assert.Equal(3, Detail.GetProperty("reply_queue_count").GetInt32());
            await CardCloses(c);
            for (var n = 0; n < 3; n++) { await c.DeliveredAsync(); await Pass(c); }
        }
        await using (var c = Connector())
        {
            await Pass(c);
            await Do(c, ha.SendFromCard(Node, "duplicate", driver: "agent", rid: "r-1"));
            Assert.Equal("delivered", Ack.GetProperty("outcome").GetString());
            Assert.Equal(["message 1", "message 2", "message 3"], replied.Select(r => r.Text));
        }
    }

    [Fact]
    public async Task A_burst_in_one_pass_keeps_all_three_messages_and_the_owner_can_still_answer()
    {
        await using var c = await Asking();
        for (var n = 1; n <= 3; n++)
        {
            c.Observe(ha.SendFromCard(Node, "message " + n, driver: "agent", rid: "r-" + n));
        }
        c.Observe(ha.Choose(SessionEntities.Field(Node, 1), "Cut now"));
        c.Observe(ha.Press(SessionEntities.Submit(Node)));
        await Pass(c);
        Assert.Single(answered);
        Assert.Empty(replied);
        Assert.Equal(3, Detail.GetProperty("reply_queue_count").GetInt32());
        await CardCloses(c);
        for (var n = 0; n < 3; n++) { await c.DeliveredAsync(); await Pass(c); }
        Assert.Equal(["message 1", "message 2", "message 3"], replied.Select(r => r.Text));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unwritable_or_corrupt_storage_never_acknowledges_a_durable_queue(bool corrupt)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "ha-connector.json.inbox");
        if (corrupt) { File.WriteAllText(path, "{broken"); }
        else { Directory.CreateDirectory(path); }
        await using var c = await Asking();
        await Do(c, ha.SendFromCard(Node, "first", driver: "agent", rid: "r-1"));
        Assert.Equal("refused", Ack.GetProperty("outcome").GetString());
        Assert.Contains("queue unavailable", Detail.GetProperty("error").GetString());
        Assert.Equal(0, Detail.GetProperty("reply_queue_count").GetInt32());
        await CardCloses(c);
        Assert.Empty(replied);
    }

    [Fact]
    public async Task A_restart_during_dispatch_reports_uncertainty_and_never_automatically_retypes()
    {
        var sent = new TaskCompletionSource<ReplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        outcome = _ => sent.Task;
        var c = await Asking();
        var disposed = false;
        try
        {
            await Do(c, ha.SendFromCard(Node, "first", driver: "agent", rid: "r-1"));
            Idle(turn: 2);
            await Pass(c);
            await Pass(c, 20);
            await c.DisposeAsync();
            disposed = true;
            await using var restarted = Connector();
            await Pass(restarted);
            await Do(restarted, ha.SendFromCard(Node, "duplicate", driver: "agent", rid: "r-1"));
            Assert.Equal("unconfirmed", Ack.GetProperty("outcome").GetString());
            Assert.True(replied.Count <= 1);
        }
        finally
        {
            sent.TrySetResult(new ReplyResult(AnswerOutcome.Delivered));
            await c.DeliveredAsync();
            if (!disposed) { await c.DisposeAsync(); }
        }
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

    [Theory]
    [InlineData(null)]
    [InlineData("agent")]
    public async Task A_session_that_ends_with_a_waiting_message_never_receives_it(string? driver)
    {
        await using var c = await Asking();
        await Do(c, ha.SendFromCard(Node, "too late", driver: driver, rid: "r-end"));
        sessions.End(A);
        await Pass(c, 20);
        await c.DeliveredAsync();
        Assert.Empty(replied);
        Assert.Empty(answered);
    }

    [Fact]
    public async Task A_queued_reply_refused_by_delivery_can_be_retried_with_the_same_id()
    {
        await using var c = await Asking();
        await Do(c, ha.SendFromCard(Node, "first", driver: "agent", rid: "r-1"));
        outcome = _ => Task.FromResult(new ReplyResult(AnswerOutcome.Refused, "nothing typed"));
        await CardCloses(c);
        Assert.Equal("refused", Ack.GetProperty("outcome").GetString());
        outcome = _ => Task.FromResult(new ReplyResult(AnswerOutcome.Delivered));
        await Do(c, ha.SendFromCard(Node, "first", driver: "agent", rid: "r-1"));
        Assert.Equal("delivered", Ack.GetProperty("outcome").GetString());
        Assert.Equal(2, replied.Count);
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
