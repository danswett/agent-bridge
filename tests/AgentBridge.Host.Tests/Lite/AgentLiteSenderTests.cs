using System.Security.Cryptography;
using System.Text;
using AgentBridge.AgentEnvelope;
using AgentBridge.Host;
using AgentBridge.Host.Lite;
using AgentBridge.Transport;
using Xunit;

public class AgentLiteSenderTests
{
    const string Account = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    static readonly byte[] OwnStatic = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];
    static readonly string Self = Wire.DeviceId(OwnStatic);
    const string Other = "33333333333333333333333333333333";
    static readonly AgentCredential Caller = new("cred-1", "agent-a1", "launch", 1, DateTimeOffset.UnixEpoch, "s", 0, [], DateTimeOffset.UnixEpoch, DateTimeOffset.MaxValue);

    sealed class Mode : ILiteMode
    {
        bool on = true;
        public bool Block;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Gate = new();
        public bool On
        {
            get { if (Block) { Entered.TrySetResult(); Gate.Wait(); } return on; }
            set => on = value;
        }
        public long KillEpoch { get; set; }
        public void TurnOff(string why) { On = false; }
        public void RaiseEpoch(long epoch) { KillEpoch = epoch; if (epoch > 0) { On = false; } }
    }
    sealed class Peers(bool pinned, LitePeer? peer = null) : ILitePeers
    {
        readonly LitePeer p = peer ?? new LitePeer(Other, [], "vm2");
        public bool TryFind(string deviceId, out LitePeer found) { found = p; return pinned && deviceId == p.DeviceId; }
        public IReadOnlyList<LitePeer> All() => pinned ? [p] : [];
    }
    sealed class Link : ILiteLink
    {
        public int Opened;
        public bool Present = true;
        public object? Request;
        public Func<object, CancellationToken, Task<LiteAck>> Reply = (_, _) => throw new InvalidOperationException();
        public string Self => AgentLiteSenderTests.Self;
        public string Account => AgentLiteSenderTests.Account;
        public IReadOnlyList<PresenceEntry> Presence => Present ? [new PresenceEntry(Other, "vm2", "host", DateTimeOffset.UnixEpoch)] : [];
        public Task<LiteAck> SendAsync(string peerId, object request, CancellationToken ct) { Opened++; Request = request; return Reply(request, ct); }
    }
    sealed class Rig : IDisposable
    {
        public readonly Mode Mode = new();
        public readonly Link Link = new();
        public readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public bool Live = true;
        public int Signed;
        public bool Pinned = true;
        public Deliver Deliver = new(new LiteDelivery(true, null));
        public AgentLiteSender Sender() => new(Mode, new Peers(Pinned), Link, _ => Live, () => { Signed++; return new EcdsaEnvelopeSigner(Key); }, OwnStatic, null, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(20));
        public Task<LiteOutcome> Send(string text = "hello", string machine = "vm2") => Sender().SendAsync(Caller, new LiteSendInput(machine, "agent-b2", text), default);
        public void Dispose() => Key.Dispose();
    }

    sealed class Deliver(LiteDelivery result) : ILiteDeliver
    {
        public string? Text;
        public Task<LiteDelivery> DeliverAsync(VerifiedEnvelope envelope, string text, CancellationToken ct) { Text = text; return Task.FromResult(result); }
    }

    // Answers as the real receiver does, so the sealed envelope is checked end to end rather than assumed.
    static void Receiving(Rig r, LiteDelivery? result = null)
    {
        var q = r.Key.ExportParameters(false).Q;
        var peer = new LitePeer(Self, [0x04, .. q.X!, .. q.Y!], "vm1");
        r.Deliver = new Deliver(result ?? new LiteDelivery(true, null));
        var receiver = new LiteReceiver(new Mode(), new Peers(true, peer), r.Deliver);
        var channel = new LiteChannel(Self, "host", OwnStatic, Other, Account, true);
        r.Link.Reply = (req, ct) => receiver.ReceiveAsync(channel, System.Text.Json.JsonSerializer.SerializeToElement(req), ct);
    }

    [Fact]
    public async Task Mode_off_returns_off_and_opens_no_channel()
    {
        using var r = new Rig();
        r.Mode.On = false;
        var o = await r.Send();
        Assert.Equal(LiteKind.Off, o.Kind);
        Assert.Equal(0, r.Link.Opened);
        Assert.Equal(0, r.Signed);
    }

    [Fact]
    public async Task A_credential_that_no_longer_holds_is_refused_before_anything_is_signed()
    {
        using var r = new Rig { Live = false };
        var o = await r.Send();
        Assert.Equal((LiteKind.Refused, AgentLiteSender.NoCredential), (o.Kind, o.Code));
        Assert.Equal(0, r.Signed);
        Assert.Equal(0, r.Link.Opened);
    }

    [Fact]
    public async Task A_machine_absent_from_presence_is_undeliverable_and_says_nothing_was_queued()
    {
        using var r = new Rig();
        r.Link.Present = false;
        var o = await r.Send();
        Assert.Equal((LiteKind.Undeliverable, "that machine is offline; nothing was queued"), (o.Kind, o.Code));
        Assert.Equal(0, r.Link.Opened);
    }

    [Fact]
    public async Task A_relay_undeliverable_frame_is_undeliverable()
    {
        using var r = new Rig();
        r.Link.Reply = (_, _) => throw new LitePeerOfflineException("gone");
        var o = await r.Send();
        Assert.Equal((LiteKind.Undeliverable, AgentLiteSender.OfflineText), (o.Kind, o.Code));
    }

    [Fact]
    public async Task A_machine_that_is_not_pinned_is_refused_not_peer()
    {
        using var r = new Rig { Pinned = false };
        var o = await r.Send();
        Assert.Equal((LiteKind.Refused, "notPeer"), (o.Kind, o.Code));
        Assert.Equal(0, r.Link.Opened);
    }

    [Fact]
    public async Task The_pinned_own_peer_list_on_disk_is_what_decides_who_may_be_sent_to()
    {
        using var r = new Rig();
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var peers = new OwnLitePeers(new OwnPeers(dir, () => true, () => Account));
            var sender = new AgentLiteSender(r.Mode, peers, r.Link, _ => true, () => { r.Signed++; return new EcdsaEnvelopeSigner(r.Key); }, OwnStatic);
            var o = await sender.SendAsync(Caller, new LiteSendInput("vm2", "agent-b2", "hi"), default);
            Assert.Equal((LiteKind.Refused, "notPeer"), (o.Kind, o.Code));
            Assert.Equal((0, 0), (r.Link.Opened, r.Signed));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task The_mode_watcher_has_finished_before_the_send_returns()
    {
        using var r = new Rig();
        var sender = new AgentLiteSender(r.Mode, new Peers(true), r.Link, _ => true, () => new EcdsaEnvelopeSigner(r.Key), OwnStatic, null, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(1));
        r.Link.Reply = async (req, _) =>
        {
            r.Mode.Block = true;
            await r.Mode.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return new LiteAck("delivered", "", "not-the-request", 0);
        };
        var send = sender.SendAsync(Caller, new LiteSendInput("vm2", "agent-b2", "hi"), default);
        await Task.Delay(300);
        Assert.False(send.IsCompleted);
        r.Mode.Gate.Set();
        Assert.Equal(LiteKind.Unconfirmed, (await send.WaitAsync(TimeSpan.FromSeconds(10))).Kind);
    }

    [Fact]
    public async Task No_acknowledgement_in_time_is_unconfirmed_never_delivered()
    {
        using var r = new Rig();
        r.Link.Reply = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; };
        var o = await r.Send();
        Assert.Equal((LiteKind.Unconfirmed, "timeout"), (o.Kind, o.Code));
    }

    [Fact]
    public async Task A_mode_switched_off_while_waiting_is_unconfirmed_with_that_reason()
    {
        using var r = new Rig();
        r.Link.Reply = async (_, ct) => { r.Mode.On = false; await Task.Delay(Timeout.Infinite, ct); return null!; };
        // The ack limit is far longer than the watcher's tick, so a loaded runner cannot let the timeout beat the
        // mode watcher (#1521: with a 200 ms limit it sometimes reported "timeout"). Only the watcher can end this send.
        var sender = new AgentLiteSender(r.Mode, new Peers(true), r.Link, _ => true, () => new EcdsaEnvelopeSigner(r.Key), OwnStatic, null, TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(1));
        var o = await sender.SendAsync(Caller, new LiteSendInput("vm2", "agent-b2", "hi"), default).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal((LiteKind.Unconfirmed, "mode switched off"), (o.Kind, o.Code));
    }

    [Fact]
    public async Task A_delivered_acknowledgement_is_delivered_and_the_receiver_checks_the_exact_text()
    {
        using var r = new Rig();
        Receiving(r);
        var o = await r.Send("héllo wörld");
        Assert.Equal(LiteKind.Delivered, o.Kind);
        Assert.Equal("héllo wörld", r.Deliver.Text);
        Assert.True(Guid.TryParseExact(o.Request, "D", out _));
    }

    [Fact]
    public async Task A_refusal_carries_the_receivers_code()
    {
        using var r = new Rig();
        Receiving(r, new LiteDelivery(false, "composerNotEmpty"));
        var o = await r.Send();
        Assert.Equal((LiteKind.Refused, "composerNotEmpty"), (o.Kind, o.Code));
    }

    [Fact]
    public async Task Text_over_the_protocol_maximum_is_refused_before_signing()
    {
        using var r = new Rig();
        var o = await r.Send(new string('x', LiteReceiver.MaxText + 1));
        Assert.Equal((LiteKind.Refused, "malformed"), (o.Kind, o.Code));
        Assert.Equal(0, r.Signed);
    }

    [Fact]
    public async Task Each_attempt_is_a_new_message_with_a_fresh_request_id()
    {
        using var r = new Rig();
        Receiving(r);
        var a = await r.Send();
        var b = await r.Send();
        Assert.NotEqual(a.Request, b.Request);
        Assert.Equal(2, r.Link.Opened);
    }
}
