using AgentBridge.Host.Delivery;

namespace AgentBridge.Host;

// A stop interrupts a restart even while the restart is waiting for delivery to finish.
sealed class PlannedStops(DeliveryGate gate, TimeSpan timeout, Action<string> log)
{
    readonly Lock state = new();
    CancellationTokenSource restart = new();
    bool stopping;

    public CancellationToken RestartToken()
    {
        lock (state) { return stopping ? new CancellationToken(true) : restart.Token; }
    }

    public bool TryCommitRestart(CancellationToken interrupted, Action handover)
    {
        lock (state)
        {
            if (stopping || interrupted.IsCancellationRequested) { return false; }
            handover();
            return true;
        }
    }

    public async Task<IDisposable?> PrepareStopAsync()
    {
        CancellationTokenSource interrupt;
        lock (state)
        {
            if (stopping) { return null; }
            stopping = true;
            interrupt = restart;
        }
        try
        {
            interrupt.Cancel();
            var lease = await gate.PrepareStopAsync(timeout);
            if (lease is not null) { return new StopLease(this, lease); }
            log("the host was not stopped: a delivery or typing is still in progress");
            Release();
            return null;
        }
        catch
        {
            Release();
            throw;
        }
    }

    void Release()
    {
        lock (state)
        {
            restart = new CancellationTokenSource();
            stopping = false;
        }
    }

    sealed class StopLease(PlannedStops owner, IDisposable lease) : IDisposable
    {
        int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
            lease.Dispose();
            owner.Release();
        }
    }
}
