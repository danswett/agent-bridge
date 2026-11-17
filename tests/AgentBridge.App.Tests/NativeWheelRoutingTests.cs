using Xunit;

namespace AgentBridge.App.Tests;

public sealed class NativeWheelRoutingTests
{
    static readonly NativeWheelTarget Focus = new(2, "OwnedKeyboardChild", 1, 10, 20);
    static readonly NativeWheelTarget Hit = new(3, "Microsoft.UI.Input.InputSite.WindowClass", 1, 10, 20);

    [Theory]
    [InlineData("OwnedKeyboardChild")]
    [InlineData("Microsoft.UI.Input.InputSite.WindowClass")]
    public void The_wheel_goes_to_the_input_site_under_the_pointer_not_the_keyboard_focus(string focusClass) =>
        Assert.Equal(Hit, NativeWheelRouting.Select(1, 10, 20, Focus with { Class = focusClass }, Hit));

    [Fact]
    public void Focus_already_on_the_hit_input_site_keeps_that_target() =>
        Assert.Equal(Hit, NativeWheelRouting.Select(1, 10, 20, Hit, Hit));

    [Theory]
    [InlineData(0, "Microsoft.UI.Input.InputSite.WindowClass", 1, 10, 20)]
    [InlineData(1, "Microsoft.UI.Input.InputSite.WindowClass", 1, 10, 20)]
    [InlineData(3, "Microsoft.UI.Input.InputSite.WindowClass", 9, 10, 20)]
    [InlineData(3, "Microsoft.UI.Input.InputSite.WindowClass", 1, 99, 20)]
    [InlineData(3, "Microsoft.UI.Input.InputSite.WindowClass", 1, 10, 99)]
    [InlineData(3, "OwnedKeyboardChild", 1, 10, 20)]
    [InlineData(3, "", 1, 10, 20)]
    [InlineData(3, "microsoft.ui.input.inputsite.windowclass", 1, 10, 20)]
    public void An_unverified_hit_is_refused_instead_of_posting_to_the_focused_child(
        int hwnd, string windowClass, int root, uint thread, uint process)
    {
        var hit = new NativeWheelTarget(hwnd, windowClass, root, thread, process);
        var error = Assert.Throws<InvalidOperationException>(() => NativeWheelRouting.Select(1, 10, 20, Hit, hit));
        Assert.Contains("input site", error.Message);
        Assert.Contains("pointer", error.Message);
    }

    [Theory]
    [InlineData(0, 1, 10, 20)]
    [InlineData(1, 1, 10, 20)]
    [InlineData(2, 9, 10, 20)]
    [InlineData(2, 1, 99, 20)]
    [InlineData(2, 1, 10, 99)]
    public void A_valid_hit_does_not_bypass_the_focused_child_ownership_guard(
        int hwnd, int root, uint thread, uint process)
    {
        var focus = Focus with { Handle = hwnd, Root = root, Thread = thread, Process = process };
        Assert.Throws<InvalidOperationException>(() => NativeWheelRouting.Select(1, 10, 20, focus, Hit));
    }
}
