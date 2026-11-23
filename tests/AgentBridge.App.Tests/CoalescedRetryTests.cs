using AgentBridge.App.Core;
using Xunit;

namespace AgentBridge.App.Tests;

// #3811: a restore skipped by a layout storm is retried once after the window, however many passes were skipped.
public sealed class CoalescedRetryTests
{
    [Fact]
    public void Many_requests_while_one_is_pending_schedule_one_retry()
    {
        var retry = new CoalescedRetry(); var scheduled = new List<(TimeSpan Delay, Action Fire)>(); var ran = 0;
        for (var i = 0; i < 50; i++) { retry.Request(TimeSpan.FromSeconds(1), (d, fire) => scheduled.Add((d, fire)), () => ran++); }
        var only = Assert.Single(scheduled);
        Assert.Equal(TimeSpan.FromSeconds(1), only.Delay);
        Assert.Equal(0, ran);
        only.Fire();
        Assert.Equal(1, ran);
    }

    [Fact]
    public void After_it_fires_the_next_request_schedules_again()
    {
        var retry = new CoalescedRetry(); var scheduled = new List<Action>(); var ran = 0;
        retry.Request(TimeSpan.Zero, (_, fire) => scheduled.Add(fire), () => ran++);
        scheduled[0]();
        retry.Request(TimeSpan.Zero, (_, fire) => scheduled.Add(fire), () => ran++);
        Assert.Equal(2, scheduled.Count);
        scheduled[1]();
        Assert.Equal(2, ran);
    }

    [Fact]
    public void A_request_made_by_the_retry_itself_schedules_the_next_one_not_a_loop()
    {
        var retry = new CoalescedRetry(); var scheduled = new List<Action>(); var ran = 0;
        void Work() { ran++; retry.Request(TimeSpan.Zero, (_, fire) => scheduled.Add(fire), Work); }
        retry.Request(TimeSpan.Zero, (_, fire) => scheduled.Add(fire), Work);
        scheduled[0]();
        Assert.Equal(1, ran);
        Assert.Equal(2, scheduled.Count);
    }
}
