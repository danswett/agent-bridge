using System.Collections.Concurrent;

namespace AgentBridge.Host.Core;

// Which transcripts were written to, as the session source's file watcher hears it, for a
// delivery waiting on the agent to record what it typed. It polled every 250 ms, and a record
// written just after a look waited out the rest of the interval: 0.66 to 0.86 s between the
// prompt being recorded and delivered, measured (#599). A watcher drops events without saying
// so, so a waiter still looks again after its poll interval whatever it hears.
public sealed class TranscriptWrites
{
    readonly ConcurrentDictionary<string, TaskCompletionSource> next = new(PathComparer);

    static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    static string Key(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    // Completes at the next write to the file after this call. Taken before a look at the file,
    // so a write between the look and the wait is not missed.
    public Task Next(string path) =>
        next.GetOrAdd(Key(path), _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public void Written(string path)
    {
        if (next.TryRemove(Key(path), out var waiting)) { waiting.TrySetResult(); }
    }

    // Waits for the write `written` stands for (from Next), or for `poll`, whichever comes first.
    public static async Task WaitAsync(Task? written, TimeSpan poll, TimeProvider time, CancellationToken ct = default)
    {
        if (written is null)
        {
            await Task.Delay(poll, time, ct).ConfigureAwait(false);
            return;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(poll, time, stop.Token);
        await Task.WhenAny(written, delay).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }
}
