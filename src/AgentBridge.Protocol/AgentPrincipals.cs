namespace AgentBridge.Protocol;

// agent.hello (design/agent-principals.md §3.4): how an agent session on this machine opens its
// pipe connection as itself. Local pipe only: no relay channel serves it, and it never leaves the
// machine.
public static class AgentProtocol
{
    public const string Hello = "agent.hello";

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
