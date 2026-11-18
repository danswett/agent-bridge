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
    readonly string folder = Directory.CreateTempSubdirectory("relay-flow-").FullName;
    readonly ECDsa signingA = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    readonly ECDsa signingB = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    CancellationTokenSource stopped = null!;
    Task recovery = Task.CompletedTask;
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
        await StopReceiverAsync();
        await relay.DisposeAsync();
        signingA.Dispose();
        signingB.Dispose();
        Directory.Delete(folder, true);
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
        service.ConfigureRelayInbox(Path.Combine(folder, "relay-replies.inbox"), (_, _) => false,
            c => c.Account == receiver.Relay.AccountId && peers.TryFind(c.PeerId, out _),
            c => receiver.Relay.Presence.Any(p => p.Id == c.PeerId && p.Kind == "host"));
        receiver.Hub.Handler = service.HandleAsync;
        stopped = new();
        recovery = service.RunRelayRecoveryAsync(stopped.Token, receiver.Relay);
    }

    async Task StopReceiverAsync()
    {
        stopped.Cancel();
        await recovery.WaitAsync(Patience);
        stopped.Dispose();
        await receiver.DisposeAsync();
    }

    async Task RestartAsync()
    {
        await StopReceiverAsync();
        receiver = await relay.ConnectAsync(relay.TokenFor(1, "fixture"), "fixture", "host", receiver.Key);
        receiver.Hub.JsonOptions = ProtocolJson.Options;
        Serve();
        await new FleetPublisher(source, receiver.Id).PublishAsync(receiver.Relay.RegistryPutAsync);
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
        Receipt(ack, rid);
        Assert.Equal("delivered", ack.GetProperty("status").GetString());
    }

    static void Receipt(JsonElement ack, string rid)
    {
        Assert.Equal(rid, ack.GetProperty("request").GetString());
        var field = Assert.Single(ack.GetProperty("reply_ack").EnumerateObject());
        Assert.Equal("rid", field.Name);
        Assert.Equal(rid, field.Value.GetString());
    }

    static void AdmittedWhileBusy(JsonElement ack, string rid)
    {
        Receipt(ack, rid);
        Assert.Equal("refused", ack.GetProperty("status").GetString());
        Assert.Equal("busy", ack.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Two_relay_senders_reach_one_roster_session_one_at_a_time_in_arrival_order()
    {
        var a = await ResolveAsync(senderA);
        var b = await ResolveAsync(senderB);
        var firstRid = Rid();
        var secondRid = Rid();
        var thirdRid = Rid();
        terminal.HoldFirst = true;
        var first = SendAsync(a.Channel, Request(senderA, signingA, a.Target, firstRid, "first"));
        try
        {
            await terminal.Started.Task.WaitAsync(Patience);
            AdmittedWhileBusy(await SendAsync(b.Channel, Request(senderB, signingB, b.Target, secondRid, "second")), secondRid);
            AdmittedWhileBusy(await SendAsync(a.Channel, Request(senderA, signingA, a.Target, thirdRid, "third")), thirdRid);
            Assert.Empty(terminal.Delivered);
            Assert.Equal([firstRid], terminal.Attempts);
            Assert.False(a.Channel.Events.TryRead(out _));
            Assert.False(b.Channel.Events.TryRead(out _));
        }
        finally { terminal.Release.TrySetResult(); }
        Delivered(await first, firstRid);
        await terminal.ExpectAsync(firstRid, secondRid, thirdRid);

        Assert.Equal([firstRid, secondRid, thirdRid], terminal.Delivered);
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

        Delivered(duplicate, firstRid);
        Assert.Equal([firstRid, secondRid], terminal.Delivered);
        Assert.Equal([firstRid, secondRid], terminal.Attempts);
    }

    [Fact]
    public async Task A_delivered_rid_is_not_redelivered_when_the_receiver_restarts()
    {
        var a = await ResolveAsync(senderA);
        var rid = Rid();
        var request = Request(senderA, signingA, a.Target, rid, "once");
        Delivered(await SendAsync(a.Channel, request), rid);
        await RestartAsync();
        var rediscovered = await ResolveAsync(senderB);
        await Task.WhenAll(
            senderA.Relay.WaitForPeerAsync(p => p.Id == rediscovered.Target.Id, Patience),
            receiver.Relay.WaitForPeerAsync(p => p.Id == senderA.Id, Patience));
        var reconnected = await senderA.Hub.OpenAsync(rediscovered.Target.Id, Patience);

        var ack = await SendAsync(reconnected, request);

        Delivered(ack, rid);
        Assert.Equal([rid], terminal.Delivered);
        Assert.Equal([rid], terminal.Attempts);
    }

    [Fact]
    public async Task An_admitted_reply_recovers_after_receiver_restart_and_interrupted_work_is_not_retyped()
    {
        var a = await ResolveAsync(senderA);
        var b = await ResolveAsync(senderB);
        var interrupted = Rid();
        var retained = Rid();
        terminal.HoldFirst = true;
        source.Sessions[0] = source.Sessions[0] with { Status = SessionStatus.Working };
        AdmittedWhileBusy(await SendAsync(a.Channel, Request(senderA, signingA, a.Target, interrupted, "interrupted")), interrupted);
        source.Sessions[0] = source.Sessions[0] with { Status = SessionStatus.Idle };
        source.Signal();
        await terminal.Started.Task.WaitAsync(Patience);
        AdmittedWhileBusy(await SendAsync(b.Channel, Request(senderB, signingB, b.Target, retained, "survive restart")), retained);
        Assert.Empty(terminal.Delivered);

        await RestartAsync();

        await terminal.ExpectAsync(retained);
        Assert.Equal([retained], terminal.Delivered);
        Assert.Equal([interrupted, retained], terminal.Attempts);
        Assert.Equal(1, terminal.MaximumInFlight);
        stopped.Cancel();
        await recovery.WaitAsync(Patience);
        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "relay-replies.inbox")));
        var attempt = Assert.Single(saved.RootElement.GetProperty("Messages").EnumerateArray(),
            m => m.GetProperty("Rid").GetString() == Guid.Parse(interrupted).ToString("N"));
        Assert.Equal("unconfirmed", attempt.GetProperty("Outcome").GetString());
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
        readonly Channel<string> completed = Channel.CreateUnbounded<string>();
        int inFlight, calls;
        public bool HoldFirst;
        public int MaximumInFlight;
        public ConcurrentQueue<string> Attempts { get; } = new();
        public ConcurrentQueue<string> Delivered { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExpectAsync(params string[] rids)
        {
            foreach (var rid in rids)
            { Assert.Equal(rid, await completed.Reader.ReadAsync().AsTask().WaitAsync(Patience)); }
        }

        public async Task<LiteDelivery> DeliverAsync(VerifiedEnvelope envelope, string text, CancellationToken ct)
        {
            bool hold;
            lock (gate)
            {
                MaximumInFlight = Math.Max(MaximumInFlight, ++inFlight);
                hold = HoldFirst && ++calls == 1;
            }
            Attempts.Enqueue(envelope.Body.Request);
            Started.TrySetResult();
            try
            {
                if (hold) { await Release.Task.WaitAsync(ct); }
                Delivered.Enqueue(envelope.Body.Request);
                completed.Writer.TryWrite(envelope.Body.Request);
                return new(true, null);
            }
            finally { lock (gate) { inFlight--; } }
        }
    }
}
