using AgentBridge.App.Core;
using System.Xml.Linq;
using Xunit;

// The machine tile's words and states (#1147, ui.md §6.6.1), from existing fields only. The same
// vectors as the PWA's: a figure not reported yet is never 0 and never colour alone.
public sealed class MachineTileTests
{
    [Fact]
    public void Machines_buttons_share_one_44_pixel_target()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "AgentBridge.App", "MainWindow.xaml"))) { dir = dir.Parent; }
        var page = XDocument.Load(Path.Combine(dir!.FullName, "src", "AgentBridge.App", "MainWindow.xaml"));
        var machines = page.Descendants().Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == "MachinesPane");
        var ns = machines.Name.Namespace;
        var style = machines.Element(ns + "ScrollViewer.Resources")?.Elements(ns + "Style")
            .SingleOrDefault(e => (string?)e.Attribute("TargetType") == "Button" && e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) is null);
        Assert.NotNull(style);
        Assert.Equal("{StaticResource DefaultButtonStyle}", (string?)style.Attribute("BasedOn"));
        Assert.Equal("44", (string?)style.Elements(ns + "Setter").Single(e => (string?)e.Attribute("Property") == "MinHeight").Attribute("Value"));
        var buttons = machines.Descendants().Where(e => e.Name.LocalName == "Button").ToList();

        Assert.Contains(buttons, e => (string?)e.Attribute("AutomationProperties.AutomationId") == "LaunchHere");
        foreach (var button in buttons)
        {
            foreach (var property in new[] { "Style", "Height", "MinHeight", "MaxHeight", "Padding" })
            {
                Assert.Null(button.Attribute(property));
            }
        }
    }

    [Fact]
    public void Health_not_reported_says_so_in_words_and_never_shows_zero()
    {
        var health = MachineTile.Health(offline: false, lastSeen: null);

        Assert.Equal("Not measured yet", health.Text);
        Assert.NotEqual("", health.Icon);
        Assert.DoesNotContain("0", health.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Health_of_an_offline_machine_says_when_it_was_last_seen()
    {
        Assert.Equal("Offline · last seen 5m ago", MachineTile.Health(offline: true, lastSeen: "5m ago").Text);
    }

    [Fact]
    public void Health_icons_differ_by_shape_not_only_colour()
    {
        Assert.NotEqual(MachineTile.Health(false, null).Icon, MachineTile.Health(true, "5m ago").Icon);
    }

    [Theory]
    [InlineData(0, "No sessions running")]
    [InlineData(1, "1 session")]
    [InlineData(3, "3 sessions")]
    public void Running_says_none_or_just_the_count(int count, string want) => Assert.Equal(want, MachineTile.Running(count));

    [Theory]
    [InlineData("Online", false, false, true)]
    [InlineData("This machine", false, false, true)]
    [InlineData("Online", true, false, false)]
    [InlineData("Online", false, true, false)]
    [InlineData("Offline", false, false, false)]
    [InlineData("Connecting", false, false, false)]
    public void Launch_here_shows_only_for_an_online_machine_that_takes_it(string status, bool viewOnly, bool held, bool want) =>
        Assert.Equal(want, MachineTile.CanLaunchHere(status, viewOnly, held));

    [Theory]
    [InlineData("Offline", false, false, "Offline: it can't start sessions now.")]
    [InlineData("Online", true, false, "View only: it takes nothing from other devices.")]
    [InlineData("Online", false, true, "Discovery held: it can't start sessions now.")]
    [InlineData("Online", false, false, null)]
    public void Launch_here_hidden_says_why_in_a_line(string status, bool viewOnly, bool held, string? want) =>
        Assert.Equal(want, MachineTile.LaunchWhy(status, viewOnly, held));

    [Theory]
    [InlineData(1, 1, "1 machine · 1 online")]
    [InlineData(3, 2, "3 machines · 2 online")]
    [InlineData(2, 0, "2 machines · 0 online")]
    public void Fleet_summary_counts_machines_and_online_only(int total, int online, string want) =>
        Assert.Equal(want, MachineTile.Fleet(total, online));

    [Theory]
    [InlineData(899, false)]
    [InlineData(900, true)]
    public void The_list_and_tile_sit_side_by_side_from_900_wide(double width, bool wide) => Assert.Equal(wide, MachineTile.IsWide(width));
}
