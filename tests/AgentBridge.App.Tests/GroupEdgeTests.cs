using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// #1617 G5c: Alt+Up/Down moves a row across group edges: the last row of a group into the next group, out of the
// last group into Sessions, Sessions into the last group; never into or out of Pinned; collapsed groups are skipped.
public sealed class GroupEdgeTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "abge-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider clock = new(Make.T0);
    string FilePath => Path.Combine(folder, "order.json");
    public void Dispose() { if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); } }
    static OrderEntry E(string id) => new("local", Make.Session(id));
    static string K(string id) => E(id).Key;
    static OrderEntry[] Rows(params string[] ids) => [.. ids.Select(E)];
    // The list as drawn: Pinned, then each group's rows, then the rows in no group (ui.md Groups).
    static string[] Ids(SessionOrder order, Arrangement a) =>
        [.. a.Pinned.Select(e => e.Session.Id),
         .. order.Groups.Items.SelectMany(g => a.Sessions.Where(e => order.Groups.GroupOf(e.Key) == g.Id)).Select(e => e.Session.Id),
         .. a.Sessions.Where(e => order.Groups.GroupOf(e.Key) is null).Select(e => e.Session.Id)];

    // Work: a, b | Home: c, d | Sessions: e, f   (Work and Home in that order)
    (SessionOrder Order, string Work, string Home, OrderEntry[] Rows) Seed(string? file = null)
    {
        var order = new SessionOrder(file, clock);
        var rows = Rows("a", "b", "c", "d", "e", "f");
        order.Arrange(rows);
        var work = order.Groups.Create("Work");
        var home = order.Groups.Create("Home");
        order.Groups.SetGroup(K("b"), work);
        order.Groups.SetGroup(K("a"), work);
        order.Groups.SetGroup(K("d"), home);
        order.Groups.SetGroup(K("c"), home);
        order.Arrange(rows);
        return (order, work, home, rows);
    }

    [Fact]
    public void The_last_row_of_a_group_moves_to_the_top_of_the_next_group()
    {
        var (order, work, home, rows) = Seed();
        Assert.Equal(["a", "b", "c", "d", "e", "f"], Ids(order, order.Arrange(rows)));
        Assert.True(order.CanMove(K("b"), 1));
        Assert.True(order.Move(K("b"), 1));
        Assert.Equal(home, order.Groups.GroupOf(K("b")));
        Assert.Equal(["a", "b", "c", "d", "e", "f"], Ids(order, order.Arrange(rows)));
        Assert.Equal(1, order.Groups.Counts()[work]);
        Assert.Equal(3, order.Groups.Counts()[home]);
    }

    [Fact]
    public void The_first_row_of_a_group_moves_to_the_bottom_of_the_one_before()
    {
        var (order, work, _, rows) = Seed();
        Assert.True(order.Move(K("c"), -1));
        Assert.Equal(work, order.Groups.GroupOf(K("c")));
        Assert.Equal(["a", "b", "c", "d", "e", "f"], Ids(order, order.Arrange(rows)));
        Assert.Equal(3, order.Groups.Counts()[work]);
    }

    [Fact]
    public void The_last_row_of_the_last_group_leaves_for_the_top_of_Sessions_and_the_first_of_Sessions_goes_back_in()
    {
        var (order, _, home, rows) = Seed();
        Assert.True(order.Move(K("d"), 1));
        Assert.Null(order.Groups.GroupOf(K("d")));
        Assert.Equal(["a", "b", "c", "d", "e", "f"], Ids(order, order.Arrange(rows)));
        Assert.True(order.Move(K("d"), -1));
        Assert.Equal(home, order.Groups.GroupOf(K("d")));
        order.Arrange(rows);  // the list is drawn again after each move
        Assert.True(order.Move(K("e"), -1));
        Assert.Equal(home, order.Groups.GroupOf(K("e")));
        Assert.Equal(["a", "b", "c", "d", "e", "f"], Ids(order, order.Arrange(rows)));
    }

    [Fact]
    public void Pinned_is_never_entered_or_left_and_the_very_first_and_last_positions_cannot_move()
    {
        var (order, work, _, rows) = Seed();
        order.SetPinned(K("f"), true);
        order.SetPinned(K("e"), true);
        order.Arrange(rows);  // Pinned: f, e | Work: a, b | Home: c, d
        Assert.False(order.CanMove(K("a"), -1));  // the first unpinned row cannot go up into Pinned
        Assert.False(order.Move(K("a"), -1));
        Assert.False(order.CanMove(K("e"), -1) && order.Groups.GroupOf(K("e")) is not null);
        Assert.False(order.CanMove(K("e"), 1));  // the last pinned row cannot go down into a group
        Assert.False(order.Move(K("e"), 1));
        Assert.True(order.IsPinned(K("e")));
        Assert.Null(order.Groups.GroupOf(K("e")));
        Assert.False(order.CanMove(K("d"), 1) && order.Groups.GroupOf(K("d")) is null);  // d goes to Sessions: only because Sessions is below
        order.Move(K("d"), 1);
        Assert.False(order.CanMove(K("d"), 1));  // the very last position
        Assert.Equal(work, order.Groups.GroupOf(K("a")));
    }

    [Fact]
    public void A_collapsed_group_is_skipped_both_ways()
    {
        var (order, work, home, rows) = Seed();
        order.SetGroupCollapsed(home, true);
        order.Arrange(rows);
        Assert.True(order.Move(K("b"), 1));  // Home is skipped: b lands at the top of Sessions
        Assert.Null(order.Groups.GroupOf(K("b")));
        order.Arrange(rows);
        Assert.True(order.Move(K("b"), -1));  // and back: Home is skipped, so Work's bottom
        Assert.Equal(work, order.Groups.GroupOf(K("b")));
    }

    [Fact]
    public void A_cross_group_move_survives_a_restart()
    {
        var (order, _, home, rows) = Seed(FilePath);
        Assert.True(order.Move(K("b"), 1));
        var again = new SessionOrder(FilePath, clock);
        Assert.Equal(home, again.Groups.GroupOf(K("b")));
        Assert.Equal(["a", "b", "c", "d", "e", "f"], Ids(again, again.Arrange(rows)));
    }

    [Fact]
    public void With_no_groups_a_move_stops_at_the_ends_as_before()
    {
        var order = new SessionOrder(null, clock);
        var rows = Rows("a", "b");
        order.Arrange(rows);
        Assert.False(order.CanMove(K("a"), -1));
        Assert.False(order.CanMove(K("b"), 1));
        Assert.True(order.CanMove(K("a"), 1));
    }
}
