using Xunit;

namespace AgentBridge.App.Tests;

// The WinUI behavior is exercised by CheckResponseBeforeLoaded on hosted Windows.
// This offline guard keeps source initialization ahead of the unchanged-response cache.
public sealed class ResponseSourceWiringTests
{
    [Fact]
    public void The_first_attach_initializes_the_response_source_before_the_unchanged_response_shortcut()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx"))) { root = Path.GetDirectoryName(root)!; }
        var source = File.ReadAllText(Path.Combine(root, "src", "AgentBridge.App", "SessionView.Conversation.cs"));
        var start = source.IndexOf("void ShowResponse(bool reset)", StringComparison.Ordinal);
        var shortcut = source.IndexOf("if (!reset &&", start, StringComparison.Ordinal);
        var initialize = source.IndexOf("reset |= ConversationList.ItemsSource is null;", start, StringComparison.Ordinal);

        Assert.True(initialize > start && initialize < shortcut,
            "An unbound response list must take the reset path before an unchanged card can return.");
    }
}
