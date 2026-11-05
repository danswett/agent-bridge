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
    public void A_code_block_is_named_by_the_text_it_draws_with_hidden_characters_as_their_escapes_nested_or_not()
    {
        var shown = Shown("```\necho ok \u2067# note\n```\n\n> ```\n> run\u200B\n> ```");

        Assert.Equal(["echo ok \\u2067# note", "run\\u200B"], shown.Select(b => b.Text));
    }

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
    public void A_cut_that_falls_between_two_blocks_alike_keeps_the_run_that_is_left()
    {
        var shown = Shown("A.\n\n---\n\nB.\n\n---\n\nC.");
        var (rule, c) = (shown[3], shown[4]);

        Assert.True(ConversationBlocks.Update(shown, MarkdownModel.Parse("---\n\nC.\n\nD.")));

        Assert.Equal(["Separator", "C.", "D."], shown.Select(b => b.Text));
        Assert.Same(rule, shown[0]);
        Assert.Same(c, shown[1]);
    }

    [Fact]
    public void A_cut_partway_through_the_first_block_replaces_it_and_keeps_the_blocks_after_it()
    {
        var shown = Shown("One.\n\nTwo.\n\nThree.");
        var (two, three) = (shown[1], shown[2]);

        Assert.True(ConversationBlocks.Update(shown, MarkdownModel.Parse("ne.\n\nTwo.\n\nThree.\n\nFour.")));

        Assert.Equal(["ne.", "Two.", "Three.", "Four."], shown.Select(b => b.Text));
        Assert.Same(two, shown[1]);
        Assert.Same(three, shown[2]);
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

// The conversation read with the keyboard as a page (#765): the keys that scroll a page scroll it,
// and Tab stops in every block with a link or a Copy button, one not realised yet included.
public sealed class ConversationKeyboardTests
{
    const int Up = 0x26, Down = 0x28, Left = 0x25, PageDown = 0x22, Home = 0x24, End = 0x23;

    static List<ConversationBlock> Blocks(string markdown) => [.. MarkdownModel.Parse(markdown).Select(b => new ConversationBlock(b))];

    [Fact]
    public void Arrows_pages_home_and_end_scroll_the_text_and_left_and_right_are_kept_from_the_list()
    {
        Assert.Equal(
            [ReadingKey.LineUp, ReadingKey.LineDown, ReadingKey.PageDown, ReadingKey.Top, ReadingKey.End, ReadingKey.Still],
            new[] { Up, Down, PageDown, Home, End, Left }.Select(k => ConversationKeyboard.Key(k, ctrl: false, shift: false, alt: false)));
        Assert.Equal(500, ConversationKeyboard.Offset(ReadingKey.Still, 500, 700, 4000));
    }

    [Fact]
    public void A_key_with_ctrl_or_shift_never_moves_between_blocks_and_only_ctrl_home_and_ctrl_end_scroll()
    {
        Assert.Equal(ReadingKey.Still, ConversationKeyboard.Key(Down, ctrl: false, shift: true, alt: false));
        Assert.Equal(ReadingKey.Still, ConversationKeyboard.Key(PageDown, ctrl: true, shift: false, alt: false));
        Assert.Equal(ReadingKey.Still, ConversationKeyboard.Key(End, ctrl: true, shift: true, alt: false));
        Assert.Equal(ReadingKey.End, ConversationKeyboard.Key(End, ctrl: true, shift: false, alt: false));
        Assert.Null(ConversationKeyboard.Key(Up, ctrl: false, shift: true, alt: true));
        Assert.Null(ConversationKeyboard.Key(0x41, ctrl: true, shift: false, alt: false));
    }

    [Fact]
    public void A_line_is_the_scrollers_step_a_page_the_viewport_and_neither_goes_past_an_end()
    {
        Assert.Equal(516, ConversationKeyboard.Offset(ReadingKey.LineDown, 500, 700, 4000));
        Assert.Equal(1200, ConversationKeyboard.Offset(ReadingKey.PageDown, 500, 700, 4000));
        Assert.Equal(4000, ConversationKeyboard.Offset(ReadingKey.PageDown, 3800, 700, 4000));
        Assert.Equal(0, ConversationKeyboard.Offset(ReadingKey.PageUp, 300, 700, 4000));
        Assert.Equal(4000, ConversationKeyboard.Offset(ReadingKey.End, 300, 700, 4000));
        Assert.Equal(0, ConversationKeyboard.Offset(ReadingKey.Top, 300, 700, 4000));
    }

    [Fact]
    public void Tab_stops_in_a_block_with_a_link_anywhere_in_it_or_a_code_block_and_not_in_plain_text()
    {
        var blocks = Blocks("Plain.\n\nSee [docs](https://example.com).\n\n```\nrun\n```\n\n> quoted [link](https://a.b)\n\n- one\n- [two](https://c.d)\n\n| a | b |\n|---|---|\n| [x](https://e.f) | y |\n\n## Heading\n\n---");

        Assert.Equal([false, true, true, true, true, true, false, false], blocks.Select(b => b.Focusable));
    }

    [Fact]
    public void Tab_past_an_unrealised_block_with_a_link_is_sent_to_that_block_first()
    {
        var blocks = Blocks("[a](https://a.b)\n\nPlain.\n\n[c](https://a.b)\n\n[d](https://a.b)");
        bool Realised(int i) => i < 2;

        Assert.Equal(2, ConversationKeyboard.Passed(blocks, from: 0, to: blocks.Count, forward: true, Realised));
        Assert.Equal(0, ConversationKeyboard.Passed(blocks, from: -1, to: 3, forward: true, i => i == 3));
    }

    [Fact]
    public void Shift_tab_into_the_response_from_below_lands_on_its_last_block_with_a_stop()
    {
        var blocks = Blocks("[a](https://a.b)\n\n[b](https://a.b)\n\nPlain.");

        Assert.Equal(1, ConversationKeyboard.Passed(blocks, from: blocks.Count, to: -1, forward: false, i => false));
    }

    [Fact]
    public void Tab_is_left_alone_when_it_passed_nothing_it_should_have_stopped_in()
    {
        var blocks = Blocks("[a](https://a.b)\n\nPlain.\n\n[c](https://a.b)");

        Assert.Null(ConversationKeyboard.Passed(blocks, from: 0, to: 2, forward: true, i => false));
        Assert.Null(ConversationKeyboard.Passed(blocks, from: 0, to: blocks.Count, forward: true, i => true));
        Assert.Null(ConversationKeyboard.Passed(blocks, from: -1, to: -1, forward: true, i => false));
    }

    [Fact]
    public void Tab_across_the_whole_response_with_no_block_realised_stops_in_its_first_block_with_a_link()
    {
        var blocks = Blocks("Plain.\n\n[b](https://a.b)\n\n[c](https://a.b)");
        var (from, to) = ConversationKeyboard.Ends(-1, null, blocks.Count, forward: true);

        Assert.Equal(1, ConversationKeyboard.Passed(blocks, from, to, forward: true, i => false));
    }

    [Fact]
    public void Tab_that_wraps_round_the_window_goes_on_to_the_end_of_the_response_first()
    {
        Assert.Equal((2, 5), ConversationKeyboard.Ends(2, -1, 5, forward: true));
        Assert.Equal((2, -1), ConversationKeyboard.Ends(2, 5, 5, forward: false));
        Assert.Equal((-1, 3), ConversationKeyboard.Ends(null, 3, 5, forward: true));
        Assert.Equal((2, 4), ConversationKeyboard.Ends(2, 4, 5, forward: true));
    }
}
