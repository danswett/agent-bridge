using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using Xunit;

public sealed class PhoneApprovalEvidenceTests
{
    static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-16T00:00:00Z");

    // Synthetic rows: no production writer emits person:<device> relay session.answer rows yet (#2649), so the
    // tests built on Row/Answered show how the recorder reads such rows, not that any phone has answered.
    static PhoneApprovalEvidenceReport Check(IEnumerable<AuditCommands.Row> rows, bool complete, string[] expected,
        bool adopted = false, bool coverage = false, long retainedThrough = 0) =>
        PhoneApprovalEvidence.Project([.. rows.Select((r, i) => r with { Gate = r.Gate! with { Sequence = retainedThrough + i + 1 } })],
            complete, expected, adopted, coverage, retainedThrough);

    // Every row of one operation shares its run, target, incarnation and question, as the real writer's rows do.
    sealed record Op(Guid Id, Guid Run, Guid Incarnation, Guid Question, Guid Target)
    {
        public static Op New() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    }

    static AuditCommands.Row Row(string device, string phase, string outcome, Op op, string path = "relay") =>
        new(At, $"person:{device}", "session.answer", outcome, Direction: path == "relay" ? "in" : "local", Request: op.Id.ToString(),
            Target: op.Target.ToString("D"),
            Gate: new GateAuditMetadata(1, op.Id, op.Run, 1, phase, path, "person-client", op.Incarnation, "none", Question: op.Question));

    static IReadOnlyList<AuditCommands.Row> Answered(string device, string path = "relay")
    {
        var op = Op.New();
        return [Row(device, "intent", "attempted", op, path), Row(device, "terminal", "delivered", op, path)];
    }

    // Per client (ClientKind/ClientPlatform from the host's roster, ha-audit-client-field.md): the same rows, grouped by client label.
    static AuditCommands.Row ClientRow(string device, string phase, string outcome, Op op, string? kind, string? platform) =>
        Row(device, phase, outcome, op) with { Gate = Row(device, phase, outcome, op).Gate! with { ClientKind = kind, ClientPlatform = platform } };

    static IReadOnlyList<AuditCommands.Row> AnsweredBy(string device, string? kind, string? platform)
    {
        var op = Op.New();
        return [ClientRow(device, "intent", "attempted", op, kind, platform), ClientRow(device, "terminal", "delivered", op, kind, platform)];
    }

    static PhoneApprovalEvidenceReport ByClient(IEnumerable<AuditCommands.Row> rows, bool complete, string[] expected) =>
        PhoneApprovalEvidence.ProjectByClient([.. rows.Select((r, i) => r with { Gate = r.Gate! with { Sequence = i + 1 } })], complete, expected);

    [Fact]
    public void Answers_are_counted_per_client_and_a_client_with_none_is_missing()
    {
        AuditCommands.Row[] rows = [.. AnsweredBy("phone-a", "pwa", "ios"), .. AnsweredBy("phone-a", "pwa", "ios"), .. AnsweredBy("pc-1", "app", "windows")];
        var report = ByClient(rows, true, ["pwa/ios", "app/windows", "app/macos"]);
        Assert.Equal(2, report.Clients.Single(c => c.Client == "pwa/ios").AnsweredAway);
        Assert.Equal(1, report.Clients.Single(c => c.Client == "app/windows").Answered);
        Assert.Equal(["app/macos"], report.Missing);
        Assert.False(report.Passed);
        Assert.True(ByClient(rows, true, ["pwa/ios", "app/windows"]).Passed);
    }

    [Fact]
    public void The_per_device_output_is_unchanged_by_the_new_fields()
    {
        AuditCommands.Row[] rows = [.. AnsweredBy("phone-a", "pwa", "ios"), .. AnsweredBy("phone-b", null, null)];
        var report = Check(rows, true, ["phone-a", "phone-b"]);
        Assert.Equal(["phone-a", "phone-b"], report.Clients.Select(c => c.Client));
        Assert.True(report.Passed);
        Assert.Equal(0, report.Unattributed);
    }

    [Fact]
    public void A_delivered_answer_without_the_client_is_unattributed_and_makes_every_client_incomplete_never_a_guess()
    {
        var report = ByClient([.. AnsweredBy("phone-a", "pwa", "ios"), .. AnsweredBy("phone-b", null, null)], true, ["pwa/ios"]);
        Assert.Equal(1, report.Unattributed);
        Assert.False(report.Complete);
        Assert.False(report.Passed);
        Assert.Equal(1, report.Clients.Single(c => c.Client == "pwa/ios").Answered);
        Assert.Equal(["pwa/ios"], report.Clients.Select(c => c.Client));
    }

    [Fact]
    public void An_operation_whose_rows_disagree_about_the_client_is_unattributed()
    {
        var op = Op.New();
        AuditCommands.Row[] rows = [ClientRow("phone-a", "intent", "attempted", op, "pwa", "ios"), ClientRow("phone-a", "terminal", "delivered", op, null, null)];
        var report = ByClient(rows, true, ["pwa/ios"]);
        Assert.Equal(1, report.Unattributed);
        Assert.Equal(["pwa/ios"], report.Missing);
        Assert.False(report.Passed);
    }

