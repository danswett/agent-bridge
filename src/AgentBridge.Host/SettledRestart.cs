namespace AgentBridge.Host;

// How this host restarts itself, whoever asked: the app (host.restart), the committed-departure
// watchdog (#805 N1) or a person's device (manage.restart, #801). First it is done with any usage
// probe tied to it, so a standalone Codex app-server is not cut mid-refresh (#238); then the
// replacement waits for this host to have fully stopped (HostHandover, #239). A remote restart
// once went straight to the handover, past the probes.
//
// A stop wins over a restart (docs/host-lifecycle.md): the settle can take 90 s, and a stop asked
// for meanwhile (the app's Stop, a shutdown, a sign-out) used to be turned into a restart by the
// handover that followed. Now the wait ends when the stop arrives, and nothing is handed over.
public sealed class SettledRestart(Func<TimeSpan, CancellationToken, Task>? settleProbes, Action handover, Action stop, CancellationToken stopping,
    Func<CancellationToken, Task<IDisposable?>>? prepareStop = null,
    Func<CancellationToken, Action, bool>? commitHandover = null)
{
    public static readonly TimeSpan SettleFor = TimeSpan.FromSeconds(90);

    public async Task RunAsync(CancellationToken interrupted = default)
    {
        using var linked = interrupted.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(stopping, interrupted) : null;
        var ending = linked?.Token ?? stopping;
        if (ending.IsCancellationRequested) { return; }
        if (settleProbes is not null)
        {
            // WaitAsync too, so a settle that ignores the token still lets the stop through at once.
            try { await settleProbes(SettleFor, ending).WaitAsync(ending); }
            catch (OperationCanceledException) when (ending.IsCancellationRequested) { return; }
        }
        if (ending.IsCancellationRequested) { return; }
        IDisposable? prepared;
        try { prepared = prepareStop is not null ? await prepareStop(ending) : null; }
        catch (OperationCanceledException) when (ending.IsCancellationRequested) { return; }
        if (prepareStop is not null && prepared is null) { return; }
        try
        {
            if (commitHandover is not null)
            {
                if (!commitHandover(ending, handover)) { prepared?.Dispose(); return; }
            }
            else
            {
                if (ending.IsCancellationRequested) { prepared?.Dispose(); return; }
                handover();
            }
        }
        catch { prepared?.Dispose(); throw; }
        stop();
    }
}
