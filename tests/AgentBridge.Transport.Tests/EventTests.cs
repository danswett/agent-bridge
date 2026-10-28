using System.Collections.Concurrent;
using System.Text.Json;
using Xunit;

namespace AgentBridge.Transport.Tests;

// Host-pushed events, {"ev", "seq", "data"}, alongside requests and responses on one channel
// (docs/protocol/v1-sessions.md, "Channel framing").
public class EventTests
{
    // A host and a client with one open channel, and the host's end of it.
    static async Task<(TestRelay Relay, Device Host, SecureChannel AtHost, SecureChannel AtClient)> ChannelAsync(Action<ChannelHub>? configureClient = null)
    {
        var relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(1, "octo");
        var host = await relay.ConnectAsync(token, "host", "host");
        var client = await relay.ConnectAsync(token, "client", "pwa");
        configureClient?.Invoke(client.Hub);
        var accepted = new TaskCompletionSource<SecureChannel>();
        host.Hub.Accepted += ch => accepted.TrySetResult(ch);
        await client.Relay.WaitForPeerAsync(p => p.Id == host.Id, TimeSpan.FromSeconds(10));
        var atClient = await client.Hub.OpenAsync(host.Id);
        var atHost = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return (relay, host, atHost, atClient);
    }

    static async Task<List<ChannelEvent>> ReadAsync(SecureChannel ch, int count)
    {
        var got = new List<ChannelEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (got.Count < count) { got.Add(await ch.Events.ReadAsync(cts.Token)); }
        return got;
    }

    [Fact]
    public async Task Events_arrive_in_the_order_sent_numbered_from_1_on_each_channel()
    {
        var (relay, host, atHost, atClient) = await ChannelAsync();
        await using var _ = relay;
        var second = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "second", "app");
        var accepted = new TaskCompletionSource<SecureChannel>();
        host.Hub.Accepted += ch => accepted.TrySetResult(ch);
        var secondAtClient = await second.Hub.OpenAsync(host.Id);
        var secondAtHost = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var sent = new List<long>();
        for (int i = 0; i < 100; i++) { sent.Add(await atHost.SendEventAsync("session.upsert", new { id = $"s{i}" })); }
        var otherSeq = await secondAtHost.SendEventAsync("session.remove", new { id = "s0" });
        var got = await ReadAsync(atClient, 100);
        var other = await ReadAsync(secondAtClient, 1);

