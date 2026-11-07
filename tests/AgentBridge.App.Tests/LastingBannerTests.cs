using AgentBridge.App.Core;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class LastingBannerTests
{
    static readonly DateTimeOffset T0 = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_banner_waits_out_its_grace_before_it_shows()
    {
        var rule = new LastingBanner();
        Assert.False(rule.Update(true, T0));
        Assert.False(rule.Update(true, T0 + TimeSpan.FromMilliseconds(2900)));
        Assert.True(rule.Update(true, T0 + TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void A_state_that_ends_inside_the_grace_is_never_shown_and_its_return_waits_the_grace_again()
    {
        var rule = new LastingBanner();
        rule.Update(true, T0);
        Assert.False(rule.Update(false, T0 + TimeSpan.FromSeconds(2)));
        Assert.False(rule.Update(true, T0 + TimeSpan.FromSeconds(4)));
        Assert.False(rule.Update(true, T0 + TimeSpan.FromSeconds(6)));
        Assert.True(rule.Update(true, T0 + TimeSpan.FromSeconds(7)));
    }

    [Fact]
    public void A_shown_banner_stays_ten_seconds_even_if_its_state_ends_and_goes_the_moment_it_is_up()
    {
        var rule = new LastingBanner();
        rule.Update(true, T0);
        rule.Update(true, T0 + TimeSpan.FromSeconds(3));
        Assert.True(rule.Update(false, T0 + TimeSpan.FromSeconds(5)));
        Assert.True(rule.Update(false, T0 + TimeSpan.FromMilliseconds(12900)));
        Assert.False(rule.Update(false, T0 + TimeSpan.FromSeconds(13)));
    }

    [Fact]
    public void A_state_that_returns_before_the_minimum_is_up_keeps_the_banner_where_it_was()
    {
        var rule = new LastingBanner();
        rule.Update(true, T0);
        rule.Update(true, T0 + TimeSpan.FromSeconds(3));
        rule.Update(false, T0 + TimeSpan.FromSeconds(5));
        Assert.True(rule.Update(true, T0 + TimeSpan.FromSeconds(6)));
        Assert.True(rule.Update(true, T0 + TimeSpan.FromSeconds(30)));
    }
}
