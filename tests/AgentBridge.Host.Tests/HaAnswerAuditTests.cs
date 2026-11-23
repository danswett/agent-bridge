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
    public async Task With_no_audit_log_the_HA_answer_delegate_still_types()
    {
        injector.OnInject = _ => Fixture.Append(claude, Recorded());
        var answer = HaRunning.AnswerDelegate(Answerer(), null)!;

        Assert.Equal(AnswerOutcome.Delivered, (await answer(Request(ClaudeId, "toolu_1", Chosen), CancellationToken.None)).Outcome);

        Assert.NotEmpty(injector.Plans);
    }

    // OWNER DECISION (b), via the chief: when the audit row cannot be written the HA answer is STILL typed, and the failure is recorded
    // as audit_write_failed in the host diagnostic log (never the audit file) plus an in-memory count the next successful write flushes.
    [Fact]
    public async Task An_HA_answer_is_still_typed_when_the_audit_cannot_be_written_and_the_failure_is_logged_without_the_answer_or_a_token()
    {
        var host = new HaAnswerAuditFixture(f.Root, Answerer(), maxBytes: 0);
        injector.OnInject = _ => Fixture.Append(claude, Recorded());

        Assert.Equal(AnswerOutcome.Delivered, (await host.Answer(Request(ClaudeId, "toolu_1", Chosen))).Outcome);

        Assert.NotEmpty(injector.Plans);
        var line = Assert.Single(host.Logged, l => l.StartsWith("audit_write_failed", StringComparison.Ordinal));
        Assert.Matches(@"^audit_write_failed kind=answer session=[A-Za-z0-9._-]{1,64} at=\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?Z$", line);
        Assert.DoesNotContain("token", line, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, host.Audit.PendingUnauditedHa);
    }

    [Fact]
    public async Task An_unaudited_HA_answer_is_counted_once_when_the_audit_next_writes_and_the_gate_fails_on_it()
    {
        var host = new HaAnswerAuditFixture(f.Root, Answerer(), block: true);
        injector.OnInject = _ => Fixture.Append(claude, Recorded());
        Assert.Equal(AnswerOutcome.Delivered, (await host.Answer(Request(ClaudeId, "toolu_1", Chosen))).Outcome);
        Assert.Equal(1, host.Audit.PendingUnauditedHa);

        host.Unblock();
        Assert.True(host.Audit.Append(new AgentAuditEntry("local", "r1", "someone", [], "agent.note", "x", "ok")));
        Assert.True(host.Audit.Append(new AgentAuditEntry("local", "r2", "someone", [], "agent.note", "x", "ok")));

        Assert.Equal("1", Assert.Single(host.Rows(), r => r.Action == "audit_write_failed").Target);
        Assert.Equal(0, host.Audit.PendingUnauditedHa);
        var report = GateAuditProjection.ReadWindow(host.Profile, DateTimeOffset.UtcNow.AddHours(-1));
        Assert.Equal(1, report.LegacyHaDispatches);
        Assert.Equal(GateVerdictKind.Fail, GateVerdict.Evaluate(report, new GateMessageEvidence(500, 0)).Kind);
    }

    [Fact]
    public async Task A_diagnostic_log_that_also_fails_cannot_stop_the_HA_answer_and_the_in_memory_count_is_flushed_later()
    {
        var host = new HaAnswerAuditFixture(f.Root, Answerer(), block: true, throwingLog: true);
        injector.OnInject = _ => Fixture.Append(claude, Recorded());

        Assert.Equal(AnswerOutcome.Delivered, (await host.Answer(Request(ClaudeId, "toolu_1", Chosen))).Outcome);

        Assert.Empty(host.Logged);
        Assert.Equal(1, host.Audit.PendingUnauditedHa);
        host.Unblock();
        Assert.True(host.Audit.Append(new AgentAuditEntry("local", "r1", "someone", [], "agent.note", "x", "ok")));
        Assert.Equal("1", Assert.Single(host.Rows(), r => r.Action == "audit_write_failed").Target);
    }

    [Fact]
    public async Task A_failed_flush_keeps_the_count_for_the_next_write()
    {
        var host = new HaAnswerAuditFixture(f.Root, Answerer(), block: true);
        injector.OnInject = _ => Fixture.Append(claude, Recorded());
        Assert.Equal(AnswerOutcome.Delivered, (await host.Answer(Request(ClaudeId, "toolu_1", Chosen))).Outcome);

        Assert.False(host.Audit.Append(new AgentAuditEntry("local", "r1", "someone", [], "agent.note", "x", "ok")));

        Assert.Equal(1, host.Audit.PendingUnauditedHa);
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

sealed class HaAnswerAuditFixture
{
    public string Profile { get; }
    public AgentAuditLog Audit { get; }
    public List<string> Logged { get; } = [];
    readonly string blocker;
    readonly Func<AnswerRequest, CancellationToken, Task<AnswerResult>> answer;
    public HaAnswerAuditFixture(string root, ISessionAnswerer answerer, long maxBytes = AgentAuditLog.MaxBytes, bool block = false, bool throwingLog = false)
    {
        Profile = Path.Combine(root, "default");
        Directory.CreateDirectory(Profile);
        blocker = Path.Combine(Profile, AgentAuditLog.FolderName);
        if (block) { File.WriteAllText(blocker, "not a folder"); }
        Audit = new AgentAuditLog(Profile, log: s => { if (throwingLog) { throw new IOException("diagnostic log unwritable"); } Logged.Add(s); }, maxBytes: maxBytes);
        answer = HaRunning.AnswerDelegate(answerer, Audit)!;
    }
    public void Unblock() { if (File.Exists(blocker)) { File.Delete(blocker); } }
    public List<AuditCommands.Row> Rows() => GateAuditProjection.Load(Profile).Rows;
    public Task<AnswerResult> Answer(AnswerRequest request) => answer(request, CancellationToken.None);
}
