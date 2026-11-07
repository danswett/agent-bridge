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

    // A process's console made at least `columns` wide (its screen buffer, the width an agent in
    // it draws to), typing nothing, and only while the id is still the process created at
    // `createdAt` (so never a later process given the same id); the width read back after, or
    // why not. NotYet: the console could not be reached, as before a process just started has
    // one, so it may be asked again.
    Task<ConsoleWidth> WidenAsync(int processId, DateTimeOffset createdAt, int columns, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ConsoleWidth(null, "this host cannot widen a session's console", false));
}

// The width a console was left at, or why it is not known.
public sealed record ConsoleWidth(int? Columns, string? Problem, bool NotYet);

// Rows, or why there are none.
public sealed record ConsoleScreen(IReadOnlyList<string>? Rows, string? Problem);

// Whether agent-ha-bridge's daemon is active for this user: the reason when it is, null
// when it is not. While it is, this host must not type into any session (design G5).
public interface IAgentHaBridgeProbe
{
    string? Active();

    // The same answer, perhaps a moment old (SharedAgentHaBridgeProbe), for what is only offered or
    // reported. Never for anything that acts: what types, presses, stops or launches asks Active.
    string? Recent() => Active();
}

// Why a process must not be typed into (it runs elevated, say), or null.
public interface ITargetGuard
{
    string? Refuse(int processId);
}