        Assert.Equal(Enumerable.Range(1, 100).Select(i => (long)i), sent);
        Assert.Equal(Enumerable.Range(1, 100).Select(i => (long)i), got.Select(e => e.Seq));
        Assert.Equal(Enumerable.Range(0, 100).Select(i => $"s{i}"), got.Select(e => e.Data.GetProperty("id").GetString()));
        Assert.All(got, e => Assert.Equal("session.upsert", e.Name));
        Assert.Equal(1, otherSeq);
        Assert.Equal(("session.remove", 1L), (other[0].Name, other[0].Seq));
    }

    [Fact]
    public async Task Events_sent_from_many_threads_interleave_with_concurrent_requests_and_every_response_finds_its_request()
    {
        var (relay, _, atHost, atClient) = await ChannelAsync();
        await using var __ = relay;
        const int Events = 400, Requests = 100;

        var senders = Enumerable.Range(0, 8).Select(t => Task.Run(async () =>
        {
            for (int i = 0; i < Events / 8; i++) { await atHost.SendEventAsync("session.upsert", new { t, i }); }
        }));
        var requests = Enumerable.Range(0, Requests).Select(n => Task.Run(async () =>
        {
            var r = await atClient.RequestAsync("echo", new { n }, TimeSpan.FromSeconds(30));
            return (n, echoed: r.GetProperty("result").GetProperty("n").GetInt32());
        })).ToArray();
        var reading = ReadAsync(atClient, Events);
        await Task.WhenAll(senders);
        var answers = await Task.WhenAll(requests);
        var events = await reading;

        Assert.All(answers, a => Assert.Equal(a.n, a.echoed));
        // Seq is taken under the send lock, so the wire order is the seq order, with no gaps
        // or repeats, however the senders raced.
        Assert.Equal(Enumerable.Range(1, Events).Select(i => (long)i), events.Select(e => e.Seq));
        // And each sender's own events stay in its order.
        foreach (var g in events.GroupBy(e => e.Data.GetProperty("t").GetInt32()))
        {
            Assert.Equal(Enumerable.Range(0, Events / 8), g.Select(e => e.Data.GetProperty("i").GetInt32()));
        }
    }

    [Fact]
    public async Task An_event_sent_while_serving_a_request_arrives_before_that_requests_response()
    {
        var (relay, host, _, atClient) = await ChannelAsync();
        await using var __ = relay;
        host.Hub.Handler = async (ch, op, _) =>
        {
            await ch.SendEventAsync("session.upsert", new { id = "a" });
            return new { done = true };
        };

        await atClient.RequestAsync("work");

        // The response is the later frame, and frames are handled in order: the event is
        // already waiting by the time the request completes.
        Assert.True(atClient.Events.TryRead(out var e));
        Assert.Equal(("session.upsert", 1L), (e!.Name, e.Seq));
    }

    [Fact]
    public async Task A_replys_follow_up_events_go_after_its_response_as_the_subscribe_snapshot_needs()
    {
        var (relay, host, _, atClient) = await ChannelAsync();
        await using var __ = relay;
        var responseSeen = new TaskCompletionSource();
        host.Hub.Handler = (ch, op, _) => Task.FromResult<object?>(new ChannelReply(new { revision = 118, count = 3 }, async c =>
        {
            // Holds the snapshot until the client has the response. Were Then run before the
            // response was sent, the response would never arrive and the request would time out.
            await responseSeen.Task;
            foreach (var id in new[] { "a", "b", "c" }) { await c.SendEventAsync("session.upsert", new { id }); }
            await c.SendEventAsync("sessions.synced", new { revision = 118 });
        }));

        var result = await atClient.RequestAsync("sessions.subscribe", timeout: TimeSpan.FromSeconds(10));
        var nothingYet = atClient.Events.TryRead(out _);
        responseSeen.SetResult();
        var events = await ReadAsync(atClient, 4);

        Assert.Equal(3, result.GetProperty("result").GetProperty("count").GetInt32());
        Assert.False(nothingYet);
        Assert.Equal(["session.upsert", "session.upsert", "session.upsert", "sessions.synced"], events.Select(e => e.Name));
        Assert.Equal([1L, 2, 3, 4], events.Select(e => e.Seq));
        Assert.Equal(118, events[3].Data.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task A_client_passes_over_event_names_and_frames_it_does_not_know_and_the_channel_keeps_working()
    {
        var (relay, _, atHost, atClient) = await ChannelAsync();
        await using var __ = relay;
        var known = new HashSet<string> { "session.upsert", "session.remove", "sessions.synced" };

        await atHost.SendEventAsync("session.renamed", new { id = "a", to = "b", future = new[] { 1, 2 } });
        // A frame of a kind this version has never heard of: no re, ev or op.
        await atHost.SendJsonAsync(new { hint = "from a newer host" });
        await atHost.SendJsonAsync(new { ev = "session.upsert", seq = 2, data = new { id = "a" }, extra = "ignored" });
        await atHost.SendJsonAsync(new { ev = "sessions.synced", seq = 3, data = new { revision = 7 } });
        var reply = await atClient.RequestAsync("echo", new { still = "working" });

        var shown = new List<ChannelEvent>();
        var all = await ReadAsync(atClient, 3);
        foreach (var e in all)
        {
            if (!known.Contains(e.Name)) { continue; }
            shown.Add(e);
        }

        Assert.Equal([1L, 2, 3], all.Select(e => e.Seq));
        Assert.Equal(["session.upsert", "sessions.synced"], shown.Select(e => e.Name));
        Assert.Equal("working", reply.GetProperty("result").GetProperty("still").GetString());
        Assert.True(atClient.IsOpen);
    }

    [Fact]
    public async Task A_reader_that_falls_behind_loses_the_oldest_events_never_the_latest_and_sees_the_gap()
    {
        var (relay, _, atHost, atClient) = await ChannelAsync(hub => hub.EventBufferCapacity = 4);
        await using var __ = relay;

        for (int i = 1; i <= 10; i++) { await atHost.SendEventAsync("session.upsert", new { i }); }
        // A response is the barrier: it is handled after every event sent before it.
        await atClient.RequestAsync("ping");
        var kept = new List<ChannelEvent>();
        while (atClient.Events.TryRead(out var e)) { kept.Add(e); }

        Assert.Equal([7L, 8, 9, 10], kept.Select(e => e.Seq));
    }

    [Fact]
    public async Task The_event_stream_ends_when_the_channel_does()
    {
        var (relay, _, atHost, atClient) = await ChannelAsync();
        await using var __ = relay;
        await atHost.SendEventAsync("session.upsert", new { id = "a" });
        var seen = new ConcurrentQueue<ChannelEvent>();
        var reader = Task.Run(async () => { await foreach (var e in atClient.Events.ReadAllAsync()) { seen.Enqueue(e); } });
        await Wait.UntilAsync(() => seen.Count == 1, "the first event");

        await relay.App.StopAsync();

        await reader.WaitAsync(TimeSpan.FromSeconds(10));
        await atClient.Closed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<IOException>(atClient.CloseReason);
        await Assert.ThrowsAsync<IOException>(() => atClient.RequestAsync("ping"));
    }

    [Fact]
    public async Task A_burst_of_events_faster_than_the_relay_allows_is_paced_rather_than_cut_off()
    {
        var relay = await TestRelay.StartAsync(o => o.MaxFramesPerSecond = 50);
        await using var _ = relay;
        var token = relay.TokenFor(1, "octo");
        var host = await relay.ConnectAsync(token, "host", "host");
        host.Relay.MaxFramesPerSecond = 40;
        var client = await relay.ConnectAsync(token, "client", "pwa");
        var accepted = new TaskCompletionSource<SecureChannel>();
        host.Hub.Accepted += ch => accepted.TrySetResult(ch);
        await client.Relay.WaitForPeerAsync(p => p.Id == host.Id, TimeSpan.FromSeconds(10));
        var atClient = await client.Hub.OpenAsync(host.Id);
        var atHost = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // A subscription snapshot for 100 sessions: more than the relay takes in a second.
        for (int i = 1; i <= 100; i++) { await atHost.SendEventAsync("session.upsert", new { i }); }
        var got = await ReadAsync(atClient, 100);

        Assert.Equal(Enumerable.Range(1, 100).Select(i => (long)i), got.Select(e => e.Seq));
        Assert.Null(host.Relay.CloseStatus);
        Assert.True(atHost.IsOpen);
    }

    [Fact]
    public async Task Events_use_the_hubs_serializer_options()
    {
        var (relay, host, atHost, atClient) = await ChannelAsync();
        await using var __ = relay;
        host.Hub.JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        await atHost.SendEventAsync("session.upsert", new Upsert("a", "Working"));
        var e = (await ReadAsync(atClient, 1))[0];

        Assert.Equal("Working", e.Data.GetProperty("statusText").GetString());
    }

    sealed record Upsert(string Id, string StatusText);
}
