using System.Security.Cryptography;
using System.Text;
using AgentBridge.AgentEnvelope;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.AgentMessaging;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Lite;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class AgentLiteDeliveryTests
{
    const string Target = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", Req = "22222222-2222-2222-2222-222222222222";
    static readonly string[] EmptyBox = ["\u25CF Done.", "", "\u257B\u2584\u2584\u2584\u2584\u2584\u2584\u2584\u2584", "\u2503", "\u2579\u2580\u2580\u2580\u2580\u2580\u2580\u2580\u2580", " \u2190 open sidebar \u00B7 Interactive \u00B7 Allow All \u00B7 / commands \u00B7 ? help \u00B7 tab next tab"];

    sealed class Fixture : ISessionSource, ISessionReplier, IConsoleInjector
    {
        public SessionStatus Status = SessionStatus.Idle;
        public bool Exists = true, FlipAfterFirstGet, AllowAllTargets;
        public string Remote = AgentMessagingSettings.OwnAccount;
        public string[] AllowAllTargetSessions = [];
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
        public IReadOnlyList<IReplyTargetGate> Gates() => [
            new ForcedAskGate(Launches),
            new AllowlistedAllTargetsGate(Launches, () => new AgentMessagingSettings
            {
                Remote = Remote,
                AllowAllTargets = AllowAllTargets,
                AllowAllTargetSessions = AllowAllTargetSessions
            })
        ];
        public LiteDeliver Build() => new(Gates(), this, this, this, Log.Add, Clock);
        public void Launch(string permissions) => Launches.Launched(Target, new LaunchedWith { Agent = "copilot", Permissions = permissions });
        public long Revision => 0; public IReadOnlyList<Session> Snapshot() => []; public SessionList List(string? cursor = null) => new([], 0);
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
    [InlineData(false), InlineData(true)]
    public async Task Gate_pause_review_rechecks_lite_target_authority_after_queued_admission(bool remoteOff)
    {
        using var home = new global::Fixture();
        var events = home.CopilotSession(Target, 42);
        global::Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.TurnEnd(3));
        home.Processes.Run(AgentKind.Copilot, 42);
        var f = new Fixture { AllowAllTargets = true, AllowAllTargetSessions = [Target] };
        f.Launch(LaunchProtocol.AllowAll);
        f.OnInject = plan => global::Fixture.Append(events, Copilot.User(plan.Sends[0].Text, 4));
        var gate = new DeliveryGate();
        var replier = new ReplyService(gate, f, home.Processes, new NoBridge(), new NoGuard(), f,
            new ReplyOptions { CopilotHome = home.CopilotHome, PlatformSupported = true, PreRead = false });
        var deliver = new LiteDeliver(f.Gates(), f, replier, f, f.Log.Add);
        using var pause = gate.PauseAdmission();
        var queued = deliver.DeliverAsync(Envelope("hi"), "hi", default);
        Assert.False(queued.IsCompleted);
        if (remoteOff) { f.Remote = AgentMessagingSettings.Off; } else { f.AllowAllTargetSessions = []; }
        pause.Dispose();
        Assert.Equal(new LiteDelivery(false, "notReplyTarget"), await queued.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(f.Plans);
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
    [InlineData(LaunchProtocol.AllowAll, true)]
    [InlineData(null, false)]
    public async Task An_allow_all_target_grant_requires_a_session_this_host_launched(string? permissions, bool allowed)
    {
        var f = new Fixture();
        if (permissions is not null) { f.Launch(permissions); }
        Assert.Equal(new LiteDelivery(false, "notReplyTarget"), await Send(f));
        Assert.Empty(f.Typed);
        f.AllowAllTargets = true;
        Assert.Equal(new LiteDelivery(false, "notReplyTarget"), await Send(f));
        Assert.Empty(f.Typed);
        f.AllowAllTargetSessions = [Target];
        Assert.Equal(allowed ? new LiteDelivery(true, null) : new LiteDelivery(false, "notReplyTarget"), await Send(f));
        Assert.Equal(allowed ? 1 : 0, f.Typed.Count);
    }
    [Theory]
    [InlineData("*")]
    [InlineData("Copilot display name")]
    [InlineData("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA")]
    [InlineData(" " + Target + " ")]
    public async Task An_allow_all_session_requires_its_exact_listed_identity_and_audits_without_message_text(string listed)
    {
        var f = new Fixture { AllowAllTargets = true }; f.Launch(LaunchProtocol.AllowAll);
        const string text = "private relay message that must not enter the audit";
        f.AllowAllTargetSessions = [listed];
        Assert.Equal(new LiteDelivery(false, "notReplyTarget"), await Send(f, text));
        Assert.Empty(f.Typed);
        Assert.Contains(f.Log, l => l.Contains("notReplyTarget", StringComparison.Ordinal));
        Assert.DoesNotContain(f.Log, l => l.Contains(text, StringComparison.Ordinal));

        f.AllowAllTargetSessions = [Target];
        f.AllowAllTargets = false;
        Assert.Equal(new LiteDelivery(false, "notReplyTarget"), await Send(f, text));
        f.AllowAllTargets = true;
        Assert.True((await Send(f, text)).Typed);
        Assert.Contains(f.Log, l => l.Contains("typed", StringComparison.Ordinal));
        f.Remote = AgentMessagingSettings.Off;
        Assert.Equal(new LiteDelivery(false, "notReplyTarget"), await Send(f, text));
        Assert.Single(f.Typed);
        Assert.DoesNotContain(f.Log, l => l.Contains(text, StringComparison.Ordinal));
    }
    [Fact]
    public async Task Gate_audits_distinguish_allowlist_refusal_and_acceptance_without_message_text()
    {
        var f = new Fixture { AllowAllTargets = true }; f.Launch(LaunchProtocol.AllowAll);
        const string text = "private message excluded from gate audits";
        Assert.False((await Send(f, text)).Typed);
        Assert.Contains(f.Log, l => l.Contains($"request {Req} gate AllowlistedAllTargetsGate: refused: the allow-all session is not enabled and listed", StringComparison.Ordinal));
        f.AllowAllTargetSessions = [Target];
        Assert.True((await Send(f, text)).Typed);
        Assert.Contains(f.Log, l => l.Contains($"request {Req} gate AllowlistedAllTargetsGate: allowed", StringComparison.Ordinal));
        f.Launch(LaunchProtocol.Ask);
        Assert.True((await Send(f, text)).Typed);
        Assert.Contains(f.Log, l => l.Contains($"request {Req} gate ForcedAskGate: allowed", StringComparison.Ordinal));
        Assert.DoesNotContain(f.Log, l => l.Contains(text, StringComparison.Ordinal));
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
        var deliver = new LiteDeliver([new ForcedAskGate(f.Launches)], f, replier, f, f.Log.Add);
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
