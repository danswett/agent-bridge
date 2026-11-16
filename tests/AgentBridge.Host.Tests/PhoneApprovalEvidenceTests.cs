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

    static AuditCommands.Row Row(string device, string phase, string outcome, Guid operation, string path = "relay") =>
        new(At, $"person:{device}", "session.answer", outcome, Direction: path == "relay" ? "in" : "local", Request: operation.ToString(),
            Target: Guid.Empty.ToString("D"),
            Gate: new GateAuditMetadata(1, operation, Guid.NewGuid(), 1, phase, path, "person-client", Guid.NewGuid(), "none", Question: Guid.NewGuid()));

    static IReadOnlyList<AuditCommands.Row> Answered(string device, string path = "relay")
    {
        var operation = Guid.NewGuid();
        return [Row(device, "intent", "attempted", operation, path), Row(device, "terminal", "delivered", operation, path)];
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
        var operation = Guid.NewGuid();
        var report = Check([Row("phone-a", "intent", "attempted", operation), Row("phone-a", "terminal", "refused", operation)], true, ["phone-a"]);
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
