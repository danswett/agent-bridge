using Microsoft.Extensions.Time.Testing;
using AgentBridge.Host.Lite;
using Xunit;

namespace AgentBridge.Host.Tests;

// The memory-only status store of busy-Claude sends (docs/message-delivery.md "Busy sends to Claude", #3828, #3856).
public sealed class BusyQueueStoreTests
{
    readonly FakeTimeProvider clock = new();
    BusyQueueStore Store() => new(clock);
    static string Req() => Guid.NewGuid().ToString();

    [Fact]
    public void One_relay_message_per_session_holds_the_slot_and_another_session_is_free()
    {
        var s = Store();
        Assert.Equal(BusyQueueStore.Begin.Started, s.TryBegin(Req(), "agent:m/a", "sess1"));
        Assert.True(s.SlotHeld("sess1"));
        Assert.Equal(BusyQueueStore.Begin.Held, s.TryBegin(Req(), "agent:m/b", "sess1"));
        Assert.Equal(BusyQueueStore.Begin.Started, s.TryBegin(Req(), "agent:m/a", "sess2"));
    }

    [Fact]
    public void The_same_request_id_never_begins_twice()
    {
        var s = Store(); var r = Req();
        Assert.Equal(BusyQueueStore.Begin.Started, s.TryBegin(r, "p", "sess1"));
        Assert.Equal(BusyQueueStore.Begin.Held, s.TryBegin(r, "p", "sess2"));
    }

    [Fact]
    public void A_send_that_typed_nothing_frees_the_slot_and_a_delivered_one_frees_it_but_queued_and_unconfirmed_keep_it()
    {
        var s = Store();
        var a = Req(); s.TryBegin(a, "p", "sess1"); s.Release(a);
        Assert.False(s.SlotHeld("sess1")); Assert.Null(s.Get(a));
        var b = Req(); s.TryBegin(b, "p", "sess1"); s.KeysWent(b); s.Settle(b, BusyQueueStore.Delivered, null, held: false);
        Assert.False(s.SlotHeld("sess1")); Assert.Equal(BusyQueueStore.Delivered, s.Get(b)!.State);
        var c = Req(); s.TryBegin(c, "p", "sess1"); s.KeysWent(c); s.Settle(c, BusyQueueStore.Queued, null, held: true);
        Assert.True(s.SlotHeld("sess1"));
        s.Settle(c, BusyQueueStore.Unconfirmed, "watchEnded", held: true);
        Assert.True(s.SlotHeld("sess1")); Assert.True(s.Get(c)!.Held);
        Assert.Equal(BusyQueueStore.Begin.Held, s.TryBegin(Req(), "p", "sess1"));
    }

    [Fact]
    public void A_record_holds_ids_a_state_and_times_and_queued_sets_until_to_the_watch_after_it()
    {
        var s = Store(); var r = Req();
        s.TryBegin(r, "agent:m/a", "sess1"); s.KeysWent(r);
        var started = s.Get(r)!;
        Assert.Equal((r, "agent:m/a", BusyQueueStore.InFlight, (string?)null, clock.GetUtcNow(), (DateTimeOffset?)null, true), (started.Request, started.Principal, started.State, started.Reason, started.At, started.Until, started.Held));
        clock.Advance(TimeSpan.FromSeconds(7));
        s.Settle(r, BusyQueueStore.Queued, null, held: true);
        Assert.Equal(clock.GetUtcNow() + BusyQueueStore.Watch, s.Get(r)!.Until);
    }

    [Fact]
    public void A_record_that_is_not_held_goes_after_the_150_second_window_and_a_held_one_stays()
    {
        var s = Store();
        var done = Req(); s.TryBegin(done, "p", "sess1"); s.KeysWent(done); s.Settle(done, BusyQueueStore.Delivered, null, held: false);
        var held = Req(); s.TryBegin(held, "p", "sess2"); s.KeysWent(held); s.Settle(held, BusyQueueStore.Unconfirmed, "noRow", held: true);
        clock.Advance(BusyQueueStore.Window + TimeSpan.FromSeconds(1));
        s.TryBegin(Req(), "p", "sess3"); // any use prunes
        Assert.Null(s.Get(done));
        Assert.NotNull(s.Get(held));
        Assert.True(s.SlotHeld("sess2"));
    }

