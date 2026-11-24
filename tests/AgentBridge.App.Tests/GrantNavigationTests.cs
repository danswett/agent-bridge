using System.Xml.Linq;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class GrantNavigationTests
{
    [Fact]
    public void Grant_issue_view_requires_explicit_digest_confirmation_and_receiver_readiness()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, "src", "AgentBridge.App", "GrantIssueView.xaml")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        var page = XDocument.Load(Path.Combine(
            directory.FullName, "src", "AgentBridge.App", "GrantIssueView.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var confirm = page.Descendants().Single(element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "GrantConfirm");
        Assert.Equal("OnConfirm", (string?)confirm.Attribute("Click"));
        Assert.Equal("{x:Bind ViewModel.CanConfirm, Mode=OneWay}", (string?)confirm.Attribute("IsEnabled"));
        var digest = page.Descendants().Single(element =>
            (string?)element.Attribute(xaml + "Name") == "PersonDigest");
        Assert.Null(digest.Attribute("Text"));
        Assert.Contains(page.Descendants(), element =>
            (string?)element.Attribute("Text") == "{x:Bind ViewModel.State, Mode=OneWay}");
    }
}
