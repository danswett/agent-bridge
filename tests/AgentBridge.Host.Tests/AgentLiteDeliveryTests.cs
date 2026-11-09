using System.Security.Cryptography;
using System.Text;
using AgentBridge.AgentEnvelope;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Lite;
using AgentBridge.Protocol;
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
        public bool Exists = true, FlipAfterFirstGet, AllowAllTargets;
        public string[] Rows = EmptyBox;
        public int Gets;
        public AnswerOutcome Outcome = AnswerOutcome.Delivered;
        public ReplyRefusal Refusal;
        public readonly List<ReplyRequest> Typed = []; public readonly List<string> Log = [];
        public readonly FakeTimeProvider Clock = new(); public readonly LiteLaunchLog Launches = new();
        public LiteDeliver Build() => new([new ForcedAskGate(Launches, () => AllowAllTargets)], this, this, this, Log.Add, Clock);
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
        public Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConsoleScreen> ReadScreenAsync(int processId, CancellationToken ct = default) => Task.FromResult(new ConsoleScreen(Rows, null));
    }
    static VerifiedEnvelope Envelope(string text, string request = Req)
    {
        var sha = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var body = new EnvelopeBody(request, "acct", "g", [], new AgentPrincipal("SENDER", "add", "sess-9", "inst", "cred", "copilot", "launcher"),
            new AgentChain("root", 0, null, 0, 1, []), new AgentProvenance(null, default, null), "message", new AgentTarget("acct", "HOST", Target, null), sha, 0, 0);
        return new VerifiedEnvelope(body, []);
    }

    static Task<LiteDelivery> Send(Fixture f, string text = "hi") => f.Build().DeliverAsync(Envelope(text), text, default);
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