    [Fact]
    public void A_full_store_refuses_a_new_send_and_a_freed_record_makes_room()
    {
        var s = Store(); string? last = null;
        for (var i = 0; i < BusyQueueStore.MaxRecords; i++) { last = Req(); Assert.Equal(BusyQueueStore.Begin.Started, s.TryBegin(last, "p", "sess" + i)); }
        Assert.Equal(BusyQueueStore.Begin.Full, s.TryBegin(Req(), "p", "another"));
        s.Release(last!);
        Assert.Equal(BusyQueueStore.Begin.Started, s.TryBegin(Req(), "p", "another"));
    }

    [Fact]
    public void The_holder_of_a_slot_is_known_with_its_transcript_and_offset_until_the_slot_frees()
    {
        var s = Store(); var r = Req();
        Assert.Null(s.Holder("sess1"));
        s.TryBegin(r, "p", "sess1"); s.Track(r, "/t/a.jsonl", 42);
        Assert.Equal(new BusyQueueStore.Holding(r, "/t/a.jsonl", 42, "p"), s.Holder("sess1"));
        Assert.Null(s.Holder("sess2"));
        s.KeysWent(r); s.Settle(r, BusyQueueStore.Queued, null, held: true);
        Assert.Equal(r, s.Holder("sess1")!.Request);
        s.Settle(r, BusyQueueStore.Delivered, null, held: false);
        Assert.Null(s.Holder("sess1"));
        var q = Req(); s.TryBegin(q, "p", "sess1"); s.Track(q, null, 0); s.Release(q);
        Assert.Null(s.Holder("sess1"));
    }

    // #3926 review P2: the store is keyed by peer + request, as the receiver's replay store is, so one peer reusing another's live request id is
    // neither refused for it nor able to touch its record. A lookup with no peer acts only when exactly one record has that request id.
    [Fact]
    public void Two_peers_may_use_the_same_request_id_and_each_record_is_its_own()
    {
        var s = Store(); var r = Req();
        Assert.Equal(BusyQueueStore.Begin.Started, s.TryBegin(r, "agent:m/a", "sess1"));
        Assert.Equal(BusyQueueStore.Begin.Started, s.TryBegin(r, "agent:m/b", "sess2"));
        Assert.Equal(BusyQueueStore.Begin.Held, s.TryBegin(r, "agent:m/a", "sess3"));   // the same peer and request never begins twice
        s.KeysWent(r, "agent:m/a"); s.Settle(r, BusyQueueStore.Delivered, null, held: false, "agent:m/a");
        Assert.Equal(BusyQueueStore.Delivered, s.Get(r, "agent:m/a")!.State);
        Assert.False(s.SlotHeld("sess1"));
        Assert.Equal(BusyQueueStore.InFlight, s.Get(r, "agent:m/b")!.State);
        Assert.True(s.SlotHeld("sess2"));
        s.Release(r, "agent:m/b");
        Assert.False(s.SlotHeld("sess2")); Assert.Null(s.Get(r, "agent:m/b")); Assert.NotNull(s.Get(r, "agent:m/a"));
        Assert.Single(s.Records());
    }

    [Fact]
    public void A_lookup_with_no_peer_acts_only_when_one_record_has_that_request_id()
    {
        var s = Store(); var r = Req();
        s.TryBegin(r, "agent:m/a", "sess1");
        Assert.Equal("agent:m/a", s.Get(r)!.Principal);
        s.TryBegin(r, "agent:m/b", "sess2");
        Assert.Null(s.Get(r));
        s.Settle(r, BusyQueueStore.Delivered, null, held: false);   // ambiguous: touches neither record
        Assert.Equal((BusyQueueStore.InFlight, BusyQueueStore.InFlight), (s.Get(r, "agent:m/a")!.State, s.Get(r, "agent:m/b")!.State));
        Assert.True(s.SlotHeld("sess1") && s.SlotHeld("sess2"));
    }
}
