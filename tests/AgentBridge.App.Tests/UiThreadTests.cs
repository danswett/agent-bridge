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
        Task? handled = null;
        await Task.Run(() => ui.Post(() => handled = Handle()));
        await handled!.WaitAsync(FakeHost.Patience);
        Assert.True(await back.Task);
    }

    [Fact]
    public async Task Nothing_posted_runs_while_a_test_step_is_part_way_through()
    {
        using var posting = new ManualResetEventSlim();
        using var ran = new ManualResetEventSlim();
        var post = Task.Run(() =>
        {
            posting.Set();
            ui.Post(ran.Set);
        });
        // The post has been made from the other thread; until this step awaits, it cannot run.
        Assert.True(posting.Wait(FakeHost.Patience), "the post");
        Assert.False(ran.Wait(TimeSpan.FromMilliseconds(100)));
        await post;
        Assert.True(ran.IsSet);
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

    // What StallWatch writes down when a test's waits stop looking (#660): every thread's stack,
    // a blocked one's included, read while the process goes on.
    [Fact]
    public void The_stall_watch_reads_the_stack_of_a_thread_blocked_elsewhere()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var parked = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        var thread = new Thread(() => ParkedUntil(parked, go)) { IsBackground = true };
        thread.Start();
        Assert.True(parked.Wait(FakeHost.Patience));
        try
        {
            var stacks = StallWatch.Stacks();
            Assert.Contains($"{nameof(UiThreadTests)}.{nameof(ParkedUntil)}", stacks);
        }
        finally { go.Set(); thread.Join(); }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void ParkedUntil(ManualResetEventSlim parked, ManualResetEventSlim go)
    {
        parked.Set();
        go.Wait();
    }
}
