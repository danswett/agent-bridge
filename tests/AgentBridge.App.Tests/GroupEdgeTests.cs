using AgentBridge.App.Core;
using Xunit;

public sealed class GroupEdgeTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "abge-" + Guid.NewGuid().ToString("N"));
    string FilePath => Path.Combine(folder, "order.json");
    static OrderEntry E(string id) => new("local", Make.Session(id));
    static string K(string id) => E(id).Key;
    static string[] Members(SessionOrder order, OrderEntry[] rows, string? group) =>
        order.Arrange(rows).Sessions.Where(e => order.Groups.GroupOf(e.Key) == group).Select(e => e.Session.Id).ToArray();
    public void Dispose() { if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); } }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Crossing_a_group_edge_joins_the_nearest_end_and_survives_restart(int delta)
    {
        var order = new SessionOrder(FilePath);
        OrderEntry[] rows = [E("a"), E("b"), E("c"), E("d")];
        order.Arrange(rows);
        var a = order.Groups.Create("A");
        var b = order.Groups.Create("B");
        foreach (var id in new[] { "a", "b" }) { order.Groups.SetGroup(K(id), a); }
        foreach (var id in new[] { "c", "d" }) { order.Groups.SetGroup(K(id), b); }
        var moved = delta > 0 ? "b" : "c";
        var destination = delta > 0 ? b : a;

        Assert.True(order.CanMove(K(moved), delta));
        Assert.True(order.Move(K(moved), delta));
        var again = new SessionOrder(FilePath);
        Assert.Equal(destination, again.Groups.GroupOf(K(moved)));
        Assert.Equal(delta > 0 ? ["b", "c", "d"] : new[] { "a", "b", "c" }, Members(again, rows, destination));
        Assert.Equal(4, again.Arrange(rows).Sessions.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void The_last_group_and_Sessions_share_a_movable_edge(int delta)
    {
        var order = new SessionOrder(null);
        OrderEntry[] rows = [E("a"), E("b"), E("c"), E("d")];
        order.Arrange(rows);
        var group = order.Groups.Create("Work");
        order.Groups.SetGroup(K("a"), group);
        order.Groups.SetGroup(K("b"), group);
        var moved = delta > 0 ? "b" : "c";
        var destination = delta > 0 ? null : group;

        Assert.True(order.CanMove(K(moved), delta));
        Assert.True(order.Move(K(moved), delta));
        Assert.Equal(destination, order.Groups.GroupOf(K(moved)));
        Assert.Equal(delta > 0 ? ["b", "c", "d"] : new[] { "a", "b", "c" }, Members(order, rows, destination));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void An_expanded_empty_group_can_be_entered_from_either_edge(int delta)
    {
        var order = new SessionOrder(null);
        OrderEntry[] rows = [E("a"), E("b")];
        order.Arrange(rows);
        var first = order.Groups.Create("First");
        var empty = order.Groups.Create("Empty");
        order.Groups.SetGroup(K("a"), first);
        var moved = delta > 0 ? "a" : "b";

        Assert.True(order.Move(K(moved), delta));
        Assert.Equal(empty, order.Groups.GroupOf(K(moved)));
        Assert.Equal([moved], Members(order, rows, empty));
    }

    [Fact]
    public void Collapsed_groups_are_skipped_both_ways_without_changing_their_members()
    {
        var order = new SessionOrder(null);
        OrderEntry[] rows = [E("a"), E("b"), E("c")];
        order.Arrange(rows);
        var a = order.Groups.Create("A");
        var hidden = order.Groups.Create("Hidden");
        var b = order.Groups.Create("B");
        order.Groups.SetGroup(K("a"), a);
        order.Groups.SetGroup(K("b"), hidden);
        order.Groups.SetGroup(K("c"), b);
        order.IsGroupCollapsed = id => id == hidden;

        Assert.True(order.Move(K("a"), 1));
        Assert.Equal(b, order.Groups.GroupOf(K("a")));
        order.Arrange(rows);
        Assert.True(order.Move(K("a"), -1));
        Assert.Equal(a, order.Groups.GroupOf(K("a")));
        Assert.Equal(hidden, order.Groups.GroupOf(K("b")));
        Assert.False(order.CanMove(K("b"), 1));
        Assert.False(order.Move(K("b"), -1));
        order.IsGroupCollapsed = _ => false;
        Assert.True(order.Move(K("a"), 1));
        Assert.Equal(hidden, order.Groups.GroupOf(K("a")));
    }

    [Theory]
    [InlineData(SessionSort.Yours)]
    [InlineData(SessionSort.Recent)]
    [InlineData(SessionSort.Machine)]
    public void An_edge_move_never_passes_a_pending_row_or_runs_in_a_sorted_view(SessionSort sort)
    {
        var order = new SessionOrder(null);
        var a = order.Groups.Create("A");
        var b = order.Groups.Create("B");
        var saved = E("a");
        var pending = new OrderEntry("other", Make.Session("b"));
        order.Groups.SetGroup(saved.Key, a);
        order.Groups.SetGroup(pending.Key, b);
        order.Arrange([saved]);
        order.Sort = sort;
        order.Arrange([saved, pending], settled: machine => machine != "other");

        Assert.False(order.CanMove(saved.Key, 1));
        Assert.False(order.Move(saved.Key, 1));
        Assert.False(order.Move(pending.Key, -1));
        Assert.Equal(a, order.Groups.GroupOf(saved.Key));
        Assert.Equal(b, order.Groups.GroupOf(pending.Key));
    }

    [Fact]
    public void Pinned_edges_are_refused_in_both_directions_even_for_group_members()
    {
        var order = new SessionOrder(null);
        OrderEntry[] rows = [E("a"), E("b"), E("c")];
        var group = order.Groups.Create("Work");
        order.Groups.SetGroup(K("a"), group);
        order.Groups.SetGroup(K("b"), group);
        order.SetPinned(K("a"), true);
        order.Arrange(rows);

        foreach (var (id, delta) in new[] { ("a", 1), ("b", -1) })
        {
            Assert.False(order.CanMove(K(id), delta));
            Assert.False(order.Move(K(id), delta));
            Assert.Equal(group, order.Groups.GroupOf(K(id)));
        }
        Assert.True(order.IsPinned(K("a")));
        Assert.False(order.IsPinned(K("b")));
    }

    [Fact]
    public void The_first_group_and_the_foot_of_Sessions_cannot_move_past_the_list()
    {
        var order = new SessionOrder(null);
        OrderEntry[] rows = [E("a"), E("b")];
        var group = order.Groups.Create("Work");
        order.Groups.SetGroup(K("a"), group);
        order.Arrange(rows);
        Assert.False(order.CanMove(K("a"), -1));
        Assert.False(order.Move(K("a"), -1));
        Assert.False(order.CanMove(K("b"), 1));
        Assert.False(order.Move(K("b"), 1));
    }
}
