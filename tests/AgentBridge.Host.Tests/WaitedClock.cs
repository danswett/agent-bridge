using Microsoft.Extensions.Time.Testing;

// A fake clock that is moved on only while the code under test waits on it: each time that code
// starts a wait (a Task.Delay is a timer), Run moves the clock on by one step. Its timeouts and
// "after so long" steps then fall after a fixed number of its own looks, however slowly a loaded
// runner gets round to them, where on the real clock a stalled pool could let a whole timeout
// pass between two looks (#479, #507, #508). A wait meant to run out still takes its whole
// timeout, of this clock's time.
sealed class WaitedClock() : FakeTimeProvider(DateTimeOffset.UtcNow)
{
    readonly SemaphoreSlim waits = new(0);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        waits.Release();
        return timer;
    }

    // Moves the clock on by step each time something starts waiting on it, until work is done.
    public async Task<T> Run<T>(Task<T> work, TimeSpan step)
    {
        while (true)
        {
            using var stop = new CancellationTokenSource();
            var waited = waits.WaitAsync(stop.Token);
            if (await Task.WhenAny(work, waited) != work)
            {
                Advance(step);
                continue;
            }
            // A wait left queued would take the next signal from a later Run on this clock, which
            // would then never move it: so it is withdrawn, and a signal it took is given back.
            stop.Cancel();
            try { await waited; waits.Release(); }
            catch (OperationCanceledException) { }
            return await work;
        }
    }
}
