using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class PhoneApprovalEvidenceTests
{
    static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-16T00:00:00Z");

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
        var report = PhoneApprovalEvidence.Project(Answered("phone-a"), true, ["phone-a", "phone-b"]);
        Assert.Equal(["phone-b"], report.Missing);
        Assert.False(report.Passed);
    }

    [Fact]
    public void Every_expected_phone_that_answered_through_the_relay_passes_with_its_own_counts()
    {
        var report = PhoneApprovalEvidence.Project([.. Answered("phone-a"), .. Answered("phone-a"), .. Answered("phone-b")], true, ["phone-a", "phone-b"]);
        Assert.Empty(report.Missing);
        Assert.True(report.Passed);
        Assert.Equal(2, report.Clients.Single(c => c.Client == "phone-a").AnsweredAway);
        Assert.Equal(1, report.Clients.Single(c => c.Client == "phone-b").Answered);
    }

    [Fact]
    public void An_answer_the_host_refused_is_asked_but_never_counted_as_answered()
    {
        var operation = Guid.NewGuid();
        var report = PhoneApprovalEvidence.Project([Row("phone-a", "intent", "attempted", operation), Row("phone-a", "terminal", "refused", operation)], true, ["phone-a"]);
        var client = Assert.Single(report.Clients);
        Assert.Equal((1, 0), (client.Asked, client.Answered));
        Assert.Equal(["phone-a"], report.Missing);
    }

    [Fact]
    public void An_answer_given_at_the_machine_does_not_show_a_phone_works_away_from_home()
    {
        var report = PhoneApprovalEvidence.Project(Answered("phone-a", "local"), true, ["phone-a"]);
        Assert.Equal(["phone-a"], report.Missing);
        Assert.Equal(0, report.Clients.Single().AnsweredAway);
    }

    [Fact]
    public void Evidence_from_an_incomplete_audit_log_never_passes()
    {
        var report = PhoneApprovalEvidence.Project(Answered("phone-a"), false, ["phone-a"]);
        Assert.False(report.Complete);
        Assert.False(report.Passed);
    }
}
