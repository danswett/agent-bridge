using System.Text.Json;
using System.Xml.Linq;
using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// #1618 G5a: the group headers the Sessions list draws between Pinned and Sessions, and the per-device collapse (ui.md Groups).
public sealed class GroupHeaderTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "abgh-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider clock = new(Make.T0);
    readonly LockedDispatcher ui = new();
    string FilePath => Path.Combine(folder, "order.json");
    public void Dispose() { if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); } }

    HostConnection Host() => new(_ => Task.FromException<AgentBridge.Local.LocalChannel>(new InvalidOperationException("Offline test")), ui);

    MainViewModel Show(HostConnection local, string? file, params (string Id, bool Asks)[] sessions)
    {
        var machine = new Machine(Machine.LocalKey, local, new CardContext { Host = local, Deliveries = new Deliveries(local, ui), Folds = new FoldStore(null), Capabilities = () => local.Capabilities });
        var main = new MainViewModel(machine, new UsageViewModel(clock), order: new SessionOrder(file, clock));
        local.Sessions.BeginSnapshot();
        foreach (var (id, asks) in sessions) { local.Sessions.Upsert(Make.Session(id, question: asks ? Make.Choice() : null)); }
        local.Sessions.EndSnapshot();
        return main;
    }

    static SessionCardViewModel Card(MainViewModel main, string id) => main.AllCards.Single(c => c.Id == id);
    static string[] Ids(MainViewModel main) => [.. main.Sessions.Select(c => c.Id)];

    [Fact]
    public async Task A_group_draws_its_header_on_its_first_member_between_Pinned_and_Sessions()
    {
        await using var local = Host();
        var main = Show(local, null, ("a", false), ("b", false), ("c", false), ("d", false));
        var work = main.CreateGroup("Work");
        main.MoveToGroup(Card(main, "c"), work);
        main.MoveToGroup(Card(main, "b"), work);
        main.SetPinned(Card(main, "d"), true);
        Assert.Equal(["d", "b", "c", "a"], Ids(main));
        var header = Assert.Single(Card(main, "b").GroupHeaders);
        Assert.Equal((work, "Work", 2, 0, true, "Work · 2"), (header.Id, header.Name, header.Count, header.Needs, header.Expanded, header.Text));
        Assert.Empty(Card(main, "c").GroupHeaders);
        Assert.Equal(("Pinned", "Sessions"), (Card(main, "d").GroupHeader, Card(main, "a").GroupHeader));
    }

    [Fact]
    public async Task Collapsing_hides_the_members_but_keeps_the_ones_that_need_you_and_says_how_many()
    {
        await using var local = Host();
        var main = Show(local, null, ("a", false), ("b", false), ("c", true));
        var work = main.CreateGroup("Work");
        main.MoveToGroup(Card(main, "b"), work);
        main.MoveToGroup(Card(main, "c"), work);
        main.SetGroupCollapsed(work, true);
        Assert.Equal(["c", "a"], Ids(main));  // b is folded away; c asks, so it stays
        var header = Assert.Single(Card(main, "c").GroupHeaders);
        Assert.Equal((false, 2, 1, "Work · 2 · 1 need you"), (header.Expanded, header.Count, header.Needs, header.Text));
        Assert.False(main.CanMove(Card(main, "c"), 1));  // its neighbour in the group is folded away: a move would step over a row nobody sees
        Assert.False(main.Move(Card(main, "c"), 1));
        main.SetGroupCollapsed(work, false);
        Assert.Equal(["c", "b", "a"], Ids(main));
        Assert.True(main.CanMove(Card(main, "c"), 1));
    }

    [Fact]
    public async Task A_header_with_no_row_to_sit_on_goes_to_the_next_row_or_to_the_end_of_the_list_and_the_open_session_stays_listed()
    {
        await using var local = Host();
        var main = Show(local, null, ("a", false), ("b", false));
        var work = main.CreateGroup("Work");
        main.CreateGroup("Empty");
        main.MoveToGroup(Card(main, "b"), work);
        main.SetGroupCollapsed(work, true);
        Assert.Equal(["a"], Ids(main));
        Assert.Equal(["Work", "Empty"], Card(main, "a").GroupHeaders.Select(h => h.Name));  // nothing of Work shows: both wait for a
        Assert.True(Card(main, "a").GroupHeaders[1].Empty);
        main.SetPinned(Card(main, "a"), true);
        Assert.Empty(Card(main, "a").GroupHeaders);
        Assert.Equal(["Work", "Empty"], main.TrailingGroupHeaders.Select(h => h.Name));  // no row follows: the list's end carries them
        main.Open(null, "b");
        Assert.Equal(["a", "b"], Ids(main));  // the session open is never folded away
        Assert.DoesNotContain(main.TrailingGroupHeaders, h => h.Name == "Work");
        main.Selected = null;
        Assert.Equal(["a"], Ids(main));  // and folded away again once it is not
    }

    [Fact]
    public async Task Collapse_is_kept_on_this_device_outside_the_group_registers_and_goes_with_the_group()
    {
        await using var local = Host();
        var main = Show(local, FilePath, ("a", false));
        var work = main.CreateGroup("Work");
        main.SetGroupCollapsed(work, true);
        Assert.True(new SessionOrder(FilePath, clock).IsGroupCollapsed(work));
        using (var json = JsonDocument.Parse(File.ReadAllText(FilePath)))
        {
            Assert.Equal(work, Assert.Single(json.RootElement.GetProperty("collapsedGroups").EnumerateArray()).GetString());
            Assert.DoesNotContain("collapsed", json.RootElement.GetProperty("groupRecords").GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        main.DeleteGroup(work);
        Assert.False(new SessionOrder(FilePath, clock).IsGroupCollapsed(work));
        Assert.True(new SessionOrder(FilePath, clock).Groups.Items.Count == 0);
    }

    [Fact]
    public void Pressing_a_header_asks_for_the_opposite_state_and_an_empty_group_reads_zero()
    {
        (string Id, bool Open)? asked = null;
        new GroupHeader("g", "Work", 2, 0, true, (id, open) => asked = (id, open)).Toggle();
        Assert.Equal(("g", false), asked);
        var empty = new GroupHeader("g", "Work", 0, 0, false);
        Assert.Equal(("Work · 0", true, 0f), (empty.Text, empty.Empty, empty.Angle));
    }

    // The markup the headers are drawn from: a Disclosure button heard by name and state, on the row, and at the list's end.
    [Fact]
    public void The_header_is_a_named_disclosure_button_used_on_rows_and_at_the_list_end()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "AgentBridge.App", "SessionsPage.xaml"))) { root = root.Parent; }
        XDocument Load(string file) => XDocument.Load(Path.Combine(root!.FullName, "src", "AgentBridge.App", file));
        var button = Load("GroupHeaderView.xaml").Descendants().Single(e => e.Name.LocalName == "DisclosureButton");
        Assert.Equal("GroupHeader", (string?)button.Attribute("AutomationProperties.AutomationId"));
        Assert.Equal("{x:Bind Header.Text, Mode=OneWay}", (string?)button.Attribute("AutomationProperties.Name"));
        Assert.Equal("{x:Bind Header.Expanded, Mode=OneWay}", (string?)button.Attribute("IsExpanded"));
        Assert.Contains(Load("SessionRow.xaml").Descendants(), e => (string?)e.Attribute("ItemsSource") == "{x:Bind Card.GroupHeaders, Mode=OneWay}");
        Assert.Contains(Load("SessionsPage.xaml").Descendants(), e => (string?)e.Attribute("ItemsSource") == "{x:Bind ViewModel.TrailingGroupHeaders, Mode=OneWay}");
    }
}
