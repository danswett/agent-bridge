using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.AgentEnvelope;
using AgentBridge.HomeAssistant;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Fleet;
using AgentBridge.Host.Lite;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class AgentLiteDeliveryTests
{
    const string Target = "11111111-1111-1111-1111-111111111111", Req = "22222222-2222-2222-2222-222222222222";
    static readonly string[] EmptyBox = ["\u25CF Done.", "", "\u257B\u2584\u2584\u2584\u2584\u2584\u2584\u2584\u2584", "\u2503", "\u2579\u2580\u2580\u2580\u2580\u2580\u2580\u2580\u2580", " \u2190 open sidebar \u00B7 Interactive \u00B7 Allow All \u00B7 / commands \u00B7 ? help \u00B7 tab next tab"];

    sealed class Fixture : ISessionSource, ISessionReplier, IConsoleInjector
    {
        public SessionStatus Status = SessionStatus.Idle;
        public bool Exists = true, FlipAfterFirstGet, AllowAllTargets, Collision;
        public Action? OnRead;
        public string[] Rows = EmptyBox;
        public int Gets;
        public AnswerOutcome Outcome = AnswerOutcome.Delivered;
        public ReplyRefusal Refusal;
        public readonly TaskCompletionSource ReplyScreenRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<KeyPlan>? OnInject;
        public readonly List<KeyPlan> Plans = [];
        int reads;
        public readonly List<ReplyRequest> Typed = []; public readonly List<string> Log = [];
        public readonly FakeTimeProvider Clock = new(); public readonly LiteLaunchLog Launches = new();
        public LiteDeliver Build() => new([new ForcedAskGate(Launches, () => AllowAllTargets)], this, this, this, Log.Add, Clock);
        public void Launch(string permissions) => Launches.Launched(Target, new LaunchedWith { Agent = "copilot", Permissions = permissions });
        public long Revision => 0;
        public IReadOnlyList<Session> Snapshot() => Get(Target) is not { } session ? [] : Collision
            ? [session, session with { Id = "11111111-1111-1111-2222-222222222222" }] : [session];
        public SessionList List(string? cursor = null) => new(Snapshot(), 0);
        public event EventHandler<SessionChange>? Changed { add { } remove { } }
        public Session? Get(string id)
        {
            if (FlipAfterFirstGet && ++Gets >= 2) { Status = SessionStatus.Working; }
            return !Exists || id != Target ? null : new Session { Id = id, Agent = AgentKind.Copilot, Status = Status, StatusSince = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch, ProcessId = 42, Activity = new Activity() };
        }
        public SessionSubscription Subscribe() => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask; public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask; public bool Available => true;
        public Task<ReplyResult> ReplyAsync(ReplyRequest request, string? peer = null, CancellationToken ct = default, DeliveryWatch? watch = null)
        { Typed.Add(request); return Task.FromResult(new ReplyResult(Outcome, "reason") { Refusal = Refusal }); }
        public Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken ct = default)
        {
            Plans.Add(plan);
            OnInject?.Invoke(plan);
            return Task.FromResult(new InjectionResult(InjectionStatus.Typed, ""));
        }
        public Task<ConsoleScreen> ReadScreenAsync(int processId, CancellationToken ct = default)
        {
            OnRead?.Invoke();
            if (Interlocked.Increment(ref reads) == 2) { ReplyScreenRead.TrySetResult(); }
            return Task.FromResult(new ConsoleScreen(Rows, null));
        }
    }
    static VerifiedEnvelope Envelope(string text, string request = Req)
    {
        var sha = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var body = new EnvelopeBody(request, "acct", "g", [], new AgentPrincipal("SENDER", "add", "sess-9", "inst", "cred", "copilot", "launcher"),
            new AgentChain("root", 0, null, 0, 1, []), new AgentProvenance(null, default, null), "message", new AgentTarget("acct", "HOST", Target, null), sha, 0, 0);
        return new VerifiedEnvelope(body, []);
    }

    static Task<LiteDelivery> Send(Fixture f, string text = "hi") => f.Build().DeliverAsync(Envelope(text), text, default);

    [Theory]
    [InlineData("idle", "delivered", null)]
    [InlineData("busy", "refused", LiteCodes.Busy)]
    [InlineData("unlaunched", "refused", LiteCodes.NotReplyTarget)]
    [InlineData("composer", "refused", LiteCodes.ComposerNotEmpty)]
    [InlineData("collision", "refused", LiteCodes.NoSuchSession)]
    [InlineData("late-collision", "refused", LiteCodes.NoSuchSession)]
    [InlineData("gone", "refused", LiteCodes.NoSuchSession)]
    public async Task A_script_sends_a_fleet_list_node_through_the_signed_path_to_only_one_live_native_session(
        string state, string outcome, string? code)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var home = new global::Fixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] ownStatic = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];
        var self = Wire.DeviceId(ownStatic);
        const string other = "33333333333333333333333333333333", account = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var credentials = new AgentCredentials(home.Root, self);
        var script = credentials.MintScript("collector").Credential;
        var channel = new ScriptChannel();
        credentials.Accept(channel, script);
        var audit = new AgentAuditLog(home.Root);
        var f = new Fixture();
        f.Launch(LaunchProtocol.Ask);
        JsonElement record = default;
        await new FleetPublisher(f, other).PublishAsync((value, _) => { record = value; return Task.CompletedTask; });
        var frames = new[] { JsonSerializer.SerializeToElement(new { machine = other, online = true, record }) };
        var fleet = new FleetList(credentials, new ScriptRoster(RelayFleetRoster.Parse(frames)));
        var q = key.ExportParameters(false).Q;
        var receiver = new LiteReceiver(new ScriptMode(), new ScriptPeers(new(self, [0x04, .. q.X!, .. q.Y!], "sender")), f.Build());
        var link = new ScriptLink(self, account, other, ownStatic, receiver);
        var sender = new AgentLiteSender(new ScriptMode(), new ScriptPeers(new(other, [], "receiver")), link,
            credentials.IsLive, () => new EcdsaEnvelopeSigner(key), ownStatic);
        var handler = LocalApi.Handler(null!, null!, new FixedCallerGuard(new(CallerKind.Other, "fixture")),
            credentials: credentials, lite: sender, fleet: fleet, audit: audit);
        var listing = JsonSerializer.SerializeToElement(await handler(channel, FleetList.Op, JsonSerializer.SerializeToElement(new { })), ProtocolJson.Options);
        var target = Assert.Single(listing.GetProperty("sessions").EnumerateArray());
        Assert.Equal(HaContract.NodeId(Target), target.GetProperty("node").GetString());
        if (state == "busy") { f.Status = SessionStatus.Working; }
        if (state == "unlaunched") { f.Launch(LaunchProtocol.AllowAll); }
        if (state == "composer") { f.Rows = [.. EmptyBox.Take(3), "\u2503 unfinished", .. EmptyBox.Skip(4)]; }
        if (state == "collision") { f.Collision = true; }
        if (state == "late-collision") { f.OnRead = () => f.Collision = true; }
        if (state == "gone") { f.Exists = false; }

        var result = Assert.IsType<LiteSendReply>(await handler(channel, LiteAgentSend.Op, JsonSerializer.SerializeToElement(new
        {
            machine = target.GetProperty("machine").GetString(), session = target.GetProperty("node").GetString(), text = "hello",
        })));

        Assert.Equal((outcome, code), (result.Outcome, result.Code));
        if (outcome == "delivered")
        {
            var typed = Assert.Single(f.Typed);
            Assert.Equal(Target, typed.Id);
            Assert.True(typed.RequireIdle);
            Assert.Contains($"from script {script.Principal}", typed.Text);
        }
        else { Assert.Empty(f.Typed); }
    }

    [Fact]
    public async Task A_live_script_send_with_a_detached_relay_is_audited_as_undeliverable_without_signing()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var home = new global::Fixture();
        byte[] ownStatic = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];
        var credentials = new AgentCredentials(home.Root, Wire.DeviceId(ownStatic));
        var script = credentials.MintScript("collector").Credential;
        var channel = new ScriptChannel();
        credentials.Accept(channel, script);
        var audit = new AgentAuditLog(home.Root);
        var signed = 0;
        var sender = new AgentLiteSender(new ScriptMode(),
            new ScriptPeers(new("33333333333333333333333333333333", [], "receiver")), new RelayLiteLink(),
            credentials.IsLive, () => { signed++; throw new InvalidOperationException("detached sends must not sign"); }, ownStatic);
        var handler = LocalApi.Handler(null!, null!, new FixedCallerGuard(new(CallerKind.Other, "fixture")),
            credentials: credentials, lite: sender, audit: audit);

        var result = Assert.IsType<LiteSendReply>(await handler(channel, LiteAgentSend.Op, JsonSerializer.SerializeToElement(new
        {
            machine = "receiver", session = HaContract.NodeId(Target), text = "hello",
        })));

        Assert.Equal(("undeliverable", AgentLiteSender.OfflineText), (result.Outcome, result.Code));
        Assert.Equal(0, signed);
        var entries = Directory.GetFiles(Path.Combine(home.Root, AgentAuditLog.FolderName), "*.jsonl")
            .SelectMany(File.ReadAllLines).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(row => row.TryGetProperty("action", out _)).ToArray();
        Assert.Equal(new[] { "attempted", "undeliverable" }, entries.Select(row => row.GetProperty("outcome").GetString()));
        Assert.All(entries, row => Assert.Equal(script.Principal, row.GetProperty("principal").GetString()));
    }

    sealed class ScriptMode : ILiteMode
    {
        public bool On => true;
        public long KillEpoch => 0;
        public void TurnOff(string why) => throw new NotSupportedException();
        public void RaiseEpoch(long epoch) => throw new NotSupportedException();
    }

    sealed class ScriptPeers(LitePeer peer) : ILitePeers
    {
        public IReadOnlyList<LitePeer> All() => [peer];
        public bool TryFind(string id, out LitePeer found) { found = peer; return id == peer.DeviceId; }
    }

    sealed class ScriptRoster(IReadOnlyList<FleetMachine> machines) : IFleetRoster
    {
        public Task<IReadOnlyList<FleetMachine>> ReadAsync(CancellationToken ct) => Task.FromResult(machines);
    }

    sealed class ScriptLink(string self, string account, string other, byte[] ownStatic, LiteReceiver receiver) : ILiteLink
    {
        public string Self => self;
        public string Account => account;
        public IReadOnlyList<PresenceEntry> Presence => [new(other, "receiver", "host", DateTimeOffset.UtcNow)];
        public Task<LiteAck> SendAsync(string peerId, object request, CancellationToken ct) =>
            receiver.ReceiveAsync(new(Self, "host", ownStatic, other, Account, true), JsonSerializer.SerializeToElement(request), ct);
    }

    sealed class ScriptChannel : IProtocolChannel
    {
        public string PeerId => "fixture";
        public string PeerName => "fixture";
        public string PeerKind => "fixture";
        public bool IsOpen => true;
        public Task Closed => Task.CompletedTask;
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }

    [Fact]
    public async Task Script_delivery_is_labelled_and_logged_as_a_script_never_an_agent()
    {
        var f = new Fixture(); f.Launch(LaunchProtocol.Ask);
        var original = Envelope("hi").Body;
        var machine = new string('1', 32);
        var identity = $"script:{machine}/pump-wake";
        var root = System.Text.Json.Nodes.JsonNode.Parse(original.Encode())!;
        root["account"] = new string('a', 32); root["generation"] = new string('0', 32);
        root["heads"] = new System.Text.Json.Nodes.JsonArray(new string('A', 43));
        root["principal"] = new System.Text.Json.Nodes.JsonObject
        {
            ["kind"] = "script", ["machine"] = machine, ["add"] = new string('A', 43),
            ["identity"] = identity, ["credential"] = new string('A', 22),
        };
        root["chain"] = new System.Text.Json.Nodes.JsonObject
        {
            ["root"] = Req, ["rootAt"] = 0, ["depth"] = 1, ["messages"] = 1,
            ["via"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                { ["account"] = new string('a', 32), ["machine"] = machine, ["session"] = identity }),
        };
        root["target"]!["account"] = new string('a', 32); root["target"]!["machine"] = new string('2', 32);
        root["action"] = "session.read";
        root["expiresAt"] = 60;
        var body = EnvelopeBody.Parse(Encoding.UTF8.GetBytes(root.ToJsonString()));
        Assert.True((await f.Build().DeliverAsync(new VerifiedEnvelope(body, []), "hi", default)).Typed);
        Assert.Contains($"from script {identity}", Assert.Single(f.Typed).Text);
        Assert.DoesNotContain("from an agent", f.Typed[0].Text);
        Assert.Contains(f.Log, line => line.Contains(identity) && line.Contains("typed"));
    }

    [Fact]
    public async Task A_forced_ask_idle_session_gets_the_text_behind_the_provenance_line()
    {
        var f = new Fixture(); f.Launch(LaunchProtocol.Ask);
        Assert.True((await Send(f, "please look at x")).Typed);
        var typed = Assert.Single(f.Typed);
        Assert.Equal(Target, typed.Id);
        Assert.StartsWith($"[Slipgates · from an agent on SENDER · session sess-9 · request {Req} · not typed by your user] please look at x", typed.Text);
        Assert.DoesNotContain(f.Log, l => l.Contains("please look at x"));
        Assert.Contains(f.Log, l => l.Contains("SENDER/sess-9") && l.Contains(Target) && l.Contains(Req) && l.Contains("typed"));
        Assert.True((await Send(f, "see \u0001@x \u200B@y")).Typed);
        Assert.Contains("\u0001＠x ＠y", f.Typed[^1].Text);
    }
    [Theory]
    [InlineData(LaunchProtocol.AllowAll)]
    [InlineData(null)]
    public async Task An_allow_all_session_or_one_this_host_did_not_launch_is_refused_and_nothing_is_typed(string? permissions)
    {
        var f = new Fixture();
        if (permissions is not null) { f.Launch(permissions); }
        Assert.Equal(new LiteDelivery(false, "notReplyTarget"), await Send(f));
        Assert.Empty(f.Typed);
        f.AllowAllTargets = true;
        Assert.Equal(permissions is not null, (await Send(f)).Typed);
    }
    [Fact]
    public async Task An_unknown_session_is_refused_with_nothing_typed()
    {
        var f = new Fixture { Exists = false }; f.Launch(LaunchProtocol.Ask);
        Assert.Equal(new LiteDelivery(false, "noSuchSession"), await Send(f));
        Assert.Empty(f.Typed);
    }
    [Theory]
    [InlineData(SessionStatus.Working)]
    [InlineData(SessionStatus.Waiting)]
    public async Task A_session_that_is_not_idle_is_refused_as_busy(SessionStatus status)
    {
        var f = new Fixture { Status = status }; f.Launch(LaunchProtocol.Ask);
        Assert.Equal(new LiteDelivery(false, "busy"), await Send(f));
        Assert.Empty(f.Typed);
    }
    [Fact]
    public async Task A_composer_with_words_in_it_is_refused_and_nothing_is_typed()
    {
        var f = new Fixture { Rows = [.. EmptyBox.Take(3), "\u2503 half a thought", .. EmptyBox.Skip(4)] }; f.Launch(LaunchProtocol.Ask);
        Assert.Equal(new LiteDelivery(false, "composerNotEmpty"), await Send(f));
        Assert.Empty(f.Typed);
    }
    [Fact]
    public async Task A_session_that_goes_busy_after_the_decision_is_refused_before_the_keys_go()
    {
        var f = new Fixture { FlipAfterFirstGet = true }; f.Launch(LaunchProtocol.Ask);
        Assert.Equal(new LiteDelivery(false, "busy"), await Send(f));
        Assert.Empty(f.Typed);
    }
    [Theory]
    [InlineData(true, SessionStatus.Working, false)]
    [InlineData(true, SessionStatus.Waiting, false)]
    [InlineData(true, SessionStatus.Idle, true)]
    [InlineData(false, SessionStatus.Working, true)]
    public async Task Lite_rechecks_idle_after_waiting_for_the_typing_slot_but_a_person_can_still_reply_while_working(
        bool lite, SessionStatus afterWait, bool typed)
    {
        using var home = new global::Fixture();
        var events = home.CopilotSession(Target, 42);
        global::Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.TurnEnd(3));
        home.Processes.Run(AgentKind.Copilot, 42);
        var f = new Fixture();
        f.Launch(LaunchProtocol.Ask);
        f.OnInject = plan => global::Fixture.Append(events, Copilot.User(plan.Sends[0].Text, 4));
        var gate = new DeliveryGate();
        var replier = new ReplyService(gate, f, home.Processes, new NoBridge(), new NoGuard(), f,
            new ReplyOptions { CopilotHome = home.CopilotHome, PlatformSupported = true, PreRead = false,
                ConfirmTimeout = TimeSpan.FromSeconds(1), PollInterval = TimeSpan.FromMilliseconds(10) });
        var deliver = new LiteDeliver([new ForcedAskGate(f.Launches, () => false)], f, replier, f, f.Log.Add);
        await gate.WaitToTypeAsync();
        Task<LiteDelivery>? delivery = null;
        Task<ReplyResult>? reply = null;
        try
        {
            if (lite) { delivery = deliver.DeliverAsync(Envelope("hi"), "hi", default); }
            else
            {
                // Match Lite's preflight screen read before entering the real replier.
                await f.ReadScreenAsync(42);
                reply = replier.ReplyAsync(new ReplyRequest { Id = Target, Request = Req, Text = "hi" });
            }
            await f.ReplyScreenRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(delivery is { IsCompleted: true } || reply is { IsCompleted: true });
            f.Status = afterWait;
        }
        finally { gate.DoneTyping(); }
        if (delivery is not null)
        {
            Assert.Equal(typed ? new LiteDelivery(true, null) : new LiteDelivery(false, LiteCodes.Busy),
                await delivery.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        else { Assert.Equal(AnswerOutcome.Delivered, (await reply!.WaitAsync(TimeSpan.FromSeconds(10))).Outcome); }
        Assert.Equal(typed ? 1 : 0, f.Plans.Count);
    }

    sealed class NoBridge : IAgentHaBridgeProbe { public string? Active() => null; }
    sealed class NoGuard : ITargetGuard { public string? Refuse(int processId) => null; }

    [Fact]
    public async Task Text_the_envelope_does_not_bind_and_text_over_the_cap_are_refused()
    {
        var f = new Fixture(); f.Launch(LaunchProtocol.Ask);
        Assert.False((await f.Build().DeliverAsync(Envelope("what was signed"), "something else", default)).Typed);
        Assert.False((await Send(f, new string('x', LiteDeliver.MaxChars + 1))).Typed);
        Assert.Equal(new LiteDelivery(false, "notReplyTarget"), await Send(f, "bad \uD800 text"));
        Assert.Equal(new LiteDelivery(false, "notReplyTarget"), await f.Build().DeliverAsync(Envelope("hi", "req-1"), "hi", default));
        Assert.Empty(f.Typed);
    }
    [Theory]
    [InlineData(AnswerOutcome.Busy, ReplyRefusal.Other, "busy")]
    [InlineData(AnswerOutcome.Stale, ReplyRefusal.Other, "noSuchSession")]
    [InlineData(AnswerOutcome.Refused, ReplyRefusal.Asking, "busy")]
    [InlineData(AnswerOutcome.Refused, ReplyRefusal.Busy, "busy")]
    [InlineData(AnswerOutcome.Refused, ReplyRefusal.ComposerUnverified, "composerNotEmpty")]
    [InlineData(AnswerOutcome.Refused, ReplyRefusal.Other, "notReplyTarget")]
    public async Task A_replier_that_does_not_deliver_is_reported_with_its_own_refusal_code(AnswerOutcome outcome, ReplyRefusal refusal, string code)
    {
        var f = new Fixture { Outcome = outcome, Refusal = refusal }; f.Launch(LaunchProtocol.Ask);
        Assert.Equal(new LiteDelivery(false, code), await Send(f));
    }
    [Fact]
    public async Task A_seventh_message_inside_a_minute_is_refused_and_the_window_then_reopens()
    {
        var f = new Fixture(); f.Launch(LaunchProtocol.Ask);
        var deliver = f.Build();
        for (var i = 0; i < LiteDeliver.MaxPerMinute; i++) { Assert.True((await deliver.DeliverAsync(Envelope("hi"), "hi", default)).Typed); }
        Assert.False((await deliver.DeliverAsync(Envelope("hi"), "hi", default)).Typed);
        f.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.True((await deliver.DeliverAsync(Envelope("hi"), "hi", default)).Typed);
    }
}
