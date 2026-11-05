using System.Collections.ObjectModel;
using AgentBridge.App.Core;
using Xunit;

// The conversation's response as a list (#765): its top-level blocks are the items, kept as they
// were when the response changes, each named by its text for a screen reader that reaches it off
// screen (#707), and the block at the top of the viewport kept in place through a change of width.
public sealed class ConversationBlocksTests
{
    static ObservableCollection<ConversationBlock> Shown(string markdown) => [.. MarkdownModel.Parse(markdown).Select(b => new ConversationBlock(b))];

    [Fact]
    public void Every_top_level_block_is_its_own_item_and_a_list_keeps_its_points_inside_it()
    {
        var shown = Shown("# Title\n\nFirst paragraph.\n\n- one\n- two\n\nLast.");

        Assert.Equal(["Title", "First paragraph.", "• one\n• two", "Last."], shown.Select(b => b.Text));
    }

    [Fact]
    public void A_response_that_grew_keeps_the_items_it_had_and_adds_the_new_blocks_after_them()
    {
        var shown = Shown("One.\n\nTwo.");
        var had = shown.ToList();

        Assert.True(ConversationBlocks.Update(shown, MarkdownModel.Parse("One.\n\nTwo.\n\nThree.")));

        Assert.Equal(["One.", "Two.", "Three."], shown.Select(b => b.Text));
        Assert.Same(had[0], shown[0]);
        Assert.Same(had[1], shown[1]);
    }

    [Fact]
    public void A_last_block_still_being_written_is_replaced_and_the_blocks_before_it_are_kept()
    {
        var shown = Shown("One.\n\nTw");
        var first = shown[0];

        Assert.True(ConversationBlocks.Update(shown, MarkdownModel.Parse("One.\n\nTwo, done.")));

        Assert.Equal(["One.", "Two, done."], shown.Select(b => b.Text));
        Assert.Same(first, shown[0]);
    }

    [Fact]
    public void A_response_cut_at_its_start_keeps_the_blocks_that_are_left_where_they_are_drawn()
    {
        var shown = Shown("One.\n\nTwo.\n\nThree.");
        var three = shown[2];

        Assert.True(ConversationBlocks.Update(shown, MarkdownModel.Parse("Two.\n\nThree.\n\nFour.")));

        Assert.Equal(["Two.", "Three.", "Four."], shown.Select(b => b.Text));
        Assert.Same(three, shown[1]);
    }

    [Fact]
    public void A_response_with_nothing_in_common_is_left_alone_for_the_caller_to_replace_whole()
    {
        var shown = Shown("One.\n\nTwo.");
        var had = shown.ToList();

        Assert.False(ConversationBlocks.Update(shown, MarkdownModel.Parse("Something else.")));
        Assert.False(ConversationBlocks.Update(shown, []));

        Assert.Equal(had, shown);
    }

    [Fact]
    public void Two_blocks_alike_are_still_two_items()
    {
        var shown = Shown("One.\n\n---\n\nTwo.\n\n---\n\nThree.");

        Assert.Equal(5, shown.Count);
        Assert.NotSame(shown[1], shown[3]);
        Assert.Equal("Separator", shown[3].ToString());
    }

    [Fact]
    public void Blocks_built_from_the_same_markdown_twice_are_the_same_block_however_they_nest()
    {
        const string markdown = "> quoted\n\n1. a\n2. b\n\n| x | y |\n|---|---|\n| 1 | 2 |\n\n```cs\nvar a = 1;\n```";
        var a = MarkdownModel.Parse(markdown);
        var b = MarkdownModel.Parse(markdown);

        Assert.All(a.Zip(b), p => Assert.True(ConversationBlocks.Same(p.First, p.Second)));
        Assert.False(ConversationBlocks.Same(MarkdownModel.Parse("1. a")[0], MarkdownModel.Parse("1. b")[0]));
    }

    [Fact]
    public void An_item_is_named_by_what_its_block_says_so_a_screen_reader_finds_it_unrealised()
    {
        var shown = Shown("See [the docs](https://example.com) **now**.\n\n3. third\n4. fourth\n\n| Name | Age |\n|---|---|\n| Ann | 3 |");

        Assert.Equal(["See the docs now.", "3. third\n4. fourth", "Name, Age\nAnn, 3"], shown.Select(b => b.ToString()));
    }
}

public sealed class ScrollAnchorTests
{
    [Fact]
    public void The_anchor_is_the_block_across_the_top_edge_of_the_viewport_not_one_scrolled_past()
    {
        var anchor = ScrollAnchor.At([(5, 40, 100), (3, -300, 200), (4, -100, 140)]);

        Assert.Equal(new ScrollAnchor(4, -100), anchor);
    }

    [Fact]
    public void A_block_that_starts_exactly_at_the_top_edge_is_the_anchor()
    {
        Assert.Equal(new ScrollAnchor(7, 0), ScrollAnchor.At([(6, -50, 50), (7, 0, 80)]));
    }

    [Fact]
    public void Nothing_is_anchored_while_the_header_above_the_blocks_is_at_the_top()
    {
        Assert.Null(ScrollAnchor.At([(0, 120, 60), (1, 180, 60)]));
        Assert.Null(ScrollAnchor.At([]));
    }

    [Fact]
    public void A_change_of_width_scrolls_by_as_much_as_the_anchor_moved_so_it_stays_where_it_was()
    {
        var anchor = new ScrollAnchor(12, -30);

        Assert.Equal(1460, anchor.OffsetFor(offset: 1000, topNow: 430, scrollable: 5000));
        Assert.Equal(700, anchor.OffsetFor(offset: 1000, topNow: -330, scrollable: 5000));
    }

    [Fact]
    public void A_correction_never_scrolls_past_either_end_of_the_conversation()
    {
        var anchor = new ScrollAnchor(12, -30);

        Assert.Equal(1200, anchor.OffsetFor(offset: 1000, topNow: 430, scrollable: 1200));
        Assert.Equal(0, anchor.OffsetFor(offset: 100, topNow: -400, scrollable: 1200));
        Assert.Equal(0, anchor.OffsetFor(offset: 100, topNow: 400, scrollable: -1));
    }
}
