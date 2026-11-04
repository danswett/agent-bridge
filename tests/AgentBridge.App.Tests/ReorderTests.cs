using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Xunit;

// Moving a session by lifting its card (#592, #594; ui.md §5.3 Lift and Reorder, §6.1 Moving):
// where the gap opens as the card is held, what moves to make room, the group it lands in, the
// places it can't land, the edges' auto-scroll, what is said, and the order a drop saves.
public sealed class ReorderTests
{
    // A list as the app lays it out: 60 a card, 30 more for a group header, top to bottom from 0.
    static List<ReorderCard> List(params (string Key, bool Pinned, bool Fixed)[] cards)
    {
        var list = new List<ReorderCard>();
        var top = 0.0;
        for (var i = 0; i < cards.Length; i++)
        {
            var first = i == 0 || cards[i - 1].Pinned != cards[i].Pinned;
            var header = cards.Any(c => c.Pinned) && first ? 30 : 0;
            list.Add(new ReorderCard(cards[i].Key, cards[i].Pinned, cards[i].Fixed, top, header, 60 + header));
            top += 60 + header;
        }
        return list;
    }

    static (string, bool, bool) S(string key, bool pinned = false, bool @fixed = false) => (key, pinned, @fixed);

    [Fact]
    public void A_card_held_two_cards_down_opens_its_gap_below_them_and_each_moves_up_one_slot()
    {
        var drag = new ReorderDrag(List(S("a"), S("b"), S("c"), S("d")), "a");
        Assert.Equal(new ReorderTarget(false, 0), drag.Origin);

        // Its centre where c's was: the gap is where c was, a and b's slots closed above it.
        var target = drag.Nearest(150, drag.Origin);
        Assert.Equal(new ReorderTarget(false, 2), target);
        var layout = drag.Layout(target);
        Assert.Equal(-60, layout.Shifts["b"]);
        Assert.Equal(-60, layout.Shifts["c"]);
        Assert.Equal(0, layout.Shifts["d"]);
        Assert.Equal(120, layout.GapTop);
        Assert.Equal((3, 4), drag.Place(target));
    }

    [Fact]
    public void When_the_held_session_ends_the_cards_after_it_close_its_slot_and_those_before_stay()
    {
        var drag = new ReorderDrag(List(S("p", pinned: true), S("q", pinned: true), S("a"), S("b")), "q");
        var closing = drag.Closing();
        Assert.False(closing.ContainsKey("p"));
        Assert.Equal(-drag.GapHeight, closing["a"]);
        Assert.Equal(-drag.GapHeight, closing["b"]);

        var unpinned = new ReorderDrag(List(S("a"), S("b"), S("c")), "b");
        Assert.Equal(["c"], unpinned.Closing().Keys);

        // The only pin going: no groups are left, so the rest close up its card and both headers.
        var only = new ReorderDrag(List(S("p", pinned: true), S("a"), S("b")), "p");
        Assert.Equal(-(60 + 30 + 30), only.Closing()["a"]);
        Assert.Equal(-(60 + 30 + 30), only.Closing()["b"]);
    }

    [Fact]
    public void The_gap_moves_only_once_another_place_is_nearer_by_more_than_a_pixel()
    {
        var drag = new ReorderDrag(List(S("a"), S("b"), S("c")), "a");
        // Halfway between place 0's gap (centre 30) and place 1's (centre 90): it stays where it is.
        Assert.Equal(new ReorderTarget(false, 0), drag.Nearest(60.5, new ReorderTarget(false, 0)));
        Assert.Equal(new ReorderTarget(false, 1), drag.Nearest(59.5, new ReorderTarget(false, 1)));
        Assert.Equal(new ReorderTarget(false, 1), drag.Nearest(61.5, new ReorderTarget(false, 0)));
    }

