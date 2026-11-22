using System.Security.Cryptography;
using System.Text;
using AgentBridge.AgentEnvelope;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Lite;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Codec = AgentBridge.Roster.Codec;
using Xunit;

public sealed class SameHostLiteTests
{
    const string Account = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string Target = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    static readonly byte[] StaticKey = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];
    static readonly string Self = Wire.DeviceId(StaticKey);
    static readonly AgentCredential Caller = new("cred-1", "sender-session", "launch", 1, DateTimeOffset.UnixEpoch, "s", 0, [], DateTimeOffset.UnixEpoch, DateTimeOffset.MaxValue);
    static readonly string[] EmptyBox = ["\u25CF Done.", "", "\u257B\u2584\u2584\u2584\u2584\u2584\u2584\u2584\u2584", "\u2503", "\u2579\u2580\u2580\u2580\u2580\u2580\u2580\u2580\u2580", " \u2190 open sidebar \u00B7 Interactive \u00B7 Allow All \u00B7 / commands \u00B7 ? help \u00B7 tab next tab"];

    sealed class Mode : ILiteMode
    {
        public bool On { get; set; } = true;
        public long KillEpoch { get; private set; }
        public void TurnOff(string why) => On = false;
        public void RaiseEpoch(long epoch) { KillEpoch = epoch; On = false; }
    }

    sealed class TargetSession : ISessionSource, ISessionReplier, IConsoleInjector, IReplyTargetGate
    {
        public SessionStatus Status = SessionStatus.Idle;
        public bool Allowed = true;
        public bool Available => true;
        public List<ReplyRequest> Replies = [];
        public Task<GateDecision> CheckAsync(VerifiedEnvelope envelope, string target) =>
            Task.FromResult(Allowed && target == Target ? GateDecision.Allow() : GateDecision.Refuse("not launched"));
        public Session? Get(string id) => id == Target ? new Session
        {
            Id = Target, Agent = AgentKind.Copilot, Status = Status, ProcessId = 42,
            StatusSince = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch, Activity = new Activity()
        } : null;
        public IReadOnlyList<Session> Snapshot() => [Get(Target)!];
        public long Revision => 0;
        public DiscoveryHeld? Held => null;
        public SessionList List(string? cursor = null) => new([], 0);
        public event EventHandler<SessionChange>? Changed { add { } remove { } }
        public SessionSubscription Subscribe() => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<ReplyResult> ReplyAsync(ReplyRequest request, string? peer = null, CancellationToken ct = default, DeliveryWatch? watch = null)
        {
            Replies.Add(request);
            return Task.FromResult(new ReplyResult(AnswerOutcome.Delivered, ""));
        }
        public Task<ConsoleScreen> ReadScreenAsync(int processId, CancellationToken ct = default) =>
            Task.FromResult(new ConsoleScreen(EmptyBox, null));
        public Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken ct = default) =>
            throw new InvalidOperationException("unit tests never inject");
    }

    sealed class Rig : IDisposable
    {
        public readonly TargetSession TargetSession = new();
        public readonly Mode Mode = new();
        public readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public bool Live = true;
        public string AccountId = Account;
        LiteReceiver? receiver;
        public LiteReceiver Receiver()
        {
            var delivery = new LiteDeliver([TargetSession], TargetSession, TargetSession, TargetSession, _ => { });
            return receiver ??= new LiteReceiver(Mode, delivery);
        }
        public Task<LiteOutcome> Send(string? machine = null)
        {
            var point = Key.ExportParameters(false).Q;
            var sender = new AgentLiteSender(Mode, new RelayLiteLink(Receiver(), StaticKey, () => AccountId), _ => Live,
                () => (new EcdsaEnvelopeSigner(Key), [0x04, .. point.X!, .. point.Y!]), StaticKey);
            return sender.SendAsync(Caller, new LiteSendInput(machine ?? Self, Target, "hello locally"), default);
        }
        public void Dispose() => Key.Dispose();
    }

    [Fact]
    public async Task An_own_device_target_delivers_to_the_local_session()
    {
        using var r = new Rig();
        Assert.Equal(LiteKind.Delivered, (await r.Send()).Kind);
        var reply = Assert.Single(r.TargetSession.Replies);
        Assert.Equal(Target, reply.Id);
        Assert.Contains("hello locally", reply.Text);
        Assert.True(reply.RequireIdle);
    }

    [Fact]
    public async Task A_busy_own_device_target_returns_busy_not_offline_and_a_fresh_retry_can_deliver()
    {
        using var r = new Rig();
        r.TargetSession.Status = SessionStatus.Working;
        var refused = await r.Send();
        Assert.Equal((LiteKind.Refused, LiteCodes.Busy), (refused.Kind, refused.Code));
        Assert.Empty(r.TargetSession.Replies);
        r.TargetSession.Status = SessionStatus.Idle;
        var retry = await r.Send();
        Assert.Equal(LiteKind.Delivered, retry.Kind);
        Assert.NotEqual(refused.Request, retry.Request);
        Assert.Single(r.TargetSession.Replies);
    }

    [Fact]
    public async Task An_unknown_host_is_refused_by_id_and_never_delivers_locally()
    {
        using var r = new Rig();
        var result = await r.Send(new string('3', 32));
        Assert.Equal(LiteKind.Refused, result.Kind);
        Assert.Contains("noSuchMachine", result.Code);
        Assert.Contains(new string('3', 32), result.Code);
        Assert.Empty(r.TargetSession.Replies);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("credential")]
    [InlineData("account")]
    [InlineData("target")]
    public async Task Local_delivery_retains_mode_credential_account_and_target_gates(string gate)
    {
        using var r = new Rig();
        if (gate == "off") { r.Mode.On = false; }
        if (gate == "credential") { r.Live = false; }
        if (gate == "account") { r.AccountId = ""; }
        if (gate == "target") { r.TargetSession.Allowed = false; }
        Assert.NotEqual(LiteKind.Delivered, (await r.Send()).Kind);
        Assert.Empty(r.TargetSession.Replies);
    }

    [Fact]
    public async Task The_local_link_rejects_unsigned_requests_through_the_real_receiver()
    {
        using var r = new Rig();
        var link = new RelayLiteLink(r.Receiver(), StaticKey, () => Account);
        var ack = await link.SendAsync(Self, Account, new { text = "unsigned" }, default);
        Assert.Equal(("refused", LiteReceiver.Malformed), (ack.Status, ack.Code));
        Assert.Empty(r.TargetSession.Replies);
    }

    [Fact]
    public async Task Signed_loopback_rejects_text_tampering_and_replays()
    {
        using var r = new Rig();
        var link = new RelayLiteLink(r.Receiver(), StaticKey, () => Account);
        var from = new LiteSender(Account, Self, Codec.Base64Url(SHA256.HashData(StaticKey)), "sender-session",
            Codec.Base64Url(new byte[12]), Codec.Base64Url(new byte[16]), "agent", "agent-bridge", "agent", null);
        var envelope = LiteProfile.Seal(from, new LiteTarget(Self, Target, Codec.Base64Url(new byte[12])),
            Guid.NewGuid().ToString("D"), Encoding.UTF8.GetBytes("signed"), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), new EcdsaEnvelopeSigner(r.Key));
        var q = r.Key.ExportParameters(false).Q;
        var signPoint = Codec.Base64Url([0x04, .. q.X!, .. q.Y!]);
        object Request(string text) => new { envelope = Codec.Base64Url(envelope), text, killEpoch = 0, signPoint };
        Assert.Equal("refused", (await link.SendAsync(Self, Account, Request("tampered"), default)).Status);
        Assert.Empty(r.TargetSession.Replies);
        Assert.Equal("delivered", (await link.SendAsync(Self, Account, Request("signed"), default)).Status);
        Assert.Equal(LiteReceiver.Replay, (await link.SendAsync(Self, Account, Request("signed"), default)).Code);
        Assert.Single(r.TargetSession.Replies);
    }

    [Fact]
    public async Task An_account_change_after_signing_never_delivers_locally()
    {
        using var r = new Rig();
        var link = new RelayLiteLink(r.Receiver(), StaticKey, () => r.AccountId);
        var q = r.Key.ExportParameters(false).Q;
        var sender = new AgentLiteSender(r.Mode, link, _ => true,
            () => { r.AccountId = new string('c', 32); return (new EcdsaEnvelopeSigner(r.Key), [0x04, .. q.X!, .. q.Y!]); }, StaticKey);
        Assert.NotEqual(LiteKind.Delivered, (await sender.SendAsync(Caller, new LiteSendInput(Self, Target, "hello"), default)).Kind);
        Assert.Empty(r.TargetSession.Replies);
    }
}
