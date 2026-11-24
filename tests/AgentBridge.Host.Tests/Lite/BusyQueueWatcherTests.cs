using Microsoft.Extensions.Time.Testing;
using AgentBridge.Host.Lite;
using Xunit;

namespace AgentBridge.Host.Tests;

// The active watch of a held busy-Claude send (docs/message-delivery.md "Busy sends to Claude", #3856): up to the 120 s watch it looks for the
// prompt Claude takes; at the end it reports watchEnded and stops, and the item stays held.
public sealed class BusyQueueWatcherTests
{
    readonly FakeTimeProvider clock = new();
    static string Req() => Guid.NewGuid().ToString();

    (BusyQueueStore Store, string Request) Held(string state = BusyQueueStore.Queued)
    {
        var s = new BusyQueueStore(clock); var r = Req();
        s.TryBegin(r, "agent:m/a", "sess1"); s.Track(r, "/t/a.jsonl", 7); s.KeysWent(r);
        s.Settle(r, state, state == BusyQueueStore.Unconfirmed ? "noRow" : null, held: true);
        return (s, r);
    }

    // Moves the fake clock one poll at a time until the watch ends or the time is spent.
    async Task Drive(Task watch, TimeSpan total)
    {
        for (var spent = TimeSpan.Zero; spent <= total && !watch.IsCompleted; spent += BusyQueueWatcher.Poll)
        {
            clock.Advance(BusyQueueWatcher.Poll);
            await Task.WhenAny(watch, Task.Delay(5));
        }
        await watch.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_queued_send_still_untaken_at_the_end_of_the_120_s_watch_is_reported_watchEnded_and_stays_held()
    {
        var (s, r) = Held();
        var asked = new List<(string Path, long From, string Request)>();
        var w = new BusyQueueWatcher(s, clock) { Taken = (p, f, q) => { asked.Add((p, f, q)); return false; } };
        await Drive(w.WatchAsync(r), TimeSpan.FromSeconds(130));
        var rec = s.Get(r)!;
        Assert.Equal((BusyQueueStore.Unconfirmed, "watchEnded", true), (rec.State, rec.Reason, rec.Held));
        Assert.True(s.SlotHeld("sess1"));
        Assert.All(asked, a => Assert.Equal(("/t/a.jsonl", 7L, r), (a.Path, a.From, a.Request)));
        Assert.NotEmpty(asked);
    }

    [Fact]
    public async Task A_prompt_taken_during_the_watch_settles_delivered_frees_the_slot_and_ends_the_watch_early()
    {
        var (s, r) = Held();
        var taken = false;
        var w = new BusyQueueWatcher(s, clock) { Taken = (_, _, _) => taken };
        var watch = w.WatchAsync(r);
        for (var i = 0; i < 5; i++) { clock.Advance(BusyQueueWatcher.Poll); await Task.WhenAny(watch, Task.Delay(5)); }
        Assert.False(watch.IsCompleted);
        taken = true;
        await Drive(watch, TimeSpan.FromSeconds(10));
        var rec = s.Get(r)!;
        Assert.Equal((BusyQueueStore.Delivered, false), (rec.State, rec.Held));
        Assert.False(s.SlotHeld("sess1"));
        Assert.True(clock.GetUtcNow() - rec.At < BusyQueueStore.Watch);   // ended long before the 120 s
    }

    [Fact]
    public async Task An_unconfirmed_noRow_send_keeps_its_reason_at_the_end_and_is_still_delivered_if_taken()
    {
        var (s, r) = Held(BusyQueueStore.Unconfirmed);
        var w = new BusyQueueWatcher(s, clock) { Taken = (_, _, _) => false };
        await Drive(w.WatchAsync(r), TimeSpan.FromSeconds(130));
        Assert.Equal((BusyQueueStore.Unconfirmed, "noRow", true), (s.Get(r)!.State, s.Get(r)!.Reason, s.Get(r)!.Held));
        var (s2, r2) = Held(BusyQueueStore.Unconfirmed);
        await Drive(new BusyQueueWatcher(s2, clock) { Taken = (_, _, _) => true }.WatchAsync(r2), TimeSpan.FromSeconds(10));
        Assert.Equal((BusyQueueStore.Delivered, false), (s2.Get(r2)!.State, s2.Get(r2)!.Held));
    }

    [Fact]
    public async Task A_record_settled_elsewhere_or_gone_ends_the_watch_without_touching_it()
    {
        var (s, r) = Held();
        var watch = new BusyQueueWatcher(s, clock) { Taken = (_, _, _) => false }.WatchAsync(r);
        clock.Advance(BusyQueueWatcher.Poll); await Task.WhenAny(watch, Task.Delay(5));
        s.Settle(r, BusyQueueStore.Delivered, null, held: false);     // the lazy check at admission got there first
        await Drive(watch, TimeSpan.FromSeconds(10));
        Assert.Equal((BusyQueueStore.Delivered, (string?)null), (s.Get(r)!.State, s.Get(r)!.Reason));
        await new BusyQueueWatcher(s, clock).WatchAsync(Req());      // an unknown request: nothing to watch
    }

    [Fact]
    public async Task An_unknown_transcript_and_a_failing_reader_never_stop_the_watch_from_reporting()
    {
        var s = new BusyQueueStore(clock); var r = Req();
        s.TryBegin(r, "p", "sess1"); s.Track(r, null, 0); s.KeysWent(r); s.Settle(r, BusyQueueStore.Queued, null, held: true);
        await Drive(new BusyQueueWatcher(s, clock) { Taken = (_, _, _) => throw new IOException("nope") }.WatchAsync(r), TimeSpan.FromSeconds(130));
        Assert.Equal("watchEnded", s.Get(r)!.Reason);
        var (s2, r2) = Held();
        await Drive(new BusyQueueWatcher(s2, clock) { Taken = (_, _, _) => throw new IOException("nope") }.WatchAsync(r2), TimeSpan.FromSeconds(130));
        Assert.Equal((BusyQueueStore.Unconfirmed, "watchEnded"), (s2.Get(r2)!.State, s2.Get(r2)!.Reason));
    }

    // #3926 review P2: a watch carries the peer of its send, so two peers that hold the same request id never touch each other's record.
    [Fact]
    public async Task A_watch_with_the_peer_settles_that_peers_record_even_when_another_peer_holds_the_same_request_id()
    {
        var s = new BusyQueueStore(clock); var r = Req();
        s.TryBegin(r, "agent:m/a", "sess1"); s.Track(r, "/t/a.jsonl", 7, principal: "agent:m/a"); s.KeysWent(r, "agent:m/a");
        s.Settle(r, BusyQueueStore.Queued, null, held: true, "agent:m/a");
        s.TryBegin(r, "agent:m/b", "sess2");
        var w = new BusyQueueWatcher(s, clock) { Taken = (_, _, _) => true };
        await Drive(w.WatchAsync(r, principal: "agent:m/a"), TimeSpan.FromSeconds(10));
        Assert.Equal(BusyQueueStore.Delivered, s.Get(r, "agent:m/a")!.State);
        Assert.Equal(BusyQueueStore.InFlight, s.Get(r, "agent:m/b")!.State);
        Assert.False(s.SlotHeld("sess1")); Assert.True(s.SlotHeld("sess2"));
    }

    // #3968 review (lane 2): the watch is the literal 120 s of the design, not "about two minutes" and not a constant that can drift.
    [Fact]
    public async Task The_watch_is_exactly_120_s_still_held_and_untouched_just_before_and_reported_watchEnded_at_the_end()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), BusyQueueStore.Watch);
        var (s, r) = Held();
        var w = new BusyQueueWatcher(s, clock) { Taken = (_, _, _) => false };
        var watch = w.WatchAsync(r);
        for (var spent = TimeSpan.Zero; spent < TimeSpan.FromSeconds(118); spent += BusyQueueWatcher.Poll) { clock.Advance(BusyQueueWatcher.Poll); await Task.WhenAny(watch, Task.Delay(5)); }
        Assert.False(watch.IsCompleted);
        Assert.Equal((BusyQueueStore.Queued, (string?)null), (s.Get(r)!.State, s.Get(r)!.Reason));
        clock.Advance(BusyQueueWatcher.Poll);               // 120 s from the Queued report
        await watch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((BusyQueueStore.Unconfirmed, "watchEnded", true), (s.Get(r)!.State, s.Get(r)!.Reason, s.Get(r)!.Held));
    }

    // #4129 (P2-1): the end-of-watch report is not atomic with the watcher's read. A send settled delivered between the two must stay delivered and
    // free: moving it back to queued or held with no slot would end watchEnded and leak the record for good.
    [Fact]
    public async Task A_send_delivered_elsewhere_while_the_watch_ends_stays_delivered_free_and_slotless()
    {
        var (s, r) = Held();
        clock.Advance(BusyQueueStore.Watch + TimeSpan.FromSeconds(1));              // the watch is already over when it looks
        var w = new BusyQueueWatcher(s, clock) { Taken = (_, _, _) => { s.Settle(r, BusyQueueStore.Delivered, null, held: false); return false; } };
        await w.WatchAsync(r).WaitAsync(TimeSpan.FromSeconds(5));
        var rec = s.Get(r)!;
        Assert.Equal((BusyQueueStore.Delivered, false), (rec.State, rec.Held));
        Assert.False(s.SlotHeld("sess1"));
        Assert.DoesNotContain(s.Records(), x => x.Held && !s.SlotHeld("sess1"));
    }

    [Fact]
    public async Task A_send_released_elsewhere_while_the_watch_ends_is_not_brought_back()
    {
        var (s, r) = Held();
        clock.Advance(BusyQueueStore.Watch + TimeSpan.FromSeconds(1));
        var w = new BusyQueueWatcher(s, clock) { Taken = (_, _, _) => { s.Release(r); return false; } };
        await w.WatchAsync(r).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(s.Get(r));
        Assert.False(s.SlotHeld("sess1"));
    }}
