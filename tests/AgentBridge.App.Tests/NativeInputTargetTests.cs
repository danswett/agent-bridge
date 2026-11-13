using Xunit;

namespace AgentBridge.App.Tests;

public sealed class NativeInputTargetTests
{
    static readonly NativeInputTarget Target = new(1, 2, 1, 1, 10, 20, 10, 20);

    [Fact]
    public void The_foreground_apps_focused_child_can_receive_native_input() => Assert.True(Target.Ready);

    [Fact]
    public void A_background_app_cannot_receive_the_test_input() =>
        Assert.False((Target with { Foreground = 9 }).Ready);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Missing_focus_and_the_top_level_fallback_cannot_receive_the_test_input(int focus) =>
        Assert.False((Target with { Focus = focus }).Ready);

    [Fact]
    public void A_foreign_root_thread_or_process_cannot_receive_the_test_input()
    {
        Assert.False((Target with { Root = 9 }).Ready);
        Assert.False((Target with { Thread = 99 }).Ready);
        Assert.False((Target with { Process = 99 }).Ready);
        Assert.False((Target with { Window = 0, Root = 0, Foreground = 0 }).Ready);
        Assert.False((Target with { Thread = 0, WindowThread = 0 }).Ready);
        Assert.False((Target with { Process = 0, WindowProcess = 0 }).Ready);
    }
}
