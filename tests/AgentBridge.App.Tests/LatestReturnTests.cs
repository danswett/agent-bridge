using AgentBridge.App.Core;
using Xunit;

public sealed class LatestReturnTests
{
    [Fact]
    public void Reaching_the_old_end_pins_before_animation_finishes_and_the_extent_grows()
    {
        var arrived = new LatestReturn(false, true).Scrolled(true, 59398, 59398);
        Assert.True(arrived.Pinned);
        Assert.False(arrived.Returning);
        var grown = arrived.Scrolled(false, 59451.46, 59920);
        Assert.True(grown.Pinned);
        Assert.False(grown.Returning);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_clamp_without_an_accepted_return_does_not_pin(bool intermediate)
    {
        Assert.False(new LatestReturn(false, false).Scrolled(intermediate, 59398, 59398).Pinned);
    }

    [Fact]
    public void A_return_finishing_short_of_the_end_does_not_arm_a_later_clamp()
    {
        var stopped = new LatestReturn(false, true).Scrolled(false, 59451.46, 59920);
        Assert.False(stopped.Returning);
        Assert.False(stopped.Scrolled(true, 59920, 59920).Pinned);
    }
}
