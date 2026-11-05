using Microsoft.Extensions.Time.Testing;
using Xunit;

// Wait.UntilAsync, which nearly every test here waits with: its patience is a number of looks,
// and the wall clock only a watchdog, so a starved runner's stall costs a look, not the wait (#909).
public sealed class WaitTests
{
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

        // The watchdog is six patiences, 60 s: passed at the tenth look, nine 7.4 s gaps (66.6 s) in,
        // long before 500 looks.
        Assert.Equal(10, looks);
        Assert.Contains("timed out waiting for what never comes (the watchdog's 60 s of wall time ran out after 10 of 500 looks", failed.Message);
        Assert.Contains("longest gap between looks 7400 ms", failed.Message);
    }
}
