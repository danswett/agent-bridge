using AgentBridge.Protocol;

namespace AgentBridge.Local;

[UnknownAs(Unknown)]
public enum ReadinessState { Unknown, Missing, NeedsAction, Working, Blocked, Failed }

public sealed record ReadinessCheck(ReadinessState State, string Detail);

// This profile's own hook setup record for one agent home. Completed means the last setup wrote
// every registration (and Codex's plugin) and intake; it is not evidence that hooks run in a new
// session. Incomplete is an owned record without that completion (an interrupted or failed setup,
// or one written before completion was recorded) and needs Repair. Unknown: not determined
// (older host, read-only host, unreadable intake or record, or another profile's record).
[UnknownAs(Unknown)]
public enum HookSetupState { Unknown, NotSetUp, Incomplete, Completed }

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
    DateTimeOffset? LastHookHandledAt = null,
    HookSetupState HookSetup = HookSetupState.Unknown);

public sealed record AgentReadinessReport(DateTimeOffset CheckedAt, IReadOnlyList<AgentReadiness> Agents, string? ReadOnlyReason = null);
