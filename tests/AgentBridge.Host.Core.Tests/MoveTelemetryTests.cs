using AgentBridge.Host.Core.Move;
using Xunit;

// S5 of the session move (#1712): the time of each of the five steps, never a fake zero.
public class MoveTelemetryTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);
    const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    static MoveTelemetryRecorder New() => new("move-1", A, B, "session-1");

    [Fact]
    public void A_complete_move_has_five_steps_with_gaps_and_a_total_equal_to_their_sum()
    {
        var r = New();
        r.Step("handoffRequested", T0);
        r.Step("handoffConfirmed", T0.AddSeconds(4));
        r.Step("artifactsPushed", T0.AddSeconds(10));
        r.Step("newLaunched", T0.AddSeconds(11));
        r.Step("newOnline", T0.AddSeconds(15.5));
        var t = r.Finish("online");

        Assert.Equal(["handoffRequested", "handoffConfirmed", "artifactsPushed", "newLaunched", "newOnline"], t.Steps.Select(s => s.Name));
        Assert.Equal([0L, 4000, 6000, 1000, 4500], t.Steps.Select(s => s.Ms));
        Assert.Equal(15500, t.TotalMs);
        Assert.Equal(t.Steps.Sum(s => s.Ms), t.TotalMs);
        Assert.Equal("newOnline", t.LastReached);
    }

    [Fact]
    public void A_move_that_stops_leaves_later_steps_not_reached_and_never_zero()
    {
        var r = New();
        r.Step("handoffRequested", T0);
        r.Step("handoffConfirmed", T0.AddSeconds(3));
        var t = r.Finish("timeout");

        Assert.Equal(2, t.Steps.Count);
        Assert.True(t.Reached("handoffConfirmed"));
        Assert.False(t.Reached("artifactsPushed"));
        Assert.DoesNotContain(t.Steps, s => s.Name == "artifactsPushed");
        Assert.Null(t.TotalMs);
        Assert.Equal("handoffConfirmed", t.LastReached);
        Assert.Contains("stopped after handoffConfirmed", t.AuditLine());
        Assert.Contains("artifactsPushed not reached", t.AuditLine());
    }

    [Fact]
    public void A_move_that_never_started_reports_no_step_reached()
    {
        var t = New().Finish("refused", "target-busy");
        Assert.Empty(t.Steps);
        Assert.Null(t.LastReached);
        Assert.Equal("target-busy", t.Reason);
        Assert.Contains("handoffRequested not reached", t.AuditLine());
    }

    [Fact]
    public void A_skipped_step_is_not_reached_and_the_next_gap_runs_from_the_last_one_taken()
    {
        var r = New();
        r.Step("handoffRequested", T0);
        r.Step("newLaunched", T0.AddSeconds(9));
        var t = r.Finish("launched");
        Assert.False(t.Reached("handoffConfirmed"));
        Assert.Equal(9000, t.Steps[1].Ms);
        Assert.Null(t.TotalMs);
    }

    [Fact]
    public void A_step_taken_twice_keeps_the_first_time_and_an_unknown_name_is_refused()
    {
        var r = New();
        r.Step("handoffRequested", T0);
        r.Step("handoffRequested", T0.AddSeconds(5));
        Assert.Single(r.Finish("failed").Steps);
        Assert.Throws<ArgumentException>(() => New().Step("somethingElse", T0));
    }

    [Fact]
    public void A_step_that_arrives_after_a_later_one_is_refused_and_a_clock_step_back_is_never_negative()
    {
        var r = New();
        r.Step("handoffRequested", T0);
        r.Step("handoffConfirmed", T0.AddSeconds(-2));
        Assert.Equal(0, r.Finish("failed").Steps[1].Ms);

        var r2 = New();
        r2.Step("artifactsPushed", T0);
        Assert.Throws<InvalidOperationException>(() => r2.Step("handoffConfirmed", T0.AddSeconds(1)));
    }

    [Fact]
    public void Bytes_add_up_and_the_audit_line_holds_only_counts_sizes_and_times()
    {
        var r = New();
        r.Step("handoffRequested", T0);
        r.AddBytes(1000, 0);
        r.AddBytes(0, 2500);
        var t = r.Finish("failed", "push-failed");
        Assert.Equal(new MoveBytes(1000, 2500), t.Bytes);
        var line = t.AuditLine();
        Assert.Contains("move-1", line);
        Assert.Contains("bytes session=1000 workspace=2500", line);
        Assert.Throws<ArgumentOutOfRangeException>(() => New().AddBytes(-1, 0));
    }

    [Fact]
    public void The_outcome_must_be_one_of_the_five_the_contract_names()
    {
        Assert.Throws<ArgumentException>(() => New().Finish("done"));
    }
}
