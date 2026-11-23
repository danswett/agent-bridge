namespace AgentBridge.Host.Lite;

// Watches a held busy-Claude send for the prompt Claude takes, up to the 120 s watch, then reports watchEnded and stops
// (docs/message-delivery.md "Busy sends to Claude", #3856). The item stays held after the report.
public sealed class BusyQueueWatcher(BusyQueueStore store, TimeProvider time)
{
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);

    readonly BusyQueueStore records = store;
    readonly TimeProvider clock = time;

    // Whether Claude took the prompt: transcript path, offset, request id. The real reader unless a test supplies one.
    public Func<string, long, string, bool> Taken { get; init; } = AgentBridge.Host.Delivery.AgentReplies.ClaudeQueue.Taken;

    public Task Start(string request) => Task.CompletedTask;
    public Task WatchAsync(string request, CancellationToken ct = default) => Task.CompletedTask;
}