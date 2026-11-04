namespace AgentBridge.Host.Core;

// Which transcripts were written to, as the session source's file watcher hears it, for a
// delivery waiting on the agent to record what it typed. It polled every 250 ms, and a record
// written just after a look waited out the rest of the interval: 0.66 to 0.86 s between the
// prompt being recorded and delivered, measured (#599). A watcher drops events without saying
// so, so a waiter still looks again after its poll interval whatever it hears.
public sealed class TranscriptWrites
{
    // Only files a delivery is waiting on right now: an entry goes when its last waiter is done,
    // so a session that ends while nobody waits leaves nothing behind.
    readonly Dictionary<string, Pending> pending = new(PathComparer);
    readonly object gate = new();

    // Every write heard, by path as the watcher gave it: lets a reader keep up with a transcript
    // it is not waiting on.
    public event Action<string>? Heard;

    static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    static string Key(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    // Completes at the next write to the file after this call. Taken before a look at the file,
    // so a write between the look and the wait is not missed; disposed once the wait is over.
    // Waiters on the same file share one signal.
    public Waiter Next(string path)
    {
        var key = Key(path);
        lock (gate)
        {
            if (!pending.TryGetValue(key, out var waiting)) { pending[key] = waiting = new Pending(); }
            waiting.Waiters++;
            return new Waiter(this, key, waiting);
        }
    }

    public void Written(string path)
    {
        Pending? waiting;
        lock (gate) { pending.Remove(Key(path), out waiting); }
        waiting?.Done.TrySetResult();
        Heard?.Invoke(path);
    }

    // Tests: how many files are waited on.
    internal int Waited
    {
        get { lock (gate) { return pending.Count; } }
    }

    void Release(string key, Pending waiting)
    {
        lock (gate)
        {
            // Already removed by a write, and perhaps replaced by a later waiter's: only this one goes.
            if (--waiting.Waiters == 0 && pending.TryGetValue(key, out var current) && current == waiting) { pending.Remove(key); }
        }
    }

    // Waits for the write `written` stands for (from Next), or for `poll`, whichever comes first.
    public static async Task WaitAsync(Waiter? written, TimeSpan poll, TimeProvider time, CancellationToken ct = default)
    {
        if (written is null)
        {
            await Task.Delay(poll, time, ct).ConfigureAwait(false);
            return;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(poll, time, stop.Token);
        await Task.WhenAny(written.Written, delay).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    internal sealed class Pending
    {
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Waiters;
    }

    public sealed class Waiter : IDisposable
    {
        readonly TranscriptWrites owner;
        readonly string key;
        readonly Pending waiting;
        int disposed;

        internal Waiter(TranscriptWrites owner, string key, Pending waiting) => (this.owner, this.key, this.waiting) = (owner, key, waiting);

        public Task Written => waiting.Done.Task;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) { owner.Release(key, waiting); }
        }
    }
}
