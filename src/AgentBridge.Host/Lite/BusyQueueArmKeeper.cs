namespace AgentBridge.Host.Lite;

// Keeps the driver arm of a held busy-Claude send waiting while the send stays held, so a prompt Claude takes long after the 10 minute arm
// still shows as the agent's (docs/message-delivery.md "Busy sends to Claude", #3856). It moves only the arm's time; see SessionDriver.KeepArm.
public sealed class BusyQueueArmKeeper(BusyQueueStore store, TimeProvider time)
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    // Starts keeping in the background. A keeper that fails leaves the arm as it was.
    public Task Start(string request, Action keep, Func<bool>? ended = null) => Task.Run(async () =>
    {
        try { await KeepAsync(request, keep, default, ended); }
        catch (Exception e) when (e is not OutOfMemoryException) { }
    });

    public async Task KeepAsync(string request, Action keep, CancellationToken ct = default, Func<bool>? ended = null)
    {
        while (!ct.IsCancellationRequested)
        {
            if (store.Get(request) is not { Held: true }) { return; }
            try { keep(); }
            catch (Exception e) when (e is not OutOfMemoryException) { }
            try { await Task.Delay(Every, time, ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}