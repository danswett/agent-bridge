using System.Xml.Linq;
using Xunit;

public sealed class SessionsHeaderTests
{
    [Fact]
    public void Group_headers_are_independent_accessible_disclosures_in_the_virtualized_list()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "AgentBridge.App", "SessionsPage.xaml"))) { dir = dir.Parent; }
        var page = XDocument.Load(Path.Combine(dir!.FullName, "src", "AgentBridge.App", "SessionsPage.xaml"));
        var header = Assert.Single(page.Descendants(), e => (string?)e.Attribute("AutomationProperties.AutomationId") == "GroupHeader");
        Assert.Equal("DisclosureButton", header.Name.LocalName);
        Assert.Equal("{x:Bind Header, Mode=OneWay}", (string?)header.Attribute("AutomationProperties.Name"));
        Assert.Equal("{x:Bind Expanded, Mode=TwoWay}", (string?)header.Attribute("IsExpanded"));
        Assert.Contains(header.Ancestors(), e => e.Name.LocalName == "GroupStyle");
        Assert.Contains(page.Descendants(), e => e.Name.LocalName == "CollectionViewSource" && (string?)e.Attribute("IsSourceGrouped") == "True");
    }

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
