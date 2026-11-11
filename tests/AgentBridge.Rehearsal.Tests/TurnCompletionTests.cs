using Xunit;

namespace AgentBridge.Rehearsal.Tests;

public class TurnCompletionTests
{
    static readonly DateTimeOffset Began = DateTimeOffset.UnixEpoch;

    static TurnCompletion Progress(bool compact = false, bool background = false, string? marker = "before") =>
        new(compact, background, marker, Began);

    static bool Complete(TurnCompletion progress, int seconds, string status = "idle",
        bool questionOpen = false, bool answered = false, bool replied = false) =>
        progress.Complete(status, questionOpen, answered, replied, Began.AddSeconds(seconds));

    [Fact]
    public void Idle_before_any_turn_progress_is_not_completion()
    {
        var progress = Progress(marker: null);
        progress.Observe("idle", "old reply");
        Assert.False(Complete(progress, 0));
        Assert.False(Complete(progress, 300));
        Assert.False(progress.Moved);
        Assert.False(progress.SawWork);
    }

    [Theory]
    [InlineData("working")]
    [InlineData("waiting")]
    [InlineData("agents")]
    [InlineData("shell")]
    public void Observed_work_requires_fifteen_seconds_of_uninterrupted_idle(string status)
    {
        var progress = Progress();
        progress.Observe(status, "before");
        Assert.True(progress.SawWork);
        Assert.False(Complete(progress, 0, status));
        Assert.False(Complete(progress, 3));
        Assert.False(Complete(progress, 17));
        Assert.True(Complete(progress, 18));
    }

    [Theory]
    [InlineData(false, 15)]
    [InlineData(true, 45)]
    public void A_changed_response_settles_even_when_the_work_was_not_sampled(bool background, int settle)
    {
        var progress = Progress(background: background);
        progress.Observe("idle", "after");
        Assert.True(progress.Moved);
        Assert.False(progress.SawWork);
        Assert.False(Complete(progress, 3));
        Assert.False(Complete(progress, 3 + settle - 1));
        Assert.True(Complete(progress, 3 + settle));
    }

    [Fact]
    public void A_question_or_renewed_work_restarts_the_idle_settling_period()
    {
        var progress = Progress();
        progress.Observe("working", "before");
        Assert.False(Complete(progress, 0));
        progress.Question();
        Assert.False(Complete(progress, 30, questionOpen: true, answered: true));
        Assert.False(Complete(progress, 33, answered: true));
        Assert.False(Complete(progress, 47, answered: true));
        Assert.False(Complete(progress, 48, "working", answered: true));
        Assert.False(Complete(progress, 51, answered: true));
        Assert.True(Complete(progress, 66, answered: true));
    }

    [Fact]
    public void A_question_that_clears_between_idle_samples_still_restarts_settling()
    {
        var progress = Progress();
        progress.Observe("working", "before");
        Assert.False(Complete(progress, 0));
        progress.Question();
        Assert.False(Complete(progress, 15, answered: true));
        Assert.True(Complete(progress, 30, answered: true));
    }

    [Fact]
    public void A_quiet_compaction_waits_a_minute_before_it_starts_settling()
    {
        var progress = Progress(compact: true);
        progress.Observe("idle", "before");
        Assert.False(Complete(progress, 59));
        Assert.False(Complete(progress, 60));
        Assert.False(Complete(progress, 74));
        Assert.True(Complete(progress, 75));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void An_answer_or_a_fresh_launch_reply_counts_as_progress(bool answered, bool replied)
    {
        var progress = Progress(marker: null);
        progress.Observe("idle", "first reply");
        Assert.False(Complete(progress, 0, answered: answered, replied: replied));
        Assert.True(Complete(progress, 15, answered: answered, replied: replied));
    }

    [Fact]
    public void A_launch_baseline_is_the_first_marker_seen_but_later_changes_stay_observed()
    {
        var progress = Progress(marker: null);
        progress.Observe("idle", "first");
        Assert.False(Complete(progress, 0));
        progress.Observe("idle", "second");
        Assert.True(progress.Moved);
        progress.Observe("idle", "first");
        Assert.True(progress.Moved);
        Assert.False(Complete(progress, 3));
        Assert.True(Complete(progress, 18));
    }
}
