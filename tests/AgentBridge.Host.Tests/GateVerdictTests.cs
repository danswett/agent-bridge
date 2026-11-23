using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class GateVerdictTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "gate-verdict-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-11-22T00:00:00Z"));
    string Profile => Path.Combine(root, "default");
    public void Dispose() { if (Directory.Exists(root)) { Directory.Delete(root, true); } }

    static GateAuditReport Report(int launches = 50, int answers = 30, int stops = 20, string[]? reasons = null, bool invalidated = false,
        int ha = 0, string path = "relay", string actor = "person-client") =>
        new([new("session.launch", path, actor, launches), new("session.answer", path, actor, answers), new("session.stop", path, actor, stops)],
            reasons ?? [], invalidated, ha);

    static readonly GateMessageEvidence Messages = new(500, 0);

    [Fact]
    public void Relay_sums_at_the_four_thresholds_with_no_fault_pass()
    {
        var verdict = GateVerdict.Evaluate(Report(), Messages);
        Assert.Equal((GateVerdictKind.Pass, 0), (verdict.Kind, verdict.ExitCode));
        Assert.Equal((50, 30, 20), (verdict.Relay["session.launch"], verdict.Relay["session.answer"], verdict.Relay["session.stop"]));
    }

    [Theory]
    [InlineData(49, 30, 20, 500, "launch")]
    [InlineData(50, 29, 20, 500, "approval")]
    [InlineData(50, 30, 19, 500, "stop")]
    [InlineData(50, 30, 20, 499, "message")]
    public void One_unmet_threshold_is_incomplete_and_names_it(int launches, int answers, int stops, int messages, string named)
    {
        var verdict = GateVerdict.Evaluate(Report(launches, answers, stops), new(messages, 0));
        Assert.Equal((GateVerdictKind.Incomplete, 2), (verdict.Kind, verdict.ExitCode));
        Assert.Contains(verdict.Reasons, r => r.Contains(named));
    }

    [Fact]
    public void A_legacy_HA_dispatch_fails_even_when_the_thresholds_are_unmet()
    {
        var verdict = GateVerdict.Evaluate(Report(launches: 1, answers: 0, stops: 0, ha: 1), new(10, 0));
        Assert.Equal((GateVerdictKind.Fail, 1), (verdict.Kind, verdict.ExitCode));
        Assert.Contains(verdict.Reasons, r => r.Contains("ha-fallback"));
    }

    [Fact]
    public void An_HA_success_never_earns_credit_and_is_a_fallback()
    {
        var verdict = GateVerdict.Evaluate(Report(launches: 50, answers: 30, stops: 20, ha: 100, path: "legacy-ha", actor: "ha-component"), Messages);
        Assert.Equal(GateVerdictKind.Fail, verdict.Kind);
        Assert.Equal(0, verdict.Relay["session.launch"]);
    }

    [Fact]
    public void A_message_recorder_HA_fallback_fails_the_window()
    {
        var verdict = GateVerdict.Evaluate(Report(), new(500, 1));
        Assert.Equal(GateVerdictKind.Fail, verdict.Kind);
        Assert.Contains(verdict.Reasons, r => r.Contains("ha-fallback"));
    }

    [Fact]
    public void An_invalidated_window_fails_with_its_totals_left_visible()
    {
        var verdict = GateVerdict.Evaluate(Report(invalidated: true), Messages);
        Assert.Equal(GateVerdictKind.Fail, verdict.Kind);
        Assert.Contains(verdict.Reasons, r => r.Contains("invalidated"));
    }

    [Fact]
    public void A_fault_wins_over_missing_evidence()
    {
        var verdict = GateVerdict.Evaluate(Report(invalidated: true, reasons: ["unresolved-intent"]), null);
        Assert.Equal(GateVerdictKind.Fail, verdict.Kind);
    }

    [Theory]
    [InlineData("unresolved-intent")]
    [InlineData("audit-incomplete")]
    [InlineData("sequence-break")]
    [InlineData("missing-window-evidence")]
    public void Missing_or_unresolved_audit_evidence_is_incomplete_even_at_the_thresholds(string reason)
    {
        var verdict = GateVerdict.Evaluate(Report(reasons: [reason]), Messages);
        Assert.Equal((GateVerdictKind.Incomplete, 2), (verdict.Kind, verdict.ExitCode));
        Assert.Contains(verdict.Reasons, r => r.Contains(reason));
    }

    [Fact]
    public void Missing_message_evidence_is_incomplete_never_zero_fallbacks()
    {
        var verdict = GateVerdict.Evaluate(Report(), null);
        Assert.Equal(GateVerdictKind.Incomplete, verdict.Kind);
        Assert.Contains(verdict.Reasons, r => r.Contains("message"));
    }

    [Fact]
    public void Local_successes_earn_no_relay_credit()
    {
        var local = new GateAuditReport([new("session.launch", "local", "person-client", 100), new("session.launch", "relay", "person-client", 10),
            new("session.answer", "relay", "person-client", 30), new("session.stop", "relay", "person-client", 20)], [], false);
        var verdict = GateVerdict.Evaluate(local, Messages);
        Assert.Equal(GateVerdictKind.Incomplete, verdict.Kind);
        Assert.Equal(10, verdict.Relay["session.launch"]);
    }

    [Fact]
    public void Relay_credit_sums_across_actor_kinds()
    {
        var mixed = new GateAuditReport([new("session.launch", "relay", "person-client", 30), new("session.launch", "relay", "agent", 20),
            new("session.answer", "relay", "person-client", 30), new("session.stop", "relay", "person-client", 20)], [], false);
        Assert.Equal(GateVerdictKind.Pass, GateVerdict.Evaluate(mixed, Messages).Kind);
    }

    // The window projection (owner decision, Nov 22): operations whose first row is at or after the start date, any run.
    DateTimeOffset Now => clock.GetUtcNow();

    string Rows(string path, string actor, string principal, string outcome, string reason, bool finish = true)
    {
        Directory.CreateDirectory(Profile);
        var log = new AgentAuditLog(Profile, clock);
        var op = Guid.NewGuid();
        var request = Guid.NewGuid().ToString();
        var session = Guid.NewGuid().ToString();
        var gate = new GateAuditMetadata(1, op, Guid.Empty, 0, "intent", path, actor, op, "none");
        var intent = new AgentAuditEntry("in", request, principal, [], "session.launch", request, "reserved", Gate: gate);
        Assert.True(log.Append(intent));
        if (finish) { Assert.True(log.Append(intent with { Target = session, Outcome = outcome, Gate = gate with { Phase = "terminal", ReasonCode = reason } })); }
        return request;
    }

    [Fact]
    public void The_window_counts_a_failed_legacy_HA_launch_as_a_dispatch()
    {
        var since = Now.AddMinutes(-1);
        Rows("legacy-ha", "ha-component", "ha:connector", "failed", "failed");
        var report = GateAuditProjection.ReadWindow(Profile, since);
        Assert.Equal(1, report.LegacyHaDispatches);
        Assert.Equal(GateVerdictKind.Fail, GateVerdict.Evaluate(report, Messages).Kind);
    }

    [Fact]
    public void The_window_does_not_count_a_failed_relay_operation_as_an_HA_dispatch()
    {
        var since = Now.AddMinutes(-1);
        Rows("relay", "person-client", "person:phone-1", "failed", "failed");
        Assert.Equal(0, GateAuditProjection.ReadWindow(Profile, since).LegacyHaDispatches);
    }

    [Fact]
    public void Relay_successes_with_no_run_earn_credit_inside_the_window()
    {
        var since = Now.AddMinutes(-1);
        Rows("relay", "person-client", "person:phone-1", "started", "none");
        var report = GateAuditProjection.ReadWindow(Profile, since);
        Assert.Equal(1, report.Totals.Single(t => t is { Action: "session.launch", Path: "relay" }).Successes);
        Assert.DoesNotContain("missing-run-context", report.IncompleteReasons);
    }

    [Fact]
    public void Operations_before_the_start_date_are_not_counted_and_a_restart_is_a_new_date()
    {
        Rows("legacy-ha", "ha-component", "ha:connector", "started", "none");
        clock.Advance(TimeSpan.FromDays(2));
        Rows("relay", "person-client", "person:phone-1", "started", "none");
        var report = GateAuditProjection.ReadWindow(Profile, Now.AddDays(-1));
        Assert.Equal(0, report.LegacyHaDispatches);
        Assert.Equal(1, report.Totals.Single(t => t.Action == "session.launch").Successes);
        var whole = GateAuditProjection.ReadWindow(Profile, Now.AddDays(-3));
        Assert.Equal(1, whole.LegacyHaDispatches);
    }

    [Fact]
    public void An_empty_window_is_missing_evidence_not_a_pass()
    {
        Rows("relay", "person-client", "person:phone-1", "started", "none");
        clock.Advance(TimeSpan.FromDays(1));
        var report = GateAuditProjection.ReadWindow(Profile, Now);
        Assert.Contains("missing-window-evidence", report.IncompleteReasons);
        Assert.Equal(GateVerdictKind.Incomplete, GateVerdict.Evaluate(report, Messages).Kind);
    }

    [Fact]
    public void An_operation_that_began_before_the_window_is_excluded_whole_not_unresolved()
    {
        Directory.CreateDirectory(Profile);
        var log = new AgentAuditLog(Profile, clock);
        var op = Guid.NewGuid();
        var request = Guid.NewGuid().ToString();
        var gate = new GateAuditMetadata(1, op, Guid.Empty, 0, "intent", "relay", "person-client", op, "none");
        var intent = new AgentAuditEntry("in", request, "person:phone-1", [], "session.launch", request, "reserved", Gate: gate);
        Assert.True(log.Append(intent));
        clock.Advance(TimeSpan.FromHours(2));
        var since = Now.AddHours(-1);
        Assert.True(log.Append(intent with { Target = Guid.NewGuid().ToString(), Outcome = "started", Gate = gate with { Phase = "terminal" } }));
        var report = GateAuditProjection.ReadWindow(Profile, since);
        Assert.DoesNotContain("unresolved-intent", report.IncompleteReasons);
        Assert.Empty(report.Totals);
    }
}
