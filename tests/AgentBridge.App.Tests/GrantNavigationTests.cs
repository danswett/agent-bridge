using System.Xml.Linq;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class GrantNavigationTests
{
    [Fact]
    public void Fleet_provides_Grants_navigation_and_mounts_the_issue_view()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, "src", "AgentBridge.App", "MainWindow.xaml")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        var page = XDocument.Load(Path.Combine(
            directory.FullName, "src", "AgentBridge.App", "MainWindow.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var fleet = page.Descendants().Single(element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "MachinesPane");
        Assert.Contains(fleet.Descendants(), element =>
            element.Name.LocalName == "Button" &&
            (string?)element.Attribute("Click") == "OnGrants" &&
            ((string?)element.Attribute("Content") == "Grants" ||
                element.Descendants().Any(text => (string?)text.Attribute("Text") == "Grants")));
        Assert.Contains(fleet.Descendants(), element =>
            element.Name.LocalName == "GrantIssueView" &&
            (string?)element.Attribute(xaml + "Name") == "GrantIssuePane");
    }
}
