using AgentBridge.App.Core;
using Xunit;

// The lock the app holds across the person's Windows sessions: the relay link's, and the one
// around creating the device key.
public sealed class UserLockTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("userlock-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Fact]
    public void Only_one_holder_at_a_time_and_it_is_free_once_let_go()
    {
        var path = Path.Combine(folder, "relay.lock");
        using (var first = UserLock.TryTake(path))
        {
            Assert.NotNull(first);
            Assert.Null(UserLock.TryTake(path));
        }
        using var again = UserLock.TryTake(path);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task A_second_copy_waits_for_the_first_to_finish_before_it_runs()
    {
        var path = Path.Combine(folder, "init.lock");
        var order = new List<string>();
        var first = UserLock.TryTake(path)!;

        var second = UserLock.RunAsync(path, () => { lock (order) { order.Add("second"); } return 2; }, TimeSpan.FromSeconds(30));
        await Task.Delay(300);
        lock (order) { order.Add("first done"); }
        first.Dispose();

        Assert.Equal(2, await second.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(["first done", "second"], order);
        // And the second let go of it in turn.
        using var after = UserLock.TryTake(path);
        Assert.NotNull(after);
    }

    [Fact]
    public async Task A_lock_held_past_the_wait_does_not_stop_the_work()
    {
        var path = Path.Combine(folder, "init.lock");
        using var stuck = UserLock.TryTake(path);
        var said = new List<string>();

        Assert.Equal(7, await UserLock.RunAsync(path, () => 7, TimeSpan.FromMilliseconds(200), said.Add));
        Assert.Contains("still held", Assert.Single(said));
    }
}
