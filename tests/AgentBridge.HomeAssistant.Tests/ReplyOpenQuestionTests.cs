using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

// #3204: agent messages wait in memory for the owner's question to close; neither they nor their
// Send presses ever become its answer.
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

    async Task<HaConnector> Asking(params string[] agents)
    {
        var c = await Ready(agents);
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
    [InlineData(true, "agent-user", "agent-user", false)]
    [InlineData(false, "agent-user", "agent-user", false)]
    [InlineData(true, "agent-user", "agent-user", true)]
    [InlineData(true, null, "agent-user", false)]
    [InlineData(true, "agent-user", null, false)]
    [InlineData(false, "agent-user", "agent-user", false, "q0")]
    public async Task An_agent_payload_and_companion_press_cannot_confirm_the_owners_completed_form(bool together, string? agent, string? user, bool restFails, string? tag = null)
    {
        await using var c = await Asking(agent is null ? [] : [agent]);
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut now"));
        var payload = ha.SendFromCard(Node, "agent message", decisionId: tag, driver: "agent", rid: "r-agent");
        if (together) { c.Observe(payload); }
        else { await Do(c, payload); }
        ha.GetFailsFor = id => restFails && id == SessionEntities.ReplyPayload(Node);
        await Do(c, ha.Press(SessionEntities.Submit(Node), user));
        Assert.Empty(answered);
        Assert.Equal(tag is null ? "queued" : "refused", Ack.GetProperty("outcome").GetString());
        await Pass(c, 20);
        Assert.Empty(answered);
        ha.GetFailsFor = null;
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Single(answered);
        await CardCloses(c);
        Assert.Equal(tag is null ? ["agent message"] : [], replied.Select(r => r.Text));
    }

    [Fact]
    public async Task A_mixed_watch_with_lagging_complete_REST_cannot_make_an_agent_press_the_owners()
    {
        await using var c = await Asking();
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut now"));
        var previous = ha.SendFromCard(Node, "first", driver: "agent", rid: "r-1");
        await Do(c, previous);
        await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user"));
        var current = ha.SendFromCard(Node, "second", driver: "agent", rid: "r-2");
        ha.States[current.EntityId] = previous.State;
        c.Observe(current with { State = current.State with { Attributes = previous.State.Attributes } });
        await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user"));
        Assert.Empty(answered);
        await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user"));
        Assert.Empty(answered);
        ha.States[current.EntityId] = current.State;
        await Do(c, current);
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Single(answered);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, false, true, true)]
    public async Task Either_half_of_a_split_payload_can_complete_or_be_replaced_without_stranding_the_question(bool stateFirst, bool replacement, bool first = false, bool owner = false, bool late = false)
    {
        await using var c = await Asking();
        HaTrigger? previous = null;
        if (!first) { previous = ha.SendFromCard(Node, "previous", decisionId: "q1", driver: "agent"); await Do(c, previous); await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user")); }
        var current = ha.SendFromCard(Node, "queued", decisionId: owner && !late ? "q1" : null, driver: owner ? null : "agent", rid: "r-1");
        var split = current with { State = stateFirst
            ? current.State with { Attributes = previous?.State.Attributes ?? JsonSerializer.SerializeToElement(new { }) }
            : current.State with { State = previous?.State.State ?? "unknown" } };
        ha.States[current.EntityId] = split.State;
        await Do(c, split);
        if (!owner) { await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user")); }
        if (replacement) { current = ha.SendFromCard(Node, "replacement", driver: "agent", rid: "r-2"); }
        if (late)
        {
            ha.AfterRead = id =>
            {
                if (id != current.EntityId) { return; }
                ha.AfterRead = null;
                ha.States[id] = current.State;
            };
            await Pass(c, 20);
        }
        else { ha.States[current.EntityId] = current.State; await Do(c, current); }
        if (replacement) { await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user")); }
        if (!owner)
        {
            await CardCloses(c);
            Assert.Equal(replacement ? "replacement" : "queued", Assert.Single(replied).Text);
            Idle(question: Pick() with { Id = "q2" }); await Pass(c);
        }
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut now"));
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Equal(owner ? "q1" : "q2", Assert.Single(answered).Question);
    }

    [Fact]
    public async Task An_idle_companion_press_does_not_consume_the_next_questions_owner_submit()
    {
        await using var c = await Asking();
        await Do(c, ha.SendFromCard(Node, "queued", driver: "agent", rid: "r-1"));
        await CardCloses(c);
        await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user"));
        Idle(question: Pick() with { Id = "q2" }); await Pass(c);
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut now"));
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Equal("q2", Assert.Single(answered).Question);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sends_counted_as_a_question_closes_are_not_counted_again_against_a_later_messages_press(bool clearFails)
    {
        await using var c = await Asking();
        for (var n = 1; n <= 3; n++)
        {
            c.Observe(ha.SendFromCard(Node, "message " + n, driver: "agent", rid: "r-" + n));
            if (n < 3) { c.Observe(ha.Press(SessionEntities.Submit(Node), "agent-user")); }
        }
        var failed = false;
        ha.PublishFailsFor = (topic, body) => clearFails && topic == HaContract.Topics(Node).DecisionAttributes && body == "{}" && !failed && (failed = true);
        Idle(turn: 2); await Pass(c); await Pass(c);
        Assert.Equal(clearFails, failed);
        Idle(question: Pick() with { Id = "q2" }); await Pass(c);
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut now"));
        await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user"));
        Assert.Empty(answered);
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Equal("q2", Assert.Single(answered).Question);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_agent_answer_tagged_for_a_replaced_question_never_confirms_the_next_one(bool seenFirst)
    {
        await using var c = await Asking();
        var tagged = ha.SendFromCard(Node, "for q1", decisionId: "q1", driver: "agent", rid: "r-q1");
        if (seenFirst) { await Do(c, tagged); } else { c.Observe(tagged); }
        Idle(question: Pick() with { Id = "q2" }); await Pass(c);
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut now"));
        await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user"));
        Assert.Empty(answered);
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Equal("q2", Assert.Single(answered).Question);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_queue_ack_publish_retries_queued_without_duplicate_delivery(bool attributes)
    {
        await using var c = await Asking();
        var topic = attributes ? HaContract.Topics(Node).ActivityAttributes : HaContract.Topics(Node).ActivityState;
        var failed = false;
        ha.PublishFailsFor = (t, _) => t == topic && !failed && (failed = true);
        await Do(c, ha.SendFromCard(Node, "queued", driver: "agent", rid: "r-1"));
        Assert.True(failed);
        Assert.Equal("queued", Ack.GetProperty("outcome").GetString());
        Assert.Equal(1, Detail.GetProperty("reply_queue_count").GetInt32());
        await CardCloses(c);
        Assert.Equal("queued", Assert.Single(replied).Text);
    }

    [Fact]
    public async Task Two_agent_sends_ahead_of_their_presses_never_confirm_the_owners_form()
    {
        await using var c = await Asking();
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut now"));
        await Do(c, ha.SendFromCard(Node, "first", driver: "agent", rid: "r-1"));
        await Do(c, ha.SendFromCard(Node, "second", driver: "agent", rid: "r-2"));
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Empty(answered);
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Single(answered);
        await CardCloses(c);
        await c.DeliveredAsync(); await Pass(c);
        Assert.Equal(["first", "second"], replied.Select(r => r.Text));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task An_agent_press_is_never_the_owners_across_reads_clears_or_attachments(bool raced, bool attached)
    {
        await using var c = await Asking();
        HaTrigger[] late = [];
        ha.AfterRead = id =>
        {
            if (!raced || id != SessionEntities.ReplyPayload(Node)) { return; }
            ha.AfterRead = null;
            late = [ha.SendFromCard(Node, "first", driver: "agent", rid: "r-1"), ha.Press(SessionEntities.Submit(Node))];
        };
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut now"));
        foreach (var hit in late) { c.Observe(hit); }
        if (!raced)
        {
            await Do(c, ha.SendFromCard(Node, "first", driver: "agent", rid: "r-1", files: attached ? [new JsonObject { ["name"] = "a.txt" }] : null));
            if (!attached) { Idle(turn: 2); await Pass(c); Idle(question: Pick()); await Pass(c); await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut now")); }
            await Do(c, ha.Press(SessionEntities.Submit(Node)));
        }
        await Pass(c, 3);
        Assert.Empty(answered);
        Assert.Equal(raced ? 1 : 0, Detail.GetProperty("reply_queue_count").GetInt32());
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Single(answered);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_earlier_agent_payload_does_not_spend_a_later_form_submit(bool stale)
    {
        await using var c = await Ready("agent-user");
        if (stale) { await Do(c, ha.SendFromCard(Node, "earlier", driver: "agent", rid: "r-old")); }
        Idle(question: Pick()); await Pass(c);
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut now"));
        await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user"));
        Assert.Single(answered);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    public async Task A_question_clear_or_replacement_captures_outstanding_agent_payloads_before_baselining(bool watched, bool submitUnreadable, bool replacement = false)
    {
        await using var c = await Asking();
        for (var n = 1; n <= (watched ? 3 : 1); n++)
        {
            var hit = ha.SendFromCard(Node, "message " + n, driver: "agent", rid: "r-" + n);
            if (watched && n < 3) { c.Observe(hit); }
        }
        ha.GetFailsFor = id => submitUnreadable && id == SessionEntities.Submit(Node);
        Idle(turn: 2, question: replacement ? Pick() with { Id = "q2" } : null);
        await Pass(c);
        ha.GetFailsFor = null;
        if (replacement) { Idle(turn: 3); await Pass(c); }
        await Pass(c, 20);
        for (var n = 0; n < 4; n++) { await c.DeliveredAsync(); await Pass(c); }
        Assert.Equal(Enumerable.Range(1, watched ? 3 : 1).Select(n => "message " + n), replied.Select(r => r.Text));
        Assert.Empty(answered);
    }

    [Fact]
    public async Task A_queued_completion_never_blocks_another_sessions_replies()
    {
        const string B = "0d1e2f30-4a5b-4c6d-8e7f-90a1b2c3d4e5";
        var nodeB = HaContract.NodeId(B);
        var held = new TaskCompletionSource<ReplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        outcome = r => r.SessionId == A ? held.Task : Task.FromResult(new ReplyResult(AnswerOutcome.Delivered));
        await using var c = await Asking();
        sessions.Set(FakeSessions.Copilot(B) with { Activity = new Activity { Response = "Done", TurnCount = 1, UserMessage = "go" } });
        await Pass(c);
        await Do(c, ha.SendFromCard(Node, "queued", driver: "agent", rid: "r-1"));
        Idle(turn: 2);
        await Pass(c);
        await Pass(c, 20);
        held.SetResult(new ReplyResult(AnswerOutcome.Delivered));
        await c.DeliveredAsync();
        await Pass(c);
        await Do(c, ha.SendFromCard(nodeB, "first", rid: "r-b1"));
        await Do(c, ha.SendFromCard(nodeB, "second", rid: "r-b2"));
        Assert.Equal(["first", "second"], replied.Where(r => r.SessionId == B).Select(r => r.Text));
        var ack = JsonDocument.Parse(ha.Retained[HaContract.Topics(nodeB).ActivityAttributes]).RootElement.GetProperty("reply_ack");
        Assert.Equal(("r-b2", "delivered"), (ack.GetProperty("rid").GetString(), ack.GetProperty("outcome").GetString()));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(17)]
    public async Task Agent_messages_wait_in_a_bounded_FIFO_for_the_owner_answer(int count)
    {
        await using var c = await Asking();

        for (var n = 1; n <= count; n++)
        {
            await Do(c, ha.SendFromCard(Node, "message " + n, driver: "agent", rid: "r-" + n, sender: "SG Builder"));
            Assert.Equal("r-" + n, Ack.GetProperty("rid").GetString());
            Assert.Equal(n > 16 ? "refused" : "queued", Ack.GetProperty("outcome").GetString());
            if (n > 16) { Assert.Contains("queue full", Detail.GetProperty("error").GetString()); }
            Assert.Equal(Math.Min(n, 16), Detail.GetProperty("reply_queue_count").GetInt32());
            await Do(c, ha.Press(SessionEntities.Submit(Node)));
        }

        Assert.Empty(replied);
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut now"));
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Single(answered);
        Assert.Empty(replied);
        await CardCloses(c);
        for (var n = 0; n < Math.Min(count, 16); n++) { await c.DeliveredAsync(); await Pass(c); }
        Assert.Equal(Enumerable.Range(1, Math.Min(count, 16)).Select(n => "message " + n), replied.Select(r => r.Text));
        await Do(c, ha.SendFromCard(Node, "message 1", driver: "agent", rid: "r-1"));
        Assert.Equal(Math.Min(count, 16), replied.Count);
        Assert.Equal(0, Detail.GetProperty("reply_queue_count").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_split_payload_at_an_open_question_is_not_refused_using_the_previous_messages_identity(bool clears)
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
        if (clears) { Idle(turn: 2); await Pass(c); }
        ha.States[current.EntityId] = current.State;
        await Do(c, current);
        Assert.True(Detail.TryGetProperty("reply_ack", out _), "The complete payload must receive its own acknowledgement.");
        Assert.Equal("r-current", Ack.GetProperty("rid").GetString());
        Assert.Equal(clears ? "delivered" : "queued", Ack.GetProperty("outcome").GetString());
        await CardCloses(c);
        Assert.Equal("current message", Assert.Single(replied).Text);
        Assert.Empty(answered);
    }

    [Fact]
    public async Task A_restart_drops_queued_messages_and_shows_an_empty_queue()
    {
        HaTrigger? last = null;
        await using (var c = await Asking())
        {
            for (var n = 1; n <= 2; n++)
            {
                await Do(c, last = ha.SendFromCard(Node, "message " + n, driver: "agent", rid: "r-" + n));
            }
            Assert.Equal(2, Detail.GetProperty("reply_queue_count").GetInt32());
        }

        await using var restarted = Connector();
        await Pass(restarted);
        await Do(restarted, last!);
        Assert.Equal(0, Detail.GetProperty("reply_queue_count").GetInt32());
        await CardCloses(restarted);
        await Pass(restarted, 20);
        Assert.Empty(replied);
    }

    [Fact]
    public async Task A_burst_in_one_pass_keeps_all_three_messages_and_the_owner_can_still_answer()
    {
        await using var c = await Asking();
        for (var n = 1; n <= 3; n++)
        {
            c.Observe(ha.SendFromCard(Node, "message " + n, driver: "agent", rid: "r-" + n));
            c.Observe(ha.Press(SessionEntities.Submit(Node)));
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
        // The same id coming back is a new lifetime: it starts with nothing queued.
        Idle();
        await Pass(c);
        await Pass(c, 20);
        await c.DeliveredAsync();
        await Pass(c);
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

    // #3224: an agent payload the watch missed and REST could not read is reconciled, once read, with the Send
    // already taken as the agent's meanwhile. A press made before that payload went in is never its companion.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_recovered_payload_settles_its_unread_companion_press_but_never_an_earlier_one(bool ownerFirst)
    {
        await using var c = await Asking();
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut now"));
        ha.GetFailsFor = id => id == SessionEntities.ReplyPayload(Node);
        if (ownerFirst) { await Do(c, ha.Press(SessionEntities.Submit(Node))); }
        ha.SendFromCard(Node, "agent message", driver: "agent", rid: "r-1");
        if (!ownerFirst) { await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user")); }
        ha.GetFailsFor = null;
        await Pass(c, 20);
        if (ownerFirst) { await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user")); }
        Assert.Empty(answered);
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Single(answered);
        await CardCloses(c);
        Assert.Equal("agent message", Assert.Single(replied).Text);
    }

    // #3224: the same holds when the payload, once readable, first shows as a split before its complete read.
    [Fact]
    public async Task A_recovered_payload_first_seen_split_still_settles_its_unread_companion_press()
    {
        await using var c = await Asking();
        var first = ha.SendFromCard(Node, "first", driver: "agent", rid: "r-1");
        await Do(c, first);
        await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user"));
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut now"));
        ha.GetFailsFor = id => id == SessionEntities.ReplyPayload(Node);
        var second = ha.SendFromCard(Node, "second", driver: "agent", rid: "r-2");
        await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user"));
        ha.GetFailsFor = null;
        await Do(c, second with { State = second.State with { Attributes = first.State.Attributes } });
        Assert.Empty(answered);
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Single(answered);
        await CardCloses(c);
        Assert.Equal(["first", "second"], replied.Select(r => r.Text));
    }

    // #3224: a split of two missed publishes (the older's stamp, the newer's attributes) clears once the newer
    // completes later on Home Assistant's clock, not only on an unrelated third publish.
    [Fact]
    public async Task A_split_of_two_missed_publishes_clears_when_the_newer_one_completes()
    {
        await using var c = await Asking();
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut now"));
        var older = ha.SendFromCard(Node, "older", driver: "agent", rid: "r-1");
        var newer = ha.SendFromCard(Node, "newer", driver: "agent", rid: "r-2");
        var split = newer.State with { State = older.State.State, LastChanged = older.State.LastChanged, LastUpdated = newer.State.LastUpdated!.Value.AddMilliseconds(-500) };
        ha.States[newer.EntityId] = split;
        await Do(c, new HaTrigger(newer.EntityId, split));
        await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user"));
        await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user"));
        ha.States[newer.EntityId] = newer.State;
        await Do(c, newer);
        Assert.Empty(answered);
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Single(answered);
        await CardCloses(c);
        Assert.Equal("newer", Assert.Single(replied).Text);
    }

    // #3224: a watch report of the new stamp with the previous message's attributes does not hide the complete
    // payload REST reads at that stamp: it is queued and acknowledged, never refused.
    [Fact]
    public async Task A_split_watch_report_does_not_hide_the_complete_payload_read_at_the_same_stamp()
    {
        await using var c = await Asking();
        var first = ha.SendFromCard(Node, "first", driver: "agent", rid: "r-1");
        await Do(c, first);
        await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user"));
        var second = ha.SendFromCard(Node, "second", driver: "agent", rid: "r-2");
        await Do(c, second with { State = second.State with { Attributes = first.State.Attributes } });
        Assert.Equal(("r-2", "queued"), (Ack.GetProperty("rid").GetString(), Ack.GetProperty("outcome").GetString()));
        await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user"));
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut now"));
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Single(answered);
        await CardCloses(c);
        Assert.Equal(["first", "second"], replied.Select(r => r.Text));
    }

    // #3224: Send presses are remembered up to a bound; an older one read again is never taken anew, so an
    // agent's press forgotten that way can never become the owner's.
    [Fact]
    public async Task Send_presses_are_kept_bounded_and_an_evicted_agent_press_read_again_is_never_the_owners()
    {
        await using var c = await Asking();
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut now"));
        var presses = new List<HaTrigger>();
        for (var n = 1; n <= 70; n++)
        {
            c.Observe(ha.SendFromCard(Node, "message " + n, driver: "agent", rid: "r-" + n));
            presses.Add(ha.Press(SessionEntities.Submit(Node), "agent-user"));
            c.Observe(presses[^1]);
            await Pass(c);
        }
        Assert.InRange(c.PressesKept(A), 1, 64);
        c.Observe(ha.SendFromCard(Node, "late", driver: "agent", rid: "r-late"));
        c.Observe(presses[0]);
        await Pass(c);
        await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user"));
        Assert.Empty(answered);
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Single(answered);
    }

    // #3296: a split of two missed agent publishes owes both their Sends, whether they come after the newer
    // completes or one before and one after: neither is ever the owner's answer to the completed form.
    // With pressedUnread, both came while no payload could be read; they settle both halves, so the owner's
    // first press answers.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Both_agent_presses_of_an_unseen_split_stay_the_agents_once_it_completes(bool olderPressFirst, bool pressedUnread)
    {
        await using var c = await Asking("agent-user");
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut now"));
        ha.GetFailsFor = id => pressedUnread && id == SessionEntities.ReplyPayload(Node);
        var older = ha.SendFromCard(Node, "older", driver: "agent", rid: "r-1");
        if (pressedUnread) { await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user")); }
        var newer = ha.SendFromCard(Node, "newer", driver: "agent", rid: "r-2");
        if (pressedUnread) { await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user")); }
        ha.GetFailsFor = null;
        var split = newer.State with { State = older.State.State, LastChanged = older.State.LastChanged, LastUpdated = newer.State.LastUpdated!.Value.AddMilliseconds(-500) };
        ha.States[newer.EntityId] = split;
        await Do(c, new HaTrigger(newer.EntityId, split));
        if (olderPressFirst) { await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user")); }
        ha.States[newer.EntityId] = newer.State;
        await Do(c, newer);
        if (!pressedUnread) { await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user")); }
        if (!pressedUnread && !olderPressFirst) { await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user")); }
        Assert.Empty(answered);
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Single(answered);
    }

    // #3298: a watch report of the first payload's stamp before its attributes arrived, delivered after the complete
    // payload was queued and its Send spent, ends once that payload reads complete again, written later.
    [Fact]
    public async Task A_delayed_initial_split_of_a_queued_payload_clears_once_it_reads_complete_again()
    {
        await using var c = await Asking("agent-user");
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut now"));
        var first = ha.SendFromCard(Node, "first", driver: "agent", rid: "r-1");
        await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user"));
        var initial = first.State with { Attributes = JsonSerializer.SerializeToElement(new { }), LastUpdated = first.State.LastUpdated!.Value.AddMilliseconds(-500) };
        await Do(c, new HaTrigger(first.EntityId, initial));
        Assert.Empty(answered);
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Single(answered);
        await CardCloses(c);
        Assert.Equal("first", Assert.Single(replied).Text);
    }
}
