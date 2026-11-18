using Xunit;

namespace AgentBridge.App.Tests;

public sealed class ConversationEndPinTests
{
    [Theory]
    [InlineData("pointer")]
    [InlineData("touch")]
    [InlineData("cancel")]
    [InlineData("press")]
    public void A_held_conversation_defers_end_correction_until_the_hold_ends(string hold)
    {
        var view = new SessionView();
        if (hold == "pointer") { view.FollowState.PointerOver(1); }
        else if (hold == "press") { view.FollowState.Pressed(1); }
        else { view.FollowState.TouchDown(1); }
        view.Follow();
        Assert.Empty(view.Scroller.Targets);
        view.Scroller.ScrollableHeight = 300;
        view.Follow();
        Assert.Empty(view.Scroller.Targets);
        if (hold == "pointer") { view.FollowState.PointerLeft(1); }
        else if (hold == "press") { view.FollowState.Released(1); }
        else if (hold == "cancel") { view.FollowState.TouchCanceled(1); }
        else { view.FollowState.TouchUp(1); }
        Assert.Equal([300.0], view.Scroller.Targets);
    }

    [Fact]
    public void Only_the_last_hold_ending_retries_the_latest_end_without_another_layout()
    {
        var view = new SessionView();
        view.FollowState.PointerOver(1);
        view.FollowState.TouchDown(2);
        view.Follow();
        view.FollowState.PointerLeft(1);
        Assert.Empty(view.Scroller.Targets);
        view.Scroller.ScrollableHeight = 400;
        view.FollowState.TouchUp(2);
        Assert.Equal([400.0], view.Scroller.Targets);
    }

    [Fact]
    public void Ending_a_hold_does_not_repin_a_destination_chosen_while_held()
    {
        var view = new SessionView();
        view.FollowState.PointerOver(1);
        view.Follow();
        view.Scroller.Bring(new Microsoft.UI.Xaml.UIElement { Parent = view.Scroller });
        view.FollowState.PointerLeft(1);
        Assert.False(view.Pinned);
        Assert.Empty(view.Scroller.Targets);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("cancel")]
    [InlineData("capture lost")]
    public void History_cannot_repin_between_a_scrollbar_press_and_its_release_or_cancel(string end)
    {
        var view = new SessionView();
        view.Scroller.VerticalOffset = view.Scroller.ScrollableHeight;
        var bar = new Microsoft.UI.Xaml.Controls.Primitives.ScrollBar { Parent = view.Scroller };
        view.Scroller.Press(new Microsoft.UI.Xaml.UIElement { Parent = bar }, pointer: 7);
        Assert.False(view.HistoryEnd);
        view.Scrolling();
        view.StopScrolling();
        view.Scroller.EndGesture(end, pointer: 8);
        Assert.False(view.HistoryEnd);
        view.Follow();
        Assert.Empty(view.Scroller.Targets);
        view.Scroller.EndGesture(end, pointer: 7);
        Assert.True(view.HistoryEnd);
    }

    [Fact]
    public void A_question_or_screen_reader_bring_request_keeps_its_destination_above_the_end()
    {
        var view = new SessionView();
        view.Follow();
        view.Scroller.Bring(new Microsoft.UI.Xaml.UIElement { Parent = view.Scroller });
        view.Scroller.VerticalOffset = 50;
        view.Scroller.ScrollableHeight = 300;
        view.StopScrolling();
        view.Follow();
        Assert.False(view.Pinned);
        Assert.Equal([200.0], view.Scroller.Targets);
    }

    [Fact]
    public void A_rejected_end_correction_can_retry_on_the_next_layout()
    {
        var view = new SessionView();
        view.Scroller.AcceptChange = false;
        view.Follow();
        view.Scroller.AcceptChange = true;
        view.Follow();
        view.Follow();
        Assert.Equal([200.0, 200.0], view.Scroller.Targets);
    }

