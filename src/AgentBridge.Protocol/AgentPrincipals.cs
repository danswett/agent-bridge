namespace AgentBridge.Protocol;

// agent.hello (design/agent-principals.md §3.4): how an agent session on this machine opens its
// pipe connection as itself. Local pipe only: no relay channel serves it, and it never leaves the
// machine.
public static class AgentProtocol
{
    public const string Hello = "agent.hello";

    // Adopting a session someone started by hand (§3.5). Adopt and Revoke are for the person at
    // the machine, from the app or the host's own command line; Credential is the adopted session
    // asking for what it was adopted for. All pipe only.
    public const string Adopt = "agent.adopt";
    public const string Revoke = "agent.revoke";
    public const string Credential = "agent.credential";

    // In the app's hello when this host can adopt a session.
    public const string AdoptCapability = "agent.adopt";

    // How long an adoption waits for its session to ask, before it lapses unused.
    public const int AdoptionMinutes = 10;

    // Adopted at most up to read+reply: launching other sessions is granted by a launch (§3.3).
    public static readonly IReadOnlyList<string> AdoptableScopes = [ScopeRead, ScopeReadReply];

    // The credential's prefix, so secret scanners and log redaction can recognise it (§3.3).
    public const string TokenPrefix = "abat1_";

    // The prefix and 32 random bytes in unpadded base64url.
    public const int TokenLength = 6 + 43;

    // What a credential may do, narrowest first (§3.3). AP1's first slice only issues the
    // credential and proves who holds it; the ops each scope opens come with the next slices.
    public const string ScopeNone = "none";
    public const string ScopeRead = "read";
    public const string ScopeReadReply = "read+reply";
    public const string ScopeReadReplyLaunch = "read+reply+launch";

    public static readonly IReadOnlyList<string> Scopes = [ScopeNone, ScopeRead, ScopeReadReply, ScopeReadReplyLaunch];

    // `agent:<machine>/<session>`: an identifier for logs and audit. What a person reads is built
    // from verified fields, never from this string (§3.1).
    public static string Principal(string machine, string session) => $"agent:{machine}/{session}";
}

public sealed record AgentHelloRequest
{
    public required string Token { get; init; }
}

public sealed record AgentHelloResult(string Principal, string Scope);

// agent.adopt: the person's choice, for one session the host found. Scope defaults to read.
public sealed record AgentAdoptRequest
{
    public required string Session { get; init; }
    public string? Scope { get; init; }
}

public sealed record AgentAdoptResult(string Session, string Scope, DateTimeOffset ExpiresAt);

public sealed record AgentRevokeRequest
{
    public required string Session { get; init; }
}

public sealed record AgentRevokeResult(string Session, bool Revoked);

// agent.credential's answer: the token, shown once, to the caller alone.
public sealed record AgentCredentialResult(string Token, string Principal, string Scope, DateTimeOffset ExpiresAt);