    [Fact]
    public void A_card_held_across_the_sessions_header_moves_the_header_one_slot_and_drops_as_pinned()
    {
        var drag = new ReorderDrag(List(S("p", pinned: true), S("a"), S("b")), "b");
        // Pinned's header 0, p 30 to 90, Sessions' header 90, a 150, b 210.
        var target = drag.Nearest(110, drag.Origin);
        Assert.Equal(new ReorderTarget(true, 1), target);
        Assert.True(drag.Crosses(target));
        var layout = drag.Layout(target);
        Assert.Equal(0, layout.PinnedHeader);
        Assert.Equal(90, layout.GapTop);
        Assert.Equal(150, layout.SessionsHeader);
        Assert.Equal(60, layout.Shifts["a"]);
        Assert.Equal(0, layout.Shifts["p"]);
        Assert.Equal((2, 2), drag.Place(target));
        Assert.Equal("Pinned, position 2 of 2. Drops as pinned.", ReorderWords.Held(drag.Place(target), drag.Crosses(target), target.Pinned));
        Assert.Equal("Pin", ReorderWords.Chip(target.Pinned));
    }

    [Fact]
    public void The_last_pinned_card_held_among_sessions_keeps_its_slot_so_nothing_under_it_moves()
    {
        var drag = new ReorderDrag(List(S("p", pinned: true), S("a"), S("b")), "p");
        var target = new ReorderTarget(false, 1);
        Assert.True(drag.Allows(target));
        var layout = drag.Layout(target);
        // Pinned keeps its header and a slot one card tall where p was: Sessions' header stays at 90.
        Assert.Equal(30, layout.Placeholder);
        Assert.Equal(90, layout.SessionsHeader);
        Assert.Equal(0, layout.Shifts["a"]);
        Assert.Equal(60, layout.Shifts["b"]);
        Assert.Equal("Unpinned. Position 2 of 3 in Sessions.", ReorderWords.Dropped(drag.Place(target), drag.Crosses(target), target.Pinned));
    }

    [Fact]
    public void With_nothing_pinned_a_held_card_has_no_pinned_group_to_drop_into()
    {
        var drag = new ReorderDrag(List(S("a"), S("b")), "b");
        Assert.False(drag.HasGroup(pinnedGroup: true));
        Assert.DoesNotContain(drag.Targets, t => t.Pinned);
        Assert.Equal(new ReorderTarget(false, 0), drag.Nearest(-500, drag.Origin));
        Assert.Null(drag.Layout(drag.Origin).SessionsHeader);
    }

    [Fact]
    public void Nothing_lands_among_rows_whose_machine_has_not_sent_its_whole_list()
    {
        var drag = new ReorderDrag(List(S("new1", @fixed: true), S("new2", @fixed: true), S("a"), S("b")), "b");
        Assert.Equal([2, 3], drag.Targets.Select(t => t.Index));
        Assert.Equal(new ReorderTarget(false, 2), drag.Nearest(-500, drag.Origin));
    }

    [Fact]
    public void The_list_scrolls_near_its_edges_faster_the_nearer_and_at_full_speed_past_them()
    {
        // 10% of 600 is 60, within 40 to 80.
        Assert.Equal(0, ReorderDrag.ScrollSpeed(300, 0, 600));
        Assert.Equal(-250, ReorderDrag.ScrollSpeed(30, 0, 600));
        Assert.Equal(1000, ReorderDrag.ScrollSpeed(600, 0, 600));
        Assert.Equal(-1000, ReorderDrag.ScrollSpeed(-20, 0, 600));
        // A short list's zone is 40, a tall one's 80.
        Assert.Equal(-250, ReorderDrag.ScrollSpeed(20, 0, 200));
        Assert.Equal(250, ReorderDrag.ScrollSpeed(1960, 0, 2000));
    }

    [Fact]
    public void Each_step_of_a_move_is_said_as_the_spec_words_it()
    {
        Assert.Equal("Picked up Fix the build, position 3 of 12 in Sessions.", ReorderWords.PickedUp("Fix the build", (3, 12), pinned: false));
        Assert.Equal("Position 5 of 12.", ReorderWords.Held((5, 12), crossing: false, pinned: false));
        Assert.Equal("Moved to position 5 of 12 in Sessions.", ReorderWords.Dropped((5, 12), crossed: false, pinned: false));
        Assert.Equal("Pinned. Position 3 of 3 in Pinned.", ReorderWords.Dropped((3, 3), crossed: true, pinned: true));
        Assert.Equal("Cancelled. Back at position 3 of 12.", ReorderWords.Cancelled((3, 12)));
        Assert.Equal("Fix the build ended. Move cancelled.", ReorderWords.Ended("Fix the build"));
        Assert.Equal("Top of Pinned.", ReorderWords.End(up: true, pinned: true));
        Assert.Equal("End of Sessions.", ReorderWords.End(up: false, pinned: false));
        Assert.Equal("Drop here to keep it pinned", ReorderWords.Keep(pinned: true));
        Assert.Equal("Drop here to unpin", ReorderWords.Keep(pinned: false));
    }

