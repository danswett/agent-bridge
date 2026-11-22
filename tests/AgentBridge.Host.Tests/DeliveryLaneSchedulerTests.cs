using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class DeliveryLaneSchedulerTests
{
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
        public bool FailWrite;
        public Fake(IEnumerable<DeliveryJournalRecord>? reservations = null)
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
            }, n => { Assert.Equal("system", n.From); Notices.Add((n.Item.Id, n.Reason)); }, Clock);
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
