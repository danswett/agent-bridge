using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.App.Tests;

// What the UI looked like at its last answered ping, for the log when it freezes: open popups, the focused element and who holds the pointer.
// NAMES ONLY: a type and an x:Name, never text, content, titles or values.
public sealed class UiStateSnapshotTests
{
    static UiElementInfo El(string type, string? name = null, int captures = 0) => new(type, name, captures);

    [Fact]
    public void Nothing_open_nothing_focused_nothing_captured_says_so()
    {
        Assert.Equal("popups: none; focus: none; pointer capture: none", UiStateSnapshot.Describe([], null));
    }

    [Fact]
    public void Popups_the_focused_element_and_the_pointer_holder_are_named_by_type_and_name()
    {
        var text = UiStateSnapshot.Describe([El("Flyout", "MenuFlyout"), El("ContentDialog")], El("TextBox", "SearchBox"));

        Assert.Equal("popups: 2 (Flyout 'MenuFlyout', ContentDialog); focus: TextBox 'SearchBox'; pointer capture: none", text);
    }

    [Fact]
    public void An_element_holding_the_pointer_is_named_wherever_it_is_found()
    {
        var text = UiStateSnapshot.Describe([El("Popup", null, 1)], El("Button", "SendButton", 2));

        Assert.Contains("pointer capture: Popup, Button 'SendButton'", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("alice@example.com's draft", "")]            // not an identifier: dropped, never echoed
    [InlineData("Send Button\nwith a secret", "")]
    [InlineData("Search_1", " 'Search_1'")]
    public void Only_a_plain_identifier_is_ever_shown_as_a_name(string name, string shown)
    {
        var text = UiStateSnapshot.Describe([], El("TextBox", name));

        Assert.Equal($"popups: none; focus: TextBox{shown}; pointer capture: none", text);
        Assert.DoesNotContain("alice", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_type_that_is_not_a_plain_type_name_is_replaced_and_long_names_are_cut()
    {
        var text = UiStateSnapshot.Describe([], El("Text: my password is hunter2", new string('N', 100)));

        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.Contains("focus: Unknown '" + new string('N', 40) + "'", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_popup_list_is_cut_and_counted()
    {
        var popups = Enumerable.Range(1, 12).Select(i => El("Flyout", "F" + i)).ToArray();

        var text = UiStateSnapshot.Describe(popups, null);

        Assert.StartsWith("popups: 12 (", text, StringComparison.Ordinal);
        Assert.Contains("Flyout 'F8', +4 more)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("F9", text, StringComparison.Ordinal);
    }
}
