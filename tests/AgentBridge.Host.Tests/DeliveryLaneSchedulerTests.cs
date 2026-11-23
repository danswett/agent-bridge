using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class DeliveryLaneSchedulerTests
{
    [Fact]
    public async Task Offer_notices_run_outside_the_outer_scheduler_lock()
    {
        using var f = new Fake(dispatch: true);
        for (var i = 0; i < 50; i++) { f.Add($"fyi-{i}", priority: "fyi"); }
        var request = Guid.NewGuid();
        var sender = new DeliverySender("a");
        Task<DeliveryOfferStatus>? query = null;
        f.Noticing = () =>
        {
            query = Task.Run(() => f.Scheduler.Query(request, "target", sender));
            Assert.True(query.Wait(TimeSpan.FromSeconds(5)), "Query was blocked by the emitting Offer.");
        };
        Assert.Equal(DeliveryOfferState.Pending, f.Scheduler.Offer(request, "target", sender, "body", () => true).State);
        Assert.Equal(DeliveryOfferState.Pending, (await (query ?? throw new InvalidOperationException("Missing notice callback."))).State);
    }

    sealed class Fake : IDisposable
    {
        public readonly FakeTimeProvider Clock = new();
        public readonly List<byte[]> Disk = [];
        public readonly List<(string Id, string Reason)> Notices = [];
        public readonly List<DeliveryHeader> Sent = [];
        public readonly DeliveryGate Gate;
        public readonly DeliveryJournal Journal;
        public readonly DeliveryLaneScheduler Scheduler;
        public (bool? Idle, bool? Empty) Screen = (true, true);
        public Func<Task> Sending = () => Task.CompletedTask;
        public Action? Noticing;
        public bool FailWrite;
        public bool Active = true;
        public DeliverySender? LastSender;
        public bool LastRequireIdle;
        public ReplyResult DispatchResult = new(AnswerOutcome.Delivered);
        public Fake(IEnumerable<DeliveryJournalRecord>? reservations = null, bool dispatch = false)
        {
            Gate = new(Clock);
            Journal = new(new byte[32], b =>
            {
                if (FailWrite) { throw new IOException("full"); }
                Disk.Add(b);
            }, _ => { }, reservations);
            Scheduler = new(Journal, Gate, _ => Screen, async (_, header, _) =>
            {
                Assert.Equal(DeliveryState.Handoff, Journal.State("target", header.Id) ??
                    Journal.State("other", header.Id));
                await Sending();
                Sent.Add(header);
            }, n => { Assert.Equal("system", n.From); Notices.Add((n.Item.Id, n.Reason)); Noticing?.Invoke(); }, Clock)
            {
                Dispatch = dispatch ? async (request, cancellation) =>
                {
                    var attempt = Guid.Parse(request.Request);
                    Assert.True(Gate.TryClaim(request.Id, attempt));
                    await Gate.WaitToTypeAsync(cancellation);
                    try
                    {
                        if (request.StillCurrent?.Invoke() != true) { return new(AnswerOutcome.Refused); }
                        LastSender = request.Sender; LastRequireIdle = request.RequireIdle;
                        var frame = request.HandoffText!();
                        Sent.Add(Assert.Single(DeliveryEnvelope.Parse(request.Id, frame, Journal.VerifyHandoff)).Header!);
                        await Sending();
                        return DispatchResult;
                    }
                    finally { Gate.DoneTyping(); Gate.Release(request.Id, attempt); }
                } : null,
            };
        }
        public bool Add(string id, DeliveryClass sender = DeliveryClass.Agent, string priority = "normal",
            string session = "target") => Scheduler.Enqueue(session,
                new(sender == DeliveryClass.System ? "system" : sender == DeliveryClass.Owner ? "owner:user" : "agent:user",
                    id, null, Clock.GetUtcNow(), priority, 0), "body " + id, sender);
        public Task<LaneResult> Next(string session = "target") => Scheduler.DeliverNextAsync(session);
        public DeliveryJournalRecord[] Records => Disk.Select(b =>
            JsonSerializer.Deserialize<DeliveryJournalRecord>(b, ProtocolJson.Options)!).ToArray();
        public void Dispose() => Journal.Dispose();
    }

    [Fact]
    public void Overflow_notice_cannot_fill_space_before_the_offered_item_is_inserted()
    {
        using var f = new Fake();
        for (var i = 0; i < 50; i++) { f.Add("f" + i, priority: "fyi"); }
        f.Noticing = () => { f.Noticing = null; Assert.True(f.Add("reentrant")); };
        Assert.True(f.Add("normal"));
        Assert.Equal(2, f.Records.Count(r => r.State == "dropped"));
    }

    [Fact]
    public async Task Pending_offer_owns_no_reply_slot_and_dedupes_bound_sender_target_body()
    {
        using var f = new Fake(dispatch: true) { Screen = (false, true) };
        var id = Guid.NewGuid(); var sender = new DeliverySender("user", "Friendly");
        Assert.Equal(DeliveryOfferState.Pending, f.Scheduler.Offer(id, "target", sender, "e\u00AD\u0301", () => f.Active).State);
        Assert.Equal(LaneResult.Waiting, await f.Next());
        Assert.Empty(f.Sent);
        Assert.Equal(DeliveryOfferState.Refused, f.Scheduler.Offer(id, "target", sender, "changed", () => true).State);
        Assert.Equal(DeliveryOfferState.Refused, f.Scheduler.Query(id, "other", sender).State);
        Assert.Equal(DeliveryOfferState.Refused, f.Scheduler.Query(id, "target", new("other")).State);
        f.Screen = (true, true);
        await f.Next().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(sender, f.LastSender); Assert.True(f.LastRequireIdle);
        Assert.Equal(1, Assert.Single(f.Sent).N);
        Assert.Equal("q1", f.Sent[0].Seq);
        Assert.Equal(DeliveryOfferState.Delivered, f.Scheduler.Query(id, "target", sender).State);
        f.Active = false;
        Assert.Equal(DeliveryOfferState.Delivered, f.Scheduler.Offer(id, "target", sender, "e\u00AD\u0301", () => f.Active).State);
        await f.Next(); Assert.Single(f.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_handoff_journal_failure_never_reports_delivery_or_admits_more_work(bool confirmed)
    {
        using var f = new Fake(dispatch: true) { DispatchResult = new(confirmed ? AnswerOutcome.Delivered : AnswerOutcome.Unconfirmed) };
        var id = Guid.NewGuid(); var sender = new DeliverySender("user");
        f.Scheduler.Offer(id, "target", sender, "body", () => true);
        f.Sending = () => { f.FailWrite = true; return Task.CompletedTask; };
        Assert.Equal(LaneResult.Unconfirmed, await f.Next());
        Assert.Equal(DeliveryOfferState.Unconfirmed, f.Scheduler.Offer(id, "target", sender, "body", () => true).State);
        Assert.Throws<InvalidOperationException>(() => f.Scheduler.Offer(Guid.NewGuid(), "target", sender, "more", () => true));
    }

    [Fact]
    public async Task Owner_overtakes_a_pending_offer_before_ReplyService_gets_the_shared_slot()
    {
        using var f = new Fake(dispatch: true);
        var agent = Guid.NewGuid(); var owner = Guid.NewGuid();
        f.Scheduler.Offer(agent, "target", new("agent"), "body", () => true);
        await f.Gate.WaitToTypeAsync();
        var waiting = f.Next();
        f.Scheduler.Offer(owner, "target", new("user", Owner: true), "owner", () => true);
        f.Gate.DoneTyping();
        Assert.Equal(LaneResult.Waiting, await waiting);
        f.Screen = (false, true); await f.Next(); Assert.False(f.LastRequireIdle);
        f.Screen = (true, true); await f.Next();
        Assert.Equal(new[] { owner.ToString(), agent.ToString() }, f.Sent.Select(h => h.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_and_same_id_never_resubmit_handoff_or_promote_Typed_to_delivered(bool handoff)
    {
        using var f = new Fake(dispatch: true) { DispatchResult = new(AnswerOutcome.Unconfirmed) { Stage = ReplyStage.Typed } };
        var id = Guid.NewGuid(); var sender = new DeliverySender("user");
        f.Scheduler.Offer(id, "target", sender, "body", () => true);
        if (handoff) { await f.Next(); Assert.Equal(DeliveryOfferState.Unconfirmed, f.Scheduler.Query(id, "target", sender).State); }
        using var restarted = new Fake(f.Records, dispatch: true);
        var state = handoff ? DeliveryOfferState.Unconfirmed : DeliveryOfferState.Dropped;
        Assert.Equal(state, restarted.Scheduler.Offer(id, "target", sender, "body", () => true).State);
        await restarted.Next(); Assert.Empty(restarted.Sent);
    }

    [Fact]
    public async Task Revoked_authority_and_failed_handoff_flush_send_zero_keys()
    {
        using var f = new Fake(dispatch: true);
        var id = Guid.NewGuid(); var sender = new DeliverySender("user");
        f.Scheduler.Offer(id, "target", sender, "body", () => f.Active);
        f.Active = false;
        Assert.Equal(DeliveryOfferState.Pending, f.Scheduler.Offer(id, "target", sender, "body", () => true).State);
        await f.Next(); Assert.Empty(f.Sent);
        f.Active = true; f.FailWrite = true;
        await Assert.ThrowsAsync<IOException>(() => f.Next());
        Assert.Empty(f.Sent);
        Assert.DoesNotContain(f.Records, r => r.State == "handoff");
    }

    [Fact]
    public async Task Explicit_revocation_seen_in_the_final_fence_cannot_renew_and_a_new_offer_proceeds()
    {
        using var f = new Fake(dispatch: true);
        var old = Guid.NewGuid(); var next = Guid.NewGuid(); var sender = new DeliverySender("user");
        var revoked = false;
        f.Scheduler.Offer(old, "target", sender, "old", () => true, () => revoked);
        await f.Gate.WaitToTypeAsync();
        var waiting = f.Next();
        revoked = true; f.Gate.DoneTyping();
        Assert.Equal(LaneResult.Waiting, await waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        revoked = false;
        Assert.Equal(LaneResult.Waiting, await f.Next());
        Assert.Equal(DeliveryOfferState.Dropped, f.Scheduler.Offer(old, "target", sender, "old", () => true).State);
        Assert.Contains(f.Notices, notice => notice.Id == old.ToString() && notice.Reason.Contains("revoked authority"));
        f.Scheduler.Offer(next, "target", sender, "new", () => true);
        Assert.Equal(LaneResult.HandedOff, await f.Next());
        Assert.Equal(next.ToString(), Assert.Single(f.Sent).Id);
    }

    [Fact]
    public async Task A_failed_revocation_flush_preserves_pending_evidence_and_freezes_without_a_drop_notice()
    {
        using var f = new Fake(dispatch: true);
        var id = Guid.NewGuid(); var sender = new DeliverySender("user"); var revoked = false;
        f.Scheduler.Offer(id, "target", sender, "body", () => true, () => revoked);
        revoked = true; f.FailWrite = true;
        await Assert.ThrowsAsync<IOException>(() => f.Next());
        Assert.Equal(DeliveryOfferState.Pending, f.Scheduler.Query(id, "target", sender).State);
        Assert.Empty(f.Sent); Assert.Empty(f.Notices);
        Assert.DoesNotContain(f.Records, record => record.State == "dropped");
        f.FailWrite = false;
        Assert.Equal(LaneResult.Waiting, await f.Next());
        Assert.Throws<InvalidOperationException>(() => f.Scheduler.Offer(Guid.NewGuid(), "target", sender, "new", () => true));
    }

    [Fact]
    public async Task Owner_is_first_in_same_second_burst_and_lanes_have_no_false_gap()
    {
        using var f = new Fake();
        f.Add("a"); f.Clock.Advance(TimeSpan.FromMilliseconds(200));
        f.Add("b", priority: "urgent"); f.Clock.Advance(TimeSpan.FromMilliseconds(200));
        f.Add("owner", DeliveryClass.Owner);
        for (var i = 0; i < 3; i++) { Assert.Equal(LaneResult.HandedOff, await f.Next()); }
        Assert.Equal(new[] { "owner", "a", "b" }, f.Sent.Select(h => h.Id));
        Assert.Equal(new[] { "u1", "q1", "q2" }, f.Sent.Select(h => h.Seq));
        Assert.Equal(new[] { "urgent", "normal", "normal" }, f.Sent.Select(h => h.Priority));
        Assert.DoesNotContain(f.Notices, n => n.Reason.Contains("gap", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fyi_flood_drops_oldest_but_never_delays_owner()
    {
        using var f = new Fake();
        for (var i = 0; i < 200; i++) { Assert.True(f.Add("f" + i, priority: "fyi")); }
        f.Add("owner", DeliveryClass.Owner);
        await f.Next(); await f.Next();
        Assert.Equal(new[] { "owner", "f150" }, f.Sent.Select(h => h.Id));
        Assert.Equal(150, f.Records.Count(r => r.State == "dropped"));
        Assert.Equal(150, f.Notices.Count);
        Assert.All(f.Records.Where(r => r.State == "dropped"), r => Assert.Equal("overflow", r.Reason));
    }

    [Fact]
    public async Task Urgent_bound_demotes_newest_system_and_refuses_eleventh_owner()
    {
        using var f = new Fake();
        f.Add("s0", DeliveryClass.System); f.Add("s1", DeliveryClass.System);
        for (var i = 0; i < 8; i++) { f.Add("o" + i, DeliveryClass.Owner); }
        Assert.False(f.Add("system-full", DeliveryClass.System));
        Assert.True(f.Add("o8", DeliveryClass.Owner));
        Assert.Equal("demoted", f.Journal.State("target", "s1")?.ToString().ToLowerInvariant());
        Assert.Contains(f.Notices, n => n.Id == "s1" && n.Reason.Contains("target", StringComparison.Ordinal));
        Assert.True(f.Add("o9", DeliveryClass.Owner));
        Assert.False(f.Add("o10", DeliveryClass.Owner));
        Assert.Contains(f.Notices, n => n.Id == "o10" && n.Reason.Contains("target", StringComparison.Ordinal));
        for (var i = 0; i < 12; i++) { await f.Next(); }
        Assert.Equal(new[] { "o0", "o1", "o2", "o3", "o4", "o5", "o6", "o7", "o8", "o9", "s1", "s0" },
            f.Sent.Select(h => h.Id));
    }

    [Fact]
    public async Task Full_normal_queue_refuses_new_normal_naming_target()
    {
        using var f = new Fake();
        for (var i = 0; i < 50; i++) { Assert.True(f.Add("n" + i)); }
        Assert.False(f.Add("new"));
        Assert.False(f.Add("new-fyi", priority: "fyi"));
        Assert.Contains(f.Notices, n => n.Id == "new" && n.Reason.Contains("target", StringComparison.Ordinal));
        Assert.Equal("new-fyi", Assert.Single(f.Records, r => r.State == "dropped").Header.Id);
        await f.Next(); Assert.Equal("n0", Assert.Single(f.Sent).Id);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(false, true)]
    [InlineData(true, null)]
    [InlineData(true, false)]
    public async Task Queueable_waits_without_reservation_when_box_or_idle_is_unsafe(bool? idle, bool? empty)
    {
        using var f = new Fake { Screen = (idle, empty) };
        f.Add("a");
        Assert.Equal(LaneResult.Waiting, await f.Next());
        Assert.Empty(f.Sent);
        Assert.DoesNotContain(f.Records, r => r.State == "handoff");
    }

    [Fact]
    public async Task Flushed_handoff_survives_kill_before_keys_and_resumes_each_session_lane()
    {
        using var f = new Fake();
        f.Add("old"); f.Sending = () => throw new IOException("killed");
        await Assert.ThrowsAsync<IOException>(() => f.Next());
        Assert.Empty(f.Sent);
        Assert.Contains(f.Records, r => r.State == "handoff" && r.Header.Seq == "q1");
        using var restarted = new Fake(f.Records);
        restarted.Add("new"); restarted.Add("owner", DeliveryClass.Owner);
        restarted.Add("other", session: "other");
        await restarted.Next(); await restarted.Next(); await restarted.Next("other");
        Assert.Equal(new[] { "u1", "q2", "q1" }, restarted.Sent.Select(h => h.Seq));
    }

    [Fact]
    public async Task Shared_delivery_gate_serializes_and_owner_overtakes_waiting_queue()
    {
        using var f = new Fake();
        f.Add("a");
        await f.Gate.WaitToTypeAsync();
        var waiting = f.Next();
        f.Add("owner", DeliveryClass.Owner);
        Assert.Equal(LaneResult.Waiting, await f.Next());
        Assert.Empty(f.Sent);
        f.Gate.DoneTyping();
        Assert.Equal(LaneResult.HandedOff, await waiting);
        Assert.Equal("owner", Assert.Single(f.Sent).Id);
    }

    [Fact]
    public async Task Failed_handoff_flush_sends_no_keys()
    {
        using var f = new Fake();
        f.Add("a"); f.FailWrite = true;
        await Assert.ThrowsAsync<IOException>(() => f.Next());
        Assert.Empty(f.Sent);
    }

    [Fact]
    public async Task Owner_enters_full_urgent_and_normal_lanes_by_dropping_displaced_system()
    {
        using var f = new Fake();
        for (var i = 0; i < 50; i++) { f.Add("n" + i); }
        for (var i = 0; i < 10; i++) { f.Add("s" + i, DeliveryClass.System); }
        f.Noticing = () => throw new IOException("notice failed");
        Assert.Throws<IOException>(() => f.Add("owner", DeliveryClass.Owner));
        f.Noticing = null;
        Assert.True(f.Add("owner", DeliveryClass.Owner));
        var dropped = Assert.Single(f.Records, r => r.State == "dropped");
        Assert.Equal("s9", dropped.Header.Id); Assert.Equal("overflow", dropped.Reason);
        Assert.Contains(f.Notices, n => n.Id == "s9" && n.Reason.Contains("target", StringComparison.Ordinal));
        f.Screen = (false, true);
        Assert.Equal(LaneResult.HandedOff, await f.Next());
        Assert.Equal("owner", Assert.Single(f.Sent).Id);
        f.Screen = (true, true);
        for (var i = 0; i < 10; i++) { await f.Next(); }
        Assert.Equal("n0", f.Sent[^1].Id);
        Assert.DoesNotContain(f.Sent, h => h.Id == "s9");
    }

    [Fact]
    public async Task Urgent_waits_for_unknown_idle_or_occupied_box_and_post_key_failure_never_retries()
    {
        using var f = new Fake();
        f.Add("owner", DeliveryClass.Owner);
        f.Screen = (null, true); Assert.Equal(LaneResult.Waiting, await f.Next());
        f.Screen = (true, false); Assert.Equal(LaneResult.Waiting, await f.Next());
        Assert.Empty(f.Sent);
        f.Screen = (true, true); f.Sending = () => { f.FailWrite = true; return Task.CompletedTask; };
        Assert.Equal(LaneResult.Unconfirmed, await f.Next());
        Assert.Equal(LaneResult.Waiting, await f.Next());
        Assert.Single(f.Sent);
        Assert.Contains(f.Notices, n => n.Reason.Contains("unconfirmed", StringComparison.Ordinal));
    }
}
