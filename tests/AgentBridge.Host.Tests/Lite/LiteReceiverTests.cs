using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.AgentEnvelope;
using Codec = AgentBridge.Roster.Codec;
using AgentBridge.Host.Lite;
using AgentBridge.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public class LiteReceiverTests
{
    const string Account = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string Self = "22222222222222222222222222222222";
    static readonly byte[] PeerStatic = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];
    static readonly string Peer = Wire.DeviceId(PeerStatic);
    static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    sealed class Mode : ILiteMode
    {
        public bool On { get; set; } = true;
        public long KillEpoch => 7;
        public void TurnOff(string why) { }
        public void RaiseEpoch(long epoch) { }
    }
    sealed class Peers(LitePeer? peer) : ILitePeers
    {
        public bool TryFind(string deviceId, out LitePeer found) { found = peer!; return peer is not null && peer.DeviceId == deviceId; }
        public IReadOnlyList<LitePeer> All() => peer is null ? [] : [peer];
    }
    sealed class Deliver(LiteDelivery result) : ILiteDeliver
    {
        public int Calls;
        public string? Text;
        public Task<LiteDelivery> DeliverAsync(VerifiedEnvelope envelope, string text, CancellationToken ct) { Calls++; Text = text; return Task.FromResult(result); }
    }
    sealed class Rig : IDisposable
    {
        public readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public readonly Mode Mode = new();
        public readonly FakeTimeProvider Time = new(Start);
        public readonly Deliver Deliver;
        public readonly LiteReceiver Receiver;
        public LiteChannel Channel = new(Peer, "host", PeerStatic, Self, Account, true);
        public Rig(LiteDelivery? delivery = null, bool knownPeer = true, ILiteDeliver? custom = null)
        {
            Deliver = new(delivery ?? new LiteDelivery(true, null));
            var q = Key.ExportParameters(false).Q;
            var peer = knownPeer ? new LitePeer(Peer, [0x04, .. q.X!, .. q.Y!], "peer") : null;
            Receiver = new(Mode, new Peers(peer), custom ?? Deliver, Time);
        }

        public void Dispose() => Key.Dispose();
        public JsonElement Request(string text = "hello", string? id = null, string? machine = null, string? add = null, string? target = null, string? signedText = null, string session = "agent-b2")
        {
            var sender = new LiteSender(Account, machine ?? Peer, add ?? Codec.Base64Url(SHA256.HashData(PeerStatic)), "agent-a1",
                Codec.Base64Url(new byte[12]), Codec.Base64Url(new byte[16]), "copilot", "agent-bridge");
            var sealedBytes = LiteProfile.Seal(sender, new LiteTarget(target ?? Self, session, Codec.Base64Url(new byte[12])), id ?? Id("1"),
                Encoding.UTF8.GetBytes(signedText ?? text), Time.GetUtcNow().ToUnixTimeSeconds(), new EcdsaEnvelopeSigner(Key));
            return JsonSerializer.SerializeToElement(new { envelope = Codec.Base64Url(sealedBytes), text, killEpoch = 0 });
        }

        public Task<LiteAck> Send(JsonElement data) => Receiver.ReceiveAsync(Channel, data, default);
    }

    static string Id(string n) => new Guid(int.Parse(n), 0, 0, new byte[8]).ToString("D");

    [Theory]
    [InlineData("mode")]
    [InlineData("peer")]
    [InlineData("expiry")]
    [InlineData("presence")]
    [InlineData("rate")]
    public async Task Busy_admission_waits_for_presence_and_reauthorizes_the_original_envelope(string changed)
    {
        using var r = new Rig();
        var folder = Directory.CreateTempSubdirectory("lite-recovery-").FullName;
        try
        {
            var source = new FleetPublisherTests.Source();
            source.Sessions = [source.Sessions[0] with { Status = AgentBridge.Protocol.SessionStatus.Working }];
            var service = new AgentBridge.Host.SessionService(source,
                new AgentBridge.Protocol.MachineInfo("fixture", "fixture", "test", "0")) { LiteReceiver = r.Receiver };
            var allowed = true; var present = true;
            var path = Path.Combine(folder, "replies.inbox");
            service.ConfigureRelayInbox(path, (_, _) => true, _ => allowed, _ => present);
            var node = AgentBridge.HomeAssistant.HaContract.NodeId(source.Sessions[0].Id);
            Refused(await r.Send(r.Request(session: node)), LiteReceiver.Busy);
            if (changed == "rate")
            {
                for (var n = 2; n <= 6; n++) { Refused(await r.Send(r.Request(id: Id(n.ToString()), session: node)), LiteReceiver.Busy); }
                Refused(await r.Send(r.Request(id: Id("7"), session: node)), LiteReceiver.Rate);
                using var snapshot = JsonDocument.Parse(File.ReadAllText(path));
                Assert.Equal(6, snapshot.RootElement.GetProperty("Messages").GetArrayLength());
            }
            if (changed == "mode") { r.Mode.On = false; }
            if (changed == "peer") { allowed = false; }
            if (changed == "expiry") { r.Time.Advance(TimeSpan.FromSeconds(121)); }
            source.Sessions[0] = source.Sessions[0] with { Status = AgentBridge.Protocol.SessionStatus.Idle };
            present = false;
            await service.RelayInbox!.RecoverAsync([node]);
            Assert.Equal(0, r.Deliver.Calls);
            present = true;
            await service.RelayInbox.RecoverAsync([node]);
            Assert.Equal(changed == "presence" ? 1 : changed == "rate" ? 6 : 0, r.Deliver.Calls);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task A_missing_target_session_is_refused_before_any_delivery()
    {
        using var r = new Rig();
        Assert.True(Codec.TryBase64Url(r.Request().GetProperty("envelope").GetString()!, out var bytes));
        var envelope = Envelope.Parse(bytes);
        var body = envelope.Body with { Target = envelope.Body.Target with { Session = null } };
        var malformed = Envelope.Encode(body.Encode(), envelope.Signature);
        Assert.Equal("refused", (await r.Send(JsonSerializer.SerializeToElement(new { envelope = Codec.Base64Url(malformed), text = "hello" }))).Status);
        Assert.Equal(0, r.Deliver.Calls);
    }

    static void Refused(LiteAck ack, string code)
    {
        Assert.Equal("refused", ack.Status);
        Assert.Equal(code, ack.Code);
        Assert.Equal(7, ack.KillEpoch);
    }

    [Fact]
    public async Task Durable_recovery_reauthorizes_the_signed_Lite_request_instead_of_replaying_raw_text()
    {
        using var r = new Rig();
        var folder = Directory.CreateTempSubdirectory("lite-inbox-").FullName;
        try
        {
            var service = new AgentBridge.Host.SessionService(new FleetPublisherTests.Source(),
                new AgentBridge.Protocol.MachineInfo("fixture", "fixture", "test", "test")) { LiteReceiver = r.Receiver };
            service.ConfigureRelayInbox(Path.Combine(folder, "replies.json"), (_, _) => true, _ => true);
            using var stopped = new CancellationTokenSource();
            stopped.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                r.Receiver.ReceiveAsync(r.Channel, r.Request(), stopped.Token));
            Assert.Equal(0, r.Deliver.Calls);
            r.Mode.On = false;
            await service.RelayInbox!.RecoverAsync(["agent-b2"]);
            Assert.Equal(0, r.Deliver.Calls);
            Assert.Contains("\"Outcome\":\"refused\"", File.ReadAllText(Path.Combine(folder, "replies.json")));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task A_good_request_is_delivered_and_acknowledged_with_its_id()
    {
        using var r = new Rig();
        var ack = await r.Send(r.Request());
        Assert.Equal("delivered", ack.Status);
        Assert.Null(ack.Code);
        Assert.Equal(Id("1"), ack.Request);
        Assert.Equal("hello", r.Deliver.Text);
        Assert.Contains("\"code\":null", JsonSerializer.Serialize(ack));
    }

    [Fact]
    public async Task Mode_off_answers_off_and_does_no_work()
    {
        using var r = new Rig();
        r.Mode.On = false;
        Refused(await r.Send(r.Request()), LiteReceiver.Off);
        Assert.Equal(0, r.Deliver.Calls + r.Receiver.Replays.Count);
        Assert.Equal("", (await r.Send(JsonSerializer.SerializeToElement(new[] { 1 }))).Request);
    }

    sealed class Cancelling(CancellationToken? caller = null) : ILiteDeliver
    {
        public Task<LiteDelivery> DeliverAsync(VerifiedEnvelope envelope, string text, CancellationToken ct) => throw new OperationCanceledException(caller ?? default);
    }

    [Fact]
    public async Task An_internal_cancellation_in_delivery_is_a_refusal_not_an_exception()
    {
        using var r = new Rig(custom: new Cancelling());
        Refused(await r.Send(r.Request()), LiteReceiver.DeliveryFailed);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => r.Receiver.ReceiveAsync(r.Channel, r.Request(id: Id("2")), cts.Token));
    }

    [Fact]
    public async Task A_peer_that_is_not_a_host_or_not_present_is_not_the_same_account()
    {
        using var r = new Rig();
        r.Channel = r.Channel with { PeerKind = "person" };
        Refused(await r.Send(r.Request()), LiteReceiver.NotSameAccount);
        r.Channel = r.Channel with { PeerKind = "host", InPresence = false };
        Refused(await r.Send(r.Request()), LiteReceiver.NotSameAccount);
        Assert.Equal(0, r.Deliver.Calls);
    }

    [Fact]
    public async Task An_unpinned_peer_or_a_static_key_that_does_not_hash_to_the_id_is_not_a_peer()
    {
        using var unknown = new Rig(knownPeer: false);
        Refused(await unknown.Send(unknown.Request()), LiteReceiver.NotPeer);
        using var r = new Rig();
        r.Channel = r.Channel with { PeerStatic = [.. PeerStatic.Reverse()] };
        Refused(await r.Send(r.Request()), LiteReceiver.NotPeer);
        Assert.Equal(0, r.Deliver.Calls);
    }

    [Fact]
    public async Task Each_check_failure_has_its_own_code()
    {
        using var r = new Rig();
        Refused(await r.Send(r.Request(text: "tampered", signedText: "hello")), Refusal.Data);
        Refused(await r.Send(r.Request(machine: new string('9', 32))), LiteReceiver.Sender);
        Refused(await r.Send(r.Request(add: Codec.Base64Url(new byte[32]))), LiteReceiver.AddMismatch);
        Refused(await r.Send(r.Request(target: new string('9', 32))), LiteReceiver.Target);
        Refused(await r.Send(JsonSerializer.SerializeToElement(new { envelope = "AAAA", text = "x" })), Refusal.Json);
        Refused(await r.Send(JsonSerializer.SerializeToElement(new { text = "x" })), LiteReceiver.Malformed);
        Refused(await r.Send(JsonSerializer.SerializeToElement(new[] { 1 })), LiteReceiver.Malformed);
        Refused(await r.Send(r.Request(text: new string('a', LiteReceiver.MaxText + 1))), LiteReceiver.Malformed);

        using var other = new Rig();
        Refused(await r.Send(other.Request(id: Id("3"))), Refusal.Signature);
        var good = r.Request(id: Id("2"));
        r.Time.Advance(TimeSpan.FromSeconds(121));
        Refused(await r.Send(good), Refusal.Lifetime);
        Assert.Equal(0, r.Deliver.Calls + r.Receiver.Replays.Count);
    }

    [Fact]
    public async Task A_repeated_request_id_is_a_replay_until_its_lifetime_passes()
    {
        using var r = new Rig();
        var first = r.Request();
        Assert.Equal("delivered", (await r.Send(first)).Status);
        r.Time.Advance(TimeSpan.FromSeconds(60));
        Refused(await r.Send(first), LiteReceiver.Replay);
        r.Time.Advance(TimeSpan.FromSeconds(91));
        Assert.Equal("delivered", (await r.Send(r.Request())).Status);
    }

    [Fact]
    public async Task The_replay_table_is_bounded_and_a_full_one_refuses_as_busy()
    {
        using var r = new Rig();
        for (var i = 0; i < ReplayStore.Max; i++) { Assert.Equal(ReplayStore.Result.Added, r.Receiver.Replays.Add("x/" + i)); }
        Refused(await r.Send(r.Request()), LiteReceiver.Busy);
        r.Time.Advance(TimeSpan.FromSeconds(151));
        Assert.Equal("delivered", (await r.Send(r.Request(id: Id("9")))).Status);
    }

    [Fact]
    public async Task Delivery_that_does_not_type_is_refused_with_its_code()
    {
        using var r = new Rig(new LiteDelivery(false, "notReplyTarget"));
        Refused(await r.Send(r.Request()), "notReplyTarget");
        Assert.Equal(new LiteDelivery(false, "notReplyTarget"), await new NoLiteDelivery().DeliverAsync(null!, "x", default));
    }

    [Fact]
    public async Task A_pair_is_capped_at_6_a_minute_and_60_an_hour_()
    {
        using var r = new Rig();
        for (var i = 1; i <= 6; i++) { Assert.Equal("delivered", (await r.Send(r.Request(id: Id(i.ToString())))).Status); }
        Refused(await r.Send(r.Request(id: Id("7"))), LiteReceiver.Rate);
        r.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("delivered", (await r.Send(r.Request(id: Id("8")))).Status);
    }
    [Fact]
    public void The_hourly_cap_holds_and_one_peer_cannot_fill_the_table_or_reset_an_active_pair()
    {
        var time = new FakeTimeProvider(Start); var cap = new RateCap(time);
        for (var i = 0; i < RateCap.PerHour; i++) { Assert.True(cap.Allow("peer", "a")); time.Advance(TimeSpan.FromSeconds(20)); }
        Assert.False(cap.Allow("peer", "a"));
        for (var i = 0; i < RateCap.MaxPerPeer * 2; i++) { cap.Allow("greedy", "g" + i); }
        Assert.False(cap.Allow("greedy", "more")); Assert.True(cap.Allow("other", "o")); Assert.False(cap.Allow("peer", "a"));
    }

    [Fact]
    public void An_active_pair_with_an_old_first_hit_is_not_evicted_and_reset()
    {
        var time = new FakeTimeProvider(Start); var cap = new RateCap(time);
        Assert.True(cap.Allow("peer", "a")); time.Advance(TimeSpan.FromMinutes(50));
        for (var i = 0; i < RateCap.PerHour - 1; i++) { Assert.True(cap.Allow("peer", "a")); time.Advance(TimeSpan.FromSeconds(10)); }
        time.Advance(TimeSpan.FromMinutes(1));
        for (var i = 0; i < RateCap.MaxPerPeer; i++) { cap.Allow("peer", "f" + i); }
        Assert.True(cap.Allow("peer", "a")); Assert.False(cap.Allow("peer", "a"));
    }
    [Fact]
    public async Task Refusals_before_verification_echo_no_request_id_and_later_ones_do()
    {
        using var r = new Rig(); Assert.Equal("", (await r.Send(r.Request(text: "tampered", signedText: "hello"))).Request);
        Assert.Equal(Id("1"), (await r.Send(r.Request(target: new string('9', 32)))).Request);
    }
}