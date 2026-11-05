using Microsoft.Extensions.Time.Testing;
using Xunit;

// Wait.UntilAsync, which nearly every test here waits with: its patience is a number of pauses
// between looks, and the wall clock only a watchdog, so a starved runner's stall costs a pause,
// not the wait (#909). The first look is free; a wait gives up only on a look after its last pause.
public sealed class WaitTests
{
    // A pause of one Look on the fake clock, which counts itself.
    static Func<Task> Pause(FakeTimeProvider clock, Action<int> counted)
    {
        var pauses = 0;
        return () =>
        {
            clock.Advance(Wait.Look);
            counted(++pauses);
            return Task.CompletedTask;
        };
    }

    [Fact]
    public async Task A_wait_that_loses_seconds_between_two_looks_still_gets_its_patience_in_looks()
    {
        var clock = new FakeTimeProvider(Make.T0);
        var looks = 0;

        // Unscheduled for 7.4 s once, as the Arm runner was (#909), then looking as usual: 13.4 s
        // on the wall clock by the 301st look, past the 10 s that used to end the wait.
        await Wait.UntilAsync(() => ++looks > 300, "the 301st look", null, null, clock, () =>
        {
            clock.Advance(looks == 1 ? TimeSpan.FromMilliseconds(7400) : Wait.Look);
            return Task.CompletedTask;
        });

        Assert.Equal(301, looks);
    }

    [Fact]
    public async Task A_wait_whose_every_look_is_starved_ends_loudly_at_its_wall_clock_watchdog()
    {
        var clock = new FakeTimeProvider(Make.T0);
        var looks = 0;

        var failed = await Assert.ThrowsAsync<TimeoutException>(() => Wait.UntilAsync(() => { looks++; return false; }, "what never comes", null, null, clock, () =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(7400));
            return Task.CompletedTask;
        }));

        // The watchdog is six patiences, 60 s: passed at the tenth look, nine 7.4 s pauses (66.6 s)
        // in, long before 500 pauses.
        Assert.Equal(10, looks);
        Assert.Contains("timed out waiting for what never comes (the watchdog's 60 s of wall time ran out after 9 of 500 pauses, 10 checks; longest gap between looks 7400 ms;", failed.Message);
    }

    // The boundaries Copilot's review found (#931): a patience of one Look is one pause and a look
    // after it, never a give-up at the first look.
    [Fact]
    public async Task A_wait_of_one_look_true_at_its_second_look_passes_after_one_pause()
    {
        var clock = new FakeTimeProvider(Make.T0);
        var (checks, pauses) = (0, 0);

        await Wait.UntilAsync(() => ++checks == 2, "the second look", Wait.Look, null, clock, Pause(clock, n => pauses = n));

        Assert.Equal((2, 1), (checks, pauses));
    }

    [Fact]
    public async Task A_wait_of_one_look_never_true_gives_up_after_one_pause_and_two_looks()
    {
        var clock = new FakeTimeProvider(Make.T0);
        var (checks, pauses) = (0, 0);

        var failed = await Assert.ThrowsAsync<TimeoutException>(() => Wait.UntilAsync(() => { checks++; return false; }, "nothing", Wait.Look, null, clock, Pause(clock, n => pauses = n)));

        Assert.Equal((2, 1), (checks, pauses));
        Assert.Contains("timed out waiting for nothing (1 of 1 pauses, 2 checks, 0.0 s; longest gap between looks 20 ms;", failed.Message);
    }

    [Fact]
    public async Task A_wait_of_the_default_patience_true_at_its_501st_look_passes()
    {
        var clock = new FakeTimeProvider(Make.T0);
        var (checks, pauses) = (0, 0);

        await Wait.UntilAsync(() => ++checks == 501, "the 501st look", null, null, clock, Pause(clock, n => pauses = n));

        Assert.Equal((501, 500), (checks, pauses));
    }

    [Fact]
    public async Task A_wait_of_the_default_patience_never_true_gives_up_after_500_pauses()
    {
        var clock = new FakeTimeProvider(Make.T0);
        var (checks, pauses) = (0, 0);

        var failed = await Assert.ThrowsAsync<TimeoutException>(() => Wait.UntilAsync(() => { checks++; return false; }, "nothing", null, null, clock, Pause(clock, n => pauses = n)));

        // 500 pauses of 20 ms: the whole 10 s of patience, then a last look.
        Assert.Equal((501, 500), (checks, pauses));
        Assert.Contains("timed out waiting for nothing (500 of 500 pauses, 501 checks, 10.0 s; longest gap between looks 20 ms;", failed.Message);
    }
}