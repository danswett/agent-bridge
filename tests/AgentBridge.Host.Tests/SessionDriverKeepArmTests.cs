using AgentBridge.Host.Core;
using Xunit;

namespace AgentBridge.Host.Tests;

// SessionDriver.KeepArm (docs/message-delivery.md "Busy sends to Claude", #3856): a held relay message keeps its arm waiting. It moves only the
// arm's time, so it grants nothing: the words, the principal and the label stay as they were.
public sealed class SessionDriverKeepArmTests
{
    static readonly DateTimeOffset T = DateTimeOffset.UnixEpoch.AddHours(1);

    [Fact]
    public void KeepArm_moves_only_the_time_of_the_arm_it_was_made_for()
    {
        var d = new SessionDrivers().For("s1");
        var (_, generation) = d.ArmAgent(T, "agent:m/a", "framed words", "label");
        var before = d.Save();
        Assert.True(d.KeepArm(T.AddMinutes(9), generation));
        var after = d.Save();
        Assert.Equal(T.AddMinutes(9), after.Pending);
        Assert.Equal((before.Driver, before.Principal, before.Expected, before.Label), (after.Driver, after.Principal, after.Expected, after.Label));
    }

    [Fact]
    public void KeepArm_does_nothing_for_another_generation_or_an_arm_that_is_gone()
    {
        var d = new SessionDrivers().For("s1");
        var (_, generation) = d.ArmAgent(T, "agent:m/a", "framed words", "label");
        Assert.False(d.KeepArm(T.AddMinutes(9), generation + 1));
        Assert.Equal(T, d.Save().Pending);
        d.Restore(new SessionDriver.Saved(SessionDrivers.Human, null, null));
        Assert.False(d.KeepArm(T.AddMinutes(11), generation));
        Assert.Null(d.Save().Pending);
    }
}