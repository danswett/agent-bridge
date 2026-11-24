namespace AgentBridge.Host.Fleet;

// One read shared by every local caller (docs/design/fleet-session-registry.md "One registry read for
// every local caller", #1396). A host has one relay connection and the relay answers one registry read
// at a time per socket's budget, so readers that arrive while a read is in flight wait for it, and a
// result younger than `freshness` (monotonic clock) answers the next reader. A failed read is never kept,
// and a different connection never sees another's answer. A caller's cancellation stops only its own wait.
// `fresh` readers (the name fence, name-uniqueness checks) never share: they start their own read.
sealed class SharedRead<T>(TimeProvider clock, TimeSpan freshness)
{
    readonly object gate = new();
    object? connection;
    Task<T>? flight;
    T? last;
    long readAt;
    bool has;

    public Task<T> GetAsync(object forConnection, Func<Task<T>> read, CancellationToken ct, bool fresh = false)
    {
        if (fresh) { return read().WaitAsync(ct); }
        Task<T> task;
        lock (gate)
        {
            if (!ReferenceEquals(connection, forConnection)) { connection = forConnection; flight = null; has = false; last = default; }
            if (has && clock.GetElapsedTime(readAt) < freshness) { return Task.FromResult(last!); }
            task = flight ??= Start(forConnection, read);
        }
        return task.WaitAsync(ct);
    }

    // A new relay connection: nothing kept or in flight belongs to it (the connection key would catch it too).
    public void Reset() { lock (gate) { connection = null; flight = null; has = false; last = default; } }

    async Task<T> Start(object forConnection, Func<Task<T>> read)
    {
        // The read runs apart from any caller's token: the callers waiting on it are not its owner.
        await Task.Yield();
        try
        {
            var result = await read();
            lock (gate)
            {
                if (ReferenceEquals(connection, forConnection)) { last = result; readAt = clock.GetTimestamp(); has = true; }
            }
            return result;
        }
        finally
        {
            lock (gate) { if (ReferenceEquals(connection, forConnection)) { flight = null; } }
        }
    }
}
