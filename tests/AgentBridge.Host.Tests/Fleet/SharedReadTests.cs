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
}
