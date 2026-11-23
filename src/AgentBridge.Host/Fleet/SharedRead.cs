namespace AgentBridge.Host.Fleet;

// One read shared by every local caller (docs/design/fleet-session-registry.md "One registry read for
// every local caller", #1396). A host has one relay connection and the relay answers one registry read
// at a time per socket's budget, so readers that arrive while a read is in flight wait for it, and a
// result younger than `freshness` (monotonic clock) answers the next reader. A failed read is never kept,
// and a different connection never sees another's answer. A caller's cancellation stops only its own wait.
// `fresh` readers (the name fence, name-uniqueness checks) never share: they start their own read.
// A read whose reply is lost would hold every sharer for the read's own timeout (30 s): sharers wait at most
// `patience` for a flight, then give up on it and ask again, and a flight given up on never stores its answer.
sealed class SharedRead<T>(TimeProvider clock, TimeSpan freshness, TimeSpan? patience = null)
{
    public static readonly TimeSpan DefaultPatience = TimeSpan.FromSeconds(5);
    readonly TimeSpan wait = patience ?? DefaultPatience;
    readonly object gate = new();
    object? connection;
    Flight? flight;
    T? last;
    long readAt;
    bool has;

    sealed class Flight { public Task<T> Task = null!; }

    public async Task<T> GetAsync(object forConnection, Func<Task<T>> read, CancellationToken ct, bool fresh = false)
    {
        if (fresh) { return await read().WaitAsync(ct); }
        for (var attempt = 0; ; attempt++)
        {
            Flight mine;
            lock (gate)
            {
                if (!ReferenceEquals(connection, forConnection)) { connection = forConnection; flight = null; has = false; last = default; }
                if (has && clock.GetElapsedTime(readAt) < freshness) { return last!; }
                mine = flight ??= Begin(forConnection, read);
            }
            try { return await mine.Task.WaitAsync(wait, clock, ct); }
            catch (TimeoutException) when (attempt == 0)
            {
                // Lost reply: let the next reader start a new read rather than wait on this one.
                lock (gate) { if (ReferenceEquals(flight, mine)) { flight = null; } }
            }
        }
    }

    // A new relay connection: nothing kept or in flight belongs to it (the connection key would catch it too).
    public void Reset() { lock (gate) { connection = null; flight = null; has = false; last = default; } }

    Flight Begin(object forConnection, Func<Task<T>> read)
    {
        var f = new Flight();
        f.Task = Run(f, forConnection, read);
        return f;
    }

    async Task<T> Run(Flight self, object forConnection, Func<Task<T>> read)
    {
        // The read runs apart from any caller's token: the callers waiting on it are not its owner.
        await Task.Yield();
        try
        {
            var result = await read();
            lock (gate)
            {
                // Only the read still wanted keeps its answer: one given up on, or a connection since changed, does not.
                if (ReferenceEquals(connection, forConnection) && ReferenceEquals(flight, self)) { last = result; readAt = clock.GetTimestamp(); has = true; }
            }
            return result;
        }
        finally
        {
            lock (gate) { if (ReferenceEquals(flight, self)) { flight = null; } }
        }
    }
}
