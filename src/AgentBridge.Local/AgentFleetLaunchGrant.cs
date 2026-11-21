namespace AgentBridge.Local;

public sealed record AgentFleetLaunchGrant(string Id, string Principal, string Scope, string Role,
    DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt = null);

public sealed record AgentFleetLaunchGrantState(string State, AgentFleetLaunchGrant? Grant = null);
