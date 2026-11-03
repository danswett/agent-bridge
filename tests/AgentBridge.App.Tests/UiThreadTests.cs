using Xunit;

// The tests' UI thread itself (TestUi, UiTestFramework): what every other test here relies on to
// see the view models as the app's one UI thread leaves them (#101).
public sealed class UiThreadTests
{
    readonly LockedDispatcher ui = new();

    [Fact]
    public async Task A_test_runs_on_its_UI_thread_before_and_after_each_await()
    {
        Assert.True(TestUi.ForTest.OnIt);
        await Task.Delay(1);
        Assert.True(TestUi.ForTest.OnIt);
        await Task.Run(() => Assert.False(TestUi.ForTest.OnIt));
        Assert.True(TestUi.ForTest.OnIt);
    }

    [Fact]
    public async Task What_an_action_posted_from_another_thread_awaits_comes_back_to_the_UI_thread()
    {
        var back = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Handle()
        {
            await Task.Delay(1);
            back.SetResult(TestUi.ForTest.OnIt);
        }
        // As a host event arrives: on the pipe's thread, posted to the UI.
        await Task.Run(() => ui.Post(() => _ = Handle()));
        Assert.True(await back.Task);
    }

    [Fact]
    public async Task Nothing_posted_runs_while_a_test_step_is_part_way_through()
    {
        var log = new List<string>();
        var posting = Task.Run(() => ui.Post(() => log.Add("posted")));
        log.Add("step begins");
        Thread.Sleep(50);
        log.Add("step ends");
        await posting;
        Assert.Equal(["step begins", "step ends", "posted"], log);
    }

    [Fact]
    public async Task Each_test_has_a_UI_thread_of_its_own()
    {
        var mine = TestUi.ForTest;
        var other = await Task.Run(() => TestUi.RunAsync(() => Task.FromResult(TestUi.ForTest)));
        Assert.NotSame(mine, other);
        Assert.True(mine.OnIt);
        Assert.False(other.OnIt);
    }
}
