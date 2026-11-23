using AgentBridge.Host.Core.AgentMessaging;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Lite;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Host.Tests;

// S5: what the lane tells the sender about an item it dropped or could not confirm. The sender reads it through the
// pull path (Query and Status); nothing is typed into any session, and no message text is ever kept.
public sealed class LaneNoticeSinkTests
{
    static LaneNoticeEvent Event(Guid id, DeliveryNoticeCause cause, string reason = "") =>
        new(new LaneNotice("t", new DeliveryHeader("system", id.ToString("D"), null, DateTimeOffset.UnixEpoch, "normal", 0), "Target t: " + reason, cause), null, false);

    [Theory]
    [InlineData("Overflow", "reason=overflow")]
    [InlineData("RevokedAuthority", "reason=revoked authority")]
    public void A_dropped_item_is_described_as_dropped_with_its_id_and_reason(string cause, string expected)
    {
        using var sink = new LaneNoticeSink(null);
        var id = Guid.NewGuid();
        sink.Accept(Event(id, Enum.Parse<DeliveryNoticeCause>(cause)));
        Assert.Equal($"dropped id={id:D} {expected}", sink.Describe(id));
    }

    [Fact]
    public void An_unconfirmed_handoff_says_it_may_have_been_delivered_and_never_that_it_was_retried()
    {
        using var sink = new LaneNoticeSink(null);
        var id = Guid.NewGuid();
        sink.Accept(Event(id, DeliveryNoticeCause.Unconfirmed));
        var text = sink.Describe(id)!;
        Assert.Contains("may have been delivered", text);
        Assert.Contains("not retried", text);
        Assert.Contains(id.ToString("D"), text);
    }

    [Theory]
    [InlineData("DemotedForOwner")]
    [InlineData("LaneFull")]
    [InlineData("Unknown")]
    public void Other_notices_such_as_a_demotion_or_a_full_lane_are_not_drops_and_are_not_recorded(string cause)
    {
        using var sink = new LaneNoticeSink(null);
        var id = Guid.NewGuid();
        sink.Accept(Event(id, Enum.Parse<DeliveryNoticeCause>(cause)));
        Assert.Null(sink.Describe(id));
        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public void The_same_id_and_cause_is_kept_once()
    {
        using var sink = new LaneNoticeSink(null);
        var id = Guid.NewGuid();
        sink.Accept(Event(id, DeliveryNoticeCause.Overflow));
        sink.Accept(Event(id, DeliveryNoticeCause.Overflow));
        Assert.Equal(1, sink.Count);
    }

    [Fact]
    public void The_record_is_bounded_and_the_oldest_is_forgotten_first()
    {
        using var sink = new LaneNoticeSink(null, capacity: 3);
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids) { sink.Accept(Event(id, DeliveryNoticeCause.Overflow)); }
        Assert.Equal(3, sink.Count);
        Assert.Null(sink.Describe(ids[0]));
        Assert.Null(sink.Describe(ids[1]));
        Assert.NotNull(sink.Describe(ids[4]));
    }

    [Fact]
    public void Neither_the_reason_text_nor_any_message_text_is_ever_kept_or_described()
    {
        using var sink = new LaneNoticeSink(null);
        var id = Guid.NewGuid();
        sink.Accept(Event(id, DeliveryNoticeCause.Overflow, "dropped overflow. SECRET BODY"));
        Assert.DoesNotContain("SECRET", sink.Describe(id));
    }
    sealed class Replier : ISessionReplier
    {
        public bool Available => true;
        public Task<ReplyResult> ReplyAsync(ReplyRequest request, string? peer = null, CancellationToken cancellationToken = default, DeliveryWatch? watch = null) =>
            Task.FromResult(new ReplyResult(AnswerOutcome.Delivered));
    }

    static LaneComposition Lane(FakeTimeProvider clock) => new(
        () => new AgentMessagingSettings { Remote = AgentMessagingSettings.OwnAccount, RemoteSet = true, LaneDelivery = true }, () => true,
        new DeliveryGate(clock), new Replier(),
        new LaneScreens((_, _) => Task.FromResult<(bool? Idle, bool? Empty)>((true, true)), clock),
        "unused", _ => { }, clock, () => new DeliveryJournal(new byte[32], _ => { }, _ => { }));

