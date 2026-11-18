using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using Xunit;

public sealed class ReplyClearTests
{
    const string Id = "99999999-9999-4999-8999-999999999999";

    [Theory]
    [InlineData("unfinished draft")]
    [InlineData("draft \u202e")]
    [InlineData("  unfinished draft  ")]
    public async Task A_confirmed_person_clear_checks_empty_twice_then_sends_once_and_remembers_the_deleted_draft(string draft)
    {
        await using var h = await Harness.Create();
        h.Draft = draft;
        h.DelayedEmpty = 2;
        var request = h.Request();
        var result = await h.Replier.ReplyAsync(request);
        Assert.Equal(AnswerOutcome.Delivered, result.Outcome);
        Assert.Equal(ShownText.Reveal(draft, rightToLeft: true), result.Cleared?.Text);
        if (draft.Contains('\u202e')) Assert.Single(result.Cleared!.Marks);
        Assert.Equal(1, h.Clears);
        Assert.Equal(1, h.Replies);
        Assert.True(h.EmptyReads >= 2);
        Assert.Equal(result, await h.Replier.ReplyAsync(request));
        Assert.Equal(1, h.Clears);
        Assert.DoesNotContain(h.Log, line => line.Contains("unfinished draft", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("peer")]
    [InlineData("target")]
    [InlineData("authority")]
    [InlineData("confirmation")]
    [InlineData("generation")]
    [InlineData("verifier")]
    [InlineData("draft-source")]
    public async Task A_clear_replay_never_discloses_escrow_to_another_sender_target_or_authority(string change)
    {
        await using var h = await Harness.Create();
        var authorized = true;
        var request = h.Request() with { IntentSource = () => authorized ? ReplyIntent.Steer : ReplyIntent.Unknown };
        Assert.Equal(AnswerOutcome.Delivered, (await h.Replier.ReplyAsync(request, "person-a")).Outcome);
        var peer = "person-a";
        switch (change)
        {
            case "peer": peer = "person-b"; break;
            case "target": request = request with { Id = Guid.NewGuid().ToString() }; break;
            case "authority": authorized = false; break;
            case "confirmation": request = request with { ClearConfirmation = null, IntentSource = null }; break;
            case "generation": request = request with { IntentSource = () => ReplyIntent.Steer }; break;
            case "verifier": request = request with { ClearConfirmation = _ => null }; break;
            case "draft-source": request = request with { ClearDraftSource = () => h.Draft }; break;
        }
        var result = await h.Replier.ReplyAsync(request, peer);
        Assert.Equal(AnswerOutcome.Refused, result.Outcome);
        Assert.Null(result.Cleared);
        Assert.Equal(1, h.Clears);
        Assert.Equal(1, h.Replies);
    }

    [Theory]
    [InlineData("  ", "", false)]
    [InlineData("", "  ", false)]
    [InlineData("  ", "", true)]
    [InlineData("", "  ", true)]
    public async Task Ambiguous_draft_spaces_are_refused_before_any_clear_key(string before, string after, bool atKeys)
    {
        await using var h = await Harness.Create();
        var request = h.Request();
        var changed = before + h.Draft + after;
        if (atKeys) h.BeforeKeys = () => h.Draft = changed;
        else h.Draft = changed;
        var result = await h.Replier.ReplyAsync(request);
        Assert.Equal(AnswerOutcome.Refused, result.Outcome);
        Assert.Null(result.Cleared);
        Assert.Equal(changed, h.Draft);
        Assert.Equal(0, h.Clears);
        Assert.Equal(0, h.Replies);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("agent")]
    [InlineData("changed")]
    [InlineData("shell")]
    [InlineData("multiline")]
    [InlineData("oversize")]
    [InlineData("permission")]
    [InlineData("no-lossless-evidence")]
    public async Task An_unconfirmed_unsafe_or_unrecoverable_composer_is_never_cleared(string condition)
    {
        await using var h = await Harness.Create();
        var request = h.Request();
        switch (condition)
        {
            case "unknown": request = request with { IntentSource = null }; break;
            case "agent": request = request with { IntentSource = () => ReplyIntent.Queueable }; break;
            case "changed": request = request with { ClearConfirmation = _ => "the composer changed" }; break;
            case "shell": h.Shell = true; break;
            case "multiline": h.Draft = "first\nsecond"; break;
            case "oversize": h.Draft = new string('x', 501); break;
            case "permission": await h.Ask(); break;
            case "no-lossless-evidence": request = request with { ClearDraftSource = null }; break;
        }
        Assert.Equal(AnswerOutcome.Refused, (await h.Replier.ReplyAsync(request)).Outcome);
        Assert.Equal(0, h.Clears);
        Assert.Equal(0, h.Replies);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("authority")]
    public async Task The_injector_rechecks_rows_and_person_authority_before_the_clear_keys(string changed)
    {
        await using var h = await Harness.Create();
        var person = true;
        h.BeforeKeys = () => { if (changed == "draft") h.Draft = "new local draft"; else person = false; };
        var result = await h.Replier.ReplyAsync(h.Request() with { IntentSource = () => person ? ReplyIntent.Steer : ReplyIntent.Unknown });
        Assert.Equal(AnswerOutcome.Refused, result.Outcome);
        Assert.Equal(0, h.Clears);
        Assert.Equal(0, h.Replies);
    }

    [Fact]
    public async Task A_request_that_goes_stale_before_clear_keys_preserves_the_draft()
    {
        await using var h = await Harness.Create();
        var current = true;
        var request = h.Request() with { StillCurrent = () => current };
        h.BeforeKeys = () => current = false;
        var result = await h.Replier.ReplyAsync(request);
        Assert.Equal(0, h.Clears);
        Assert.Equal(0, h.Replies);
        Assert.Equal("unfinished draft", h.Draft);
        Assert.Null(result.Cleared);
        Assert.Equal(AnswerOutcome.Stale, result.Outcome);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_failed_clear_or_a_question_after_clearing_returns_the_draft_without_sending(bool question, bool unresponsive)
    {
        await using var h = await Harness.Create();
        h.Stuck = !question;
        h.Unresponsive = unresponsive;
        h.AfterClear = question ? h.Ask : null;
        var result = await h.Replier.ReplyAsync(h.Request());
        Assert.Equal(AnswerOutcome.Refused, result.Outcome);
        Assert.Equal("unfinished draft", result.Cleared?.Text);
        Assert.Equal(1, h.Clears);
        Assert.Equal(0, h.Replies);
    }

    [Fact]
    public async Task An_empty_composer_needs_no_clear_and_wire_fields_cannot_supply_clear_authority()
    {
        await using var h = await Harness.Create();
        var wire = JsonSerializer.Deserialize<ReplyRequest>(
            $$"""{"id":"{{Id}}","request":"{{Guid.NewGuid()}}","text":"go on","clearConfirmation":{},"clearDraftSource":{},"intentSource":"Steer"}""", ProtocolJson.Options)!;
        Assert.Null(wire.ClearConfirmation);
        Assert.Null(wire.ClearDraftSource);
        Assert.Equal(AnswerOutcome.Refused, (await h.Replier.ReplyAsync(wire)).Outcome);
        h.Draft = "";
        var result = await h.Replier.ReplyAsync(h.Request());
        Assert.Equal(AnswerOutcome.Delivered, result.Outcome);
        Assert.Null(result.Cleared);
        Assert.Equal(0, h.Clears);
        Assert.Equal(1, h.Replies);
    }

    sealed class Harness : IConsoleInjector, IAgentHaBridgeProbe, ITargetGuard, IAsyncDisposable
    {
        readonly Fixture f = new();
        readonly SessionSource source;
        readonly string events;
        public readonly ReplyService Replier;
        public readonly List<string> Log = [];
        public string Draft = "unfinished draft";
        public bool Shell, Stuck, Unresponsive;
        public int Clears, Replies, EmptyReads, DelayedEmpty;
        public Action? BeforeKeys;
        public Func<Task>? AfterClear;
        Harness()
        {
            events = f.CopilotSession(Id, 100);
            Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.TurnEnd(3));
            f.Processes.Run(AgentKind.Copilot, 100);
            source = f.Source();
            Replier = new ReplyService(new DeliveryGate(), source, f.Processes, this, this, this, new ReplyOptions
            {
                CopilotHome = f.CopilotHome, PlatformSupported = true, PreRead = false,
                ConfirmTimeout = TimeSpan.FromSeconds(1), PollInterval = TimeSpan.FromMilliseconds(10),
            }, Log.Add);
        }
        public static async Task<Harness> Create()
        {
            var h = new Harness();
            await h.source.RefreshAsync();
            return h;
        }
        public ReplyRequest Request()
        {
            var expected = EscapedApproval.Shown(ShownText.Reveal(Draft, rightToLeft: true));
            return new()
            {
                Id = Id, Request = Guid.NewGuid().ToString(), Text = "go on",
                IntentSource = () => ReplyIntent.Steer,
                ClearConfirmation = digest => digest == expected ? null : "changed composer",
                ClearDraftSource = () => Draft,
            };
        }
        public async Task Ask()
        {
            Fixture.Append(events, Copilot.Event("permission.requested",
                new { requestId = "r", permissionRequest = new { kind = "shell" } }, 5));
            await source.RefreshAsync();
        }
        public async Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken ct = default)
        {
            BeforeKeys?.Invoke();
            if (plan.Proceed?.Invoke() is { } why) return new(InjectionStatus.NothingTyped, why);
            var rows = Rows();
            if (plan.Sends.Any(s => (s.Expect ?? []).Any(r => r.Row >= rows.Count || rows[r.Row].TrimEnd(' ') != r.Text.TrimEnd(' '))
                || (s.Checks ?? []).Any(c => !c.Passes(rows))))
                return new(InjectionStatus.NothingTyped, "screen-changed");
            if (!plan.Sends.Any(s => s.Enter))
            {
                Assert.Equal("\u001b[1;5F\u0015", Assert.Single(plan.Sends).Text);
                Clears++;
                if (!Stuck && DelayedEmpty == 0) Draft = "";
                if (AfterClear is { } after) await after();
            }
            else
            {
                Assert.Equal("", Draft);
                Replies++;
                Fixture.Append(events, Copilot.User(plan.Sends[0].Text, 10));
            }
            return new(InjectionStatus.Typed, "");
        }
        IReadOnlyList<string> Rows() =>
            ["\u25CF Done.", "", "\u257B\u2584\u2584\u2584\u2584\u2584\u2584\u2584\u2584",
             .. Draft.Split('\n').Select(line => "\u2503 " + line),
             "\u2579\u2580\u2580\u2580\u2580\u2580\u2580\u2580\u2580",
             Shell ? " Shell \u00B7 Allow All" : Draft.Length == 0 ? " Interactive \u00B7 / commands \u00B7 ? help" : " Interactive \u00B7 @ files \u00B7 # issues"];
        public Task<ConsoleScreen> ReadScreenAsync(int processId, CancellationToken ct = default)
        {
            if (Clears > 0 && Unresponsive) return new TaskCompletionSource<ConsoleScreen>().Task;
            if (Clears > 0 && DelayedEmpty > 0 && --DelayedEmpty == 0) Draft = "";
            if (Draft.Length == 0) EmptyReads++;
            return Task.FromResult(new ConsoleScreen(Rows(), null));
        }
        public string? Active() => null;
        public string? Refuse(int processId) => null;
        public async ValueTask DisposeAsync() { await source.DisposeAsync(); f.Dispose(); }
    }
}
