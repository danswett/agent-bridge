namespace AgentBridge.Host.Delivery;

// Services turn injector exceptions into normal delivery results. Mark uncertainty before
// those catches run: disposing the worker handle does not prove a child stopped typing.
internal sealed class GateTrackedInjector(IConsoleInjector inner, DeliveryGate gate) : IConsoleInjector
{
    public async Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default)
    {
        try { return await inner.InjectAsync(processId, plan, cancellationToken).ConfigureAwait(false); }
        catch { gate.MarkJournalUncertain(); throw; }
    }

    public async Task<ConsoleScreen> ReadScreenAsync(int processId, CancellationToken cancellationToken = default)
    {
        try { return await inner.ReadScreenAsync(processId, cancellationToken).ConfigureAwait(false); }
        catch { gate.MarkJournalUncertain(); throw; }
    }
}
