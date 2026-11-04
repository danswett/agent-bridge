using System.Xml.Linq;
using AgentBridge.App.Core;
using Xunit;

// Motion (ui.md §5.3, #232): every entry timed by the generated AbMotion* resources and nothing
// longer than its token; with Windows' animation effects off, nothing travels and each entry is
// instant or the cross-fade; and the conversation following a question only while the person is
// at its end and not touching it.
public sealed class MotionTests
{
    // The generated file the app loads (tools/design-gen), read as the app reads it: by key.
    static readonly MotionCatalogue Catalogue = MotionCatalogue.From(Generated());

    static Func<string, string?> Generated()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var doc = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "DesignResources.xaml"));
        var values = doc.Root!.Elements()
            .Where(e => (string?)e.Attribute(x + "Key") is { } k && k.StartsWith("AbMotion", StringComparison.Ordinal))
            .ToDictionary(e => (string)e.Attribute(x + "Key")!, e => e.Value.Trim());
        return key => values.GetValueOrDefault(key);
    }

    [Fact]
    public void Every_entry_runs_for_its_generated_token_and_none_runs_past_300_ms()
    {
        Assert.Equal(15, MotionCatalogue.Names.Count);
        foreach (var name in MotionCatalogue.Names)
        {
            var entry = Catalogue[name];
            var enter = Catalogue.Enter(name, animations: true);
            var exit = Catalogue.Exit(name, animations: true);
            Assert.Equal((entry.Duration, entry.Ease, entry.Distance), (enter.Duration, enter.Ease, enter.Distance));
            Assert.InRange(enter.Duration, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(300));
            // Leaving is never slower than entering.
            Assert.InRange(exit.Duration, TimeSpan.FromMilliseconds(1), enter.Duration);
        }
        // Spot checks against ui.md's table: push is emphasis over medium, leaving a pane is fast.
        Assert.Equal((TimeSpan.FromMilliseconds(250), 24.0), (Catalogue["Push"].Duration, Catalogue["Push"].Distance));
        Assert.Equal(TimeSpan.FromMilliseconds(83), Catalogue.Exit("Pane", true).Duration);
        Assert.Equal(new MotionEase(0, 0, 0, 1), Catalogue["Push"].Ease);
        Assert.Equal(new MotionEase(1, 0, 1, 1), Catalogue["Toast"].ExitEase);
    }

    [Fact]
    public void With_animation_effects_off_nothing_travels_and_each_entry_is_instant_or_the_cross_fade()
    {
        var crossFades = new List<string>();
        foreach (var name in MotionCatalogue.Names)
        {
            foreach (var step in new[] { Catalogue.Enter(name, animations: false), Catalogue.Exit(name, animations: false) })
            {
                Assert.Equal(0, step.Distance);
                if (Catalogue[name].Reduced == MotionReduced.CrossFade) { Assert.Equal(Catalogue.CrossFade, step.Duration); }
                else { Assert.False(step.Runs, $"{name} is instant with animations off"); }
            }
            if (Catalogue[name].Reduced == MotionReduced.CrossFade) { crossFades.Add(name); }
        }
        Assert.Equal(["Push", "Pane", "ListItem", "Acknowledge", "Toast", "Sheet"], crossFades);
        Assert.False(MotionCatalogue.Loops(animations: false));
    }

    [Fact]
    public void The_glyphs_turn_at_the_generated_periods()
    {
        Assert.Equal(TimeSpan.FromSeconds(1.2), Catalogue.WorkingPeriod);
        Assert.Equal(TimeSpan.FromSeconds(8), Catalogue.DelegatingPeriod);
    }

    [Fact]
    public void A_missing_motion_resource_is_an_error_not_an_untimed_motion()
    {
        var all = Generated();
        Assert.Throws<KeyNotFoundException>(() => MotionCatalogue.From(k => k == "AbMotionPushDuration" ? null : all(k)));
    }

    [Fact]
    public void A_question_is_followed_only_while_the_person_is_at_the_end_of_the_conversation()
    {
        var follow = new ConversationFollow();
        follow.Scrolled(offset: 600, scrollable: 600.4);
        Assert.True(follow.Follows);

        // Scrolled back through it: never moved.
        follow.Scrolled(offset: 200, scrollable: 600);
        Assert.False(follow.Follows);

        // Back at the end, then a long response grows the conversation under them without the
        // view moving: they are still at the end, and the next question is followed.
        follow.Scrolled(offset: 600, scrollable: 600);
        follow.Scrolled(offset: 600, scrollable: 1400);
        Assert.True(follow.Follows);

        // Another session shown starts afresh.
        follow.Scrolled(offset: 100, scrollable: 1400);
        Assert.False(follow.Follows);
        follow.Reset();
        Assert.True(follow.Follows);
    }

    [Fact]
    public void A_finger_on_the_conversation_holds_it_even_once_the_pan_has_taken_the_touch_over()
    {
        var follow = new ConversationFollow();
        follow.Scrolled(0, 0);
        follow.TouchDown(1);
        Assert.False(follow.Follows);

        // Windows cancels the pointer as the pan takes over, with the finger still down.
        follow.PanStarted();
        follow.TouchCanceled(1);
        Assert.False(follow.Follows);

        follow.PanEnded();
        Assert.True(follow.Follows);

        // Two fingers: the view is held until the last one lifts.
        follow.TouchDown(2);
        follow.TouchDown(3);
        follow.TouchUp(2);
        Assert.False(follow.Follows);
        follow.TouchUp(3);
        Assert.True(follow.Follows);

        // A touch cancelled with no pan (the window lost it) ends the touch.
        follow.TouchDown(4);
        follow.TouchCanceled(4);
        Assert.True(follow.Follows);
    }

    [Fact]
    public void A_mouse_over_the_conversation_holds_it_as_a_finger_would()
    {
        var follow = new ConversationFollow();
        follow.PointerOver(1);
        Assert.False(follow.Follows);
        follow.PointerLeft(1);
        Assert.True(follow.Follows);
    }

    [Fact]
    public void Answer_controls_take_no_click_for_500_ms_after_they_appear_or_are_scrolled_into_view_and_a_key_at_once()
    {
        var t0 = DateTimeOffset.Parse("2026-11-02T16:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var guard = new AnswerGuard();
        Assert.True(guard.Accepts(pointer: true, t0));
        Assert.Equal(TimeSpan.FromMilliseconds(500), AnswerGuard.Window);

        guard.Arm(t0);
        Assert.False(guard.Accepts(pointer: true, t0.AddMilliseconds(499)));
        Assert.True(guard.Accepts(pointer: false, t0));
        Assert.True(guard.Accepts(pointer: true, t0.AddMilliseconds(500)));

        // Scrolled into view again: the window starts again from there.
        guard.Arm(t0.AddMilliseconds(400));
        Assert.False(guard.Accepts(pointer: true, t0.AddMilliseconds(800)));
        Assert.Equal(TimeSpan.FromMilliseconds(100), guard.Remaining(t0.AddMilliseconds(800)));
        Assert.True(guard.Accepts(pointer: true, t0.AddMilliseconds(900)));
    }

    [Fact]
    public void Answer_controls_take_no_click_again_when_their_tab_comes_back_but_not_while_it_stays()
    {
        var t0 = DateTimeOffset.Parse("2026-11-02T16:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var guard = new AnswerGuard();
        Assert.True(guard.Shown(true, t0));
        Assert.False(guard.Shown(true, t0.AddSeconds(1)));
        Assert.True(guard.Accepts(pointer: true, t0.AddSeconds(1)));

        // Tools picked, then Conversation again: its question is back under the pointer.
        Assert.False(guard.Shown(false, t0.AddSeconds(2)));
        Assert.True(guard.Shown(true, t0.AddSeconds(3)));
        Assert.False(guard.Accepts(pointer: true, t0.AddSeconds(3).AddMilliseconds(499)));
        Assert.True(guard.Accepts(pointer: true, t0.AddSeconds(3).AddMilliseconds(500)));
    }

    [Fact]
    public void A_question_still_scrolling_into_view_is_held_until_500_ms_after_it_stops_and_a_later_scroll_holds_nothing()
    {
        var t0 = DateTimeOffset.Parse("2026-11-02T16:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var guard = new AnswerGuard();
        guard.Arm(t0);
        guard.Scrolled(t0.AddMilliseconds(300));
        guard.Scrolled(t0.AddMilliseconds(700));
        Assert.False(guard.Accepts(pointer: true, t0.AddMilliseconds(1199)));
        Assert.True(guard.Accepts(pointer: true, t0.AddMilliseconds(1200)));

        guard.Scrolled(t0.AddSeconds(2));
        Assert.True(guard.Accepts(pointer: true, t0.AddSeconds(2)));
    }

    [Fact]
    public void A_mouse_pressed_in_the_conversation_holds_it_after_leaving_until_released()
    {
        var follow = new ConversationFollow();
        follow.PointerOver(1);
        follow.Pressed(1);
        follow.PointerLeft(1);
        Assert.False(follow.Follows);
        follow.Released(1);
        Assert.True(follow.Follows);
    }

    // Answers moved where they are (#491): one look of two answers (Approve once over Deny) in a
    // conversation scrolled to `offset` of `scrollable`, its viewport at (200, 100) in a 1440×900
    // window, the question 160 tall at y 8 in what it scrolls.
    static AnswerLook Look(double dx = 0, double dy = 0, double offset = 300, double scrollable = 300, bool person = false,
        double top = 100, double window = 900, double question = 160, double extent = 1000, double questionY = 8, int answers = 2) =>
        new([.. new[] { (240.0 + dx, 150.0 + dy), (240.0 + dx, 190.0 + dy) }.Take(answers)],
            [new ScrollerLook(200, top, 0, offset, 0, scrollable, 900, 700, 900, extent, person)],
            1440, window, new LookBox(24, questionY, 852, question));

    static AnswerMoves From(AnswerLook first)
    {
        var moves = new AnswerMoves();
        Assert.False(moves.Read(first));
        return moves;
    }

    [Fact]
    public void Answers_that_move_more_than_a_pixel_down_or_across_are_held_again_and_a_pixel_is_not()
    {
        var moves = From(Look());
        Assert.False(moves.Read(Look(dy: 1)));
        Assert.True(moves.Read(Look(dy: 2.5)));
        // A window narrowed within one layout moves a centred conversation sideways.
        Assert.True(moves.Read(Look(dx: -8, dy: 2.5)));
        Assert.False(moves.Read(Look(dx: -8, dy: 2.5)));
        // One more answer shown, or one fewer, is a move too.
        Assert.True(moves.Read(Look(dx: -8, dy: 2.5, answers: 1)));
        // Forgotten (another question drawn): the next look is where they are, not a move.
        moves.Forget();
        Assert.False(moves.Read(Look(dy: 400)));
    }

    [Fact]
    public void Only_a_scroll_the_person_makes_is_taken_off_where_the_answers_were()
    {
        // The person scrolls the conversation 120 back: the answers go down with it, under no pointer.
        var moves = From(Look());
        Assert.False(moves.Read(Look(dy: 120, offset: 180, person: true)));

        // The app scrolling the same way (a question or a row brought into view), or anything else
        // that isn't the person: a move.
        moves = From(Look());
        Assert.True(moves.Read(Look(dy: 120, offset: 180)));
    }

    [Fact]
    public void Answers_drifting_less_than_a_pixel_a_look_are_held_once_the_drift_adds_up_to_more()
    {
        // A centred column drifting 0.75 a look (a window edge dragged slowly): the second look is
        // 1.5 from where they were last taken to be, and so on every other look.
        var moves = From(Look());
        var found = Enumerable.Range(1, 40).Where(i => moves.Read(Look(dx: 0.75 * i))).ToList();
        Assert.Equal(Enumerable.Range(1, 20).Select(i => 2 * i), found);

        // Rounding back and forth adds up to nothing.
        moves = From(Look());
        Assert.All(Enumerable.Range(0, 20), i => Assert.False(moves.Read(Look(dy: i % 2 == 0 ? 0.5 : -0.5))));

        // The person's scrolling is taken off as it adds up too.
        moves = From(Look());
        Assert.All(Enumerable.Range(1, 40), i => Assert.False(moves.Read(Look(dy: 0.75 * i, offset: 300 - 0.75 * i, person: true))));
    }

    [Fact]
    public void A_scroll_and_a_move_in_the_same_look_still_find_the_move()
    {
        // The person scrolls 120 back as the strip appears above the conversation and pushes it 60 down.
        var moves = From(Look());
        Assert.True(moves.Read(Look(dy: 180, offset: 180, person: true, top: 160)));
    }

    [Fact]
    public void A_scroller_pulled_back_to_its_end_is_never_taken_off_even_as_the_person_scrolls()
    {
        // Rows below shrink at the list's end: it is pulled back from 300 to 200, and the answers
        // slide 100 down under a still pointer, in the middle of the person's own scrolling.
        var moves = From(Look());
        Assert.True(moves.Read(Look(dy: 100, offset: 200, scrollable: 200, person: true, extent: 900)));

        // The new size laid out first, still scrolled past it, then pulled back in a later look.
        moves = From(Look());
        Assert.False(moves.Read(Look(scrollable: 200, person: true, extent: 900)));
        Assert.True(moves.Read(Look(dy: 100, offset: 200, scrollable: 200, person: true, extent: 900)));

        // Pulled back to 0 as it stops overflowing.
        moves = From(Look(offset: 50, scrollable: 50, extent: 750));
        Assert.True(moves.Read(Look(dy: 50, offset: 0, scrollable: 0, person: true, extent: 700)));

        // Pulled back from 300 to its new end at 200, and scrolled 50 further up by the person in
        // the same look: none of it taken off.
        moves = From(Look());
        Assert.True(moves.Read(Look(dy: 150, offset: 150, scrollable: 200, person: true, extent: 900)));

        // Pulled back 0.75 a look, forty looks running, as the person scrolls: each one a clamp,
        // and the slide adds up to a move as any other does.
        moves = From(Look());
        var found = Enumerable.Range(1, 40).Where(i => moves.Read(Look(dy: 0.75 * i, offset: 300 - 0.75 * i, scrollable: 300 - 0.75 * i, person: true, extent: 1000 - 0.75 * i))).ToList();
        Assert.Equal(Enumerable.Range(1, 20).Select(i => 2 * i), found);

        // The person scrolling to the end is no clamp.
        moves = From(Look(offset: 100));
        Assert.False(moves.Read(Look(dy: -200, offset: 300, person: true)));
    }

    [Fact]
    public void The_persons_own_question_growing_as_they_type_is_theirs_only_when_nothing_else_changed()
    {
        // Other's box opens: the question grows 40, what scrolls grows 40, Send is 40 lower and a
        // box is added. Theirs, when they did it; a move when they didn't.
        var grown = Look(question: 200, extent: 1040) with { Answers = [(240, 150), (240, 230), (240, 270)] };
        Assert.False(From(Look()).Read(grown, own: true));
        Assert.True(From(Look()).Read(grown, own: false));

        // Anything else in the same look is a move whatever they are doing: the window resized, the
        // conversation's viewport moved (the strip, a banner), the title wrapping above the question
        // in what scrolls, or what scrolls growing by more than the question did (a response).
        Assert.True(From(Look()).Read(grown with { WindowHeight = 880 }, own: true));
        Assert.True(From(Look()).Read(Look(dy: 60, top: 160, question: 200, extent: 1040), own: true));
        Assert.True(From(Look()).Read(Look(dy: 20, question: 200, extent: 1060, questionY: 28), own: true));
        Assert.True(From(Look()).Read(Look(dy: 40, question: 200, extent: 1100), own: true));

        // Their question shrinking at the end (an error going) pulls the conversation back: theirs.
        Assert.False(From(Look()).Read(Look(dy: 30, offset: 270, scrollable: 270, question: 130, extent: 970), own: true));

        // Their question resized nothing: an answer moved anyway is a move.
        Assert.True(From(Look()).Read(Look(dy: 30), own: true));
    }
}
