using Xunit;

// PollClock's rule for a budget once a frozen clock is let run: a budget (a wait for a session to register, a window to
// look ready) is jumped to only after the service has LOOKED, never because a slow machine kept its first look waiting.
public sealed class PollClockTests
{
    [Fact]
    public async Task A_budget_is_not_jumped_to_before_the_service_has_looked_however_slow_its_first_look_is()
    {
        await using var clock = new PollClock(DateTimeOffset.UtcNow, frozen: true);
        var fired = new List<string>();
        void Note(string what) { lock (fired) { fired.Add(what); } }
        clock.Run();
        using var budget = clock.CreateTimer(_ => Note("budget"), null, TimeSpan.FromMilliseconds(300), Timeout.InfiniteTimeSpan);

        // A loaded machine: the service is still busy with its first look, so nothing but the budget is armed, for longer than
        // the old 200 ms of wall time that was taken for "it is only waiting on the budget".
        await Task.Delay(500);
        lock (fired) { Assert.Empty(fired); }

        // Its first looks are then made: it waits a poll interval between each. Only after these does the budget run out.
        using var poll = clock.CreateTimer(_ => Note("look"), null, TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(25));
        await WaitAsync(() => { lock (fired) { return fired.Contains("budget"); } });

        lock (fired)
        {
            var before = fired.TakeWhile(x => x != "budget").Count(x => x == "look");
            Assert.True(before >= 3, $"the budget ran out after {before} looks: {string.Join(",", fired.Take(8))}");
        }
    }

    [Fact]
    public async Task A_service_that_never_polls_still_has_its_budget_jumped_to_after_the_backstop()
    {
        await using var clock = new PollClock(DateTimeOffset.UtcNow, frozen: true);
        var ran = new TaskCompletionSource();
        clock.Run();
        using var budget = clock.CreateTimer(_ => ran.TrySetResult(), null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);

        await ran.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    static async Task WaitAsync(Func<bool> done)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            await Task.Delay(10);
        }
    }
}
