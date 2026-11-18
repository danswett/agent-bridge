using Xunit;

public sealed class PollClockTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_launch_budget_waits_for_its_first_look_despite_wall_clock_quiet(bool unrelatedLook)
    {
        await using var clock = new PollClock(frozen: true);
        clock.Run();
        clock.ExpectLook();
        var before = clock.GetUtcNow();
        var budget = Task.Delay(TimeSpan.FromSeconds(5), clock);
        if (unrelatedLook)
        {
            Task other;
            using (ExecutionContext.SuppressFlow())
            {
                other = Task.Run(() => { clock.ExpectLook(); clock.Looked(); clock.Looked(); });
            }
            await other;
        }
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(budget.IsCompleted);
        Assert.Equal(before, clock.GetUtcNow());
        await Task.Run(clock.Looked);
        await budget.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(before.AddSeconds(5), clock.GetUtcNow());
        clock.Looked();
        await Task.Delay(TimeSpan.FromSeconds(5), clock).WaitAsync(TimeSpan.FromSeconds(10));
    }
}
