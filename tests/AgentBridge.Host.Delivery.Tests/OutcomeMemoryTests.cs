using AgentBridge.Host.Delivery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public class OutcomeMemoryTests
{
    [Fact]
    public async Task A_repeat_whose_recheck_confirms_at_once_hears_how_long_after_its_own_arrival_that_was()
    {
        // The recheck reads the late record and confirms without awaiting anything. Started before
        // the repeat's watch took its arrival, its confirmation came first, and the repeat heard
        // confirmed 0 for a read that took it 40 ms (#510 review).
        var time = new FakeTimeProvider(Fixture.T0);
        var memory = new OutcomeMemory<string>(time, TimeSpan.FromMinutes(10));
        var key = Guid.NewGuid();
        DeliveryTimeline? delivery = null;
        DeliveryTimeline Timeline() => new(time, "answer", key, _ => { });
        Assert.Equal("unconfirmed", await memory.Once(key, new DeliveryWatch(), Timeline, t =>
        {
            delivery = t;
            t.Typed(false);
            return Task.FromResult("unconfirmed");
        }, _ => { }));
        time.Advance(TimeSpan.FromSeconds(5));
        var repeat = new DeliveryWatch();
        var reads = 0;

        var later = await memory.Once(key, repeat, Timeline, _ => throw new InvalidOperationException("a repeat delivered again"), _ => { },
            recheck: _ =>
            {
                Interlocked.Increment(ref reads);
                time.Advance(TimeSpan.FromMilliseconds(40));
                delivery!.Confirmed();
                return Task.FromResult<string?>("delivered");
            });

        Assert.Equal("delivered", later);
        Assert.Equal(40, repeat.Timings!.Confirmed);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task Repeats_that_arrive_while_a_recheck_still_reads_share_it_rather_than_starting_their_own()
    {
        var time = new FakeTimeProvider(Fixture.T0);
        var memory = new OutcomeMemory<string>(time, TimeSpan.FromMinutes(10));
        var key = Guid.NewGuid();
        DeliveryTimeline Timeline() => new(time, "answer", key, _ => { });
        await memory.Once(key, new DeliveryWatch(), Timeline, _ => Task.FromResult("unconfirmed"), _ => { });
        var reading = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        Task<string?> Recheck(string _) { Interlocked.Increment(ref reads); return reading.Task; }

        var first = memory.Once(key, new DeliveryWatch(), Timeline, _ => throw new InvalidOperationException(), _ => { }, Recheck);
        var second = memory.Once(key, new DeliveryWatch(), Timeline, _ => throw new InvalidOperationException(), _ => { }, Recheck);
        reading.SetResult("delivered");

        Assert.Equal(["delivered", "delivered"], [await first, await second]);
        Assert.Equal(1, reads);
    }
}