    [Fact]
    public void An_unattributed_refused_answer_also_makes_the_evidence_incomplete()
    {
        var op = Op.New();
        var report = ByClient([ClientRow("phone-a", "intent", "attempted", op, null, null), ClientRow("phone-a", "terminal", "refused", op, null, null)], true, []);
        Assert.Equal(1, report.Unattributed);
        Assert.False(report.Passed);
    }

    [Fact]
    public void Per_client_evidence_from_an_incomplete_log_or_with_no_rows_follows_the_same_rules()
    {
        Assert.False(ByClient(AnsweredBy("phone-a", "pwa", "ios"), false, ["pwa/ios"]).Passed);
        var empty = ByClient([], true, []);
        Assert.True(empty.Passed);
        Assert.Empty(empty.Clients);
    }
    [Fact]
    public void A_phone_that_never_answered_an_approval_is_missing_and_the_check_fails()
    {
        var report = Check(Answered("phone-a"), true, ["phone-a", "phone-b"]);
        Assert.Equal(["phone-b"], report.Missing);
        Assert.False(report.Passed);
    }

    [Fact]
    public void Given_synthetic_relay_rows_every_expected_phone_that_answered_passes_with_its_own_counts()
    {
        var report = Check([.. Answered("phone-a"), .. Answered("phone-a"), .. Answered("phone-b")], true, ["phone-a", "phone-b"]);
        Assert.Empty(report.Missing);
        Assert.True(report.Passed);
        Assert.Equal(2, report.Clients.Single(c => c.Client == "phone-a").AnsweredAway);
        Assert.Equal(1, report.Clients.Single(c => c.Client == "phone-b").Answered);
    }

    [Fact]
    public void An_answer_the_host_refused_is_asked_but_never_counted_as_answered()
    {
        var op = Op.New();
        var report = Check([Row("phone-a", "intent", "attempted", op), Row("phone-a", "terminal", "refused", op)], true, ["phone-a"]);
        var client = Assert.Single(report.Clients);
        Assert.Equal((1, 0), (client.Asked, client.Answered));
        Assert.Equal(["phone-a"], report.Missing);
    }

    [Fact]
    public void An_answer_given_at_the_machine_does_not_show_a_phone_works_away_from_home()
    {
        var report = Check(Answered("phone-a", "local"), true, ["phone-a"]);
        Assert.Equal(["phone-a"], report.Missing);
        Assert.Equal(0, report.Clients.Single().AnsweredAway);
    }

    [Fact]
    public void Evidence_from_an_incomplete_audit_log_never_passes()
    {
        var report = Check(Answered("phone-a"), false, ["phone-a"]);
        Assert.False(report.Complete);
        Assert.False(report.Passed);
    }

    [Theory]
    [InlineData(true, false, 0L)]
    [InlineData(false, true, 0L)]
    [InlineData(false, false, 4L)]
    public void Evidence_from_an_adopted_trimmed_or_coverage_broken_audit_log_never_passes(bool adopted, bool coverage, long retainedThrough)
    {
        var report = Check(Answered("phone-a"), true, ["phone-a"], adopted, coverage, retainedThrough);
        Assert.Empty(report.Missing);
        Assert.False(report.Passed);
    }

    [Fact]
    public void An_operation_with_both_a_delivered_and_a_refused_terminal_never_passes()
    {
        var op = Op.New();
        var report = Check([Row("phone-a", "intent", "attempted", op), Row("phone-a", "terminal", "delivered", op),
            Row("phone-a", "terminal", "refused", op)], true, ["phone-a"]);
        Assert.False(report.Passed);
    }

    [Fact]
    public void An_operation_whose_path_changes_between_its_rows_never_counts_as_an_answer_away()
    {
        var op = Op.New();
        var report = Check([Row("phone-a", "intent", "attempted", op, "relay"), Row("phone-a", "terminal", "delivered", op, "local")],
            true, ["phone-a"]);
        Assert.False(report.Passed);
    }

    [Fact]
    public void Evidence_with_a_repeated_or_missing_sequence_never_passes()
    {
        Assert.False(PhoneApprovalEvidence.Project(Answered("phone-a"), true, ["phone-a"]).Passed);
        var gap = Answered("phone-a").Select((r, i) => r with { Gate = r.Gate! with { Sequence = i * 2 + 1 } }).ToArray();
        Assert.False(PhoneApprovalEvidence.Project(gap, true, ["phone-a"]).Passed);
    }
}

// What the host really writes today for an answer (the writer's own rows, no synthetic ones): it is audited as
// person:local on the local path, so no phone is ever shown as having answered and the check cannot pass (#2649).
public sealed partial class AgentAnswerFlowTests
{
    [Fact]
    public async Task An_answer_the_real_writer_audits_shows_no_phone_and_the_check_does_not_pass()
    {
        var host = new ApprovalAuditFixture(f.Root, source, Answerer());
        injector.OnInject = _ => Fixture.Append(claude, Recorded());
        Assert.Equal(AnswerOutcome.Delivered, (await host.Answer(Request(ClaudeId, "toolu_1", Chosen))).Outcome);

        var report = PhoneApprovalEvidence.Read(host.Profile, ["phone-a"]);

        Assert.Equal("person:local", Assert.Single(host.Rows(), r => r.Gate!.Phase == "terminal").Principal);
        Assert.Equal(["phone-a"], report.Missing);
        Assert.False(report.Passed);
    }
}
