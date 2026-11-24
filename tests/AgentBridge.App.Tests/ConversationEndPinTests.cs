using Xunit;

namespace AgentBridge.App.Tests;

public sealed class ConversationEndPinTests
{
    [Theory, InlineData(18), InlineData(299), InlineData(596)]
    public void Native_layout_ownership_keeps_the_end_pin_but_the_same_UIA_retreat_outside_layout_releases_it(double drop)
    {
        var view = new SessionView();
        (view.Scroller.ViewportHeight, view.Scroller.ScrollableHeight) = (299, 50216);
        view.Moved(50216);
        view.LayoutForProbe(() => view.Moved(50216 - drop));
        Assert.True(view.Pinned, view.PinState());
        Assert.Equal(0, view.Releases);
        view.Follow();
        Assert.Equal(50216, Assert.Single(view.Scroller.Targets));
        view.Moved(50216);
        view.Moved(50216 - drop);
        view.Scroller.Targets.Clear();
        view.Follow();
        Assert.False(view.Pinned);
        Assert.Equal(1, view.Releases);
        Assert.Empty(view.Scroller.Targets);
    }

    [Theory, InlineData("wheel"), InlineData("bar"), InlineData("navigation"), InlineData("hold")]
    public void Owned_layout_restore_does_not_overwrite_input_or_a_hold_started_during_measurement(string input)
    {
        var view = new SessionView { MeasuredExtent = 200 };
        view.Moved(200);
        view.LayoutDuringMeasure = () =>
        {
            if (input == "wheel") { view.Scroller.Wheel(); }
            else if (input == "bar") { view.Scroller.Press(new Microsoft.UI.Xaml.Controls.Primitives.ScrollBar { Parent = view.Scroller }); }
            else if (input == "navigation") { view.Scroller.Bring(new Microsoft.UI.Xaml.FrameworkElement()); }
            else { view.FollowState.PointerOver(7); }
            view.Moved(100);
        };
        view.ReplyForProbe();
        view.DrainForProbe();
        Assert.Empty(view.Scroller.Targets);
        Assert.Equal(100, view.Scroller.VerticalOffset);
        Assert.Equal(input == "hold", view.Pinned);
    }

    [Fact]
    public void Nested_native_layout_keeps_the_outer_ownership_until_a_later_UIA_retreat()
    {
        var view = new SessionView();
        view.Moved(200);
        view.LayoutForProbe(() =>
        {
            view.LayoutForProbe(() => view.Moved(181));
            view.Moved(150);
        });
        Assert.True(view.Pinned);
        view.Moved(200);
        view.Moved(150);
        Assert.False(view.Pinned);
    }

    [Fact]
    public void Failed_native_layout_does_not_suppress_the_next_UIA_retreat()
    {
        var view = new SessionView();
        view.Moved(200);
        Assert.Throws<InvalidOperationException>(() => view.LayoutForProbe(() => throw new InvalidOperationException("Layout probe failed.")));
        view.Moved(150);
        Assert.False(view.Pinned);
    }

    [Theory, InlineData("hover"), InlineData("touch"), InlineData("press"), InlineData("pan")]
    public void Native_anchoring_follows_a_pin_but_yields_to_each_real_hold_and_to_UIA_retreat(string hold)
    {
        var view = new SessionView();
        view.Moved(200);
        view.Follow();
        Assert.Equal(Microsoft.UI.Xaml.Controls.ItemsUpdatingScrollMode.KeepLastItemInView, view.NativeMode);
        if (hold == "hover") { view.FollowState.PointerOver(7); }
        else if (hold == "touch") { view.FollowState.TouchDown(7); }
        else if (hold == "press") { view.FollowState.Pressed(7); }
        else { view.FollowState.PanStarted(); }
        view.Follow();
        Assert.Equal(Microsoft.UI.Xaml.Controls.ItemsUpdatingScrollMode.KeepItemsInView, view.NativeMode);
        if (hold == "hover") { view.FollowState.PointerLeft(7); }
        else if (hold == "touch") { view.FollowState.TouchUp(7); }
        else if (hold == "press") { view.FollowState.Released(7); }
        else { view.FollowState.PanEnded(); }
        Assert.Equal(Microsoft.UI.Xaml.Controls.ItemsUpdatingScrollMode.KeepLastItemInView, view.NativeMode);
        view.Moved(150);
        Assert.False(view.Pinned);
        Assert.Equal(Microsoft.UI.Xaml.Controls.ItemsUpdatingScrollMode.KeepItemsInView, view.NativeMode);
    }

