using System.Diagnostics;
using Xunit.Abstractions;
using Xunit.Sdk;

// Linked into App and Host tests, so each assembly counts its own running collections.
internal sealed class TestCollectionActivity
{
    static int total;
    int active;

    internal static int Total => Volatile.Read(ref total);
    internal int Count => Volatile.Read(ref active);

    internal void Started()
    {
        Interlocked.Increment(ref active);
        Interlocked.Increment(ref total);
    }

    internal void Finished()
    {
        Interlocked.Decrement(ref total);
        Interlocked.Decrement(ref active);
    }

    internal async Task<T> RunAsync<T>(Func<Task<T>> run)
    {
        Started();
        try { return await run(); }
        finally { Finished(); }
    }
}

// Observe the stock Host runner after admission, not its queued semaphore waiters.
internal sealed class TestCollectionMessageBus(IMessageBus inner, TestCollectionActivity activity) : IMessageBus
{
    public bool QueueMessage(IMessageSinkMessage message)
    {
        if (message is ITestCollectionStarting)
        {
            activity.Started();
            var accepted = false;
            try { return accepted = inner.QueueMessage(message); }
            finally { if (!accepted) { activity.Finished(); } }
        }
        if (message is ITestCollectionFinished)
        {
            try { return inner.QueueMessage(message); }
            finally { activity.Finished(); }
        }
        return inner.QueueMessage(message);
    }

    public void Dispose() => inner.Dispose();
}

internal static class TestTimeoutDiagnostics
{
    internal static string Describe()
    {
        ThreadPool.GetMaxThreads(out var maximum, out _);
        ThreadPool.GetAvailableThreads(out var available, out _);
        using var process = Process.GetCurrentProcess();
        // Available workers are relative to the maximum, not the minimum or ThreadCount.
        // These are adjacent observations, not an atomic snapshot or a cause of the timeout.
        return $"pool threads {ThreadPool.ThreadCount}, queued {ThreadPool.PendingWorkItemCount}; "
            + $"pool workers available {available}, busy {maximum - available}, max {maximum}; "
            + $"active test collections {TestCollectionActivity.Total}; total process threads {process.Threads.Count}";
    }

    internal static async Task WaitAsync(Task task, TimeSpan timeout)
    {
        try { await task.WaitAsync(timeout); }
        catch (TimeoutException error)
        {
            throw new TimeoutException($"{error.Message} ({Describe()})", error);
        }
    }

    internal static async Task<T> WaitAsync<T>(Task<T> task, TimeSpan timeout)
    {
        await WaitAsync((Task)task, timeout);
        return await task;
    }
}
