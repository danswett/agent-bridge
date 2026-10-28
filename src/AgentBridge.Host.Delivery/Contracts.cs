namespace AgentBridge.Host.Delivery;

public enum InjectionStatus
{
    // Every key was written.
    Typed,
    // Nothing reached the console: the worker could not start, attach or open its input.
    NothingTyped,
    // Some keys may have reached the console, and nobody can say how many: the worker
    // died, hung or failed part way. Never retried.
    Interrupted,
}

public sealed record InjectionResult(InjectionStatus Status, string Detail);

// Types a plan into the console of a process. The real one is the injector worker
// (WorkerInjector); tests drive the whole answer flow with a fake.
public interface IConsoleInjector
{
    Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default);

    // The text of the visible window of a process's console, a row a line, read without
    // typing anything. An injector that cannot read screens says so, and whatever needed
    // the screen is not typed.
    Task<ConsoleScreen> ReadScreenAsync(int processId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ConsoleScreen(null, "this host cannot read the session's screen; answer at the machine"));
}

// Rows, or why there are none.
public sealed record ConsoleScreen(IReadOnlyList<string>? Rows, string? Problem);

// Whether agent-ha-bridge's daemon is active for this user: the reason when it is, null
// when it is not. While it is, this host must not type into any session (design G5).
public interface IAgentHaBridgeProbe
{
    string? Active();
}

// Why a process must not be typed into (it runs elevated, say), or null.
public interface ITargetGuard
{
    string? Refuse(int processId);
}