    [Fact]
    public async Task A_real_revoked_drop_reaches_the_sink_through_the_composition_and_a_disposed_sink_hears_nothing_more()
    {
        var clock = new FakeTimeProvider();
        using var lane = Lane(clock);
        var sink = new LaneNoticeSink(lane);
        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lane.Notices += _ => heard.TrySetResult(); // after the sink's own handler, so the sink has seen it
        var revoked = false;
        var id = Guid.NewGuid();
        Assert.Equal(DeliveryOfferState.Pending, lane.Offer(id, "t", new("user"), "SECRET BODY", () => true, () => revoked).State);
        revoked = true;
        await lane.Resolve().Scheduler!.DeliverNextAsync("t");
        await heard.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal($"dropped id={id:D} reason=revoked authority", sink.Describe(id));

        sink.Dispose();
        var heardAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lane.Notices += _ => heardAgain.TrySetResult();
        var second = Guid.NewGuid(); var revokedAgain = false;
        Assert.Equal(DeliveryOfferState.Pending, lane.Offer(second, "t", new("user"), "body", () => true, () => revokedAgain).State);
        revokedAgain = true;
        await lane.Resolve().Scheduler!.DeliverNextAsync("t");
        await heardAgain.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(sink.Describe(second));
    }

    // S2b-2b: the count behind the "Delivery failures" push. One call per new drop, revoked-authority drop or unconfirmed
    // handoff; never the id, the cause or any text.
    [Fact]
    public void A_listener_is_told_once_per_new_counted_notice_and_not_for_a_demotion_a_full_lane_or_a_repeat()
    {
        using var sink = new LaneNoticeSink(null);
        var told = 0;
        using var listener = sink.Forward(() => told++);
        var first = Guid.NewGuid();
        sink.Accept(Event(first, DeliveryNoticeCause.Overflow));
        sink.Accept(Event(first, DeliveryNoticeCause.Overflow));  // the same id and cause again
        Assert.Equal(1, told);
        sink.Accept(Event(Guid.NewGuid(), DeliveryNoticeCause.RevokedAuthority));
        sink.Accept(Event(Guid.NewGuid(), DeliveryNoticeCause.Unconfirmed));
        Assert.Equal(3, told);
        foreach (var cause in new[] { DeliveryNoticeCause.DemotedForOwner, DeliveryNoticeCause.LaneFull, DeliveryNoticeCause.Unknown })
        {
            sink.Accept(Event(Guid.NewGuid(), cause));
        }
        Assert.Equal(3, told);
    }

    [Fact]
    public void A_notice_the_sink_later_forgets_to_stay_bounded_is_not_counted_again_and_a_new_cause_for_an_id_is_new()
    {
        using var sink = new LaneNoticeSink(null, capacity: 1);
        var told = 0;
        using var listener = sink.Forward(() => told++);
        var id = Guid.NewGuid();
        sink.Accept(Event(id, DeliveryNoticeCause.Overflow));
        sink.Accept(Event(id, DeliveryNoticeCause.Unconfirmed));  // the same message, a different cause: a second failure
        sink.Accept(Event(Guid.NewGuid(), DeliveryNoticeCause.Overflow));  // evicts the oldest, which is no new count
        Assert.Equal(3, told);
    }

    [Fact]
    public void A_disposed_listener_is_told_nothing_and_a_listener_that_throws_never_breaks_the_sink()
    {
        using var sink = new LaneNoticeSink(null);
        var told = 0;
        var listener = sink.Forward(() => told++);
        sink.Accept(Event(Guid.NewGuid(), DeliveryNoticeCause.Overflow));
        listener.Dispose();
        sink.Accept(Event(Guid.NewGuid(), DeliveryNoticeCause.Overflow));
        Assert.Equal(1, told);
        using var thrower = sink.Forward(() => throw new InvalidOperationException("the push is gone"));
        var id = Guid.NewGuid();
        sink.Accept(Event(id, DeliveryNoticeCause.Overflow));  // must not throw into the lane
        Assert.Equal($"dropped id={id:D} reason=overflow", sink.Describe(id));
    }
}
