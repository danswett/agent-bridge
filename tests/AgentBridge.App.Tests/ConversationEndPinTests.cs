using Xunit;

namespace AgentBridge.App.Tests;

public sealed class ConversationEndPinTests
{
    [Theory, InlineData(1), InlineData(2), InlineData(3), InlineData(4), InlineData(5), InlineData(6), InlineData(7), InlineData(8), InlineData(11), InlineData(299)]
    public void Returning_near_the_end_reengages_replies_but_a_reader_remaining_back_is_not_pulled(double gap)
    {
        var view = new SessionView();
        (view.Scroller.ViewportHeight, view.Scroller.ScrollableHeight) = (299, 52147);
        view.Moved(52147); view.Moved(51848);
        Assert.False(view.Pinned);
        view.Moved(52147 - gap);
        view.FollowState.Scrolled(51848, 52147);
        view.FollowState.Scrolled(view.Scroller.VerticalOffset, 52147);
        Assert.Equal(gap <= 8, view.FollowState.AtEnd);
        Assert.Equal(gap <= 8, view.HistoryEnd);
        foreach (var extent in new[] { 52145.0, 52287 })
        {
            view.ReplyForProbe();
            view.MeasuredExtent = extent + 4;
            view.DrainForProbe();
            if (gap <= 8)
            {
                if (view.Scroller.Targets.Count > 0)
                {
                    view.Scroller.ScrollableHeight = extent; view.Moved(Assert.Single(view.Scroller.Targets) - 8); view.Follow();
                    Assert.Equal(2, view.Scroller.Targets.Count); view.Moved(view.Scroller.Targets[^1]);
                }
                Assert.True(Math.Abs(extent - view.Scroller.VerticalOffset) <= 1.5, view.PinState());
                view.ReleasePin(); view.AnchorForProbe(extent); view.Scroller.Targets.Clear();
                view.Moved(extent - 1000, final: false); view.Moved(extent - 4); view.Follow();
                Assert.False(view.Pinned);
                view.Scroller.Targets.Clear();
            }
            else { Assert.Empty(view.Scroller.Targets); Assert.Equal(52147 - gap, view.Scroller.VerticalOffset); }
        }
    }

    [Fact]
    public void Pin_diagnostics_keep_only_the_last_three_releases_and_view_requests()
    {
        var view = new SessionView();
        for (var n = 1; n <= 4; n++)
        {
            view.ReleasePin($"release{n}", n);
            view.ChangeConversationView(view.Scroller, n, by: $"request{n}", line: n);
        }
        var state = view.PinState();
        Assert.DoesNotContain("release1:", state); Assert.DoesNotContain("request1:", state);
        for (var n = 2; n <= 4; n++) { Assert.Contains($"release{n}:{n} at (100, 200)", state); Assert.Contains($"request{n}:{n} at (100, 200) to={n}", state); }
    }

    [Theory, InlineData(1), InlineData(4), InlineData(8)]
    public void A_layout_drop_under_a_line_keeps_the_pin_and_renders_replies_5_and_6_at_the_end(double drop)
    {
        var view = new SessionView();
        (view.Scroller.ViewportHeight, view.Scroller.ScrollableHeight) = (299, 52145);
        view.Moved(52145); view.Moved(52145 - drop);
        foreach (var extent in new[] { 52145.0, 52287 })
        {
            view.Scroller.ScrollableHeight = extent;
            view.Follow();
            view.Moved(System.Linq.Enumerable.LastOrDefault(view.Scroller.Targets));
            Assert.True(Math.Abs(extent - view.Scroller.VerticalOffset) <= 1.5, $"{view.Scroller.VerticalOffset} of {extent}; {view.PinState()}");
        }
    }

    [Fact]
    public void Review_a_stale_pending_target_does_not_undo_a_small_reader_retreat_after_a_hold()
    {
        var view = new SessionView();
        view.Scroller.ViewportHeight = 299;
        view.Scroller.ScrollableHeight = 52459;
        view.Moved(52304);
        view.Follow();
        view.FollowState.PointerOver(7);
        view.Scroller.ScrollableHeight = 52304;
        view.Follow();
        view.Moved(52204);
        view.Scroller.Targets.Clear();
        view.FollowState.PointerLeft(7);
        Assert.False(view.Pinned, view.PinState());
        Assert.Empty(view.Scroller.Targets);
    }

