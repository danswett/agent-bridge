using AgentBridge.App.Core;
using System.Globalization;
using System.Xml.Linq;
using Xunit;

// The machine tile's words and states (#1147, ui.md §6.6.1), from existing fields only. The same
// vectors as the PWA's: a figure not reported yet is never 0 and never colour alone.
public sealed class MachineTileTests
{
    static XDocument MainWindow()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "AgentBridge.App", "MainWindow.xaml"))) { dir = dir.Parent; }
        return XDocument.Load(Path.Combine(dir!.FullName, "src", "AgentBridge.App", "MainWindow.xaml"));
    }

    [Fact]
    public void Fleet_names_the_page_heading_and_navigation_without_renaming_the_route()
    {
        var page = MainWindow();
        var nav = page.Descendants().Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == "NavMachines");
        Assert.Equal("Fleet", (string?)nav.Attribute("AutomationProperties.Name"));
        Assert.Equal("Fleet", (string?)nav.Attribute("ToolTipService.ToolTip"));
        Assert.Equal("machines", (string?)nav.Attribute("Tag"));
        var machines = page.Descendants().Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == "MachinesPane");
        var heading = machines.Descendants().Single(e => (string?)e.Attribute("AutomationProperties.HeadingLevel") == "Level1");
        Assert.Equal("Fleet", (string?)heading.Attribute("Text"));
        Assert.DoesNotContain(page.Descendants(), e =>
            (string?)e.Attribute("Text") == "Machines" &&
            e.Attribute("AutomationProperties.HeadingLevel") is not null);
    }

    [Fact]
    public void Machines_buttons_share_one_44_pixel_target()
    {
        var page = MainWindow();
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

    static string? Id(XElement e) => (string?)e.Attribute("AutomationProperties.AutomationId");
    static XElement MachinesPane() => MainWindow().Descendants().Single(e => Id(e) == "MachinesPane");
    static XElement Tile() => MachinesPane().Descendants().Single(e => Id(e) == "MachineRow");

    // #2604: every machine is an expanded card in an equal-width wrapping grid that fills the page.
    [Fact]
    public void Fleet_cards_fill_the_page_in_a_wrapping_grid_not_one_capped_column()
    {
        var pane = MachinesPane();
        var list = pane.Descendants().Single(e => Id(e) == "MachineList");

        Assert.DoesNotContain(list.AncestorsAndSelf().TakeWhile(e => e != pane), e => e.Attribute("MaxWidth") is not null);
        var ns = list.Name.Namespace;
        var layout = Assert.Single(list.Elements().Where(e =>
            e.Name == ns + (list.Name.LocalName + ".ItemsPanel") ||
            e.Name == ns + (list.Name.LocalName + ".Layout")));
        var panel = layout.Name.LocalName.EndsWith(".ItemsPanel", StringComparison.Ordinal)
            ? Assert.Single(Assert.Single(layout.Elements(ns + "ItemsPanelTemplate")).Elements())
            : Assert.Single(layout.Elements());
        Assert.Contains(panel.Name, new[] { ns + "ItemsWrapGrid", ns + "VariableSizedWrapGrid", ns + "UniformGridLayout" });
        Assert.Equal("Horizontal", (string?)panel.Attribute("Orientation"));
    }

    // #2604: two bar rows, CPU then Memory, inside the tile after Health and before Running.
    [Fact]
    public void Tile_draws_CPU_then_Memory_bars_after_Health_and_before_Running()
    {
        var order = Tile().Descendants().ToList();
        var health = order.FindIndex(e => Id(e) == "MachineHealth");
        var cpu = order.FindIndex(e => (string?)e.Attribute("Text") == "CPU");
        var memory = order.FindIndex(e => (string?)e.Attribute("Text") == "Memory");
        var running = order.FindIndex(e => Id(e) == "MachineRunning");

        Assert.True(health >= 0 && cpu > health && memory > cpu && running > memory,
            $"Health {health}, CPU {cpu}, Memory {memory}, Running {running}");
    }

    // #2604: each bar row keeps fixed label, bar and value columns, so refreshed figures never move it.
    [Theory]
    [InlineData("CPU")]
    [InlineData("Memory")]
    public void Each_bar_row_keeps_fixed_label_bar_and_value_columns(string label)
    {
        var text = Tile().Descendants().SingleOrDefault(e => (string?)e.Attribute("Text") == label);

        Assert.NotNull(text);
        var row = text.Parent!;
        Assert.Equal("Grid", row.Name.LocalName);
        var ns = row.Name.Namespace;
        var columns = row.Elements(ns + "Grid.ColumnDefinitions").Elements(ns + "ColumnDefinition").ToList();
        Assert.Equal(3, columns.Count);
        foreach (var column in new[] { columns[0], columns[2] })
        {
            Assert.True(double.TryParse((string?)column.Attribute("Width"), NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
                && double.IsFinite(width) && width > 0, "Label and value columns must have fixed positive widths.");
        }
        Assert.Equal("*", (string?)columns[1].Attribute("Width"));
        Assert.Equal("0", (string?)text.Attribute("Grid.Column") ?? "0");
        var bar = Assert.Single(row.Elements(ns + "ProgressBar"));
        Assert.Equal("1", (string?)bar.Attribute("Grid.Column"));
        Assert.Contains(row.Elements(ns + "TextBlock"), e => (string?)e.Attribute("Grid.Column") == "2");
    }

    [Theory]
    [InlineData(899, false)]
    [InlineData(900, true)]
    public void The_list_and_tile_sit_side_by_side_from_900_wide(double width, bool wide) => Assert.Equal(wide, MachineTile.IsWide(width));
}
