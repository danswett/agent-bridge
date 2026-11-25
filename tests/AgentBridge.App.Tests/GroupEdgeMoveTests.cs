using AgentBridge.App.Core;
using Xunit;

// Alt+Up/Down across group edges (#1617 G5c): groups in list order, then ungrouped Sessions; Pinned is a wall.
public sealed class GroupEdgeMoveTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "abe-" + Guid.NewGuid().ToString("N"));
    string FilePath => Path.Combine(folder, "order.json");
    static OrderEntry E(string id) => new("local", Make.Session(id));
    static string K(string id) => E(id).Key;
    public void Dispose() { if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); } }

    static readonly OrderEntry[] Rows = new[] { "a", "b", "c", "d", "e" }.Select(E).ToArray();

    // A = {a, b}, B = {c, d}, Sessions = {e}, in that order on screen.
    (SessionOrder Order, string A, string B) Seed(string? path = null)
    {
        var order = new SessionOrder(path);
        order.Arrange(Rows);
        var a = order.Groups.Create("A");
        var b = order.Groups.Create("B");
        foreach (var id in new[] { "e", "d", "c", "b", "a" }) { order.Groups.SetGroup(K(id), id is "a" or "b" ? a : id is "c" or "d" ? b : null); }
        return (order, a, b);
    }

    static string Screen(SessionOrder order)
    {
        var rows = order.Arrange(Rows).Sessions;
        var groups = order.Groups.Items.Select(g => g.Id).Append(null).ToList();
        return string.Join(" | ", groups.Select(g => string.Concat(rows.Where(r => order.Groups.GroupOf(r.Key) == g).Select(r => r.Session.Id))));
    }

    [Fact]
    public void Down_from_the_last_row_of_a_group_enters_the_next_group_at_its_top()
    {
        var (order, _, b) = Seed();
        Assert.Equal("ab | cd | e", Screen(order));
        Assert.True(order.Move(K("b"), 1));
        Assert.Equal("a | bcd | e", Screen(order));
        Assert.Equal(b, order.Groups.GroupOf(K("b")));
    }

    [Fact]
    public void Up_from_the_first_row_of_a_group_enters_the_previous_group_at_its_foot()
    {
        var (order, a, _) = Seed();
        Assert.True(order.Move(K("c"), -1));
        Assert.Equal("abc | d | e", Screen(order));
        Assert.Equal(a, order.Groups.GroupOf(K("c")));
    }

    [Fact]
    public void Down_from_the_last_group_leaves_it_for_Sessions_and_up_from_Sessions_enters_the_last_group()
    {
        var (order, _, b) = Seed();
        Assert.True(order.Move(K("d"), 1));
        Assert.Null(order.Groups.GroupOf(K("d")));
        Assert.Equal("ab | c | de", Screen(order));
        Assert.True(order.Move(K("d"), -1));
        Assert.Equal("ab | cd | e", Screen(order));
        Assert.Equal(b, order.Groups.GroupOf(K("d")));
        Assert.True(order.Move(K("e"), -1));
        Assert.Equal("ab | cde | ", Screen(order));
    }

    [Fact]
    public void A_row_enters_an_empty_group_and_leaves_it_again()
    {
        var (order, _, _) = Seed();
        var empty = order.Groups.Create("C");
        Assert.True(order.Move(K("e"), -1));
        Assert.Equal(empty, order.Groups.GroupOf(K("e")));
        Assert.Equal("ab | cd | e | ", Screen(order));
        Assert.True(order.Move(K("e"), 1));
        Assert.Null(order.Groups.GroupOf(K("e")));
    }

    [Fact]
    public void A_collapsed_group_is_skipped_in_both_directions()
    {
        var (order, a, b) = Seed();
        var c = order.Groups.Create("C");
        order.Groups.SetGroup(K("e"), c);
        order.GroupCollapsed = id => id == b;
        Assert.Equal("ab | cd | e | ", Screen(order));
        Assert.True(order.Move(K("b"), 1));
        Assert.Equal(c, order.Groups.GroupOf(K("b")));
        Assert.Equal("a | cd | be | ", Screen(order));
        Assert.True(order.Move(K("b"), -1));
        Assert.Equal(a, order.Groups.GroupOf(K("b")));
    }

    [Fact]
    public void Collapsed_groups_beyond_the_last_open_one_are_passed_to_Sessions()
    {
        var (order, _, b) = Seed();
        order.GroupCollapsed = id => id == b;
        Assert.True(order.Move(K("b"), 1));
        Assert.Null(order.Groups.GroupOf(K("b")));
    }

    [Fact]
    public void Pinned_is_a_wall_both_ways()
    {
        var (order, a, _) = Seed();
        order.SetPinned(K("a"), true);
        order.Arrange(Rows);
        Assert.False(order.CanMove(K("a"), 1));
        Assert.False(order.Move(K("a"), 1));
        Assert.True(order.IsPinned(K("a")));
        Assert.False(order.CanMove(K("b"), -1));
        Assert.False(order.Move(K("b"), -1));
        Assert.Equal(a, order.Groups.GroupOf(K("b")));
        Assert.False(order.IsPinned(K("b")));
    }

    [Fact]
    public void Pinned_is_a_wall_with_no_groups_at_all()
    {
        var order = new SessionOrder(null);
        order.Arrange(Rows);
        order.SetPinned(K("a"), true);
        order.Arrange(Rows);
        Assert.False(order.Move(K("a"), 1));
        Assert.False(order.Move(K("b"), -1));
        Assert.True(order.IsPinned(K("a")));
    }

    [Fact]
    public void The_very_first_and_last_rows_cannot_move_further()
    {
        var (order, _, _) = Seed();
        Assert.False(order.CanMove(K("a"), -1));
        Assert.False(order.CanMove(K("e"), 1));
        Assert.False(order.Move(K("a"), -1));
        Assert.False(order.Move(K("e"), 1));
        Assert.True(order.CanMove(K("b"), 1));
        Assert.True(order.CanMove(K("e"), -1));
        Assert.Equal("ab | cd | e", Screen(order));
    }

    [Fact]
    public void A_move_across_an_edge_survives_a_restart()
    {
        var (order, _, b) = Seed(FilePath);
        Assert.True(order.Move(K("b"), 1));
        var again = new SessionOrder(FilePath);
        Assert.Equal(b, again.Groups.GroupOf(K("b")));
        Assert.Equal("a | bcd | e", Screen(again));
    }
}