    [Theory]
    [InlineData(false, -155)]
    [InlineData(true, -155)]
    [InlineData(false, 155)]
    [InlineData(true, 155)]
    public void Review_a_small_reader_retreat_during_remeasurement_releases_the_pin(bool layoutFirst, double delta)
    {
        var view = new SessionView();
        view.Scroller.ViewportHeight = 299;
        view.Scroller.ScrollableHeight = 52304;
        view.Moved(52304);
        view.Scroller.ScrollableHeight = 52304 + delta;
        if (layoutFirst) { view.Follow(); }
        view.Moved(Math.Min(52304, view.Scroller.ScrollableHeight) - 100);
        view.Scroller.Targets.Clear();
        view.Follow();
        Assert.False(view.Pinned, view.PinState());
        Assert.Empty(view.Scroller.Targets);
    }

    [Theory, InlineData(0), InlineData(6)]
    public void A_page_back_of_one_viewport_during_a_pending_correction_or_small_remeasure_releases_the_pin(double remeasure)
    {
        var view = new SessionView();
        (view.Scroller.ViewportHeight, view.Scroller.ScrollableHeight) = (299, 52449);
        view.Moved(52449);
        view.Scrolling(52449);
        view.Scroller.ScrollableHeight = 52449 - remeasure;
        view.Moved(52150);
        view.StopScrolling();
        view.Scroller.Targets.Clear();
        view.Follow();
        Assert.False(view.Pinned, view.PinState());
        Assert.Empty(view.Scroller.Targets);
    }

    [Fact]
    public void Review_the_original_pending_correction_reader_retreat_releases_the_pin()
    {
        var view = new SessionView();
        view.Moved(200);
        view.Scroller.ScrollableHeight = 300;
        view.Follow();
        view.Moved(150);
        view.Scroller.Targets.Clear();
        view.Follow();
        Assert.False(view.Pinned);
        Assert.Empty(view.Scroller.Targets);
    }

    [Theory]
    [InlineData("pan", false)]
    [InlineData("scroll", false)]
    [InlineData("key", false)]
    [InlineData("pan", true)]
    [InlineData("scroll", true)]
    [InlineData("key", true)]
    public void A_reply_correction_never_undoes_navigation_that_already_released_the_pin(string gesture, bool startsAfterQueue)
    {
        var view = new SessionView();
        view.ReleasePin();
        if (!startsAfterQueue) { view.GestureForProbe(gesture); }
        view.KeepPlaceForProbe();
        view.GestureForProbe(gesture);
        view.Scroller.VerticalOffset = 80;
        view.DrainForProbe();
        Assert.Equal(80, view.Scroller.VerticalOffset);
        Assert.Empty(view.Scroller.Targets);
    }

