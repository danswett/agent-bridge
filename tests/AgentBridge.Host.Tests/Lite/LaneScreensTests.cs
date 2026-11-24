using AgentBridge.Host.Lite;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class LaneScreensTests
{
    readonly FakeTimeProvider clock = new();
    (bool? Idle, bool? Empty) value = (true, true);
    Exception? failure;
    int reads;

    LaneScreens Create() => new((_, _) =>
    {
        reads++;
        return failure is null ? Task.FromResult(value) : Task.FromException<(bool? Idle, bool? Empty)>(failure);
    }, clock);

    [Fact]
    public async Task A_session_never_read_is_unknown_and_a_fresh_read_is_returned_until_it_is_older_than_three_seconds()
    {
        var screens = Create();
        Assert.Equal((null, null), screens.Get("t"));
        await screens.RefreshAsync("t");
        Assert.Equal((true, true), screens.Get("t"));
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal((true, true), screens.Get("t"));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal((null, null), screens.Get("t"));
        Assert.Equal((null, null), screens.Get("other"));
    }

    [Fact]
    public async Task A_failed_read_makes_the_session_unknown_even_after_a_good_one()
    {
        var screens = Create();
        await screens.RefreshAsync("t");
        failure = new IOException("gone");
        await screens.RefreshAsync("t");
        Assert.Equal((null, null), screens.Get("t"));
        failure = null;
        await screens.RefreshAsync("t");
        Assert.Equal((true, true), screens.Get("t"));
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task A_cancelled_read_propagates_and_leaves_the_earlier_snapshot_to_age_out()
    {
        var screens = Create();
        await screens.RefreshAsync("t");
        failure = new OperationCanceledException();
        await Assert.ThrowsAsync<OperationCanceledException>(() => screens.RefreshAsync("t"));
        Assert.Equal((true, true), screens.Get("t"));
    }

    static readonly string[] Box = ["\u25CF Done.", "", "\u257B\u2584\u2584\u2584\u2584\u2584\u2584\u2584\u2584", "\u2503", "\u2579\u2580\u2580\u2580\u2580\u2580\u2580\u2580\u2580",
        " \u2190 open sidebar \u00B7 Interactive \u00B7 Allow All \u00B7 / commands \u00B7 ? help \u00B7 tab next tab"];
    static readonly string[] Draft = [Box[0], Box[1], Box[2], "\u2503 half a thought", Box[4], " Interactive \u00B7 Allow All \u00B7 @ files \u00B7 # issues"];
    static readonly string[] Working = [Box[0], Box[1], Box[2], "\u2503", Box[4], " \u25D0 Working esc interrupt \u00B7 ctrl+q enqueue"];

    [Fact]
    public void The_screen_is_empty_only_for_the_main_box_without_a_draft_and_idle_only_when_no_turn_runs()
    {
        Assert.Equal((true, true), LaneScreens.Map(true, Box));
        Assert.Equal((false, true), LaneScreens.Map(false, Box));
        Assert.Equal((true, false), LaneScreens.Map(true, Draft));
        Assert.Equal((false, false), LaneScreens.Map(true, Working));
        Assert.Equal((null, null), LaneScreens.Map(true, ["nothing here"]));
    }
}
