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
        public JsonElement Request(string text = "hello", string? id = null, string? machine = null, string? add = null, string? target = null, string? signedText = null, byte[]? point = null, long killEpoch = 0)
        {
            var sender = new LiteSender(Account, machine ?? Peer, add ?? Codec.Base64Url(SHA256.HashData(PeerStatic)), "agent-a1",
                Codec.Base64Url(new byte[12]), Codec.Base64Url(new byte[16]), "copilot", "agent-bridge");
            var sealedBytes = LiteProfile.Seal(sender, new LiteTarget(target ?? Self, "agent-b2", Codec.Base64Url(new byte[12])), id ?? Id("1"),
                Encoding.UTF8.GetBytes(signedText ?? text), Time.GetUtcNow().ToUnixTimeSeconds(), new EcdsaEnvelopeSigner(Key));
            return JsonSerializer.SerializeToElement(new { envelope = Codec.Base64Url(sealedBytes), text, killEpoch, signPoint = Codec.Base64Url(point ?? Point) });
        }

        public Task<LiteAck> Send(JsonElement data) => Receiver.ReceiveAsync(Channel, data, default);
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
    // #1117: the budget counts deliveries that may have typed. A refusal that typed nothing (busy, notReplyTarget, composerNotEmpty, ...) gives it back,
    // so busy retries cannot lock a pair out with `rate`.
    sealed class Scripted(Func<int, LiteDelivery> next) : ILiteDeliver
    {
        public int Calls;
        public Task<LiteDelivery> DeliverAsync(VerifiedEnvelope envelope, string text, CancellationToken ct) => Task.FromResult(next(Calls++));
    }
    sealed class Throwing : ILiteDeliver
    {
        public Task<LiteDelivery> DeliverAsync(VerifiedEnvelope envelope, string text, CancellationToken ct) => throw new InvalidOperationException("boom");
    }
    [Theory]
    [InlineData("busy")]
    [InlineData("notReplyTarget")]
    [InlineData("composerNotEmpty")]
    public async Task Refusals_that_typed_nothing_never_spend_the_pairs_rate_budget(string code)
    {
        using var r = new Rig(new LiteDelivery(false, code));
        for (var i = 1; i <= RateCap.PerMinute * 3; i++) { Refused(await r.Send(r.Request(id: Id(i.ToString()))), code); }
    }
    [Fact]
    public async Task Busy_refusals_between_deliveries_leave_the_full_budget_for_the_deliveries()
    {
        var deliver = new Scripted(n => n % 2 == 0 ? new LiteDelivery(false, "busy") : new LiteDelivery(true, null));
        using var r = new Rig(custom: deliver);
        var delivered = 0;
        for (var i = 1; i <= RateCap.PerMinute * 2; i++) { var ack = await r.Send(r.Request(id: Id(i.ToString()))); if (ack.Status == "delivered") { delivered++; } else { Refused(ack, "busy"); } }
        Assert.Equal(RateCap.PerMinute, delivered);
        Refused(await r.Send(r.Request(id: Id("99"))), LiteReceiver.Rate); // six deliveries really did fill the minute
        r.Time.Advance(TimeSpan.FromSeconds(61));
        Refused(await r.Send(r.Request(id: Id("100"))), "busy"); // reached and refused by delivery, then refunded
        Assert.Equal("delivered", (await r.Send(r.Request(id: Id("101")))).Status);
    }
    [Theory]
    [InlineData(LiteKind.Unconfirmed)]
    [InlineData(LiteKind.Queued)]
    [InlineData(LiteKind.Pending)]
    public async Task A_delivery_that_may_have_typed_or_was_accepted_keeps_the_budget_spent(LiteKind kind)
    {
        using var r = new Rig(new LiteDelivery(false, null) { Outcome = kind });
        for (var i = 1; i <= RateCap.PerMinute; i++) { Assert.Equal(kind.ToString().ToLowerInvariant(), (await r.Send(r.Request(id: Id(i.ToString())))).Status); }
        Refused(await r.Send(r.Request(id: Id("7"))), LiteReceiver.Rate);
    }
    [Fact]
    public async Task A_delivery_that_throws_may_have_typed_so_it_keeps_the_budget_spent()
    {
        using var r = new Rig(custom: new Throwing());
        for (var i = 1; i <= RateCap.PerMinute; i++) { Refused(await r.Send(r.Request(id: Id(i.ToString()))), LiteReceiver.DeliveryFailed); }
        Refused(await r.Send(r.Request(id: Id("7"))), LiteReceiver.Rate);
    }
    sealed class RefusingQuery : ILiteDeliver, ILiteReplayQuery
    {
        public Task<LiteDelivery> DeliverAsync(VerifiedEnvelope envelope, string text, CancellationToken ct) => Task.FromResult(new LiteDelivery(true, null));
        public Task<LiteDelivery> QueryAsync(VerifiedEnvelope envelope, string text, CancellationToken ct) => Task.FromResult(new LiteDelivery(false, "busy"));
    }
    [Fact]
    public async Task A_replay_query_never_spends_and_never_refunds_even_when_it_is_refused()
    {
        using var r = new Rig(custom: new RefusingQuery());
        var first = r.Request(id: Id("1"));
        Assert.Equal("delivered", (await r.Send(first)).Status);             // one hit spent
        for (var i = 0; i < RateCap.PerMinute; i++) { Refused(await r.Send(first), "busy"); }   // replays: refused, and they must not refund that hit
        for (var i = 2; i <= RateCap.PerMinute; i++) { Assert.Equal("delivered", (await r.Send(r.Request(id: Id(i.ToString())))).Status); }
        Refused(await r.Send(r.Request(id: Id("7"))), LiteReceiver.Rate);    // six deliveries in the minute: the seventh is limited
    }
    [Fact]
    public async Task A_result_that_did_not_type_and_carries_no_code_stays_spent()
    {
        using var r = new Rig(new LiteDelivery(false, null));
        for (var i = 1; i <= RateCap.PerMinute; i++) { Refused(await r.Send(r.Request(id: Id(i.ToString()))), LiteReceiver.DeliveryFailed); }
        Refused(await r.Send(r.Request(id: Id("7"))), LiteReceiver.Rate);
    }    [Fact]
    public void A_refund_gives_back_exactly_the_hit_it_was_issued_for_and_a_second_or_stale_refund_is_a_no_op()
    {
        var time = new FakeTimeProvider(Start); var cap = new RateCap(time);
        Assert.True(cap.TryAllow("peer", "a", out var first));
        for (var i = 1; i < RateCap.PerMinute; i++) { Assert.True(cap.TryAllow("peer", "a", out _)); }
        Assert.False(cap.TryAllow("peer", "a", out _));
        cap.Refund("peer", "a", first);
        Assert.True(cap.TryAllow("peer", "a", out _)); Assert.False(cap.TryAllow("peer", "a", out _));
        cap.Refund("peer", "a", first); // already given back: nothing more
        Assert.False(cap.TryAllow("peer", "a", out _));
        cap.Refund("peer", "never-seen", 12345); // an unknown pair or token is ignored
        Assert.True(cap.TryAllow("peer", "never-seen", out _));
    }
    [Fact]
    public void A_refund_removes_the_refused_requests_own_old_hit_not_a_newer_typed_ones_so_the_window_still_holds()
    {
        // R1 starts and is still in flight; five others are accepted and typed 30 s later; R1 is then refused busy and refunded.
        // Its own hit (t=0) must go. Dropping the newest hit instead leaves t=0 behind, it expires first, and a 7th typed send gets in.
        var time = new FakeTimeProvider(Start); var cap = new RateCap(time);
        Assert.True(cap.TryAllow("peer", "a", out var r1));
        time.Advance(TimeSpan.FromSeconds(30));
        for (var i = 0; i < 5; i++) { Assert.True(cap.TryAllow("peer", "a", out _)); }
        time.Advance(TimeSpan.FromSeconds(1));
        cap.Refund("peer", "a", r1);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(cap.TryAllow("peer", "a", out _));   // the sixth typed send in the minute
        time.Advance(TimeSpan.FromSeconds(29));          // t=61: R1's own old hit would have expired by now
        Assert.False(cap.TryAllow("peer", "a", out _));  // six typed sends in the last minute: the seventh is refused
    }
    [Fact]
    public void A_refund_after_its_hit_was_pruned_is_a_no_op_and_takes_nothing_else()
    {
        var time = new FakeTimeProvider(Start); var cap = new RateCap(time);
        Assert.True(cap.TryAllow("peer", "a", out var old));
        time.Advance(TimeSpan.FromHours(2));
        for (var i = 0; i < RateCap.PerMinute; i++) { Assert.True(cap.TryAllow("peer", "a", out _)); }
        cap.Refund("peer", "a", old);
        Assert.False(cap.TryAllow("peer", "a", out _));
    }
    sealed class Gated(Func<int, Task<LiteDelivery>> next) : ILiteDeliver
    {
        public int Calls;
        public Task<LiteDelivery> DeliverAsync(VerifiedEnvelope envelope, string text, CancellationToken ct) => next(Calls++);
    }
    [Fact]
    public async Task An_in_flight_refusal_gives_back_its_own_old_hit_so_the_seventh_typed_send_in_a_minute_is_still_rate_limited()
    {
        var gate = new TaskCompletionSource<LiteDelivery>();
        using var r = new Rig(custom: new Gated(n => n == 0 ? gate.Task : Task.FromResult(new LiteDelivery(true, null))));
        var first = r.Send(r.Request(id: Id("1")));                 // in flight, holding the t=0 hit
        r.Time.Advance(TimeSpan.FromSeconds(30));
        for (var i = 2; i <= 6; i++) { Assert.Equal("delivered", (await r.Send(r.Request(id: Id(i.ToString())))).Status); }
        r.Time.Advance(TimeSpan.FromSeconds(1));
        gate.SetResult(new LiteDelivery(false, "busy"));
        Refused(await first, "busy");
        r.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("delivered", (await r.Send(r.Request(id: Id("7")))).Status);   // sixth typed in the minute
        r.Time.Advance(TimeSpan.FromSeconds(29));                                    // t=61
        Refused(await r.Send(r.Request(id: Id("8"))), LiteReceiver.Rate);            // the seventh is refused
    }    [Fact]
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