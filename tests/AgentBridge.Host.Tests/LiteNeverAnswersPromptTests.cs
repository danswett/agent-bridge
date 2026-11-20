using System.Security.Cryptography;
using System.Text;
using AgentBridge.AgentEnvelope;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Lite;
using AgentBridge.Protocol;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class LiteNeverAnswersPromptTests
{
    const string Target = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    const string Request = "22222222-2222-2222-2222-222222222222";

    public static IEnumerable<object[]> PromptMessages()
    {
        foreach (var mode in new[] { LaunchProtocol.Ask, LaunchProtocol.AllowAll })
        foreach (var trust in new[] { false, true })
        foreach (var staleComposer in new[] { false, true })
        foreach (var text in new[] { "please review the change", "y", "1", "yes", "\r", "Enter", "Allow" })
            yield return [mode, trust, staleComposer, text];
    }

    [Theory]
    [MemberData(nameof(PromptMessages))]
    public async Task Relayed_text_never_answers_a_console_prompt_even_with_an_empty_stale_composer(
        string mode, bool trust, bool staleComposer, string text)
    {
        var f = new Fixture(mode);
        var dialog = trust
            ? new[] { "Confirm folder trust", "C:\\fixture", "Do you trust the files in this folder?",
                "\u276F 1. Yes", "  2. Yes, and remember this folder for future sessions", "  3. No (Esc)" }
            : new[] { "Run command", "dotnet build", "Do you want to run this command?",
                "\u276F 1. Yes", "  2. Yes, and don't ask again for `dotnet` in this directory",
                "  3. No, and tell Copilot what to do differently (Esc to stop)" };
        f.Rows = [.. staleComposer ? f.Rows : [], .. dialog,
            " \u2191\u2193 to navigate \u00B7 Enter to select \u00B7 Esc to cancel"];
        var before = f.Rows.ToArray();
        // Discovery deliberately has no Question: only the injector's screen reveals the prompt.
        Assert.Equal(new LiteDelivery(false, LiteCodes.ComposerNotEmpty), await Send(f, text));
        Assert.Empty(f.Replies);
        Assert.Empty(f.Plans);
        Assert.Equal(before, f.Rows);
        Assert.Equal(1, f.ScreenReads);
    }

    [Theory]
    [InlineData(LaunchProtocol.Ask)]
    [InlineData(LaunchProtocol.AllowAll)]
    public async Task A_nonempty_composer_keeps_its_refusal_code_and_its_draft(string mode)
    {
        var f = new Fixture(mode);
        f.Rows[1] = "\u2503 unfinished draft";
        var before = f.Rows.ToArray();
        Assert.Equal(new LiteDelivery(false, LiteCodes.ComposerNotEmpty), await Send(f, "yes"));
        Assert.Empty(f.Replies);
        Assert.Empty(f.Plans);
        Assert.Equal(before, f.Rows);
    }

    [Theory]
    [InlineData(LaunchProtocol.Ask, SessionStatus.Working)]
    [InlineData(LaunchProtocol.Ask, SessionStatus.Waiting)]
    [InlineData(LaunchProtocol.AllowAll, SessionStatus.Working)]
    [InlineData(LaunchProtocol.AllowAll, SessionStatus.Waiting)]
    public async Task A_busy_target_keeps_the_busy_code_without_reading_or_typing(string mode, SessionStatus status)
    {
        var f = new Fixture(mode) { Status = status };
        Assert.Equal(new LiteDelivery(false, LiteCodes.Busy), await Send(f, "1"));
        Assert.Empty(f.Replies);
        Assert.Empty(f.Plans);
        Assert.Equal(0, f.ScreenReads);
    }

    [Theory]
    [InlineData(LaunchProtocol.Ask)]
    [InlineData(LaunchProtocol.AllowAll)]
    public async Task An_idle_empty_composer_without_a_dialog_still_receives_the_framed_message(string mode)
    {
        var f = new Fixture(mode);
        Assert.Equal(new LiteDelivery(true, null), await Send(f, "yes"));
        Assert.True(Assert.Single(f.Replies).RequireIdle);
        var send = Assert.Single(Assert.Single(f.Plans).Sends);
        Assert.Contains("not typed by your user", send.Text);
        Assert.EndsWith("] yes", send.Text);
    }

    static Task<LiteDelivery> Send(Fixture f, string text)
    {
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var body = new EnvelopeBody(Request, "acct", "g", [],
            new AgentPrincipal("SENDER", "add", "sender-session", "inst", "cred", "copilot", "launcher"),
            new AgentChain("root", 0, null, 0, 1, []), new AgentProvenance(null, default, null),
            "message", new AgentTarget("acct", "HOST", Target, null), hash, 0, 0);
        return new LiteDeliver([new AlwaysAllowGate()], f, f, f, _ => { })
            .DeliverAsync(new VerifiedEnvelope(body, []), text, default);
    }

    sealed class AlwaysAllowGate : IReplyTargetGate
    {
        public Task<GateDecision> CheckAsync(VerifiedEnvelope env, string targetSession) =>
            Task.FromResult(GateDecision.Allow());
    }

    sealed class Fixture(string mode) : ISessionSource, ISessionReplier, IConsoleInjector
    {
        public string[] Rows =
        [
            "\u257B\u2584\u2584\u2584\u2584\u2584\u2584\u2584\u2584", "\u2503",
            "\u2579\u2580\u2580\u2580\u2580\u2580\u2580\u2580\u2580",
            $" \u2190 open sidebar \u00B7 Interactive \u00B7 {(mode == LaunchProtocol.Ask ? "Ask permission" : "Allow All")} \u00B7 / commands \u00B7 ? help"
        ];
        public SessionStatus Status = SessionStatus.Idle;
        public int ScreenReads;
        public List<ReplyRequest> Replies { get; } = [];
        public List<KeyPlan> Plans { get; } = [];
        public Session? Get(string id) => id == Target ? new Session
        {
            Id = Target, Agent = AgentKind.Copilot, ProcessId = 42, Status = Status,
            StatusSince = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch, Activity = new Activity()
        } : null;
        public long Revision => 0;
        public DiscoveryHeld? Held => null;
        public SessionList List(string? cursor = null) => new([Get(Target)!], 0);
        public IReadOnlyList<Session> Snapshot() => [Get(Target)!];
        public event EventHandler<SessionChange>? Changed { add { } remove { } }
        public SessionSubscription Subscribe() => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public bool Available => true;
        public async Task<ReplyResult> ReplyAsync(ReplyRequest request, string? peer = null,
            CancellationToken ct = default, DeliveryWatch? watch = null)
        {
            // No downstream refusal can hide a regression in LiteDeliver's screen guard.
            Replies.Add(request);
            await InjectAsync(42, new KeyPlan([new KeySend(Assert.IsType<string>(request.Text), true, 0, 0)]), ct);
            return new ReplyResult(AnswerOutcome.Delivered, "fixture");
        }
        public Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken ct = default)
        {
            Plans.Add(plan);
            Rows = ["fixture received injected text"];
            return Task.FromResult(new InjectionResult(InjectionStatus.Typed, ""));
        }
        public Task<ConsoleScreen> ReadScreenAsync(int processId, CancellationToken ct = default)
        {
            ScreenReads++;
            return Task.FromResult(new ConsoleScreen(Rows, null));
        }
    }
}
