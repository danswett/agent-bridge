using AgentBridge.Host;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using Xunit;

// An answer pressed on a Home Assistant card is audited like a launch and a stop (#1401): legacy-ha, ha-component, ha:connector,
// intent before typing, a terminal after the confirmed delivery. Without it the windowed gate (audit gate) could not see an HA
// approval as an HA fallback.
public sealed partial class AgentAnswerFlowTests
{
    [Fact]
    public async Task An_HA_answer_records_intent_before_typing_and_a_terminal_attributed_to_the_connector_and_counts_as_an_HA_dispatch()
    {
        var host = new HaAnswerAuditFixture(f.Root, Answerer());
        var before = -1;
        injector.OnInject = _ => { before = host.Rows().Count; Fixture.Append(claude, Recorded()); };

        Assert.Equal(AnswerOutcome.Delivered, (await host.Answer(Request(ClaudeId, "toolu_1", Chosen))).Outcome);

        Assert.Equal(1, before);
        var rows = host.Rows();
        Assert.Equal(new[] { "intent", "terminal" }, rows.Select(r => r.Gate!.Phase));
        Assert.Equal("delivered", rows[1].Outcome);
        Assert.All(rows, r =>
        {
            Assert.Equal(("legacy-ha", "ha-component"), (r.Gate!.Path, r.Gate.ActorKind));
            Assert.Equal("ha:connector", r.Principal);
            Assert.Equal("session.answer", r.Action);
        });
        Assert.Equal(rows[0].Gate!.Question, rows[1].Gate!.Question);
        // The gate counts it as an HA dispatch, so a window with an HA approval cannot read PASS.
        var report = GateAuditProjection.ReadWindow(host.Profile, DateTimeOffset.UtcNow.AddHours(-1));
        Assert.Equal(1, report.LegacyHaDispatches);
        Assert.Equal(GateVerdictKind.Fail, GateVerdict.Evaluate(report, new GateMessageEvidence(500, 0)).Kind);
    }

    [Fact]
    public void Launch_rows_written_with_the_old_connector_principal_are_still_read_and_counted()
    {
        var root = Path.Combine(f.Root, "old");
        var folder = Path.Combine(root, "default");
        Directory.CreateDirectory(folder);
        var log = new AgentAuditLog(folder);
        var op = Guid.NewGuid();
        var request = Guid.NewGuid().ToString();
        var gate = new GateAuditMetadata(1, op, Guid.Empty, 0, "intent", "legacy-ha", "ha-component", op, "none");
        var intent = new AgentAuditEntry("local", request, "ha-component:local", [], "session.launch", request, "reserved", Gate: gate);
        Assert.True(log.Append(intent));
        Assert.True(log.Append(intent with { Target = Guid.NewGuid().ToString(), Outcome = "started", Gate = gate with { Phase = "terminal" } }));

        Assert.Equal(1, GateAuditProjection.ReadWindow(folder, DateTimeOffset.UtcNow.AddHours(-1)).LegacyHaDispatches);
        var listing = new StringWriter();
        Assert.Equal(0, AuditCommands.Run(["audit", "--profile", "default"], root, listing, new StringWriter()));
        Assert.Contains("ha-component:local", listing.ToString());
    }
    [Fact]
    public async Task With_no_audit_log_the_HA_answer_delegate_refuses_instead_of_typing()
    {
        var answer = HaRunning.AnswerDelegate(Answerer(), null)!;

        Assert.Equal(AnswerOutcome.Refused, (await answer(Request(ClaudeId, "toolu_1", Chosen), CancellationToken.None)).Outcome);

        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task An_unwritable_audit_refuses_an_HA_answer_before_anything_is_typed()
    {
        var host = new HaAnswerAuditFixture(f.Root, Answerer(), maxBytes: 0);

        Assert.Equal(AnswerOutcome.Refused, (await host.Answer(Request(ClaudeId, "toolu_1", Chosen))).Outcome);

        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task An_HA_answer_to_a_question_that_is_gone_writes_no_row()
    {
        var host = new HaAnswerAuditFixture(f.Root, Answerer());

        Assert.Equal(AnswerOutcome.Stale, (await host.Answer(Request(ClaudeId, "gone", Chosen))).Outcome);

        Assert.Empty(host.Rows());
    }

    [Fact]
    public async Task An_unconfirmed_HA_answer_has_only_an_unresolved_intent_and_no_credit()
    {
        var host = new HaAnswerAuditFixture(f.Root, Answerer());

        Assert.Equal(AnswerOutcome.Unconfirmed, (await host.Answer(Request(ClaudeId, "toolu_1", Chosen))).Outcome);

        Assert.Equal("intent", Assert.Single(host.Rows()).Gate!.Phase);
        Assert.Contains("unresolved-intent", GateAuditProjection.ReadWindow(host.Profile, DateTimeOffset.UtcNow.AddHours(-1)).IncompleteReasons);
    }
}

sealed class HaAnswerAuditFixture(string root, ISessionAnswerer answerer, long maxBytes = AgentAuditLog.MaxBytes)
{
    public string Profile { get; } = Path.Combine(root, "default");
    readonly Func<AnswerRequest, CancellationToken, Task<AnswerResult>> answer =
        HaRunning.AnswerDelegate(answerer, new AgentAuditLog(Path.Combine(root, "default"), maxBytes: maxBytes))!;
    public List<AuditCommands.Row> Rows() => GateAuditProjection.Load(Profile).Rows;
    public Task<AnswerResult> Answer(AnswerRequest request) => answer(request, CancellationToken.None);
}
