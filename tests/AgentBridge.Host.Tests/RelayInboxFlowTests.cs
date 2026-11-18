using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AgentBridge.AgentEnvelope;
using AgentBridge.HomeAssistant;
using AgentBridge.Host;
using AgentBridge.Host.Fleet;
using AgentBridge.Host.Lite;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Codec = AgentBridge.Roster.Codec;
using Xunit;

[Trait("Category", "Network")]
public sealed class RelayInboxFlowTests : IAsyncLifetime
{
    const string SessionId = "0123456789abcdef";
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    readonly FleetPublisherTests.Source source = new()
    {
        Sessions = [new Session
        {
            Id = SessionId, Agent = AgentKind.Copilot, Status = SessionStatus.Idle,
            StatusSince = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            Activity = new Activity(),
        }],
    };
    readonly Terminal terminal = new();
    readonly Channel<string> arrivals = Channel.CreateUnbounded<string>();
    readonly ECDsa signingA = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    readonly ECDsa signingB = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    TestRelay relay = null!;
    Device receiver = null!, senderA = null!, senderB = null!;

    public async Task InitializeAsync()
    {
        relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(1, "fixture");
        receiver = await relay.ConnectAsync(token, "fixture", "host");
        senderA = await relay.ConnectAsync(token, "fixture", "host");
        senderB = await relay.ConnectAsync(token, "fixture", "host");
        receiver.Hub.JsonOptions = ProtocolJson.Options;
        Serve();
        await new FleetPublisher(source, receiver.Id).PublishAsync(receiver.Relay.RegistryPutAsync);
    }

    public async Task DisposeAsync()
    {
        terminal.Release.TrySetResult();
        await relay.DisposeAsync();
        signingA.Dispose();
        signingB.Dispose();
    }

    void Serve()
    {
        var peers = new Pins(
            new LitePeer(senderA.Id, Point(signingA), "fixture"),
            new LitePeer(senderB.Id, Point(signingB), "fixture"));
        var service = new SessionService(source, new MachineInfo(receiver.Id, "fixture", "test", "0"))
        {
            LiteReceiver = new LiteReceiver(new Mode(), peers, terminal),
        };
        receiver.Hub.Handler = (channel, op, data) =>
        {
            var pending = service.HandleAsync(channel, op, data);
            if (op == LiteReceiver.Op)
            {
                var envelope = Envelope.Parse(CodecBytes(data.GetProperty("envelope").GetString()!));
                arrivals.Writer.TryWrite(envelope.Body.Request);
            }
            return pending;
        };
    }

    async Task<(SecureChannel Channel, FleetMachine Target)> ResolveAsync(Device sender)
    {
        var roster = new RelayFleetRoster();
        roster.Attach(sender.Relay);
        var snapshot = await roster.ReadScopedAsync(CancellationToken.None);
        Assert.Equal(sender.Relay.AccountId, snapshot.FleetScope);
        var target = Assert.Single(snapshot.Machines, m =>
            m.Online && m.Sessions.Any(s => s.Node == HaContract.NodeId(SessionId)));
        await Task.WhenAll(
            sender.Relay.WaitForPeerAsync(p => p.Id == target.Id, Patience),
            receiver.Relay.WaitForPeerAsync(p => p.Id == sender.Id, Patience));
        return (await sender.Hub.OpenAsync(target.Id, Patience), target);
    }

    static byte[] Point(ECDsa key)
    {
        var q = key.ExportParameters(false).Q;
        return [0x04, .. q.X!, .. q.Y!];
    }

    static byte[] CodecBytes(string encoded)
    {
        Assert.True(Codec.TryBase64Url(encoded, out var bytes));
        return bytes;
    }

    static string Rid() => Guid.NewGuid().ToString("D");

