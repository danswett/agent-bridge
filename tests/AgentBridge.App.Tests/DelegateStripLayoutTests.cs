using System.Globalization;
using System.Xml.Linq;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class DelegateStripLayoutTests
{
    static XElement Strip()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "AgentBridge.App", "SessionView.xaml"))) { dir = dir.Parent; }
        return XDocument.Load(Path.Combine(dir!.FullName, "src", "AgentBridge.App", "SessionView.xaml"))
            .Descendants().Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == "DelegateStrip");
    }

    static double[] Margin(string value) => value.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();

    [Fact]
    public void Cards_and_overflow_text_leave_the_scrollbar_a_lane_outside_their_focus_bounds()
    {
        var strip = Strip();
        var style = strip.Descendants().Single(e => e.Name.LocalName == "Style" && (string?)e.Attribute("TargetType") == "ListViewItem");
        var item = Margin((string)style.Elements().Single(e => (string?)e.Attribute("Property") == "Margin").Attribute("Value")!);
        var footer = strip.Elements().Single(e => e.Name.LocalName == "ListView.Footer").Elements().Single();
        var more = Margin((string)footer.Attribute("Margin")!);

        // The expanded Windows scrollbar needs 16 DIPs, with 4 more for the focus visual.
        Assert.True(item[3] >= 20, $"Card bottom clearance was {item[3]} DIPs.");
        Assert.True(more[3] >= 20, $"Overflow text bottom clearance was {more[3]} DIPs.");
        Assert.True(item[0] >= 4 && item[1] >= 4 && item[2] >= 4);
        Assert.True(more[2] >= 4, "The last words need a trailing inset inside the scroll extent.");
        Assert.Equal("NoWrap", (string?)footer.Attribute("TextWrapping"));
        Assert.Equal("None", (string?)footer.Attribute("TextTrimming"));
    }
}
