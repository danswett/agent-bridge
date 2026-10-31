using System.Runtime.Versioning;
using System.Security.Principal;
using AgentBridge.Host.Migration;
using Xunit;


public class TaskAccessTests
{
    [SupportedOSPlatform("windows")] static SecurityIdentifier User => new("S-1-5-21-1-2-3-1000");
    [SupportedOSPlatform("windows")] static SecurityIdentifier Administrators => new("S-1-5-32-544");

    // As the rehearsal's VM found them (#114): from an elevated prompt, Administrators own the
    // task and the user may only read it; from a normal one, the user owns it with full access.
    const string FromElevatedPrompt = "O:BAG:S-1-5-21-1-2-3-513D:(A;ID;0x1f019f;;;BA)(A;ID;0x1f019f;;;SY)(A;ID;FA;;;BA)(A;;FR;;;S-1-5-21-1-2-3-1000)";
    const string FromNormalPrompt = "O:S-1-5-21-1-2-3-1000G:S-1-5-21-1-2-3-513D:(A;ID;0x1f019f;;;BA)(A;ID;0x1f019f;;;SY)(A;ID;FA;;;S-1-5-21-1-2-3-1000)(A;;FR;;;S-1-5-21-1-2-3-1000)";

    [Fact]
    public void A_task_registered_from_an_elevated_prompt_cannot_be_changed_by_its_user_unelevated()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        Assert.False(TaskAccess.CanModify(FromElevatedPrompt, User, []));
    }

    [Fact]
    public void The_same_task_can_be_changed_with_the_administrators_group_enabled()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        Assert.True(TaskAccess.CanModify(FromElevatedPrompt, User, [Administrators]));
    }

    [Fact]
    public void A_task_registered_from_a_normal_prompt_can_be_changed_by_its_user()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        Assert.True(TaskAccess.CanModify(FromNormalPrompt, User, []));
    }

    [Fact]
    public void A_denial_listed_before_an_allowance_wins()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        Assert.False(TaskAccess.CanModify("D:(D;;GW;;;S-1-5-21-1-2-3-1000)(A;;FA;;;S-1-5-21-1-2-3-1000)", User, []));
    }

    [Fact]
    public void A_generic_write_allowance_counts_as_write()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        Assert.True(TaskAccess.CanModify("D:(A;;GW;;;S-1-5-21-1-2-3-1000)", User, []));
    }

    [Fact]
    public void A_descriptor_that_cannot_be_read_leaves_the_answer_unknown()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        Assert.Null(TaskAccess.CanModify("not a descriptor", User, []));
    }
}
