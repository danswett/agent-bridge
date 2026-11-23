using AgentBridge.App.Core;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class UiExceptionGuardTests
{
    static readonly DateTimeOffset Start = new(2026, 11, 22, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Three_exceptions_in_ten_seconds_are_survived_and_the_fourth_is_not()
    {
        var guard = new UiExceptionGuard();
        Assert.True(guard.Allow(Start));
        Assert.True(guard.Allow(Start.AddSeconds(1)));
        Assert.True(guard.Allow(Start.AddSeconds(9)));
        Assert.False(guard.Allow(Start.AddSeconds(9.5)));
    }

    [Fact]
    public void A_quiet_window_lets_the_app_survive_again()
    {
        var guard = new UiExceptionGuard();
        for (var i = 0; i < 3; i++) { Assert.True(guard.Allow(Start.AddSeconds(i))); }
        Assert.True(guard.Allow(Start.AddSeconds(30)));
    }

    [Fact]
    public void Refused_exceptions_do_not_extend_the_storm()
    {
        var guard = new UiExceptionGuard();
        for (var i = 0; i < 3; i++) { Assert.True(guard.Allow(Start)); }
        Assert.False(guard.Allow(Start.AddSeconds(5)));
        Assert.True(guard.Allow(Start.AddSeconds(10.5)));
    }
}