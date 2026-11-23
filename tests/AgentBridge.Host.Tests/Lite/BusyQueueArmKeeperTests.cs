using Microsoft.Extensions.Time.Testing;
using AgentBridge.Host.Lite;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class BusyQueueArmKeeperTests
{
    readonly FakeTimeProvider clock = new();

    (BusyQueueStore Store, string Request) Held()
    {
        var s = new BusyQueueStore(clock); var r = Guid.NewGuid().ToString();
        s.TryBegin(r, "p", "sess1"); s.KeysWent(r); s.Settle(r, BusyQueueStore.Unconfirmed, "noRow", held: true);
        return (s, r);
    }

    [Fact]
    public async Task The_arm_is_kept_every_minute_while_the_send_is_held_and_the_keeper_ends_when_it_is_freed()
    {
        var (s, r) = Held(); var kept = 0;
        var keeping = new BusyQueueArmKeeper(s, clock).KeepAsync(r, () => kept++);
        for (var i = 0; i < 5; i++) { clock.Advance(BusyQueueArmKeeper.Every); await Task.WhenAny(keeping, Task.Delay(5)); }
        Assert.True(kept >= 5);
        Assert.False(keeping.IsCompleted);
        s.Settle(r, BusyQueueStore.Delivered, null, held: false);
        clock.Advance(BusyQueueArmKeeper.Every);
        await keeping.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_record_that_is_not_held_or_unknown_is_never_kept_and_a_failing_keep_does_not_end_the_keeper()
    {
        var kept = 0;
        await new BusyQueueArmKeeper(new BusyQueueStore(clock), clock).KeepAsync(Guid.NewGuid().ToString(), () => kept++);
        Assert.Equal(0, kept);
        var (s, r) = Held();
        var keeping = new BusyQueueArmKeeper(s, clock).KeepAsync(r, () => { kept++; throw new InvalidOperationException("arm gone"); });
        for (var i = 0; i < 3; i++) { clock.Advance(BusyQueueArmKeeper.Every); await Task.WhenAny(keeping, Task.Delay(5)); }
        Assert.True(kept >= 3);
        Assert.False(keeping.IsCompleted);
        s.Release(r);
        clock.Advance(BusyQueueArmKeeper.Every);
        await keeping.WaitAsync(TimeSpan.FromSeconds(5));
    }
}