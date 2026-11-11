using System.Xml.Linq;
using Xunit;

public sealed class SessionsHeaderTests
{
    [Fact]
    public void New_session_is_only_a_plus_in_a_named_square_target()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "AgentBridge.App", "SessionsPage.xaml"))) { dir = dir.Parent; }
        var page = XDocument.Load(Path.Combine(dir!.FullName, "src", "AgentBridge.App", "SessionsPage.xaml"));
        var button = page.Descendants().Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == "NewSession");
        var icon = Assert.Single(button.Elements());
        Assert.Equal("ContentControl", icon.Name.LocalName);
        Assert.Equal("{StaticResource AbIconPlus}", (string?)icon.Attribute("Style"));
        Assert.Equal("16", (string?)icon.Attribute("Width"));
        Assert.Equal("16", (string?)icon.Attribute("Height"));
        Assert.Equal("32", (string?)button.Attribute("Width"));
        Assert.Equal("32", (string?)button.Attribute("Height"));
        Assert.Equal("0", (string?)button.Attribute("Padding"));
        Assert.Equal("{StaticResource AccentButtonStyle}", (string?)button.Attribute("Style"));
        Assert.Equal("New session", (string?)button.Attribute("AutomationProperties.Name"));
        Assert.Equal("New session", (string?)button.Attribute("ToolTipService.ToolTip"));
    }
}