    [Theory]
    [InlineData("thumb")]
    [InlineData("track")]
    [InlineData("arrow")]
    public void A_scrollbar_press_releases_the_pin_before_dragging_or_clicking_scrolls(string part)
    {
        var view = new SessionView();
        var bar = new Microsoft.UI.Xaml.Controls.Primitives.ScrollBar { Parent = view.Scroller };
        var source = new Microsoft.UI.Xaml.UIElement { Parent = bar };
        var pressed = part == "thumb" ? new Microsoft.UI.Xaml.UIElement { Parent = source } : source;

        view.Scroller.Press(pressed);

        Assert.False(view.Pinned);
        Assert.Equal(1, view.Releases);
        // Layout can arrive before the first intermediate ViewChanged, or after the final one.
        view.Follow();
        view.Scrolling();
        view.Scroller.VerticalOffset = 50;
        view.Scroller.ScrollableHeight = 300;
        view.Follow();
        view.StopScrolling();
        view.Follow();
        Assert.Empty(view.Scroller.Targets);
    }

    [Fact]
    public void Follow_does_not_correct_the_end_while_the_person_is_scrolling()
    {
        var view = new SessionView();
        view.Scrolling();
        view.Follow();
        Assert.Empty(view.Scroller.Targets);
    }

    [Fact]
    public void An_in_flight_correction_still_follows_a_growing_end()
    {
        var view = new SessionView();
        view.Scrolling(correction: 150);
        view.Follow();
        Assert.Equal([200.0], view.Scroller.Targets);
        Assert.Equal(200, view.AnchorAt);
    }

    [Fact]
    public void A_nested_scrollbar_or_conversation_content_does_not_release_the_pin()
    {
        var view = new SessionView();
        var nested = new Microsoft.UI.Xaml.Controls.ScrollViewer { Parent = view.Scroller };
        var bar = new Microsoft.UI.Xaml.Controls.Primitives.ScrollBar { Parent = nested };
        view.Scroller.Press(new Microsoft.UI.Xaml.UIElement { Parent = bar });
        view.Scroller.Press(new Microsoft.UI.Xaml.UIElement { Parent = view.Scroller });
        Assert.True(view.Pinned);
        Assert.Equal(0, view.Releases);
    }

    [Fact]
    public void The_existing_wheel_release_still_leaves_the_readers_offset_alone()
    {
        var view = new SessionView();
        view.Scroller.Wheel();
        view.Follow();
        Assert.False(view.Pinned);
        Assert.Empty(view.Scroller.Targets);
    }

    [Fact]
    public void A_pin_follows_extent_growth_while_an_earlier_correction_is_still_in_flight()
    {
        const double offset = 257484;
        double? pending = offset;
        foreach (var extent in new[] { 257600.0, 257835.0 })
        {
            var target = ConversationEndPin.Correction(true, offset, extent, pending);
            Assert.Equal((double?)extent, target);
            pending = target;
            Assert.Null(ConversationEndPin.Correction(true, offset, extent, pending));
        }
        Assert.Null(ConversationEndPin.Correction(true, 257835, 257835, null));
    }

    [Fact]
    public void A_final_view_change_short_of_the_new_end_gets_another_correction() =>
        Assert.Equal((double?)257835, ConversationEndPin.Correction(true, 257484, 257835, null));

    [Fact]
    public void Releasing_the_pin_leaves_the_readers_offset_alone_even_if_the_extent_grows()
    {
        Assert.Null(ConversationEndPin.Correction(false, 257484, 257835, 257484));
        Assert.Null(ConversationEndPin.Correction(false, 257484, 258000, null));
    }

    [Theory]
    [InlineData(257835.0, null)]
    [InlineData(257834.75, null)]
    [InlineData(257484.0, 257834.75)]
    public void A_reached_or_already_pending_end_needs_no_duplicate_correction(double offset, double? pending) =>
        Assert.Null(ConversationEndPin.Correction(true, offset, 257835, pending));
}