    [Theory]
    [InlineData(false, 52149)]
    [InlineData(true, 52149)]
    [InlineData(false, 52145)]
    [InlineData(true, 52145)]
    public void A_screen_reader_move_during_remeasurement_releases_the_end_pin(bool layoutFirst, double finalExtent)
    {
        var view = new SessionView();
        view.Scroller.ViewportHeight = 299;
        view.Scroller.ScrollableHeight = 52304;
        view.Moved(52304);
        view.Scroller.ScrollableHeight = 52149;
        if (layoutFirst) { view.Follow(); view.Scroller.Targets.Clear(); }
        view.Scroller.ScrollableHeight = finalExtent;
        view.Moved(50000);
        view.Follow();
        Assert.False(view.Pinned);
        Assert.Empty(view.Scroller.Targets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_pending_end_correction_survives_further_remeasurement_and_a_short_landing(bool layoutFirst)
    {
        var view = new SessionView();
        view.Scroller.ViewportHeight = 299;
        view.Scroller.ScrollableHeight = 52304;
        view.Moved(52304);
        view.Scroller.ScrollableHeight = 52149;
        if (layoutFirst) { view.Follow(); }
        view.Scroller.ScrollableHeight = 52145;
        view.Moved(51848);
        Assert.True(view.Pinned);
        view.Follow();
        Assert.Equal(52145, view.Scroller.Targets.Last());
        view.Moved(52141);
        view.Follow();
        Assert.True(view.Pinned);
        Assert.Equal(52145, view.Scroller.Targets.Last());
        view.Scroller.ScrollableHeight = 52287;
        view.Follow();
        Assert.Equal(52287, view.Scroller.Targets.Last());
        view.Moved(52287);
        view.Moved(52187);
        Assert.False(view.Pinned);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Scrolling_up_while_still_hovered_resumes_cached_history_drawing(bool heldBeforeFrame)
    {
        var view = new SessionView();
        view.PrepareHistoryForProbe();
        if (heldBeforeFrame) { view.FollowState.PointerOver(7); }
        view.FirstFrameForProbe();
        if (!heldBeforeFrame)
        {
            view.FollowState.PointerOver(7);
            view.DrawEarlierForProbe();
        }
        Assert.False(view.HistoryDrawing);
        Assert.Equal(1, view.ShownCount);
        view.Scroller.Wheel();
        view.Moved(50);
        Assert.False(view.Pinned);
        Assert.True(view.FollowState.Held);
        Assert.True(view.HistoryDrawing);
        view.DrawEarlierForProbe();
        Assert.Equal(2, view.ShownCount);
    }

    [Fact]
    public void Releasing_the_pin_before_the_first_frame_keeps_cached_drawing_deferred()
    {
        var view = new SessionView();
        view.PrepareHistoryForProbe();
        view.FollowState.PointerOver(7);
        view.Scroller.Wheel();
        view.Moved(50);
        Assert.False(view.HistoryDrawing);
        view.FirstFrameForProbe();
        Assert.True(view.HistoryDrawing);
        view.DrawEarlierForProbe();
        Assert.Equal(2, view.ShownCount);
    }

    [Fact]
    public void Releasing_the_pin_after_cached_history_is_drawn_does_not_restart_the_timer()
    {
        var view = new SessionView();
        view.PrepareHistoryForProbe();
        view.FirstFrameForProbe();
        view.DrawEarlierForProbe();
        Assert.Equal(2, view.ShownCount);
        Assert.False(view.HistoryDrawing);
        view.Scroller.Wheel();
        Assert.False(view.HistoryDrawing);
    }

    [Fact]
    public void Resetting_a_session_forgets_scrollbar_custody_without_a_release()
    {
        var view = new SessionView();
        view.Scroller.VerticalOffset = view.Scroller.ScrollableHeight;
        view.Scroller.Press(new Microsoft.UI.Xaml.Controls.Primitives.ScrollBar { Parent = view.Scroller }, 7);
        Assert.False(view.HistoryEnd);
        view.ResetSessionForProbe();
        Assert.True(view.HistoryEnd);
    }

    [Fact]
    public void Resetting_a_session_lets_go_of_the_hold_its_scrollbar_press_left_behind()
    {
        var view = new SessionView();
        view.Scroller.Press(new Microsoft.UI.Xaml.Controls.Primitives.ScrollBar { Parent = view.Scroller }, 7);
        view.FollowState.Pressed(7);
        view.ResetSessionForProbe();
        view.Scroller.EndGesture("capture lost", pointer: 7, contact: true);
        Assert.False(view.FollowState.Held);
    }

    [Fact]
    public void A_screen_reader_moving_the_view_back_from_the_end_releases_the_pin_where_growth_does_not()
    {
        var view = new SessionView();
        view.Moved(200);
        view.Scroller.ScrollableHeight = 300;
        view.Follow();
        // Content above shrinks under an anchored view, then the pin's own correction lands.
        view.Scroller.ScrollableHeight = 200;
        view.Moved(100);
        view.Follow();
        view.Moved(200);
        Assert.True(view.Pinned);
        view.Moved(50);
        view.Follow();
        Assert.False(view.Pinned);
        Assert.Equal([300.0, 200.0], view.Scroller.Targets);
    }

    [Theory]
    [InlineData(false, -155)]
    [InlineData(true, -155)]
    [InlineData(false, 155)]
    [InlineData(true, 155)]
    public void A_remeasured_extent_does_not_release_the_bottom_pin_when_the_list_adjusts_its_anchor(bool layoutFirst, double delta)
    {
        var view = new SessionView();
        view.Scroller.ViewportHeight = 299;
        view.Scroller.ScrollableHeight = 52304;
        view.Moved(52304);
        var extent = view.Scroller.ScrollableHeight = 52304 + delta;
        if (layoutFirst) { view.Follow(); view.Moved(52304, final: false); }
        view.Moved(extent - 299);
        Assert.True(view.Pinned);
        view.Follow();
        Assert.Equal(extent, view.Scroller.Targets.Last());
        view.Moved(extent);
        view.Moved(extent - 100);
        Assert.False(view.Pinned);
    }

    [Theory]
    [InlineData(52304)]
    [InlineData(52149)]
    public void A_shrinking_extent_keeps_the_old_or_clamped_end_pinned(double offset)
    {
        var view = new SessionView();
        view.Scroller.ScrollableHeight = 52304;
        view.Moved(52304);
        view.Scroller.ScrollableHeight = 52149;
        view.Moved(offset);
        view.Follow();
        Assert.True(view.Pinned);
        Assert.Equal(offset == 52304 ? [52149.0] : [], view.Scroller.Targets);
    }

    [Fact]
    public void A_prepend_during_our_own_pending_correction_compensates_the_added_rows()
    {
        var view = new SessionView();
        view.PrepareHistoryForProbe();
        view.ReleasePin();
        view.Scrolling(correction: 150);
        view.DrawEarlierForProbe();
        Assert.Equal(2, view.ShownCount);
        Assert.Equal([300.0], view.Scroller.Targets);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Rewatching_the_same_session_still_releases_the_pin_on_the_first_move_back_from_the_end(bool remeasured)
    {
        var view = new SessionView();
        if (remeasured)
        {
            view.Scroller.ViewportHeight = 299;
            view.Scroller.ScrollableHeight = 52304; view.Moved(52304);
            view.Scroller.ScrollableHeight = 52459; view.Follow();
            view.Scroller.ScrollableHeight = 52304; view.Follow();
            view.Scroller.Targets.Clear();
        }
        else { view.Moved(200); }
        view.ResetSessionForProbe();
        view.Follow();
        view.Moved(remeasured ? 52005 : 50);
        view.Follow();
        Assert.False(view.Pinned);
        Assert.Empty(view.Scroller.Targets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_held_pinned_prepend_waits_without_moving_or_inserting_rows(bool heldBeforeFrame)
    {
        var view = new SessionView();
        view.PrepareHistoryForProbe();
        if (heldBeforeFrame) { view.FollowState.PointerOver(7); }
        view.FirstFrameForProbe();
        Assert.Equal(!heldBeforeFrame, view.HistoryDrawing);
        view.FollowState.PointerOver(7);
        view.FollowState.TouchDown(8);
        view.DrawEarlierForProbe();
        Assert.False(view.HistoryDrawing);
        view.DrainForProbe();
        view.Follow();
        Assert.Empty(view.Scroller.Targets);
        Assert.Equal(1, view.ShownCount);
        view.FollowState.PointerLeft(7);
        Assert.False(view.HistoryDrawing);
        view.FollowState.TouchUp(8);
        Assert.True(view.HistoryDrawing);
        view.DrawEarlierForProbe();
        Assert.Equal(2, view.ShownCount);
        Assert.Contains(view.Scroller.ScrollableHeight, view.Scroller.Targets);
    }

    [Theory]
    [InlineData("held")]
    [InlineData("unheld")]
    [InlineData("ended")]
    [InlineData("navigation")]
    public void Opening_another_session_places_its_end_despite_a_hold_unless_navigation_takes_over(string state)
    {
        var view = new SessionView();
        view.PrepareHistoryForProbe();
        if (state != "unheld") { view.FollowState.PointerOver(7); }
        view.SwitchForProbe();
        if (state == "ended") { view.FollowState.PointerLeft(7); }
        if (state == "navigation") { view.Scroller.Wheel(); }
        view.DrainForProbe();
        view.Follow();
        if (state == "navigation") { Assert.Empty(view.Scroller.Targets); }
        else { Assert.Equal([view.Scroller.ScrollableHeight], view.Scroller.Targets); }
    }

    [Theory]
    [InlineData(10, 50, double.NaN, 0, true)]
    [InlineData(-50, 200, double.NaN, 0, true)]
    [InlineData(-50, 200, 0, 0, false)]
    [InlineData(-50, 200, double.NaN, 10, false)]
    public void Only_bring_requests_that_move_the_target_take_navigation_custody(double top, double height, double alignment, double offset, bool still)
    {
        var view = new SessionView();
        view.PrepareHistoryForProbe();
        view.Scroller.Bring(new Microsoft.UI.Xaml.UIElement { Top = top, Height = height }, alignment, offset);
        Assert.Equal(still, view.Pinned);
        Assert.Equal(still, view.HistoryEnd);
        view.DrawEarlierForProbe();
        Assert.Equal(still ? 2 : 1, view.ShownCount);
    }

    [Fact]
    public void A_programmatic_scroll_into_view_anchored_elsewhere_keeps_the_pin_and_lets_earlier_history_draw()
    {
        var view = new SessionView();
        view.PrepareHistoryForProbe();
        view.ProgrammaticBringForProbe(new Microsoft.UI.Xaml.UIElement { Top = -50, Height = 200 }, 0);
        view.AnchorForProbe(50);
        view.Moved(50);
        view.StopScrolling();
        Assert.True(view.Pinned);
        Assert.Equal(0, view.Releases);
        view.DrawEarlierForProbe();
        Assert.Equal(2, view.ShownCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_unloaded_or_replaced_session_drops_scrollbar_and_navigation_custody_without_a_release(bool switchSession)
    {
        var view = new SessionView();
        view.PrepareHistoryForProbe();
        view.Scroller.Press(new Microsoft.UI.Xaml.Controls.Primitives.ScrollBar { Parent = view.Scroller }, 7);
        view.Scroller.Bring(new Microsoft.UI.Xaml.UIElement { Top = -50, Height = 200 }, 0);
        if (switchSession) { view.SwitchForProbe(); }
        else { view.DetachForProbe(); }
        view.Scroller.VerticalOffset = view.Scroller.ScrollableHeight;
        Assert.True(view.HistoryEnd);
    }

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

    [Fact]
    public void History_waits_for_requested_navigation_not_an_earlier_correction_even_when_a_hold_ends()
    {
        var view = new SessionView();
        view.PrepareHistoryForProbe();
        view.Scroller.ScrollableHeight = 250;
        view.Follow();
        view.FollowState.PointerOver(1);
        view.Scroller.Bring(new Microsoft.UI.Xaml.UIElement { Parent = view.Scroller });
        Assert.False(view.HistoryEnd);
        view.FollowState.PointerLeft(1);
        Assert.False(view.HistoryEnd);
        Assert.Equal([250.0], view.Scroller.Targets);
        view.Scroller.VerticalOffset = 250;
        view.StopScrolling();
        view.DrawEarlierForProbe();
        view.DrainForProbe();
        Assert.Equal(1, view.ShownCount);
        Assert.Equal([250.0], view.Scroller.Targets);
        view.Scroller.VerticalOffset = 100;
        view.StopScrolling();
        Assert.False(view.HistoryEnd);
        view.DrawEarlierForProbe();
        Assert.Equal(2, view.ShownCount);
        view.Scroller.VerticalOffset = view.Scroller.ScrollableHeight;
        view.StopScrolling();
        Assert.True(view.HistoryEnd);
    }

    [Theory]
    [InlineData("release", false)]
    [InlineData("cancel", false)]
    [InlineData("capture lost", false)]
    [InlineData("capture lost", true)]
    public void History_cannot_repin_between_a_scrollbar_press_and_its_release_or_cancel(string end, bool contact)
    {
        var view = new SessionView();
        view.Scroller.VerticalOffset = view.Scroller.ScrollableHeight;
        var bar = new Microsoft.UI.Xaml.Controls.Primitives.ScrollBar { Parent = view.Scroller };
        view.Scroller.Press(new Microsoft.UI.Xaml.UIElement { Parent = bar }, pointer: 7);
        if (contact) { view.FollowState.Pressed(7); }
        Assert.False(view.HistoryEnd);
        view.Scrolling();
        view.StopScrolling();
        view.Scroller.EndGesture(end, pointer: 8, contact);
        Assert.False(view.HistoryEnd);
        view.Follow();
        Assert.Empty(view.Scroller.Targets);
        view.Scroller.EndGesture(end, pointer: 7, contact);
        new Microsoft.UI.Xaml.Controls.ScrollViewer().EndGesture("release", pointer: 7);
        Assert.True(view.HistoryEnd);
        Assert.False(view.FollowState.Held);
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
