using AgentBridge.Protocol;

namespace AgentBridge.Local;

public sealed record AgentVerificationRequest(string Request, string Agent, string Workspace, bool Confirmed = false, string? Profile = null);

[UnknownAs(Unknown)]
public enum VerificationState { Unknown, Running, Verified, NeedsAction, Blocked, Cancelled, Failed }

public sealed record AgentVerificationReport(
    string Request,
    string Agent,
    string Workspace,
    string? Profile,
    VerificationState State,
    string Detail,
    DateTimeOffset StartedAt,
    DateTimeOffset CheckedAt,
    ReadinessCheck Launch,
    ReadinessCheck Discovery,
    ReadinessCheck Provider,
    ReadinessCheck Hooks,
    string? Session = null,
    DateTimeOffset? DiscoveredAt = null,
    DateTimeOffset? ProviderObservedAt = null,
    DateTimeOffset? HookReceivedAt = null);

public sealed record AgentVerificationCancel(string Request);
