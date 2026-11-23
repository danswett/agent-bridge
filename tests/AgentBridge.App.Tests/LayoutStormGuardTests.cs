using AgentBridge.App.Core;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class LayoutStormGuardTests
{
    static readonly DateTimeOffset Start = new(2026, 11, 22, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sixty_passes_in_a_second_proceed_and_the_next_is_a_storm_with_its_count()
    {
        var guard = new LayoutStormGuard();
        for (var i = 0; i < LayoutStormGuard.Limit; i++) { Assert.True(guard.Pass(Start.AddMilliseconds(i), out _)); }
        Assert.False(guard.Pass(Start.AddMilliseconds(900), out var count));
        Assert.Equal(LayoutStormGuard.Limit + 1, count);
    }

    [Fact]
    public void A_steady_fifty_passes_a_second_is_never_a_storm()
    {
        var guard = new LayoutStormGuard();
        for (var i = 0; i < 500; i++) { Assert.True(guard.Pass(Start.AddMilliseconds(i * 20), out var count)); Assert.True(count <= 51); }
    }

    [Fact]
    public void A_quiet_second_ends_the_storm()
    {
        var guard = new LayoutStormGuard();
        for (var i = 0; i < 200; i++) { guard.Pass(Start, out _); }
        Assert.False(guard.Pass(Start.AddMilliseconds(999), out _));
        Assert.True(guard.Pass(Start.AddSeconds(2), out var count));
        Assert.Equal(1, count);
    }
}