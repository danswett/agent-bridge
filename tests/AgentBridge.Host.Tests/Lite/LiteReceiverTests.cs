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
        public long Raised;
        public void TurnOff(string why) { }
        public void RaiseEpoch(long epoch) { Raised = epoch; On = false; }
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
        public Rig(LiteDelivery? delivery = null, ILiteDeliver? custom = null)
        {
            Deliver = new(delivery ?? new LiteDelivery(true, null));
            Receiver = new(Mode, custom ?? Deliver, Time);
        }

        public byte[] Point { get { var q = Key.ExportParameters(false).Q; return [0x04, .. q.X!, .. q.Y!]; } }
        public void Dispose() => Key.Dispose();
        public JsonElement Request(string text = "hello", string? id = null, string? machine = null, string? add = null, string? target = null, string? signedText = null, byte[]? point = null, long killEpoch = 0, string? session = null)
        {
            var sender = new LiteSender(Account, machine ?? Peer, add ?? Codec.Base64Url(SHA256.HashData(PeerStatic)), session ?? "agent-a1",
                Codec.Base64Url(new byte[12]), Codec.Base64Url(new byte[16]), "copilot", "agent-bridge");
            var sealedBytes = LiteProfile.Seal(sender, new LiteTarget(target ?? Self, "agent-b2", Codec.Base64Url(new byte[12])), id ?? Id("1"),
                Encoding.UTF8.GetBytes(signedText ?? text), Time.GetUtcNow().ToUnixTimeSeconds(), new EcdsaEnvelopeSigner(Key));
            return JsonSerializer.SerializeToElement(new { envelope = Codec.Base64Url(sealedBytes), text, killEpoch, signPoint = Codec.Base64Url(point ?? Point) });
        }

        public Task<LiteAck> Send(JsonElement data) => Receiver.ReceiveAsync(Channel, data, default);

        // A receipt lookup (#3875): a signed lite session.read envelope for an earlier request id, empty data.
        public JsonElement Probe(string? id = null, byte[]? peerStatic = null, string text = "", string? session = null)
        {
            var stat = peerStatic ?? PeerStatic;
            var sender = new LiteSender(Account, Wire.DeviceId(stat), Codec.Base64Url(SHA256.HashData(stat)), session ?? "agent-a1",
                Codec.Base64Url(new byte[12]), Codec.Base64Url(new byte[16]), "copilot", "agent-bridge");
            var sealedBytes = LiteProfile.SealReceipt(sender, new LiteTarget(Self, "agent-b2", Codec.Base64Url(new byte[12])), id ?? Id("1"),
                Time.GetUtcNow().ToUnixTimeSeconds(), new EcdsaEnvelopeSigner(Key));
            return JsonSerializer.SerializeToElement(new { envelope = Codec.Base64Url(sealedBytes), text, killEpoch = 0, signPoint = Codec.Base64Url(Point) });
        }
    }

    static string Id(string n) => new Guid(int.Parse(n), 0, 0, new byte[8]).ToString("D");

    sealed class Querying : ILiteDeliver, ILiteReplayQuery
    {
        public int Offers, Queries;
        public Task<LiteDelivery> DeliverAsync(VerifiedEnvelope envelope, string text, CancellationToken ct)
        { Offers++; return Task.FromResult(new LiteDelivery(false, null) { Outcome = LiteKind.Pending }); }
        public Task<LiteDelivery> QueryAsync(VerifiedEnvelope envelope, string text, CancellationToken ct)
        { Queries++; return Task.FromResult(new LiteDelivery(false, null) { Outcome = LiteKind.Unconfirmed }); }
    }

    [Fact]
    public async Task A_verified_reoffer_queries_prior_lane_state_without_another_offer_or_bypassing_trust()
    {
        var delivery = new Querying();
        using var r = new Rig(custom: delivery);
        var request = r.Request();
        Assert.Equal("pending", (await r.Send(request)).Status);
        Assert.Equal("unconfirmed", (await r.Send(request)).Status);
        Assert.Equal((1, 1), (delivery.Offers, delivery.Queries));
        r.Channel = r.Channel with { InPresence = false };
        Refused(await r.Send(request), LiteReceiver.NotSameAccount);
        Assert.Equal((1, 1), (delivery.Offers, delivery.Queries));
    }

    static void Refused(LiteAck ack, string code)
    {
        Assert.Equal("refused", ack.Status);
        Assert.Equal(code, ack.Code);
        Assert.Equal(7, ack.KillEpoch);
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
    public async Task A_signed_envelope_from_another_account_is_refused_even_with_a_matching_present_host_channel_key()
    {
        using var r = new Rig();
        r.Channel = r.Channel with { Account = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
        Assert.Null(r.Channel.Untrusted);
        Refused(await r.Send(r.Request()), LiteRefusal.Account);
        Assert.Equal(0, r.Deliver.Calls + r.Receiver.Replays.Count);
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
    public async Task A_higher_kill_epoch_from_a_present_same_account_host_turns_the_mode_off_before_any_delivery()
    {
        using var stranger = new Rig();
        stranger.Channel = stranger.Channel with { PeerStatic = [.. PeerStatic.Reverse()] };
        Refused(await stranger.Send(stranger.Request(killEpoch: 9)), LiteReceiver.NotPeer);
        stranger.Channel = stranger.Channel with { PeerStatic = PeerStatic, InPresence = false };
        Refused(await stranger.Send(stranger.Request(killEpoch: 9)), LiteReceiver.NotSameAccount);
        Assert.Equal(0, stranger.Mode.Raised);
        using var r = new Rig();
        Assert.Equal("delivered", (await r.Send(r.Request(killEpoch: 7))).Status);
        Assert.Equal(0, r.Mode.Raised);
        Refused(await r.Send(r.Request(id: Id("2"), killEpoch: 9)), LiteReceiver.Off);
        Assert.Equal(9, r.Mode.Raised);
        Assert.Equal(1, r.Deliver.Calls);
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
        r.Channel = r.Channel with { InPresence = true, Account = "" };
        Refused(await r.Send(r.Request()), LiteReceiver.NotSameAccount);
        Assert.Equal(0, r.Deliver.Calls);
    }

    [Fact]
    public async Task A_static_key_that_does_not_hash_to_the_id_is_not_a_peer_and_no_pin_is_needed()
    {
        using var r = new Rig();
        r.Channel = r.Channel with { PeerStatic = [.. PeerStatic.Reverse()] };
        Refused(await r.Send(r.Request()), LiteReceiver.NotPeer);
        r.Channel = r.Channel with { PeerStatic = null };
        Refused(await r.Send(r.Request()), LiteReceiver.NotPeer);
        Assert.Equal(0, r.Deliver.Calls);
    }

    [Fact]
    public async Task Each_check_failure_has_its_own_code()
    {
        using var r = new Rig();
        Refused(await r.Send(r.Request(point: [1, 2, 3])), Refusal.Point);
        Refused(await r.Send(JsonSerializer.SerializeToElement(new { envelope = "AAAA", text = "x" })), LiteReceiver.Malformed);
        Refused(await r.Send(r.Request(text: "tampered", signedText: "hello")), Refusal.Data);
        Refused(await r.Send(r.Request(machine: new string('9', 32))), LiteReceiver.Sender);
        Refused(await r.Send(r.Request(add: Codec.Base64Url(new byte[32]))), LiteReceiver.AddMismatch);
        Refused(await r.Send(r.Request(target: new string('9', 32))), LiteReceiver.Target);
        Refused(await r.Send(JsonSerializer.SerializeToElement(new { envelope = "AAAA", text = "x", signPoint = "AAAA" })), Refusal.Json);
        Refused(await r.Send(JsonSerializer.SerializeToElement(new { text = "x" })), LiteReceiver.Malformed);
        Refused(await r.Send(JsonSerializer.SerializeToElement(new[] { 1 })), LiteReceiver.Malformed);
        Refused(await r.Send(r.Request(text: new string('a', LiteReceiver.MaxText + 1))), LiteReceiver.Malformed);

        using var other = new Rig();
        Refused(await r.Send(r.Request(id: Id("3"), point: other.Point)), Refusal.Signature);
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

    sealed class Gated : ILiteDeliver
    {
        public readonly TaskCompletionSource<LiteDelivery> Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public Task<LiteDelivery> DeliverAsync(VerifiedEnvelope envelope, string text, CancellationToken ct) { Calls++; return Gate.Task; }
    }

    static void Receipt(LiteAck ack, string outcome, string request)
    {
        Assert.Equal("receipt", ack.Status);
        Assert.Equal(outcome, ack.Code);
        Assert.Equal(request, ack.Request);
        Assert.Equal(7, ack.KillEpoch);
    }

    [Fact]
    public async Task A_probe_of_a_delivered_request_answers_its_outcome_and_types_nothing()
    {
        using var r = new Rig();
        Assert.Equal("delivered", (await r.Send(r.Request())).Status);
        Receipt(await r.Send(r.Probe()), "delivered", Id("1"));
        Assert.Equal(1, r.Deliver.Calls);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("composerNotEmpty")]
    [InlineData("noSuchSession")]
    public async Task A_probe_of_a_refused_request_answers_the_refusal_code(string code)
    {
        using var r = new Rig(new LiteDelivery(false, code));
        Assert.Equal("refused", (await r.Send(r.Request())).Status);
        Receipt(await r.Send(r.Probe()), "refused:" + code, Id("1"));
        Assert.Equal(1, r.Deliver.Calls);
    }

    [Fact]
    public async Task A_probe_of_a_request_still_being_delivered_answers_in_flight_then_the_outcome()
    {
        var gated = new Gated();
        using var r = new Rig(custom: gated);
        var first = r.Send(r.Request());
        Receipt(await r.Send(r.Probe()), "in-flight", Id("1"));
        gated.Gate.SetResult(new LiteDelivery(true, null));
        Assert.Equal("delivered", (await first).Status);
        Receipt(await r.Send(r.Probe()), "delivered", Id("1"));
        Assert.Equal(1, gated.Calls);
    }

    [Fact]
    public async Task A_probe_of_a_lane_request_answers_its_pending_state()
    {
        using var r = new Rig(custom: new Querying());
        Assert.Equal("pending", (await r.Send(r.Request())).Status);
        Receipt(await r.Send(r.Probe()), "pending", Id("1"));
    }

    [Fact]
    public async Task A_probe_of_an_id_never_seen_answers_unknown_types_nothing_and_does_not_use_the_id_up()
    {
        using var r = new Rig();
        var ack = await r.Send(r.Probe(Id("9")));
        Assert.Equal("unknown", ack.Status);
        Assert.Null(ack.Code);
        Assert.Equal(Id("9"), ack.Request);
        Assert.Equal(0, r.Deliver.Calls);
        Assert.Equal("delivered", (await r.Send(r.Request(id: Id("9")))).Status);
    }

    [Fact]
    public async Task Only_the_peer_that_made_a_request_can_read_its_outcome()
    {
        using var r = new Rig();
        Assert.Equal("delivered", (await r.Send(r.Request())).Status);
        byte[] other = [.. Enumerable.Range(40, 32).Select(i => (byte)i)];
        r.Channel = new LiteChannel(Wire.DeviceId(other), "host", other, Self, Account, true);
        Assert.Equal("unknown", (await r.Send(r.Probe(peerStatic: other))).Status);
    }

    [Fact]
    public async Task A_probe_after_the_replay_memory_expired_answers_unknown()
    {
        using var r = new Rig();
        Assert.Equal("delivered", (await r.Send(r.Request())).Status);
        r.Time.Advance(ReplayStore.Lifetime + TimeSpan.FromSeconds(1));
        Assert.Equal("unknown", (await r.Send(r.Probe())).Status);
    }

    [Fact]
    public async Task A_probe_keeps_every_gate_off_an_untrusted_channel_and_data_that_is_not_empty()
    {
        using var r = new Rig();
        Assert.Equal("delivered", (await r.Send(r.Request())).Status);
        Refused(await r.Send(r.Probe(text: "x")), LiteReceiver.Malformed);
        r.Channel = r.Channel with { InPresence = false };
        Refused(await r.Send(r.Probe()), LiteReceiver.NotSameAccount);
        r.Channel = r.Channel with { InPresence = true };
        r.Mode.On = false;
        Refused(await r.Send(r.Probe()), LiteReceiver.Off);
    }

    [Fact]
    public async Task Probes_of_one_request_are_rate_limited_without_spending_the_pairs_delivery_budget()
    {
        using var r = new Rig();
        Assert.Equal("delivered", (await r.Send(r.Request())).Status);
        for (var i = 0; i < RateCap.PerMinute; i++) { Assert.Equal("receipt", (await r.Send(r.Probe())).Status); }
        Refused(await r.Send(r.Probe()), LiteReceiver.Rate);
        Assert.Equal("delivered", (await r.Send(r.Request(id: Id("2")))).Status);
    }

    sealed class Throwing(Exception error) : ILiteDeliver
    {
        public Task<LiteDelivery> DeliverAsync(VerifiedEnvelope envelope, string text, CancellationToken ct) => throw error;
    }

    sealed class Waiting : ILiteDeliver
    {
        public Task<LiteDelivery> DeliverAsync(VerifiedEnvelope envelope, string text, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith<LiteDelivery>(_ => throw new InvalidOperationException(), ct);
    }

    [Fact]
    public async Task Another_session_on_the_same_host_cannot_read_a_requests_outcome()
    {
        using var r = new Rig();
        Assert.Equal("delivered", (await r.Send(r.Request())).Status);
        Assert.Equal("unknown", (await r.Send(r.Probe(session: "agent-c3"))).Status);
        Receipt(await r.Send(r.Probe()), "delivered", Id("1"));
    }

    [Fact]
    public async Task A_delivery_that_throws_is_journaled_unconfirmed_never_refused()
    {
        using var r = new Rig(custom: new Throwing(new InvalidOperationException("boom")));
        Refused(await r.Send(r.Request()), LiteReceiver.DeliveryFailed);
        Receipt(await r.Send(r.Probe()), "unconfirmed:" + LiteReceiver.DeliveryFailed, Id("1"));
    }

    [Fact]
    public async Task A_delivery_cancelled_part_way_is_journaled_unconfirmed()
    {
        using var r = new Rig(custom: new Waiting());
        using var cancel = new CancellationTokenSource();
        var pending = r.Receiver.ReceiveAsync(r.Channel, r.Request(), cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Receipt(await r.Send(r.Probe()), "unconfirmed:cancelled", Id("1"));
    }

    [Fact]
    public async Task A_tampered_probe_action_fails_its_signature_and_a_reply_is_never_taken_for_a_probe()
    {
        using var r = new Rig();
        Assert.Equal("delivered", (await r.Send(r.Request())).Status);
        var probe = r.Probe();
        Assert.True(Codec.TryBase64Url(probe.GetProperty("envelope").GetString()!, out var raw));
        var envelope = System.Text.Json.Nodes.JsonNode.Parse(raw)!.AsObject();
        Assert.True(Codec.TryBase64Url((string)envelope["body"]!, out var body));
        envelope["body"] = Codec.Base64Url(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Replace("session.read", "session.repla", StringComparison.Ordinal)));
        var tampered = JsonSerializer.SerializeToElement(new { envelope = Codec.Base64Url(Encoding.UTF8.GetBytes(envelope.ToJsonString())), text = "", killEpoch = 0, signPoint = probe.GetProperty("signPoint").GetString() });
        Assert.Equal("refused", (await r.Send(tampered)).Status);
        Assert.Equal(1, r.Deliver.Calls);
    }
}
