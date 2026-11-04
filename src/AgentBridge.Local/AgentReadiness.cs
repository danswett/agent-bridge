using AgentBridge.Protocol;

namespace AgentBridge.Local;

[UnknownAs(Unknown)]
public enum ReadinessState { Unknown, Missing, NeedsAction, Working, Blocked, Failed }

public sealed record ReadinessCheck(ReadinessState State, string Detail);

// Evidence for separate setup steps, not an overall "ready" flag. An executable, a registration
// or an event from an existing session cannot prove that a new session is ready end to end.
public sealed record AgentReadiness(
    string Id,
    string Label,
    string Home,
    string? Executable,
    string? Version,
    ReadinessCheck Installation,
    ReadinessCheck VersionCheck,
    ReadinessCheck Hooks,
    ReadinessCheck Authentication,
    ReadinessCheck Activation,
    DateTimeOffset? LastHookHandledAt = null);

public sealed record AgentReadinessReport(DateTimeOffset CheckedAt, IReadOnlyList<AgentReadiness> Agents, string? ReadOnlyReason = null);