    static object Request(Device sender, ECDsa key, FleetMachine target, string rid, string text)
    {
        var session = Assert.Single(target.Sessions);
        var principal = new LiteSender(sender.Relay.AccountId, sender.Id,
            Codec.Base64Url(SHA256.HashData(sender.Key.Public)), "fixture",
            Codec.Base64Url(new byte[12]), Codec.Base64Url(new byte[16]), "agent", "fixture");
        var envelope = LiteProfile.Seal(principal, new LiteTarget(target.Id, session.Node, Codec.Base64Url(new byte[12])),
            rid, Encoding.UTF8.GetBytes(text), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), new EcdsaEnvelopeSigner(key));
        return new { envelope = Codec.Base64Url(envelope), text, killEpoch = 0 };
    }

    static async Task<JsonElement> SendAsync(SecureChannel channel, object request)
    {
        var response = await channel.RequestAsync(LiteReceiver.Op, request, Patience);
        Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText());
        return response.GetProperty("result");
    }

    static void Delivered(JsonElement ack, string rid)
    {
        Assert.Equal(rid, ack.GetProperty("request").GetString());
        Assert.Equal("delivered", ack.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Two_relay_senders_reach_one_roster_session_one_at_a_time_in_arrival_order()
    {
        var a = await ResolveAsync(senderA);
        var b = await ResolveAsync(senderB);
        var firstRid = Rid();
        var secondRid = Rid();
        terminal.HoldFirst = true;
        var first = SendAsync(a.Channel, Request(senderA, signingA, a.Target, firstRid, "first"));
        Task<JsonElement>? second = null;
        try
        {
            await terminal.Started.Task.WaitAsync(Patience);
            Assert.Equal(firstRid, await arrivals.Reader.ReadAsync().AsTask().WaitAsync(Patience));
            second = SendAsync(b.Channel, Request(senderB, signingB, b.Target, secondRid, "second"));
            Assert.Equal(secondRid, await arrivals.Reader.ReadAsync().AsTask().WaitAsync(Patience));
        }
        finally { terminal.Release.TrySetResult(); }
        Delivered(await first, firstRid);
        Delivered(await second!, secondRid);

        Assert.Equal([firstRid, secondRid], terminal.Delivered);
        Assert.Equal(1, terminal.MaximumInFlight);
    }

    [Fact]
    public async Task Each_rid_gets_its_own_ack_and_a_duplicate_is_not_delivered_twice()
    {
        var a = await ResolveAsync(senderA);
        var b = await ResolveAsync(senderB);
        var firstRid = Rid();
        var secondRid = Rid();
        var first = Request(senderA, signingA, a.Target, firstRid, "first");
        Delivered(await SendAsync(a.Channel, first), firstRid);
        Delivered(await SendAsync(b.Channel, Request(senderB, signingB, b.Target, secondRid, "second")), secondRid);

        var duplicate = await SendAsync(a.Channel, first);

        Assert.Equal(firstRid, duplicate.GetProperty("request").GetString());
        Assert.Equal([firstRid, secondRid], terminal.Delivered);
    }

    [Fact]
    public async Task A_delivered_rid_is_not_redelivered_when_the_receiver_restarts()
    {
        var a = await ResolveAsync(senderA);
        var rid = Rid();
        var request = Request(senderA, signingA, a.Target, rid, "once");
        Delivered(await SendAsync(a.Channel, request), rid);
        await receiver.DisposeAsync();
        receiver = await relay.ConnectAsync(relay.TokenFor(1, "fixture"), "fixture", "host", receiver.Key);
        receiver.Hub.JsonOptions = ProtocolJson.Options;
        Serve();
        await new FleetPublisher(source, receiver.Id).PublishAsync(receiver.Relay.RegistryPutAsync);
        var rediscovered = await ResolveAsync(senderB);
        await Task.WhenAll(
            senderA.Relay.WaitForPeerAsync(p => p.Id == rediscovered.Target.Id, Patience),
            receiver.Relay.WaitForPeerAsync(p => p.Id == senderA.Id, Patience));
        var reconnected = await senderA.Hub.OpenAsync(rediscovered.Target.Id, Patience);

        var ack = await SendAsync(reconnected, request);

        Assert.Equal(rid, ack.GetProperty("request").GetString());
        Assert.Equal([rid], terminal.Delivered);
    }

    [Fact]
    public async Task An_undelivered_rid_is_retained_and_dispatched_after_a_receiver_restart_without_resending()
    {
        var a = await ResolveAsync(senderA);
        var rid = Rid();
        terminal.Busy = true;
        var ack = await SendAsync(a.Channel, Request(senderA, signingA, a.Target, rid, "survive restart"));
        Assert.Empty(terminal.Delivered);
        await receiver.DisposeAsync();
        terminal.Busy = false;
        receiver = await relay.ConnectAsync(relay.TokenFor(1, "fixture"), "fixture", "host", receiver.Key);
        receiver.Hub.JsonOptions = ProtocolJson.Options;
        Serve();
        await new FleetPublisher(source, receiver.Id).PublishAsync(receiver.Relay.RegistryPutAsync);

        Assert.Equal(rid, ack.GetProperty("request").GetString());
        Assert.Equal("queued", ack.GetProperty("status").GetString());
        await terminal.Finished.Task.WaitAsync(Patience);
        Assert.Equal([rid], terminal.Delivered);
    }

    sealed class Mode : ILiteMode
    {
        public bool On => true;
        public long KillEpoch => 0;
        public void TurnOff(string why) => throw new NotSupportedException();
        public void RaiseEpoch(long epoch) => throw new NotSupportedException();
    }

    sealed class Pins(params LitePeer[] peers) : ILitePeers
    {
        public IReadOnlyList<LitePeer> All() => peers;
        public bool TryFind(string id, out LitePeer peer)
        {
            peer = peers.SingleOrDefault(p => p.DeviceId == id)!;
            return peer is not null;
        }
    }

    // Only the terminal is a stand-in; roster lookup, relay channels, envelope checks and
    // acknowledgements use production code. No test adapter forwards relay requests into HA.
    sealed class Terminal : ILiteDeliver
    {
        readonly object gate = new();
        int inFlight, calls;
        public bool HoldFirst, Busy;
        public int MaximumInFlight;
        public ConcurrentQueue<string> Delivered { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LiteDelivery> DeliverAsync(VerifiedEnvelope envelope, string text, CancellationToken ct)
        {
            if (Busy) { return new(false, LiteCodes.Busy); }
            bool hold;
            lock (gate)
            {
                MaximumInFlight = Math.Max(MaximumInFlight, ++inFlight);
                hold = HoldFirst && ++calls == 1;
            }
            Started.TrySetResult();
            try
            {
                if (hold) { await Release.Task.WaitAsync(ct); }
                Delivered.Enqueue(envelope.Body.Request);
                Finished.TrySetResult();
                return new(true, null);
            }
            finally { lock (gate) { inFlight--; } }
        }
    }
}
