using AgentBridge.Host.Fleet;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// #1396: every local reader of the registry shares one relay read, so two agent sessions replying in the
// same second no longer spend the relay's read budget twice.
public sealed class SharedReadTests
{
    static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
    readonly object connection = new();

    SharedRead<int> Reader() => new(time, Window);

    [Fact]
    public async Task Readers_that_arrive_while_a_read_is_in_flight_share_its_one_result()
    {
        var reads = 0; var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shared = Reader();
        Task<int> Read() { reads++; return gate.Task; }
        var all = Enumerable.Range(0, 5).Select(_ => shared.GetAsync(connection, Read, CancellationToken.None)).ToArray();
        gate.SetResult(7);
        Assert.All(await Task.WhenAll(all), v => Assert.Equal(7, v));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task A_read_inside_the_freshness_window_is_answered_without_asking_again_and_one_after_it_asks_again()
    {
        var reads = 0;
        var shared = Reader();
        Task<int> Read() => Task.FromResult(++reads);
        Assert.Equal(1, await shared.GetAsync(connection, Read, CancellationToken.None));
        time.Advance(Window - TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, await shared.GetAsync(connection, Read, CancellationToken.None));
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, await shared.GetAsync(connection, Read, CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_read_reaches_every_waiting_caller_and_is_never_answered_from_cache()
    {
        var reads = 0; var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shared = Reader();
        Task<int> Read() { reads++; return reads == 1 ? gate.Task : Task.FromResult(42); }
        var waiting = new[] { shared.GetAsync(connection, Read, CancellationToken.None), shared.GetAsync(connection, Read, CancellationToken.None) };
        gate.SetException(new InvalidOperationException("refused: slow-down"));
        foreach (var w in waiting) { await Assert.ThrowsAsync<InvalidOperationException>(() => w); }
        Assert.Equal(42, await shared.GetAsync(connection, Read, CancellationToken.None));
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task A_different_connection_never_receives_the_previous_connections_answer()
    {
        var shared = Reader();
        Assert.Equal(1, await shared.GetAsync(connection, () => Task.FromResult(1), CancellationToken.None));
        Assert.Equal(2, await shared.GetAsync(new object(), () => Task.FromResult(2), CancellationToken.None));
    }

    [Fact]
    public async Task One_callers_cancellation_does_not_cancel_the_read_the_others_wait_for()
    {
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shared = Reader(); var reads = 0;
        Task<int> Read() { reads++; return gate.Task; }
        using var cancel = new CancellationTokenSource();
        var first = shared.GetAsync(connection, Read, cancel.Token);
        var second = shared.GetAsync(connection, Read, CancellationToken.None);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        gate.SetResult(9);
        Assert.Equal(9, await second);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task A_fresh_read_starts_its_own_read_inside_the_window_and_while_another_is_in_flight()
    {
        var reads = 0; var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shared = Reader();
        Task<int> Slow() { reads++; return gate.Task; }
        var inFlight = shared.GetAsync(connection, Slow, CancellationToken.None);
        Assert.Equal(100, await shared.GetAsync(connection, () => { reads++; return Task.FromResult(100); }, CancellationToken.None, fresh: true));
        gate.SetResult(1);
        Assert.Equal(1, await inFlight);
        Assert.Equal(2, reads); // the fence's own read and the in-flight one: it did not join the earlier read
        time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(200, await shared.GetAsync(connection, () => { reads++; return Task.FromResult(200); }, CancellationToken.None, fresh: true));
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task Reset_discards_the_kept_result_so_the_next_reader_asks_again()
    {
        var reads = 0; var shared = Reader();
        Task<int> Read() => Task.FromResult(++reads);
        Assert.Equal(1, await shared.GetAsync(connection, Read, CancellationToken.None));
        shared.Reset();
        Assert.Equal(2, await shared.GetAsync(connection, Read, CancellationToken.None));
    }

    [Fact]
    public async Task A_read_still_in_flight_for_the_old_connection_is_not_kept_for_the_new_one()
    {
        var old = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shared = Reader(); var other = new object(); var newReads = 0;
        var oldRead = shared.GetAsync(connection, () => old.Task, CancellationToken.None);
        Assert.Equal(2, await shared.GetAsync(other, () => { newReads++; return Task.FromResult(2); }, CancellationToken.None));
        old.SetResult(1);
        Assert.Equal(1, await oldRead);
        Assert.Equal(2, await shared.GetAsync(other, () => { newReads++; return Task.FromResult(3); }, CancellationToken.None));
        Assert.Equal(1, newReads);
    }

    static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_lost_reply_does_not_hold_the_readers_for_the_whole_read_timeout()
    {
        var reads = 0; var lost = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shared = new SharedRead<int>(time, Window, Patience);
        Task<int> Read() { return ++reads == 1 ? lost.Task : Task.FromResult(42); }
        var first = shared.GetAsync(connection, Read, CancellationToken.None);
        var second = shared.GetAsync(connection, Read, CancellationToken.None);
        await Task.Delay(50);
        time.Advance(Patience);
        Assert.Equal(42, await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(42, await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task A_read_given_up_on_never_replaces_the_newer_answer_when_it_finally_returns()
    {
        var reads = 0; var lost = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shared = new SharedRead<int>(time, Window, Patience);
        Task<int> Read() { return ++reads == 1 ? lost.Task : Task.FromResult(42); }
        var first = shared.GetAsync(connection, Read, CancellationToken.None);
        await Task.Delay(50);
        time.Advance(Patience);
        Assert.Equal(42, await first.WaitAsync(TimeSpan.FromSeconds(5)));
        lost.SetResult(7);
        await Task.Delay(50);
        Assert.Equal(42, await shared.GetAsync(connection, () => Task.FromResult(99), CancellationToken.None));
    }
}
