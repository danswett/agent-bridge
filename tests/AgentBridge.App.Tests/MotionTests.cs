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
}
