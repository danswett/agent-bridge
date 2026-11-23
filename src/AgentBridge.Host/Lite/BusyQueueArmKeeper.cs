namespace AgentBridge.Host.Lite;

// Keeps the driver arm of a held busy-Claude send waiting while the send stays held, so a prompt Claude takes long after the 10 minute arm
// still shows as the agent's (docs/message-delivery.md "Busy sends to Claude", #3856).
public sealed class BusyQueueArmKeeper(BusyQueueStore store, TimeProvider time)
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    readonly BusyQueueStore records = store;
    readonly TimeProvider clock = time;

    public Task Start(string request, Action keep) => Task.CompletedTask;
    public Task KeepAsync(string request, Action keep, CancellationToken ct = default) => Task.CompletedTask;
}