using System.Security.Cryptography;
using System.Text;
using AgentBridge.AgentEnvelope;
using AgentBridge.Host;
using AgentBridge.Host.Lite;
using AgentBridge.Transport;
using Xunit;

public partial class AgentLiteSenderTests
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
    sealed class Link : ILiteLink
    {
        public int Opened;
        public bool Present = true;
        public object? Request;
        public Func<object, CancellationToken, Task<LiteAck>> Reply = (_, _) => throw new InvalidOperationException();
        public string Self => AgentLiteSenderTests.Self;
        public string Account { get; set; } = AgentLiteSenderTests.Account;
        public List<PresenceEntry> Hosts = [new PresenceEntry(Other, "node-b", "host", DateTimeOffset.UnixEpoch)];
        public IReadOnlyList<PresenceEntry> Presence => Present ? Hosts : [];
        public string? Bound;
        public Task<LiteAck> SendAsync(string peerId, string account, object request, CancellationToken ct) { Opened++; Bound = account; Request = request; return Reply(request, ct); }
    }
    sealed class Rig : IDisposable
    {
        public readonly Mode Mode = new();
        public readonly Link Link = new();
        public readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public bool Live = true;
        public int Signed;
        public Deliver Deliver = new(new LiteDelivery(true, null));
        public (IEnvelopeSigner, byte[]) Signer { get { var q = Key.ExportParameters(false).Q; return (new EcdsaEnvelopeSigner(Key), [0x04, .. q.X!, .. q.Y!]); } }
        public AgentLiteSender Sender(TimeProvider? time = null) => new(Mode, Link, _ => Live, () => { Signed++; return Signer; }, OwnStatic, time, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(20));
        public Task<LiteOutcome> Send(string text = "hello", string machine = Other) => Sender().SendAsync(Caller, new LiteSendInput(machine, "agent-b2", text), default);
        public void Dispose() => Key.Dispose();
    }

    sealed class Deliver(LiteDelivery result) : ILiteDeliver
    {
        public string? Text;
        public VerifiedEnvelope? Envelope;
        public Task<LiteDelivery> DeliverAsync(VerifiedEnvelope envelope, string text, CancellationToken ct) { Envelope = envelope; Text = text; return Task.FromResult(result); }
    }

    [Fact]
    public async Task Script_credentials_arrive_as_scripts_without_leaking_credential_material()
    {
        using var r = new Rig();
        Receiving(r);
        const string canary = "abat1_CANARY_NOT_A_PRINCIPAL";
        var script = AgentCredential.Script(canary, Self, "pump-wake", DateTimeOffset.UtcNow);
        var result = await r.Sender().SendAsync(script, new LiteSendInput(Other, "agent-b2", "hello"), default);
        Assert.Equal(LiteKind.Delivered, result.Kind);
        var wire = Encoding.UTF8.GetString(r.Deliver.Envelope!.Body.Encode());
        Assert.Contains("\"kind\":\"script\"", wire);
        Assert.Contains($"\"identity\":\"script:{Self}/pump-wake\"", wire);
        Assert.DoesNotContain("\"agent\":", wire);
        Assert.DoesNotContain(canary, wire);
    }

    [Theory]
    [InlineData(true, true, LiteKind.Undeliverable)]
    [InlineData(false, true, LiteKind.Refused)]
    [InlineData(true, false, LiteKind.Refused)]
    public async Task Detached_script_sends_validate_the_stable_host_identity_and_live_grant_before_offline_fallback(
        bool ownMachine, bool live, LiteKind expected)
    {
        using var r = new Rig();
        r.Link.Account = "";
        r.Live = live;
        var script = AgentCredential.Script("fixture-credential", ownMachine ? Self : Other, "collector", DateTimeOffset.UtcNow);

        var result = await r.Sender().SendAsync(script, new LiteSendInput(Other, "agent-b2", "hello"), default);

        Assert.Equal(expected, result.Kind);
        Assert.Equal(expected == LiteKind.Undeliverable ? AgentLiteSender.OfflineText : AgentLiteSender.NoCredential, result.Code);
        Assert.Equal(0, r.Signed);
    }

    [Theory]
    [InlineData("session", "script:machine/pump")]
    [InlineData("script", "agent:machine/session")]
    [InlineData("script", "person:machine/person")]
    [InlineData("person", null)]
    public async Task Credential_kind_confusion_is_refused_before_signing(string kind, string? principal)
    {
        using var r = new Rig();
        var caller = Caller with { Kind = kind, Principal = principal };
        var result = await r.Sender().SendAsync(caller, new LiteSendInput(Other, "agent-b2", "hello"), default);
        Assert.Equal((LiteKind.Refused, AgentLiteSender.NoCredential), (result.Kind, result.Code));
        Assert.Equal(0, r.Signed);
        Assert.Equal(0, r.Link.Opened);
    }

    // Answers as the real receiver does, so the sealed envelope is checked end to end rather than assumed.
    static void Receiving(Rig r, LiteDelivery? result = null, string? target = null)
    {
        r.Deliver = new Deliver(result ?? new LiteDelivery(true, null));
        var receiver = new LiteReceiver(new Mode(), r.Deliver);
        var channel = new LiteChannel(Self, "host", OwnStatic, target ?? Other, Account, true);
        r.Link.Reply = (req, ct) => receiver.ReceiveAsync(channel, System.Text.Json.JsonSerializer.SerializeToElement(req), ct);
    }

    [Fact]
    public async Task A_renamed_host_is_reached_by_device_id_not_its_successors_reused_name()
    {
        using var r = new Rig();
        Receiving(r);
        r.Link.Hosts = [new(Other, "Renamed", "host", DateTimeOffset.UnixEpoch),
            new(new string('4', 32), "node-b", "host", DateTimeOffset.UnixEpoch)];
        Assert.Equal(LiteKind.Delivered, (await r.Send(machine: Other)).Kind);
        Assert.Equal(Other, r.Deliver.Envelope!.Body.Target.Machine);
        var refused = await r.Send(machine: "node-b");
        Assert.Equal(LiteKind.Refused, refused.Kind);
        Assert.Contains("node-b", refused.Code);
        Assert.Equal((1, 1), (r.Link.Opened, r.Signed));
    }

    [Theory, InlineData("node-b"), InlineData("node"), InlineData("NODE-B"), InlineData("33333333")]
    public async Task Names_and_fragments_are_refused_even_when_a_present_host_has_that_name(string machine)
    {
        using var r = new Rig();
        Receiving(r);
        r.Link.Hosts = [new(Other, machine, "host", DateTimeOffset.UnixEpoch)];
        var refused = await r.Send(machine: machine);
        Assert.Equal(LiteKind.Refused, refused.Kind);
        Assert.Contains("refused:", refused.Code);
        Assert.Contains(machine, refused.Code);
        Assert.Equal((0, 0), (r.Link.Opened, r.Signed));
    }

    [Fact]
    public async Task A_stale_device_id_is_a_loud_refusal_and_never_targets_a_name_reusing_successor()
    {
        using var r = new Rig();
        r.Link.Hosts = [new(new string('4', 32), Other, "host", DateTimeOffset.UnixEpoch)];
        var refused = await r.Send(machine: Other);
        Assert.Equal(LiteKind.Refused, refused.Kind);
        Assert.Contains("refused:", refused.Code);
        Assert.Contains(Other, refused.Code);
        Assert.Equal((0, 0), (r.Link.Opened, r.Signed));
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

    [Theory]
    [InlineData(LiteKind.Pending)]
    [InlineData(LiteKind.Queued)]
    [InlineData(LiteKind.Unconfirmed)]
    public async Task Relay_lane_states_survive_receiver_and_sender_without_becoming_delivery(LiteKind kind)
    {
        using var r = new Rig();
        Receiving(r, new LiteDelivery(false, "lane reason") { Outcome = kind });
        var result = await r.Send();
        Assert.Equal(kind, result.Kind);
        Assert.Equal("lane reason", result.Code);
        Assert.Equal(1, r.Link.Opened);
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
    public async Task A_credential_that_lapses_while_the_key_is_made_sends_nothing()
    {
        using var r = new Rig();
        var sender = new AgentLiteSender(r.Mode, r.Link, _ => r.Live, () => { r.Live = false; return r.Signer; }, OwnStatic);
        var o = await sender.SendAsync(Caller, new LiteSendInput(Other, "agent-b2", "hello"), default);
        Assert.Equal((LiteKind.Refused, AgentLiteSender.NoCredential), (o.Kind, o.Code));
        Assert.Equal(0, r.Link.Opened);
    }

    [Fact]
    public async Task Messaging_turned_off_while_the_key_is_made_sends_nothing_and_reports_off()
    {
        using var r = new Rig();
        Receiving(r);
        var sender = new AgentLiteSender(r.Mode, r.Link, _ => true, () => { r.Mode.On = false; return r.Signer; }, OwnStatic);
        var o = await sender.SendAsync(Caller, new LiteSendInput(Other, "agent-b2", "hello"), default);
        Assert.Equal((LiteKind.Off, "off"), (o.Kind, o.Code));
        Assert.Equal(0, r.Link.Opened);
        Assert.Null(r.Deliver.Text);
        // A key that could not be made because the mode went off reports off, not a missing key.
        r.Mode.On = true;
        o = await new AgentLiteSender(r.Mode, r.Link, _ => true, () => { r.Mode.On = false; return null; }, OwnStatic)
            .SendAsync(Caller, new LiteSendInput(Other, "agent-b2", "hello"), default);
        Assert.Equal((LiteKind.Off, "off"), (o.Kind, o.Code));
        Assert.Equal(0, r.Link.Opened);
    }

    [Fact]
    public async Task A_device_id_names_only_that_host_never_another_host_whose_name_looks_like_it()
    {
        using var r = new Rig();
        var wrongHost = new string('4', 32);
        Assert.True(Wire.IsDeviceId(Other));
        r.Link.Hosts = [new PresenceEntry(wrongHost, Other, "host", DateTimeOffset.UnixEpoch)];
        // The wrong host would accept: the envelope names it, so only the sender's choice keeps the text away.
        var receiver = new LiteReceiver(new Mode(), r.Deliver);
        var channel = new LiteChannel(Self, "host", OwnStatic, wrongHost, Account, true);
        r.Link.Reply = (req, ct) => receiver.ReceiveAsync(channel, System.Text.Json.JsonSerializer.SerializeToElement(req), ct);
        var o = await r.Send(machine: Other);
        Assert.Equal(LiteKind.Refused, o.Kind);
        Assert.Contains(Other, o.Code);
        Assert.Equal((0, 0), (r.Link.Opened, r.Signed));
        Assert.Null(r.Deliver.Text);
    }

    [Fact]
    public async Task A_sign_in_while_the_key_is_made_keeps_the_send_on_the_account_that_chose_the_target()
    {
        using var r = new Rig();
        Receiving(r);
        var sender = new AgentLiteSender(r.Mode, r.Link, _ => true, () => { r.Link.Account = "acct-other"; return r.Signer; }, OwnStatic);
        Assert.Equal(LiteKind.Delivered, (await sender.SendAsync(Caller, new LiteSendInput(Other, "agent-b2", "hello"), default)).Kind);
        Assert.Equal(Account, r.Link.Bound);
    }

    [Fact]
    public async Task A_machine_absent_from_presence_is_refused_by_id_and_says_nothing_was_queued()
    {
        using var r = new Rig();
        r.Link.Present = false;
        var o = await r.Send();
        Assert.Equal(LiteKind.Refused, o.Kind);
        Assert.Contains(Other, o.Code);
        Assert.Contains("nothing was queued", o.Code);
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
    public async Task Only_the_exact_present_host_is_a_target_despite_duplicate_names()
    {
        using var r = new Rig();
        r.Link.Hosts = [new PresenceEntry(Other, "node-b", "phone", DateTimeOffset.UnixEpoch), new PresenceEntry(Self, "node-b", "host", DateTimeOffset.UnixEpoch)];
        Assert.Equal(LiteKind.Refused, (await r.Send()).Kind);
        Assert.Equal((0, 0), (r.Link.Opened, r.Signed));
        r.Link.Hosts = [new PresenceEntry(Other, "node-b", "host", DateTimeOffset.UnixEpoch), new PresenceEntry(new string('4', 32), "NODE-B", "host", DateTimeOffset.UnixEpoch)];
        Receiving(r);
        var o = await r.Send();
        Assert.Equal(LiteKind.Delivered, o.Kind);
        Assert.Equal(Other, r.Deliver.Envelope!.Body.Target.Machine);
        r.Link.Account = "";
        Assert.Equal(LiteKind.Undeliverable, (await r.Send(machine: Other)).Kind);
        Assert.Equal((1, 1), (r.Link.Opened, r.Signed));
    }

    [Fact]
    public async Task The_own_host_device_id_resolves_locally_without_presence_and_names_never_route()
    {
        using var r = new Rig();
        Receiving(r, target: Self);
        r.Link.Present = false;
        Assert.Equal(LiteKind.Delivered, (await r.Send(machine: Self)).Kind);
        Assert.Equal(Self, r.Deliver.Envelope!.Body.Target.Machine);
        r.Link.Present = true;
        r.Link.Hosts = [new PresenceEntry(Self, "node-b", "host", DateTimeOffset.UnixEpoch)];
        r.Link.Hosts.Add(new PresenceEntry(Other, "node-b", "host", DateTimeOffset.UnixEpoch));
        var refused = await r.Send(machine: "node-b");
        Assert.Equal(LiteKind.Refused, refused.Kind);
        Assert.Contains("names never route", refused.Code);
        Assert.Equal((1, 1), (r.Link.Opened, r.Signed));
    }

    [Fact]
    public async Task The_host_signer_needs_no_pin_or_setup_step_and_makes_its_key_only_while_the_mode_is_on()
    {
        using var r = new Rig();
        Receiving(r);
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            r.Mode.On = false;
            Assert.Null(AgentLiteSender.HostSigner(dir, r.Mode)());
            Assert.False(File.Exists(AgentSignKey.PathOf(dir)));
            r.Mode.On = true;
            var sender = new AgentLiteSender(r.Mode, r.Link, _ => true, AgentLiteSender.HostSigner(dir, r.Mode), OwnStatic);
            Assert.Equal(LiteKind.Delivered, (await sender.SendAsync(Caller, new LiteSendInput(Other, "agent-b2", "hi"), default)).Kind);
            Assert.Equal(["agent-sign"], Directory.GetDirectories(dir).Select(Path.GetFileName));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task The_mode_watcher_has_finished_before_the_send_returns()
    {
        using var r = new Rig();
        var sender = new AgentLiteSender(r.Mode, r.Link, _ => true, () => r.Signer, OwnStatic, null, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(1));
        r.Link.Reply = async (req, _) =>
        {
            r.Mode.Block = true;
            await r.Mode.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return new LiteAck("delivered", "", "not-the-request", 0);
        };
        var send = sender.SendAsync(Caller, new LiteSendInput(Other, "agent-b2", "hi"), default);
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
        var sender = new AgentLiteSender(r.Mode, r.Link, _ => true, () => r.Signer, OwnStatic, null, TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(1));
        var o = await sender.SendAsync(Caller, new LiteSendInput(Other, "agent-b2", "hi"), default).WaitAsync(TimeSpan.FromSeconds(30));
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
