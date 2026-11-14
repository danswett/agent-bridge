using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed partial class AgentAnswerFlowTests
{
    [Fact]
    public async Task A_confirmed_multifield_answer_records_intent_before_typing_and_one_credit_despite_duplicates_or_stale_answers()
    {
        var host = new ApprovalAuditFixture(f.Root, source, Answerer());
        var before = -1;
        injector.OnInject = _ => { before = host.Rows().Count; Fixture.Append(claude, Recorded()); };
        var request = Request(ClaudeId, "toolu_1", Chosen);
        Assert.Equal(AnswerOutcome.Delivered, (await host.Answer(request)).Outcome);
        Assert.Equal(1, before);
        Assert.Equal(AnswerOutcome.Delivered, (await host.Answer(request with { Request = Guid.NewGuid().ToString() })).Outcome);
        Assert.Equal(AnswerOutcome.Stale, (await host.Answer(request with { Request = Guid.NewGuid().ToString(), Question = "gone" })).Outcome);
        var rows = host.Rows();
        Assert.Equal(new[] { "intent", "terminal" }, rows.Select(r => r.Gate!.Phase));
        Assert.Equal("delivered", rows[1].Outcome);
        Assert.All(rows, r => { Assert.Equal("person-client", r.Gate!.ActorKind); Assert.Equal("local", r.Gate.Path); Assert.Equal(Guid.Empty, r.Gate.Run); });
        Assert.Equal(rows[0].Gate!.Question, rows[1].Gate!.Question);
        Assert.Equal(0, Assert.Single(GateAuditProjection.Read(host.Profile, Guid.Empty).Totals).Successes);
        var qualified = host.QualifiedProjection();
        Assert.Equal(1, Assert.Single(qualified.Totals).Successes);
        Assert.False(qualified.Invalidated);
        Assert.Empty(qualified.IncompleteReasons);
        var stored = string.Concat(Directory.GetFiles(Path.Combine(host.Profile, "audit")).Select(File.ReadAllText));
        Assert.DoesNotContain("Which database", stored);
        Assert.DoesNotContain("SQLite", stored);
        Assert.All(rows, r => { Assert.Null(r.DataSha256); Assert.Null(r.TextLength); });
    }

    [Fact]
    public async Task An_unconfirmed_answer_has_only_an_unresolved_intent_and_no_credit()
    {
        var host = new ApprovalAuditFixture(f.Root, source, Answerer());
        Assert.Equal(AnswerOutcome.Unconfirmed, (await host.Answer(Request(ClaudeId, "toolu_1", Chosen))).Outcome);
        Assert.Equal("intent", Assert.Single(host.Rows()).Gate!.Phase);
        var report = host.QualifiedProjection();
        Assert.Equal(0, Assert.Single(report.Totals).Successes);
        Assert.Contains("unresolved-intent", report.IncompleteReasons);
    }

    [Fact]
    public async Task An_unwritable_approval_intent_refuses_before_typing()
    {
        var host = new ApprovalAuditFixture(f.Root, source, Answerer(), maxBytes: 0);
        Assert.Equal(AnswerOutcome.Refused, (await host.Answer(Request(ClaudeId, "toolu_1", Chosen))).Outcome);
        Assert.Empty(injector.Plans);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_late_confirmation_writes_one_terminal_without_typing_again(bool gone)
    {
        var answerer = Answerer();
        var host = new ApprovalAuditFixture(f.Root, source, answerer);
        var request = Request(ClaudeId, "toolu_1", Chosen);
        Assert.Equal(AnswerOutcome.Unconfirmed, (await host.Answer(request)).Outcome);
        await answerer.Swept;
        Assert.True(File.Exists(Words));
        var typed = injector.Plans.Count;
        Fixture.Append(claude, Recorded());
        if (gone)
        {
            await AskAnotherAsync();
            // Source notifications dispatch later; Swept may still be the previous pass.
            await Until(() => !File.Exists(Words));
            await answerer.Swept;
            Assert.Equal("toolu_2", source.Get(ClaudeId)?.Question?.Id);
            Assert.False(File.Exists(Words));
            Assert.Equal(new[] { "intent", "terminal" }, host.Rows().Select(r => r.Gate!.Phase));
        }
        Assert.Equal(AnswerOutcome.Delivered, (await host.Answer(request)).Outcome);
        Assert.Equal(AnswerOutcome.Delivered, (await host.Answer(request)).Outcome);
        Assert.Equal(typed, injector.Plans.Count);
        Assert.Equal(new[] { "intent", "terminal" }, host.Rows().Select(r => r.Gate!.Phase));
        var report = host.QualifiedProjection();
        Assert.Equal(1, Assert.Single(report.Totals).Successes);
        Assert.Empty(report.IncompleteReasons);
        Assert.False(report.Invalidated);
    }

    [Fact]
    public async Task Nothing_typed_closes_the_intent_as_refused_and_a_fresh_request_can_still_answer()
    {
        var host = new ApprovalAuditFixture(f.Root, source, Answerer());
        var request = Request(ClaudeId, "toolu_1", Chosen);
        injector.Result = new(InjectionStatus.NothingTyped, "fixture refusal");
        Assert.Equal(AnswerOutcome.Refused, (await host.Answer(request)).Outcome);
        Assert.Equal("refused", host.Rows()[1].Outcome);
        injector.Result = new(InjectionStatus.Typed, "ok");
        injector.OnInject = _ => Fixture.Append(claude, Recorded());
        Assert.Equal(AnswerOutcome.Delivered, (await host.Answer(request with { Request = Guid.NewGuid().ToString() })).Outcome);
        var report = host.QualifiedProjection();
        Assert.Equal(1, Assert.Single(report.Totals).Successes);
        Assert.Empty(report.IncompleteReasons);
        Assert.False(report.Invalidated);
    }

    [Fact]
    public async Task A_request_uuid_reused_for_another_question_keeps_the_redirected_operation_binding()
    {
        var host = new ApprovalAuditFixture(f.Root, source, Answerer());
        var request = Request(ClaudeId, "toolu_1", Chosen);
        injector.OnInject = _ => Fixture.Append(claude, Recorded());
        Assert.Equal(AnswerOutcome.Delivered, (await host.Answer(request)).Outcome);
        await AskAnotherAsync();
        injector.OnInject = _ => Fixture.Append(claude, Claude.Answered("toolu_2", second++,
            new Dictionary<string, string> { ["Which database?"] = "SQLite", ["Which features?"] = "Auth, Search" }));
        Assert.Equal(AnswerOutcome.Delivered, (await host.Answer(request with { Question = "toolu_2" })).Outcome);
        Assert.Equal(2, host.Rows().Select(r => r.Gate!.Operation).Distinct().Count());
        var report = host.QualifiedProjection();
        Assert.Equal(2, Assert.Single(report.Totals).Successes);
        Assert.Empty(report.IncompleteReasons);
        Assert.False(report.Invalidated);
    }

    [Fact]
    public async Task An_authenticated_agent_refusal_is_one_credential_attributed_row_without_credit_or_an_unresolved_attempt()
    {
        var credentials = new AgentCredentials(f.Root, "machine1");
        var host = new ApprovalAuditFixture(f.Root, source, Answerer(), credentials);
        var started = DateTimeOffset.UtcNow;
        var token = credentials.Mint("source", "launch", 100, started, AgentProtocol.ScopeRead, chain: ["agent:upstream/parent"]);
        credentials.Accept(host.Channel, credentials.Check(token, new ProcessAncestry([new PeerProcess(100, started, "agent.exe")], true)).Accepted!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Answer(Request(ClaudeId, "toolu_1", Chosen)));
        var row = Assert.Single(host.Rows());
        Assert.Equal("refused", row.Outcome);
        Assert.Equal("agent:machine1/source", row.Principal);
        Assert.Equal("agent:upstream/parent", Assert.Single(row.Chain!));
        var gate = Assert.IsType<GateAuditMetadata>(row.Gate);
        Assert.Equal("agent", gate.ActorKind);
        Assert.Equal("local", gate.Path);
        Assert.True(gate.Question is { } question && question != Guid.Empty);
        var report = host.QualifiedProjection();
        Assert.Equal(0, Assert.Single(report.Totals).Successes);
        Assert.Empty(report.IncompleteReasons);
        Assert.False(report.Invalidated);
        Assert.Empty(injector.Plans);
    }
}

public sealed partial class CopilotPermissionFlowTests
{
    [Fact]
    public async Task A_delivered_negative_decision_is_one_approval()
    {
        Completes("denied-interactively-by-user");
        var host = new ApprovalAuditFixture(f.Root, source, Answerer());
        Assert.Equal(AnswerOutcome.Delivered, (await host.Answer(Request("deny"))).Outcome);
        Assert.Equal(2, host.Rows().Count);
        Assert.Equal(1, Assert.Single(host.QualifiedProjection().Totals).Successes);
    }
}

sealed class ApprovalAuditFixture(string root, ISessionSource source, ISessionAnswerer answerer,
    AgentCredentials? credentials = null, long maxBytes = AgentAuditLog.MaxBytes)
{
    public string Profile { get; } = Path.Combine(root, "default");
    public IProtocolChannel Channel { get; } = new AuditChannel();
    readonly Func<IProtocolChannel, string, JsonElement, Task<object?>> handle = LocalApi.Handler(
        new SessionService(source, new MachineInfo("host", "Fixture", "windows", "0.1.0"), answers: answerer), null!,
        new FixedCallerGuard(new Caller(CallerKind.App, "fixture")), credentials: credentials,
        audit: new AgentAuditLog(Path.Combine(root, "default"), maxBytes: maxBytes));
    public List<AuditCommands.Row> Rows() => GateAuditProjection.Load(Profile).Rows;
    public async Task<AnswerResult> Answer(AnswerRequest request) => Assert.IsType<AnswerResult>(await handle(Channel, Ops.SessionAnswer,
        JsonSerializer.SerializeToElement(new { request.Id, request.Question, request.Request, request.Fields, request.Text,
            run = Guid.NewGuid(), path = "relay", actorKind = "ha-component", principal = "owner" }, ProtocolJson.Options)));
    public GateAuditReport QualifiedProjection()
    {
        // Qualify captured host evidence only in an isolated reader fixture, never in the host.
        var profile = Path.Combine(Path.GetDirectoryName(Profile)!, "qualified");
        var run = Guid.NewGuid();
        foreach (var r in Rows()) { Assert.True(new AgentAuditLog(profile).Append(new AgentAuditEntry(r.Direction!, r.Request!, r.Principal,
            r.Chain!, r.Action, r.Target!, r.Outcome, Gate: r.Gate! with { Run = run }))); }
        return GateAuditProjection.Read(profile, run);
    }
    sealed class AuditChannel : IProtocolChannel
    {
        public string PeerId => "fixture";
        public string PeerName => "fixture";
        public string PeerKind => "fixture";
        public bool IsOpen => true;
        public Task Closed => Task.Delay(Timeout.Infinite);
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }
}