    [Fact]
    public void Intermediate_layout_scrolling_is_not_reader_ownership_of_native_anchoring()
    {
        var view = new SessionView();
        view.Moved(200);
        view.Scrolling();
        view.Follow();
        Assert.True(view.Pinned);
        Assert.Equal(Microsoft.UI.Xaml.Controls.ItemsUpdatingScrollMode.KeepLastItemInView, view.NativeMode);
    }

    [Fact]
    public void Near_end_return_acquires_native_anchoring_before_bound_history_notifications()
    {
        var view = new SessionView();
        view.PrepareHistoryForProbe();
        var extent = view.Scroller.ScrollableHeight;
        view.Moved(extent);
        view.Moved(extent - 100);
        view.Moved(extent - 4);
        var observed = new List<bool>();
        view.ObserveHistoryForProbe(() => observed.Add(view.Pinned &&
            view.NativeMode == Microsoft.UI.Xaml.Controls.ItemsUpdatingScrollMode.KeepLastItemInView));
        view.AppendHistoryForProbe();
        view.DrainForProbe();
        Assert.NotEmpty(observed);
        Assert.All(observed, pinned => Assert.True(pinned));
    }

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

    [Theory, InlineData(false, false), InlineData(true, false), InlineData(false, true), InlineData(true, true)]
    public void A_stationary_shrink_keeps_its_signed_evidence_but_later_growth_cannot_excuse_a_reader_retreat(bool layoutFirst, bool laterGrowth)
    {
        var view = new SessionView();
        (view.Scroller.ViewportHeight, view.Scroller.ScrollableHeight) = (299, 200);
        view.Moved(200);
        view.Scroller.ScrollableHeight = 300; view.Follow();
        view.Scroller.ScrollableHeight = 200;
        if (laterGrowth) { view.Follow(); view.Scroller.ScrollableHeight = 355; }
        if (layoutFirst) { view.Follow(); }
        view.Moved(100);
        Assert.Equal(!laterGrowth, view.Pinned);
        view.Scroller.Targets.Clear(); view.Follow();
        if (laterGrowth) { Assert.Empty(view.Scroller.Targets); }
        else { Assert.Equal(200, Assert.Single(view.Scroller.Targets)); }
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

    [Theory, InlineData(false, 150), InlineData(true, 299), InlineData(true, 100)]
    public void Rewatching_the_same_session_still_releases_the_pin_on_the_first_move_back_from_the_end(bool remeasured, double retreat)
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
        view.Moved((remeasured ? 52304 : 200) - retreat);
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
        // Opened at the top: a wheel turned up there moves nothing (#4313), so the person's move is turning it down.
        if (state == "navigation") { view.Scroller.Wheel(-120); }
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

    // #1756: dragging the thumb (or holding an arrow) toward the end of a long mixed transcript realises
    // tool rows taller than their estimate. Each realisation moved the block at the top, and the layout
    // correction put it back with ChangeView under the held thumb: the bar bounced up, the end never came.
    [Theory]
    [InlineData("thumb")]
    [InlineData("arrow")]
    [InlineData("track")]
    public void A_held_scrollbar_part_is_never_moved_by_the_top_block_correction_until_released(string part)
    {
        var view = new SessionView();
        (view.Scroller.ViewportHeight, view.Scroller.ScrollableHeight) = (600, 48000);
        var bar = new Microsoft.UI.Xaml.Controls.Primitives.ScrollBar { Parent = view.Scroller };
        var source = new Microsoft.UI.Xaml.UIElement { Parent = bar };
        view.Scroller.Press(part == "thumb" ? new Microsoft.UI.Xaml.UIElement { Parent = source } : source);
        // Alternate tool rows (estimated 48, real 160) and text rows (estimated 48, real 90) realised above the
        // top block as the drag goes on: the block, at 40 now, was taken that much higher at each step.
        var growth = new[] { 112.0, 42, 112, 42, 112, 42 };
        for (var step = 0; step < growth.Length; step++)
        {
            view.Moved(42000 + step * 1000);
            view.TakeAnchorForProbe(40 - growth[step]);
            view.LaidOutForProbe();
            Assert.Empty(view.Scroller.Targets);
        }
        view.Scroller.EndGesture("release");
        view.LaidOutForProbe();
        // Once let go, the last realisation's move was already drawn under the thumb: it starts again from there
        // rather than pulling the view back on release (#4243).
        Assert.Empty(view.Scroller.Targets);
    }

    // #4243 trackpad: a tool row realises above the top block while a pan (or the thumb) is held still, so the
    // person sees what they read move down. The release raises no view change, and the first layout after it
    // corrected against the anchor from before: a second jump, back, on release. It now starts again from where
    // the person left it, and a later realisation is still kept in place.
    [Theory]
    [InlineData("pan")]
    [InlineData("thumb")]
    public void Rows_realised_under_a_held_gesture_are_not_corrected_after_its_release(string gesture)
    {
        var view = new SessionView();
        (view.Scroller.ViewportHeight, view.Scroller.ScrollableHeight) = (600, 48000);
        view.Moved(30000);
        var thumb = new Microsoft.UI.Xaml.UIElement { Parent = new Microsoft.UI.Xaml.UIElement { Parent = new Microsoft.UI.Xaml.Controls.Primitives.ScrollBar { Parent = view.Scroller } } };
        if (gesture == "pan") { view.GestureForProbe("pan"); view.ReleasePin(); } else { view.Scroller.Press(thumb); }
        view.Moved(29800, final: false);
        view.TakeAnchorForProbe(40);
        view.BlockTopForProbe = 152;
        view.LaidOutForProbe();
        Assert.Empty(view.Scroller.Targets);
        if (gesture == "pan") { view.GestureForProbe("none"); } else { view.Scroller.EndGesture("release"); }
        view.LaidOutForProbe();
        Assert.Empty(view.Scroller.Targets);
        // A text row realised after the release is kept in place as before.
        view.BlockTopForProbe = 194;
        view.LaidOutForProbe();
        Assert.Equal([29842.0], view.Scroller.Targets);
    }

    // A wheel hold is not a held gesture for the release rule above: its notch ends in its own view changes, and
    // one that raises none lapses. A row realised above the top block in its start window is still put back once
    // the hold lapses; accepting it moved the view a notch plus the row (CI 45325080892: 48, 48, 48, 402, ...).
    [Fact]
    public void A_row_realised_during_a_wheel_start_window_is_still_kept_in_place_after_it_lapses()
    {
        var view = new SessionView();
        (view.Scroller.ViewportHeight, view.Scroller.ScrollableHeight) = (600, 48000);
        view.Moved(30000);
        view.TakeAnchorForProbe(40);
        view.Scroller.Wheel();
        view.BlockTopForProbe = 394;
        view.LaidOutForProbe();
        Assert.Empty(view.Scroller.Targets);
        view.LapseWheelForProbe();
        view.LaidOutForProbe();
        Assert.Equal([30354.0], view.Scroller.Targets);
    }

    // #4243: a wheel notch's animation starts before its first view change reaches us. A correction of ours
    // landing in that gap (a row realised above the top block, an earlier-history prepend) is a ChangeView
    // without animation, which cancels the notch where it is: identical notches then move different distances.
    [Theory]
    [InlineData("top-block")]
    [InlineData("earlier-history")]
    public void Identical_wheel_notches_move_the_same_distance_while_rows_realise_between_them(string work)
    {
        var view = new SessionView();
        if (work == "earlier-history")
        {
            view.PrepareHistoryForProbe();
            view.LayoutDuringMeasure = () => view.Scroller.ScrollableHeight = 48000 + view.ShownCount * 160;
            view.FirstFrameForProbe();
        }
        (view.Scroller.ViewportHeight, view.Scroller.ScrollableHeight) = (600, 48160);
        view.Moved(30000);
        const double notch = 144; // one 120 wheel delta: three 48 DIP lines
        var moved = new List<double>();
        // Tool rows realise 112 taller than estimated, text rows 42.
        foreach (var grew in new[] { 112.0, 42, 112, 42, 112, 42, 112, 42 })
        {
            var from = view.Scroller.VerticalOffset;
            view.Scroller.Targets.Clear();
            view.Scroller.Wheel();
            if (work == "top-block") { view.TakeAnchorForProbe(40 - grew); view.LaidOutForProbe(); }
            else { view.DrawEarlierForProbe(); view.DrainForProbe(); }
            // Measured as the app would see it: the notch ends at our target if we asked for one, else where it was sent.
            var to = view.Scroller.Targets.Count > 0 ? view.Scroller.Targets[^1] : from - notch;
            view.Moved(from - notch / 2, final: false);
            view.Moved(to);
            moved.Add(Math.Round(from - to, 2));
        }
        Assert.True(moved.All(d => d == notch), $"requested {notch} per notch, moved: {string.Join(", ", moved)}");
        // Once the notch's own view changes arrive, the corrections work again.
        view.Scroller.Targets.Clear();
        view.TakeAnchorForProbe(-72);
        view.LaidOutForProbe();
        Assert.Equal([view.Scroller.VerticalOffset + 112], view.Scroller.Targets);
    }

    // #4243 owner repro: spinning the wheel down "goes about 3 notches and then jumps back up four notches". The
    // last rows realise as the end comes into view (a tall tool row 112 over its estimate), and the list's native
    // remeasure lands one viewport (about four notches) short of the end. The wheel had released the pin and the
    // pointer over the conversation holds end-following, so nothing undid it; spinning on looped.
    [Fact]
    public void Spinning_the_wheel_down_to_the_end_never_moves_the_view_back_up_and_stops_at_the_end()
    {
        var view = new SessionView();
        (view.Scroller.ViewportHeight, view.Scroller.ScrollableHeight) = (600, 48000);
        view.Moved(48000);
        view.FollowState.PointerOver(7);
        var (offsets, extent) = (new List<double> { 47424 }, 48000.0);
        view.Scroller.Wheel(); view.Moved(47712, final: false); view.Moved(47424);
        for (var notch = 0; notch < 8; notch++)
        {
            view.Scroller.Targets.Clear();
            var from = view.Scroller.VerticalOffset;
            view.Scroller.Wheel(-120);
            view.Moved(Math.Min(from + 72, extent), final: false);
            view.Moved(Math.Min(from + 144, extent));
            if (view.Scroller.VerticalOffset >= extent - 0.5 && extent < 48112)
            {
                extent = 48112;
                view.LayoutForProbe(() => { view.Scroller.ScrollableHeight = extent; view.Moved(extent - 600); });
            }
            view.Follow();
            view.DrainForProbe();
            if (view.Scroller.Targets.Count > 0) { view.Moved(view.Scroller.Targets[^1]); }
            offsets.Add(view.Scroller.VerticalOffset);
        }
        var back = offsets.Zip(offsets.Skip(1), (a, b) => a - b).Max();
        Assert.True(back <= 0.5 && Math.Abs(offsets[^1] - extent) <= 0.5,
            $"offset after each notch: {string.Join(", ", offsets)} (end {extent}, worst move back {back})");
    }

    // A wheel event that raises no view change (another scroller took it, or a zero delta): its hold lapses by
    // itself, and only the latest notch's lapse lets go.
    [Fact]
    public void A_wheel_with_no_view_change_lets_go_after_its_start_window()
    {
        var view = new SessionView();
        view.Moved(200);
        view.Scroller.Wheel();
        Assert.False(view.HistoryEnd);
        view.TakeAnchorForProbe(-72);
        view.LaidOutForProbe();
        Assert.Empty(view.Scroller.Targets);
        var first = view.DispatcherTimersForProbe;
        view.Scroller.Wheel();
        first.Elapse();
        view.LaidOutForProbe();
        Assert.Empty(view.Scroller.Targets);
        // Still at the end once it lapses: the next reply re-engages the pin there.
        view.LapseWheelForProbe();
        Assert.True(view.HistoryEnd, view.PinState());
    }

    // #4313 (P2 on #4252): the wheel turned down at the very end cannot move the view, yet it released the pin and held
    // the view for 250 ms. A reply streaming in meanwhile was not followed, and once it grew past the 8 DIP slack later
    // updates never re-engaged: the view was left above a growing reply. Turning the wheel at the edge it points to
    // now leaves the pin and the view alone; turning it away from the end still releases at once.
    [Fact]
    public void A_wheel_turned_down_at_the_very_end_keeps_following_a_streaming_reply()
    {
        var view = new SessionView { MeasuredExtent = 48000 };
        (view.Scroller.ViewportHeight, view.Scroller.ScrollableHeight) = (600, 48000);
        view.Moved(48000);
        view.Scroller.Wheel(-120);
        foreach (var extent in new[] { 48300.0, 48600 })
        {
            view.MeasuredExtent = view.Scroller.ScrollableHeight = extent;
            view.ReplyForProbe();
            view.DrainForProbe();
            view.LapseWheelForProbe();
            Assert.True(view.Scroller.Targets.Count > 0 && view.Scroller.Targets[^1] == extent, $"follows the reply to {extent}: {view.PinState()}");
            view.Moved(extent);
        }
        Assert.Equal(0, view.Releases);
        view.Scroller.Wheel(120);
        Assert.False(view.Pinned);
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
