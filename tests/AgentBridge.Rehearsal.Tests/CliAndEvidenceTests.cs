using AgentBridge.Protocol;
using Xunit;

namespace AgentBridge.Rehearsal.Tests;

public class CliAndEvidenceTests
{
    [Fact]
    public void An_option_a_command_does_not_take_is_refused_rather_than_ignored()
    {
        var e = Assert.Throws<UsageException>(() => Cli.Parse(["g5", "--expect-online"]));
        Assert.Contains("--expect-online", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_longest_command_wins_and_its_arguments_and_options_are_read()
    {
        var p = Cli.Parse(["expect", "answer", "abc12345", "--field", "a=1", "--field", "b=2", "--step", "E2-03"]);
        Assert.Equal("expect answer", p.Name);
        Assert.Equal(["abc12345"], p.Args);
        Assert.Equal(["a=1", "b=2"], p.All("--field"));
        Assert.Equal("E2-03", p.Option("--step"));
    }

    [Fact]
    public void A_missing_required_option_a_repeated_single_option_and_a_stray_argument_are_refused()
    {
        Assert.Throws<UsageException>(() => Cli.Parse(["expect", "reply", "abc12345"]));
        Assert.Throws<UsageException>(() => Cli.Parse(["log", "--contains", "a", "--contains", "b"]));
        Assert.Throws<UsageException>(() => Cli.Parse(["host", "extra"]));
        Assert.Throws<UsageException>(() => Cli.Parse(["note", "--result", "pass", "--detail", "seen"]));
    }

    [Fact]
    public void The_report_shows_each_steps_latest_attempt_and_the_checklist_steps_not_yet_run()
    {
        using var s = new Scratch();
        var evidence = new Evidence(Path.Combine(s.Root, "run-1"));
        var at = new DateTimeOffset(2026, 10, 28, 20, 0, 0, TimeSpan.Zero);
        evidence.Record("E2-01", "expect answer", ["expect", "answer"], Outcome.Fail("nothing recorded within 30 s"), at);
        evidence.Record("E2-01", "expect answer", ["expect", "answer"], Outcome.Pass("the session recorded the answer"), at.AddMinutes(1));
        evidence.Record("D-03", "g5", ["g5"], Outcome.Pass("agent-ha-bridge is not running for this user"), at);
        var report = evidence.Report(TimeZoneInfo.Utc, ["D-03", "E2-01", "F-02"]);
        Assert.Contains("| E2-01 | pass | 2 | 2026-10-28 20:01:00 |", report, StringComparison.Ordinal);
        Assert.Contains("2 steps: 2 pass.", report, StringComparison.Ordinal);
        Assert.Contains("Not run yet (1): F-02.", report, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mark_is_kept_between_runs_of_the_tool()
    {
        using var s = new Scratch();
        var at = DateTimeOffset.UtcNow;
        new Evidence(s.Root).SaveMark("abc12345", new Mark(Transcripts.Copilot, "events.jsonl", 42, at));
        var mark = new Evidence(s.Root).Marks()["ABC12345"];
        Assert.Equal(42, mark.Offset);
        Assert.Equal(Transcripts.Copilot, mark.Kind);
    }

    static Session Session(string id, AgentKind agent, SessionStatus status, Activity? activity = null, Question? question = null) => new()
    {
        Id = id,
        Agent = agent,
        Status = status,
        StatusSince = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Activity = activity ?? new Activity(),
        Question = question,
        Title = $"rehearsal {id}",
    };

    [Fact]
    public void Id_only_gives_the_one_matching_session_and_fails_on_none_or_several()
    {
        Session[] sessions = [Session("cp-11111111", AgentKind.Copilot, SessionStatus.Idle), Session("cl-22222222", AgentKind.Claude, SessionStatus.Working)];
        var one = HostChecks.Sessions(sessions, Cli.Parse(["sessions", "--agent", "claude", "--id-only"]));
        Assert.Equal(Verdict.Pass, one.Verdict);
        Assert.Equal("cl-22222222", one.Detail);
        Assert.Equal(Verdict.Fail, HostChecks.Sessions(sessions, Cli.Parse(["sessions", "--title", "rehearsal", "--id-only"])).Verdict);
        Assert.Equal(Verdict.Fail, HostChecks.Sessions(sessions, Cli.Parse(["sessions", "--agent", "codex", "--id-only"])).Verdict);
    }

    [Fact]
    public void A_session_is_checked_for_what_it_waits_on_and_how_often_it_compacted()
    {
        Session[] sessions =
        [
            Session("cp-11111111", AgentKind.Copilot, SessionStatus.Idle, new Activity { Compactions = 1, Background = Background.Of(0, 1) },
                new Question { Id = "q1", Kind = QuestionKind.Approval, Message = "run git status?", AskedAt = DateTimeOffset.UtcNow, AnswerAtMachine = true }),
        ];
        Assert.Equal(Verdict.Pass, HostChecks.Sessions(sessions, Cli.Parse(["sessions", "--id", "cp-11111111", "--expect-background", "--expect-compactions", "1", "--expect-answer-at-machine"])).Verdict);
        Assert.Equal(Verdict.Fail, HostChecks.Sessions(sessions, Cli.Parse(["sessions", "--id", "cp-11111111", "--expect-question"])).Verdict);
        Assert.Equal(Verdict.Pass, HostChecks.Sessions(sessions, Cli.Parse(["sessions", "--id", "gone-0000", "--expect-gone"])).Verdict);
    }
}
