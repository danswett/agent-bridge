using AgentBridge.App.Core;
using Xunit;

// #3982: the posted native wheel is aimed at the window a real wheel hits, never at one that is not the app's.
public sealed class NativeWheelTargetTests
{
    const nint App = 0x100, Focus = 0x200, Child = 0x300, Elsewhere = 0x900;
    static nint Root(nint window) => window switch { App or Focus or Child => App, _ => 0x777 };

    [Fact]
    public void A_wheel_is_aimed_at_the_app_window_under_the_pointer_when_it_is_not_the_focus_window()
    {
        Assert.Equal(Child, NativeWheelTarget.Choose(App, Child, Focus, Root));
    }

    [Fact]
    public void When_the_window_under_the_pointer_is_the_focus_window_it_is_the_same_window_as_before()
    {
        // x64: the pointer's window and the focus window are one, so the aim does not change and no coverage is lost.
        Assert.Equal(Focus, NativeWheelTarget.Choose(App, Focus, Focus, Root));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x900)]
    public void With_nothing_or_another_processs_window_under_the_pointer_it_falls_back_to_the_focus_window(int under)
    {
        Assert.Equal(Focus, NativeWheelTarget.Choose(App, under, Focus, Root));
    }

    [Fact]
    public void With_no_app_window_it_falls_back_to_the_focus_window()
    {
        Assert.Equal(Focus, NativeWheelTarget.Choose(0, Child, Focus, Root));
    }
}
