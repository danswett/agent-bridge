using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;
using Xunit;

public sealed partial class ReplyTests
{
    string InboxPath => Path.Combine(folder, "ha-connector.json.inbox");

    [Fact]
    public async Task Different_rids_with_the_same_stamp_have_distinct_durable_deliveries()
    {
        await using var c = await Ready();
        c.Observe(ha.SendFromCard(Node, "sender A", rid: "same-at-a", at: "same-at"));
        c.Observe(ha.SendFromCard(Node, "sender B", rid: "same-at-b", at: "same-at"));
        for (var n = 0; n < 5; n++) { await Pass(c); await c.DeliveredAsync(); }

        Assert.Equal(["sender A", "sender B"], replied.Select(r => r.Text));
        Assert.Equal(2, replied.Select(r => r.Request).Distinct().Count());
    }

    [Fact]
    public async Task A_burst_larger_than_the_stamp_cache_never_retypes_a_submitted_reply_after_restart()
    {
        await using (var first = await Ready())
        {
            for (var n = 0; n < 65; n++) { first.Observe(ha.SendFromCard(Node, $"burst-{n}", rid: $"burst-{n}")); }
            await Pass(first);
            await first.DeliveredAsync();
            Assert.Equal("burst-0", Assert.Single(replied).Text);
        }
        await using var restarted = Connector();
        for (var n = 0; n < 68; n++) { await Pass(restarted); await restarted.DeliveredAsync(); }

        Assert.Equal(Enumerable.Range(0, 65).Select(n => $"burst-{n}"), replied.Select(r => r.Text));
        Assert.Null(new DurableInbox(InboxPath).Peek(Node));
    }

    [Fact]
    public async Task A_terminal_only_question_refuses_and_removes_a_queued_reply_instead_of_delivering_it_later()
    {
        await using var c = await Ready();
        c.Observe(ha.SendFromCard(Node, "queued before question", rid: "terminal-queued"));
        Idle(question: Pick() with { AnswerAtMachine = true });
        await Pass(c);

        Assert.Equal("refused", Ack.GetProperty("outcome").GetString());
        Assert.Null(new DurableInbox(InboxPath).Peek(Node));
        await CardCloses(c);
        Assert.Empty(replied);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_interrupted_rid_retried_during_a_question_keeps_its_unconfirmed_outcome(bool terminal)
    {
        await using (var first = await Ready())
        {
            first.Observe(ha.SendFromCard(Node, "possibly submitted", rid: "interrupted-question"));
            new DurableInbox(InboxPath).Start(Node, "interrupted-question", time.GetUtcNow());
        }
        Idle(question: Pick() with { AnswerAtMachine = terminal });
        await using var restarted = Connector();
        await Pass(restarted);
        await Do(restarted, ha.SendFromCard(Node, "retry", driver: "agent", rid: "interrupted-question"));

        Assert.Equal("unconfirmed", Ack.GetProperty("outcome").GetString());
        Assert.Null(new DurableInbox(InboxPath).Peek(Node));
        await CardCloses(restarted);
        Assert.Empty(replied);
    }

    [Fact]
    public async Task A_session_ending_while_the_host_is_down_does_not_leave_pending_entries_using_inbox_capacity()
    {
        await using (var first = await Ready())
        {
            first.Observe(ha.SendFromCard(Node, "session will end", rid: "ended-offline"));
        }
        sessions.End(A);
        await using var restarted = Connector();
        await Pass(restarted);
        Assert.NotNull(new DurableInbox(InboxPath).Peek(Node));
        await Pass(restarted);
        Assert.NotNull(new DurableInbox(InboxPath).Peek(Node));
        await Pass(restarted);

        Assert.Null(new DurableInbox(InboxPath).Peek(Node));
        Assert.Empty(JsonNode.Parse(File.ReadAllText(InboxPath))!["Messages"]!.AsArray());
        Assert.Empty(replied);
    }

    [Fact]
    public async Task A_transiently_absent_published_session_keeps_its_queued_reply()
    {
        await using var c = await Ready();
        c.Observe(ha.SendFromCard(Node, "survives discovery gap", rid: "gap"));
        sessions.End(A);
        await Pass(c);
        Assert.NotNull(new DurableInbox(InboxPath).Peek(Node));
        Idle();
        for (var n = 0; n < 3; n++) { await Pass(c); await c.DeliveredAsync(); }

        Assert.Equal("survives discovery gap", Assert.Single(replied).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inbox_overflow_is_acknowledged_refused_and_stays_refused_after_restart(bool totalLimit)
    {
        var release = new TaskCompletionSource<ReplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        outcome = _ => release.Task;
        await using (var c = await Ready())
        {
            var messages = Enumerable.Range(0, totalLimit ? 1000 : 100).Select(n =>
            {
                var node = totalLimit ? "other" + n / 100 : Node;
                var hit = ha.SendFromCard(node, "accepted", rid: "seed" + n);
                return new InboxMessage(node, "seed" + n, "sender", JsonSerializer.Serialize(hit.State), time.GetUtcNow(), null);
            }).ToList();
            File.WriteAllText(InboxPath, JsonSerializer.Serialize(new { Version = 1, Messages = messages }));
            var mark = ha.Log.Count;
            try
            {
                c.Observe(ha.SendFromCard(Node, "must not be typed", rid: "overflow", sender: "overflow-sender"));
                await Pass(c);
                Assert.Contains(NotesSince(mark), n => n.Summary == "Reply NOT sent" &&
                    n.Attributes.TryGetProperty("reply_ack", out var a) &&
                    a.GetProperty("rid").GetString() == "overflow" && a.GetProperty("outcome").GetString() == "refused" &&
                    a.GetProperty("sender").GetString() == "overflow-sender" && !a.TryGetProperty("duplicate", out _));
            }
            finally { release.TrySetResult(new ReplyResult(AnswerOutcome.Delivered)); }
            await c.DeliveredAsync();
        }
        new DurableInbox(InboxPath).Deliver(totalLimit ? "other0" : Node, "seed0", time.GetUtcNow());
        await using var restarted = Connector();
        await Pass(restarted);
        var retryMark = ha.Log.Count;
        await Do(restarted, ha.SendFromCard(Node, "retry after capacity frees", rid: "overflow"));

        Assert.Contains(NotesSince(retryMark), n => n.Attributes.TryGetProperty("reply_ack", out var a) &&
            a.GetProperty("rid").GetString() == "overflow" && a.GetProperty("outcome").GetString() == "refused" &&
            a.GetProperty("duplicate").GetBoolean());
        Assert.DoesNotContain(replied, r => r.Text is "must not be typed" or "retry after capacity frees");
    }

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
