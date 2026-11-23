using AgentBridge.Host.Delivery.AgentReplies;

namespace AgentBridge.Host.Lite;

// Watches a held busy-Claude send for the prompt Claude takes, up to the 120 s watch, then reports watchEnded and stops
// (docs/message-delivery.md "Busy sends to Claude", #3856). The item stays held after the report: a prompt taken later is still found when
// the slot is next checked (D2b-2b1). A record freed or settled elsewhere ends the watch without being touched.
public sealed class BusyQueueWatcher(BusyQueueStore store, TimeProvider time)
{
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);

    // Whether Claude took the prompt: transcript path, offset, request id. The real reader unless a test supplies one.
    public Func<string, long, string, bool> Taken { get; init; } = ClaudeQueue.Taken;

    // Starts the watch in the background. A watcher that fails leaves the record held, exactly as it was.
    public Task Start(string request, Func<bool>? ended = null) => Task.Run(async () =>
    {
        try { await WatchAsync(request, default, ended); }
        catch (Exception e) when (e is not OutOfMemoryException) { }
    });

    public async Task WatchAsync(string request, CancellationToken ct = default, Func<bool>? ended = null)
    {
        if (store.Get(request) is not { Held: true } first) { return; }
        // A queued send is watched until its Accepted time plus the watch; one that was unconfirmed with no row, for the watch from now.
        var until = first.Until ?? time.GetUtcNow() + BusyQueueStore.Watch;
        var (transcript, offset) = store.Tracking(request);
        while (!ct.IsCancellationRequested)
        {
            var now = store.Get(request);
            if (now is null || !now.Held || now.State == BusyQueueStore.Delivered) { return; }
            if (transcript is not null && Read(transcript, offset, request)) { store.Settle(request, BusyQueueStore.Delivered, null, held: false); return; }
            if (time.GetUtcNow() >= until)
            {
                // An unconfirmed noRow send keeps its reason; a queued one is now reported watchEnded. Either stays held.
                if (now.State == BusyQueueStore.Queued) { store.Settle(request, BusyQueueStore.Unconfirmed, "watchEnded", held: true); }
                return;
            }
            try { await Task.Delay(Poll, time, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    bool Read(string path, long from, string request)
    {
        try { return Taken(path, from, request); }
        catch (Exception e) when (e is not OutOfMemoryException) { return false; }
    }
}