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
}
