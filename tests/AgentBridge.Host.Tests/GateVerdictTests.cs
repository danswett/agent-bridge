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

    [Theory]
    [InlineData(500, -1)]
    [InlineData(-1, 0)]
    [InlineData(int.MinValue, int.MinValue)]
    public void Message_evidence_with_a_negative_count_cannot_be_constructed(int delivered, int fallbacks) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new GateMessageEvidence(delivered, fallbacks));


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

    // Trim detection is per stream: launches live in launches-*.jsonl and everything else in agents-*.jsonl, each with
    // its own retained floor. Mixing the streams let an old launch row hide a trim of the agents stream (review P1-A).
    static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-10T00:00:00Z");

    static AuditCommands.Row GateRow(string action, DateTimeOffset at, long sequence, bool gate = true)
    {
        var op = Guid.NewGuid();
        return new(at, "person:phone-1", action, "reserved", Gate: gate ? new GateAuditMetadata(1, op, Guid.Empty, sequence, "intent", "relay", "person-client", op, "none") : null);
    }

    [Fact]
    public void An_old_launch_row_cannot_hide_a_trim_of_the_agents_stream()
    {
        AuditCommands.Row[] rows = [GateRow("session.launch", Start.AddDays(-10), 1), GateRow("session.answer", Start.AddDays(1), 5), GateRow("session.stop", Start.AddDays(2), 6)];
        Assert.True(GateAuditProjection.TrimmedIntoWindow(rows, agentsRetained: 4, launchesRetained: 0, Start));
    }

    [Fact]
    public void An_old_agents_row_cannot_hide_a_trim_of_the_launches_stream()
    {
        AuditCommands.Row[] rows = [GateRow("session.answer", Start.AddDays(-10), 1), GateRow("session.launch", Start.AddDays(1), 5)];
        Assert.True(GateAuditProjection.TrimmedIntoWindow(rows, agentsRetained: 0, launchesRetained: 4, Start));
    }

    [Fact]
    public void A_trim_that_stops_before_the_window_start_is_not_a_coverage_break()
    {
        AuditCommands.Row[] rows = [GateRow("session.answer", Start.AddDays(-2), 5), GateRow("session.launch", Start.AddDays(-1), 6)];
        Assert.False(GateAuditProjection.TrimmedIntoWindow(rows, agentsRetained: 4, launchesRetained: 4, Start));
    }

    [Fact]
    public void An_untrimmed_stream_is_never_a_break_even_when_its_rows_begin_after_the_start()
    {
        AuditCommands.Row[] rows = [GateRow("session.answer", Start.AddDays(3), 1), GateRow("session.launch", Start.AddDays(3), 2)];
        Assert.False(GateAuditProjection.TrimmedIntoWindow(rows, agentsRetained: 0, launchesRetained: 0, Start));
    }

    [Fact]
    public void A_trimmed_agents_stream_with_no_retained_gate_rows_is_a_break()
    {
        AuditCommands.Row[] rows = [GateRow("session.launch", Start.AddDays(-5), 1)];
        Assert.True(GateAuditProjection.TrimmedIntoWindow(rows, agentsRetained: 3, launchesRetained: 0, Start));
    }

    [Fact]
    public void Agents_rows_without_gate_metadata_do_not_bound_the_trim()
    {
        AuditCommands.Row[] rows = [GateRow("session.send", Start.AddDays(-5), 0, gate: false), GateRow("session.answer", Start.AddDays(1), 5)];
        Assert.True(GateAuditProjection.TrimmedIntoWindow(rows, agentsRetained: 4, launchesRetained: 0, Start));
    }

    // grant.* rows (reserve, issue, revoke, update) are not launch, approval or stop credit. Two reserves of the same
    // grant share an identity, so counting them tripped the duplicate-identity check and failed the window (review P2).
    void GrantReserve(AgentAuditLog log, Guid target, Guid incarnation)
    {
        var op = Guid.NewGuid();
        var binding = new string('a', 64);
        var gate = new GateAuditMetadata(1, op, Guid.Empty, 0, "intent", "relay", "person-client", incarnation, "none", GrantBinding: binding);
        var intent = new AgentAuditEntry("in", op.ToString(), "person:phone-1", [], "grant.reserve", target.ToString(), "reserved", Gate: gate);
        Assert.True(log.Append(intent));
        Assert.True(log.Append(intent with { Outcome = "admitted", Gate = gate with { Phase = "terminal" } }));
    }

    [Fact]
    public void Repeated_grant_reserves_in_the_window_do_not_invalidate_it()
    {
        Directory.CreateDirectory(Profile);
        var since = Now.AddMinutes(-1);
        var log = new AgentAuditLog(Profile, clock);
        var target = Guid.NewGuid();
        var incarnation = Guid.NewGuid();
        GrantReserve(log, target, incarnation);
        GrantReserve(log, target, incarnation);
        Rows("relay", "person-client", "person:phone-1", "started", "none");
        var report = GateAuditProjection.ReadWindow(Profile, since);
        Assert.False(report.Invalidated);
        Assert.DoesNotContain(report.Totals, t => t.Action.StartsWith("grant."));
        Assert.Equal(1, report.Totals.Single(t => t is { Action: "session.launch", Path: "relay" }).Successes);
    }

    // Real retention files (review 2/3): the log trims agents-*.jsonl itself once daily files are older than 30 days.
    // Evidence trimmed into the window is missing evidence: INCOMPLETE, never a pass and never an invented fault.
    void AnswerRow(AgentAuditLog log)
    {
        var op = Guid.NewGuid();
        var session = Guid.NewGuid().ToString();
        var gate = new GateAuditMetadata(1, op, Guid.Empty, 0, "intent", "relay", "person-client", op, "none", Question: Guid.NewGuid());
        var intent = new AgentAuditEntry("in", op.ToString(), "person:phone-1", [], "session.answer", session, "reserved", Gate: gate);
        Assert.True(log.Append(intent));
        Assert.True(log.Append(intent with { Outcome = "delivered", Gate = gate with { Phase = "terminal" } }));
    }

    // One agents row a day for 41 days, with a launch row on day 0 that launch retention keeps. Returns day 0.
    DateTimeOffset FortyOneDays()
    {
        Directory.CreateDirectory(Profile);
        var day0 = Now;
        var log = new AgentAuditLog(Profile, clock);
        Rows("relay", "person-client", "person:phone-1", "started", "none");
        for (var day = 0; day <= 40; day++)
        {
            AnswerRow(log);
            clock.Advance(TimeSpan.FromDays(1));
        }
        return day0;
    }

    [Fact]
    public void Agents_files_trimmed_into_the_window_are_incomplete_even_when_an_old_launch_row_survives()
    {
        var day0 = FortyOneDays();
        var report = GateAuditProjection.ReadWindow(Profile, day0.AddDays(5));
        Assert.Contains("retention-trimmed-into-window", report.IncompleteReasons);
        Assert.False(report.Invalidated);
        var verdict = GateVerdict.Evaluate(report, new GateMessageEvidence(500, 0));
        Assert.Equal(GateVerdictKind.Incomplete, verdict.Kind);
        Assert.Contains(verdict.Reasons, r => r.Contains("retention-trimmed-into-window"));
    }

    [Fact]
    public void A_window_older_than_the_thirty_day_retention_is_incomplete_once_files_have_been_trimmed()
    {
        var day0 = FortyOneDays();
        Assert.True(Now - day0.AddDays(5) > TimeSpan.FromDays(29));
        Assert.Contains("retention-trimmed-into-window", GateAuditProjection.ReadWindow(Profile, Now.AddDays(-36)).IncompleteReasons);
    }

    [Fact]
    public void Agents_files_trimmed_before_the_window_start_are_fine()
    {
        var day0 = FortyOneDays();
        var report = GateAuditProjection.ReadWindow(Profile, day0.AddDays(25));
        Assert.DoesNotContain("retention-trimmed-into-window", report.IncompleteReasons);
        Assert.False(report.Invalidated);
        Assert.Equal(16, report.Totals.Single(t => t is { Action: "session.answer", Path: "relay" }).Successes);
    }
}
