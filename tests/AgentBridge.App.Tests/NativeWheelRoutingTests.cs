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
    [InlineData("OwnedKeyboardChild", "DesktopChildSiteBridge")]
    [InlineData("DesktopChildSiteBridge", "DesktopChildSiteBridge")]
    [InlineData("InputSiteWindowClass", "Microsoft.UI.Content.DesktopChildSiteBridge")]
    [InlineData("Microsoft.UI.Content.DesktopChildSiteBridge", "Microsoft.UI.Content.DesktopChildSiteBridge")]
    public void A_verified_child_bridge_under_the_pointer_receives_the_wheel(string focusClass, string bridgeClass)
    {
        var bridge = Hit with { Class = bridgeClass };
        Assert.Equal(bridge, NativeWheelRouting.Select(1, 10, 20, Focus with { Class = focusClass }, bridge));
    }

    [Theory]
    [InlineData(0, 1, 10, 20)]
    [InlineData(1, 1, 10, 20)]
    [InlineData(3, 0, 10, 20)]
    [InlineData(3, 9, 10, 20)]
    [InlineData(3, 1, 0, 20)]
    [InlineData(3, 1, 99, 20)]
    [InlineData(3, 1, 10, 0)]
    [InlineData(3, 1, 10, 99)]
    public void An_unverified_or_foreign_child_bridge_is_refused(
        int hwnd, int root, uint thread, uint process)
    {
        foreach (var windowClass in new[] { "DesktopChildSiteBridge", "Microsoft.UI.Content.DesktopChildSiteBridge" })
        {
            var bridge = new NativeWheelTarget(hwnd, windowClass, root, thread, process);
            var error = Assert.Throws<InvalidOperationException>(() => NativeWheelRouting.Select(1, 10, 20, Focus, bridge));
            Assert.Contains("pointer input site is not verified", error.Message);
        }
    }

    [Theory]
    [InlineData(0, "Microsoft.UI.Input.InputSite.WindowClass", 1, 10, 20)]
    [InlineData(1, "Microsoft.UI.Input.InputSite.WindowClass", 1, 10, 20)]
    [InlineData(3, "Microsoft.UI.Input.InputSite.WindowClass", 9, 10, 20)]
    [InlineData(3, "Microsoft.UI.Input.InputSite.WindowClass", 1, 99, 20)]
    [InlineData(3, "Microsoft.UI.Input.InputSite.WindowClass", 1, 10, 99)]
    [InlineData(3, "OwnedKeyboardChild", 1, 10, 20)]
    [InlineData(3, "", 1, 10, 20)]
    [InlineData(3, "microsoft.ui.input.inputsite.windowclass", 1, 10, 20)]
    [InlineData(3, "desktopchildsitebridge", 1, 10, 20)]
    [InlineData(3, "DesktopChildSiteBridgeOther", 1, 10, 20)]
    [InlineData(3, "microsoft.ui.content.desktopchildsitebridge", 1, 10, 20)]
    [InlineData(3, "Microsoft.UI.Content.DesktopChildSiteBridgeOther", 1, 10, 20)]
    [InlineData(3, "Other.Microsoft.UI.Content.DesktopChildSiteBridge", 1, 10, 20)]
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
        foreach (var hit in new[] { Hit, Hit with { Class = "Microsoft.UI.Content.DesktopChildSiteBridge" } })
        {
            Assert.Throws<InvalidOperationException>(() => NativeWheelRouting.Select(1, 10, 20, focus, hit));
        }
    }
}
