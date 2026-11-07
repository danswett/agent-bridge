namespace AgentBridge.Host.Delivery;

// Services turn injector exceptions into normal delivery results. Mark uncertainty before
// those catches run: disposing the worker handle does not prove a child stopped typing.
internal sealed class GateTrackedInjector(IConsoleInjector inner, DeliveryGate gate) : IConsoleInjector
{
    public async Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default)
    {
        try { return await inner.InjectAsync(processId, plan, cancellationToken).ConfigureAwait(false); }
        catch (Exception e)
        {
            // Only an injector that proves its worker's end lets a planned stop through later.
            gate.MarkWorkerUncertain(e is InjectorWorkerException failed ? failed.Exited : null);
            throw;
        }
    }

    // The --screen worker never sends keys, so a failed read proves nothing was typed. Marking
    // here made one transient launch follow-up read block quiet acquisition until restart.
    public Task<ConsoleScreen> ReadScreenAsync(int processId, CancellationToken cancellationToken = default) =>
        inner.ReadScreenAsync(processId, cancellationToken);
}
