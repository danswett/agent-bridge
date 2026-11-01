using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Xunit;

// The sessions list's order, kept per device (#164, ui.md §6.1; pwa/test/unit/session-order.test.mjs
// holds the PWA to the same rules).
public sealed class OrderTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "abo-" + Guid.NewGuid().ToString("N")[..8]);
    string File => Path.Combine(folder, "order.json");

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    static OrderEntry E(string id, int minute, string machine = "local") => new(machine, Make.Session(id, updated: Make.T0.AddMinutes(minute)));

    static string[] Ids(IEnumerable<OrderEntry> list) => [.. list.Select(e => e.Session.Id)];
    static string K(string id, string machine = "local") => SessionOrder.KeyOf(machine, id);

    [Fact]
    public void Upserts_never_reorder_rows_in_your_order_as_three_sessions_update_in_turn()
    {
        var o = new SessionOrder(null);
        OrderEntry[] list = [E("a", 3), E("b", 2), E("c", 1)];
        Assert.Equal(["a", "b", "c"], Ids(o.Arrange(list).Sessions));
        foreach (var (id, minute) in new[] { ("c", 10), ("b", 11), ("a", 12), ("c", 13) })
        {
            list = [.. list.Select(e => e.Session.Id == id ? E(id, minute) : e)];
            Assert.Equal(["a", "b", "c"], Ids(o.Arrange(list).Sessions));
        }
    }

    [Fact]
    public void A_new_session_lands_at_the_top_once_and_then_stays_where_it_is_put()
    {
        var o = new SessionOrder(null);
        List<OrderEntry> list = [E("a", 3), E("b", 2)];
        o.Arrange(list);
        list.Add(E("new", 1));
        Assert.Equal(["new", "a", "b"], Ids(o.Arrange(list).Sessions));
        Assert.True(o.Move(K("new"), 1));
        list[2] = E("new", 30);
        Assert.Equal(["a", "new", "b"], Ids(o.Arrange(list).Sessions));
    }

    [Fact]
    public void A_machines_list_drawn_as_it_arrives_is_placed_newest_first_once_the_machine_has_sent_it_all()
    {
        var o = new SessionOrder(null);
        var settled = false;
        List<OrderEntry> list = [];
        foreach (var e in new[] { E("old", 1), E("mid", 5), E("newest", 9) })
        {
            list.Add(e);
            o.Arrange(list, settled: _ => settled);
        }
        settled = true;
        Assert.Equal(["newest", "mid", "old"], Ids(o.Arrange(list, settled: _ => settled).Sessions));
        list[0] = E("old", 20);
        Assert.Equal(["newest", "mid", "old"], Ids(o.Arrange(list, settled: _ => settled).Sessions));
    }

    [Fact]
    public void A_row_its_machine_has_not_finished_sending_can_neither_move_nor_be_passed_until_it_has_a_place()
    {
        var o = new SessionOrder(null);
        o.Arrange([E("saved", 1)]);
        var settled = false;
        OrderEntry[] list = [E("a", 9, "m2"), E("b", 8, "m2"), E("saved", 1)];
        bool Settled(string m) => m != "m2" || settled;
        Assert.Equal(["a", "b", "saved"], Ids(o.Arrange(list, settled: Settled).Sessions));
        Assert.False(o.CanMove(K("a", "m2"), 1));
        Assert.False(o.Move(K("b", "m2"), -1));
        Assert.False(o.CanMove(K("saved"), -1));
        Assert.False(o.Place(K("saved"), K("a", "m2"), after: false));
        settled = true;
        o.Arrange(list, settled: Settled);
        Assert.True(o.Move(K("b", "m2"), -1));
        Assert.Equal(["b", "a", "saved"], Ids(o.Arrange(list, settled: Settled).Sessions));
    }

    [Fact]
    public void Moves_pins_and_the_sort_survive_a_restart_of_the_app()
    {
        OrderEntry[] list = [E("a", 3), E("b", 2), E("c", 1)];
        var o = new SessionOrder(File);
        o.Arrange(list);
        Assert.True(o.Move(K("a"), 1));
        Assert.True(o.Place(K("c"), K("b"), after: false));
        o.SetPinned(K("b"), true);
        o.Sort = SessionSort.Machine;

        var again = new SessionOrder(File);
        Assert.Equal(SessionSort.Machine, again.Sort);
        again.Sort = SessionSort.Yours;
        var a = again.Arrange(list);
        Assert.Equal(["b"], Ids(a.Pinned));
        Assert.Equal(["c", "a"], Ids(a.Sessions));
    }

    [Fact]
    public void Pinned_rows_sit_above_the_rest_and_an_unpinned_row_goes_back_to_the_top_of_the_rest()
    {
        var o = new SessionOrder(null);
        OrderEntry[] list = [E("a", 3), E("b", 2), E("c", 1)];
        o.Arrange(list);
        o.SetPinned(K("c"), true);
        o.SetPinned(K("b"), true);
        var a = o.Arrange(list);
        Assert.Equal(["c", "b"], Ids(a.Pinned));
        Assert.Equal(["a"], Ids(a.Sessions));
        // Moves stay within a group.
        Assert.False(o.CanMove(K("b"), 1));
        Assert.True(o.Move(K("b"), -1));
        Assert.Equal(["b", "c"], Ids(o.Arrange(list).Pinned));
        o.SetPinned(K("c"), false);
        a = o.Arrange(list);
        Assert.Equal(["b"], Ids(a.Pinned));
        Assert.Equal(["c", "a"], Ids(a.Sessions));
    }

    [Fact]
    public void A_row_dragged_among_the_pinned_ones_is_pinned_and_one_dragged_out_is_unpinned()
    {
        var o = new SessionOrder(null);
        OrderEntry[] list = [E("a", 3), E("b", 2), E("c", 1)];
        o.Arrange(list);
        o.SetPinned(K("a"), true);
        o.Arrange(list);
        Assert.True(o.Place(K("c"), K("a"), after: true));
        Assert.Equal(["a", "c"], Ids(o.Arrange(list).Pinned));
        Assert.True(o.Place(K("a"), K("b"), after: true));
        var r = o.Arrange(list);
        Assert.Equal(["c"], Ids(r.Pinned));
        Assert.Equal(["b", "a"], Ids(r.Sessions));
    }

    [Fact]
    public void Most_recent_and_machine_sort_within_each_group_and_rows_move_only_in_your_order()
    {
        var o = new SessionOrder(null);
        OrderEntry[] list = [E("a", 3, "m2"), E("b", 2, "m1"), E("c", 1, "m2")];
        o.Arrange(list);
        list[2] = E("c", 9, "m2");
        o.Sort = SessionSort.Recent;
        Assert.Equal(["c", "a", "b"], Ids(o.Arrange(list).Sessions));
        var names = new Dictionary<string, string> { ["m1"] = "Alpha", ["m2"] = "Beta" };
        o.Sort = SessionSort.Machine;
        Assert.Equal(["b", "a", "c"], Ids(o.Arrange(list, nameOf: m => names[m]).Sessions));
        o.SetPinned(K("c", "m2"), true);
        var r = o.Arrange(list, nameOf: m => names[m]);
        Assert.Equal(["c"], Ids(r.Pinned));
        Assert.Equal(["b", "a"], Ids(r.Sessions));
        Assert.False(o.CanMove(K("a", "m2"), -1));
        Assert.False(o.Move(K("a", "m2"), -1));
    }

    [Fact]
    public void An_order_that_cannot_be_kept_lists_sessions_most_recent_first()
    {
        // A directory where the file should be: every write fails, on Windows and Linux alike.
        Directory.CreateDirectory(File);
        var o = new SessionOrder(File);
        Assert.Equal(SessionSort.Yours, o.Sort);
        OrderEntry[] list = [E("a", 1), E("b", 2)];
        Assert.Equal(["b", "a"], Ids(o.Arrange(list).Sessions));
        Assert.False(o.Usable);
        Assert.Equal(SessionSort.Recent, o.Sort);
        Assert.Equal(["a", "b"], Ids(o.Arrange([E("a", 5), E("b", 2)]).Sessions));
    }

    [Fact]
    public void A_row_has_news_once_its_session_updates_after_it_was_last_open_but_not_on_first_sight()
    {
        var o = new SessionOrder(null);
        o.Arrange([E("a", 1)]);
        Assert.False(o.Unread(K("a"), Make.T0.AddMinutes(1)));
        Assert.True(o.Unread(K("a"), Make.T0.AddMinutes(2)));
        o.MarkSeen(K("a"), Make.T0.AddMinutes(2));
        Assert.False(o.Unread(K("a"), Make.T0.AddMinutes(2)));
        o.MarkSeen(K("a"), Make.T0.AddMinutes(1));
        Assert.False(o.Unread(K("a"), Make.T0.AddMinutes(2)));
    }

    [Fact]
    public void A_machines_complete_list_forgets_rows_of_its_sessions_that_ended_unseen()
    {
        var o = new SessionOrder(File);
        o.Arrange([E("a", 1), E("b", 2), E("x", 0, "m2")]);
        o.Synced("local", new HashSet<string>(["b"]));
        var r = new SessionOrder(File).Arrange([E("a", 1), E("b", 2), E("x", 0, "m2")]);
        // a was forgotten, so it comes back as new, at the top; the other machine's row is kept.
        Assert.Equal(["a", "b", "x"], Ids(r.Sessions));
    }
}
