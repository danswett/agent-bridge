using AgentBridge.App.Core;
using Xunit;

// The self-test's look at whether a text box's clear button arms the answer guard (SelfTest.AnswerMoves). It
// settles unrelated setup first, then looks once, shortly after the button appears: a hold the appearance armed
// must still fail, however briefly the guard would have held (#3752's Codex review: waiting after the action tests
// only that a hold ends, so a real regression would pass).
public sealed class HoldProbeTests
{
    // A clock of the probe's own: waits cost nothing, and the question is free or held by what has happened.
    sealed class Question
    {
        public bool Held;
        public TimeSpan Now;
        public readonly List<string> Steps = [];
        public Func<bool> Free => () => !Held;
        public Task<bool> Until(Func<bool> free, TimeSpan timeout)
        {
            Steps.Add("settle");
            return Task.FromResult(free());
        }
        public Task Delay(TimeSpan span) { Now += span; Steps.Add($"wait {span.TotalMilliseconds:0}"); return Task.CompletedTask; }
    }

    [Fact]
    public async Task A_hold_the_buttons_appearance_arms_fails_the_look_however_soon_the_guard_would_let_go()
    {
        var q = new Question();

        var (settled, held) = await HoldProbe.AroundAsync(q.Free, q.Until, q.Delay, () => { q.Held = true; return Task.CompletedTask; });

        Assert.True(settled);
        Assert.True(held);
    }

    [Fact]
    public async Task A_button_that_arms_nothing_passes_the_look_even_after_setup_held_the_question_a_while()
    {
        var q = new Question { Held = true };
        // The setup's own hold ends while the probe settles, as a loaded runner's late layout does.
        Task<bool> until(Func<bool> free, TimeSpan timeout) { q.Held = false; return Task.FromResult(free()); }

        var (settled, held) = await HoldProbe.AroundAsync(q.Free, until, q.Delay, () => Task.CompletedTask);

        Assert.True(settled);
        Assert.False(held);
    }

    [Fact]
    public async Task A_hold_that_never_ends_before_the_action_is_not_settled_and_the_button_is_not_blamed()
    {
        var q = new Question { Held = true };

        var (settled, held) = await HoldProbe.AroundAsync(q.Free, q.Until, q.Delay, () => Task.CompletedTask);

        Assert.False(settled);
        Assert.True(held); // still held: the caller fails on settled first
    }

    [Fact]
    public async Task The_look_comes_after_a_quiet_longer_than_the_guard_and_a_short_wait_after_the_action()
    {
        var q = new Question();

        await HoldProbe.AroundAsync(q.Free, q.Until, q.Delay, () => { q.Steps.Add("act"); return Task.CompletedTask; });

        Assert.Equal(["settle", "wait 600", "act", "wait 100"], q.Steps);
        Assert.True(HoldProbe.Quiet > TimeSpan.FromMilliseconds(500));
    }
}