    [Fact]
    public void A_motion_interrupted_part_way_is_found_where_its_curve_has_got_to()
    {
        var standard = new MotionEase(0.33, 0, 0.67, 1);
        Assert.Equal(0, standard.At(0));
        Assert.Equal(1, standard.At(1));
        Assert.Equal(0.5, standard.At(0.5), 3);
        // Eased at both ends: behind a straight line early, ahead of it late.
        Assert.True(standard.At(0.25) < 0.25);
        Assert.True(standard.At(0.75) > 0.75);
    }

    static OrderEntry E(string id, int minute, string machine = "local") => new(machine, Make.Session(id, updated: Make.T0.AddMinutes(minute)));
    static string K(string id, string machine = "local") => SessionOrder.KeyOf(machine, id);
    static string[] Ids(IEnumerable<OrderEntry> list) => [.. list.Select(e => e.Session.Id)];

    [Fact]
    public void A_drop_saves_the_card_at_its_place_among_the_others_pinning_it_across_the_header_as_one_change()
    {
        var file = Path.Combine(Path.GetTempPath(), "abr-" + Guid.NewGuid().ToString("N")[..8], "order.json");
        try
        {
            OrderEntry[] list = [E("a", 4), E("b", 3), E("c", 2), E("p", 1)];
            var o = new SessionOrder(file);
            o.Arrange(list);
            o.SetPinned(K("p"), true);
            o.Arrange(list);

            Assert.True(o.MoveTo(K("a"), pinned: false, index: 2));
            Assert.Equal(["b", "c", "a"], Ids(o.Arrange(list).Sessions));
            Assert.True(o.MoveTo(K("c"), pinned: true, index: 0));
            var again = new SessionOrder(file).Arrange(list);
            Assert.Equal(["c", "p"], Ids(again.Pinned));
            Assert.Equal(["b", "a"], Ids(again.Sessions));
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(file)!, recursive: true); } catch (DirectoryNotFoundException) { } }
    }

    [Fact]
    public void A_drop_among_rows_with_no_place_yet_lands_just_below_them_and_a_held_row_with_none_cannot_drop()
    {
        var o = new SessionOrder(null);
        o.Arrange([E("saved", 1), E("other", 0)]);
        OrderEntry[] list = [E("x", 9, "m2"), E("saved", 1), E("other", 0)];
        o.Arrange(list, settled: m => m != "m2");
        Assert.True(o.IsFixed(K("x", "m2")));
        Assert.False(o.MoveTo(K("x", "m2"), pinned: false, index: 2));
        Assert.True(o.MoveTo(K("other"), pinned: false, index: 0));
        Assert.Equal(["x", "other", "saved"], Ids(o.Arrange(list, settled: m => m != "m2").Sessions));
    }

    [Fact]
    public void A_drop_lands_among_the_cards_as_drawn_while_it_was_held_whatever_arrived_or_ended_meanwhile()
    {
        var o = new SessionOrder(null);
        o.Arrange([E("a", 4), E("b", 3), E("c", 2), E("d", 1)]);
        // d held and let go between b and c; meanwhile n started and c ended.
        OrderEntry[] now = [E("n", 9), E("a", 4), E("b", 3), E("d", 1)];
        o.Arrange(now);
        Assert.True(o.MoveTo(K("d"), pinned: false, index: 2, drawn: [K("a"), K("b"), K("c")]));
        Assert.Equal(["n", "a", "b", "d"], Ids(o.Arrange(now).Sessions));
    }
}
