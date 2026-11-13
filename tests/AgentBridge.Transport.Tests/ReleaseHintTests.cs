using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AgentBridge.Relay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Transport.Tests;

[Trait("Category", "Network")]
public class ReleaseHintTests
{
    [Fact]
    public async Task Changed_tags_reach_each_capable_host_once_and_a_later_host_gets_the_latest_once()
    {
        using var feed = new Feed();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var relay = await TestRelay.StartAsync(releaseFeed: feed, releaseTime: clock);
        using var a = await Peer.Connect(relay, "true");
        using var b = await Peer.Connect(relay, "true", account: 2);
        using var old = await Peer.Connect(relay, null);
        using var no = await Peer.Connect(relay, "false");
        using var duplicate = await Peer.Connect(relay, "true,\"releaseHint\":true");
        using var text = await Peer.Connect(relay, "\"true\"");
        using var app = await Peer.Connect(relay, "true", kind: "app");
        await Wait.UntilAsync(() => feed.Requests.Count == 1, "initial release-list request");
        feed.Body = List(("v0.1.12", "win"), ("v0.1.9", "win"), ("v0.1.11", "win-arm64"));
        clock.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => a.Hints.Count == 2 && b.Hints.Count == 2, "one hint per channel on each capable host");
        using var late = await Peer.Connect(relay, "true");
        await Wait.UntilAsync(() => late.Hints.Count == 2, "latest hints on connect");
        clock.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => feed.Requests.Count == 3, "unchanged-list poll");
        using var barrier = await Peer.Connect(relay, "true");
        await Wait.UntilAsync(() => barrier.Hints.Count == 2, "unchanged-list delivery barrier");
        foreach (var peer in new[] { a, b, late })
        {
            Assert.Equal(new[] { "v0.1.11", "v0.1.12" }, peer.Hints.Select(Tag).Order().ToArray());
            Assert.All(peer.Hints, frame =>
            {
                Assert.True(frame.Length < 256);
                using var doc = JsonDocument.Parse(frame);
                Assert.Equal(4, doc.RootElement.EnumerateObject().Count());
                var stamp = doc.RootElement.GetProperty("seenAt").GetString()!;
                Assert.True(stamp.Length < 40 && stamp.EndsWith('Z'));
                Assert.True(doc.RootElement.GetProperty("seenAt").GetDateTimeOffset() <= clock.GetUtcNow());
            });
        }
        foreach (var peer in new[] { old, no, duplicate, text, app }) { Assert.Empty(peer.Hints); }
    }

    [Fact]
    public async Task Polls_are_conditional_once_a_minute_and_failures_or_304s_send_nothing()
    {
        using var feed = new Feed { Body = List(("v0.1.1", "win")) };
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var relay = await TestRelay.StartAsync(releaseFeed: feed, releaseTime: clock);
        using var host = await Peer.Connect(relay, "true");
        await Wait.UntilAsync(() => host.Hints.Count == 1, "initial hint");
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Single(feed.Requests);
        feed.Status = HttpStatusCode.NotModified;
        clock.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => feed.Requests.Count == 2, "conditional 304");
        feed.Fail = true;
        clock.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => feed.Requests.Count == 3, "unreachable release relay");
        feed.Fail = false;
        feed.Status = HttpStatusCode.OK;
        feed.Body = List(("v0.1.2", "win"));
        clock.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => host.Hints.Count == 2, "recovery hint");
        Assert.Equal(new[] { "", "\"list\"", "\"list\"", "\"list\"" }, feed.Requests.ToArray());
        Assert.Equal(new[] { "v0.1.1", "v0.1.2" }, host.Hints.Select(Tag).ToArray());
    }

    [Theory]
    [InlineData("v0.1.01", "win")]
    [InlineData("v0.1.9223372036854775808", "win")]
    [InlineData("v0.1.1x", "win")]
    [InlineData("v0.1.3", "linux")]
    public async Task Invalid_tags_and_channels_are_never_sent(string tag, string channel)
    {
        using var feed = new Feed { Body = List((tag, channel)) };
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var relay = await TestRelay.StartAsync(releaseFeed: feed, releaseTime: clock);
        using var host = await Peer.Connect(relay, "true");
        await Wait.UntilAsync(() => feed.Requests.Count == 1, "invalid list poll");
        feed.Body = List(("v0.1.2", "win"));
        clock.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => host.Hints.Count != 0, "valid list after invalid list");
        Assert.Equal("v0.1.2", Tag(Assert.Single(host.Hints)));
    }

    [Fact]
    public async Task A_slow_release_relay_does_not_block_latest_hints_on_connect()
    {
        using var feed = new Feed { Body = List(("v0.1.1", "win")) };
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var relay = await TestRelay.StartAsync(releaseFeed: feed, releaseTime: clock);
        using var first = await Peer.Connect(relay, "true");
        await Wait.UntilAsync(() => first.Hints.Count == 1, "initial hint");
        feed.Stall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.Body = List(("v0.1.2", "win"));
        clock.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => feed.Requests.Count == 2, "stalled HTTP request");
        using var late = await Peer.Connect(relay, "true");
        await Wait.UntilAsync(() => late.Hints.Count == 1, "cached hint while HTTP is stalled");
        Assert.False(feed.Stall.Task.IsCompleted);
        feed.Stall.SetResult();
        await Wait.UntilAsync(() => first.Hints.Count == 2 && late.Hints.Count == 2, "hints after HTTP recovers");
        Assert.Equal(new[] { "v0.1.1", "v0.1.2" }, late.Hints.Select(Tag).ToArray());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Failed_subscriber_sends_do_not_stop_later_polls_or_leak_initial_subscriptions(bool disposed, bool initial)
    {
        using var feed = new Feed { Body = List(("v0.1.1", "win")) };
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var relay = await TestRelay.StartAsync(releaseFeed: feed, releaseTime: clock);
        using var host = await Peer.Connect(relay, "true");
        await Wait.UntilAsync(() => host.Hints.Count == 1, "initial hint");
        var watcher = relay.App.Services.GetRequiredService<ReleaseHintWatcher>();
        var attempts = 0;
        Func<byte[], Task> send = _ => Interlocked.Increment(ref attempts) == 1 && !initial ? Task.CompletedTask
            : Task.FromException(disposed ? new ObjectDisposedException("socket") : new InvalidOperationException("socket"));
        using var faulty = initial ? null : await watcher.SubscribeAsync(send);
        if (initial) { await Assert.ThrowsAsync(disposed ? typeof(ObjectDisposedException) : typeof(InvalidOperationException), () => watcher.SubscribeAsync(send)); }
        for (var run = 2; run <= 3; run++)
        {
            feed.Body = List(($"v0.1.{run}", "win"));
            clock.Advance(TimeSpan.FromMinutes(1));
            await Wait.UntilAsync(() => host.Hints.Count == run, "healthy subscriber after a failed send");
        }
        Assert.Equal(initial ? 1 : 3, Volatile.Read(ref attempts));
    }

    static string Tag(byte[] frame) { using var doc = JsonDocument.Parse(frame); return doc.RootElement.GetProperty("tag").GetString()!; }
    static string List(params (string Tag, string Channel)[] entries) => JsonSerializer.Serialize(entries.Select(e =>
        new { tag_name = e.Tag, draft = false, assets = new[] { new { name = $"releases.{e.Channel}.json" } } }));

    sealed class Feed : HttpMessageHandler
    {
        public string Body = "[]";
        public HttpStatusCode Status = HttpStatusCode.OK;
        public bool Fail;
        public TaskCompletionSource? Stall;
        public ConcurrentQueue<string> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://releases.slipgates.com/repos/danswett/agent-bridge/releases?per_page=10&page=1", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            var (body, status, fail) = (Body, Status, Fail);
            Requests.Enqueue(string.Join(",", request.Headers.IfNoneMatch));
            if (fail) { throw new HttpRequestException("offline"); }
            if (Stall is { } stall) { await stall.Task.WaitAsync(cancellationToken); }
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            response.Headers.ETag = new EntityTagHeaderValue("\"list\"");
            return response;
        }
    }

    sealed class Peer : IDisposable
    {
        readonly ClientWebSocket ws = new();
        readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(30));
        public ConcurrentQueue<byte[]> Hints { get; } = new();
        Task reader = Task.CompletedTask;
        public static async Task<Peer> Connect(TestRelay relay, string? capability, int account = 1, string kind = "host")
        {
            var peer = new Peer();
            using var key = P256KeyPair.Generate();
            await peer.ws.ConnectAsync(RelayClient.SocketUri(relay.Url), peer.stop.Token);
            var hello = JsonSerializer.Serialize(new { t = "hello", token = relay.TokenFor(account, "test"), pub = Wire.Hex(key.Public), name = "hint test", kind });
            if (capability is not null) { hello = hello[..^1] + ",\"releaseHint\":" + capability + "}"; }
            await peer.ws.SendAsync(Encoding.UTF8.GetBytes(hello), WebSocketMessageType.Text, true, peer.stop.Token);
            using var challenge = JsonDocument.Parse((await Wire.ReceiveAsync(peer.ws, Wire.MaxControl, peer.stop.Token))!.Value.Data);
            var c = challenge.RootElement;
            var mac = Wire.ProofMac(key.Dh(Wire.Unhex(c.GetProperty("eph").GetString()!)), Wire.Unhex(c.GetProperty("nonce").GetString()!), c.GetProperty("account").GetString()!);
            await peer.ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { t = "proof", mac = Wire.Hex(mac) }), WebSocketMessageType.Text, true, peer.stop.Token);
            while (true)
            {
                using var frame = JsonDocument.Parse((await Wire.ReceiveAsync(peer.ws, Wire.MaxControl, peer.stop.Token))!.Value.Data);
                if (frame.RootElement.GetProperty("t").GetString() == "ready") { break; }
            }
            peer.reader = peer.Read();
            return peer;
        }
        async Task Read()
        {
            try
            {
                while (await Wire.ReceiveAsync(ws, Wire.MaxControl, stop.Token) is { } frame)
                {
                    using var doc = JsonDocument.Parse(frame.Data);
                    if (doc.RootElement.GetProperty("t").GetString() == "release.hint") { Hints.Enqueue(frame.Data); }
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
        }
        public void Dispose() { stop.Cancel(); ws.Abort(); reader.GetAwaiter().GetResult(); ws.Dispose(); stop.Dispose(); }
    }
}
