using System.Xml.Linq;
using Xunit;

namespace AgentBridge.App.Tests;

public class FeedbackDialogTests
{
    [Fact]
    public void The_review_dialog_names_its_controls_and_keeps_optional_content_closed()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx"))) { root = Path.GetDirectoryName(root)!; }
        var path = Path.Combine(root, "src", "AgentBridge.App", "FeedbackDialog.xaml");
        Assert.True(File.Exists(path), "The Windows feedback entry needs a review/send dialog, not the unavailable placeholder.");
        var dialog = XDocument.Load(path).Root!;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var controls = dialog.DescendantsAndSelf().Where(e => e.Attribute(x + "Name") is not null).ToList();
        foreach (var control in controls)
        {
            Assert.False(string.IsNullOrWhiteSpace((string?)control.Attribute("AutomationProperties.AutomationId")));
            Assert.False(string.IsNullOrWhiteSpace((string?)control.Attribute("AutomationProperties.Name")));
        }
        Assert.Equal("False", (string?)controls.Single(e => e.Name.LocalName == "Expander").Attribute("IsExpanded"));
        Assert.Equal("False", (string?)controls.Single(e => (string?)e.Attribute(x + "Name") == "Turns").Attribute("IsChecked"));
        Assert.Contains("encrypted on this device, processed automatically", dialog.ToString(), StringComparison.Ordinal);
    }
}
