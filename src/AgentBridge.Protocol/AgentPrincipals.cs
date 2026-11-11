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

// The temporary AP5-lite send (#1351). The same op name is spoken twice: an agent asks its own host
// over the pipe (AgentLiteSendRequest), and the sending host asks the target host over a secure
// channel (AgentLiteDelivery). The agent's answer is AgentLiteSendResult.
public static class AgentLite
{
    public const string Send = "agent.lite.send";

    // What a send came to. Only Delivered means the target host accepted the message.
    public const string Delivered = "delivered";
    public const string Refused = "refused";
    public const string Undeliverable = "undeliverable";
    public const string Unconfirmed = "unconfirmed";
    public const string Off = "off";

    // The longest reply, in characters: the same limit as a reply sent through Home Assistant.
    public const int MaxTextChars = 255;

    // The bytes an envelope's dataSha256 binds: both hosts build them from the text alone.
    public static byte[] DataOf(string text) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { text }, ProtocolJson.Options);
}

public sealed record AgentLiteSendRequest
{
    public required string Machine { get; init; }
    public required string Session { get; init; }
    public required string Instance { get; init; }
    public required string Text { get; init; }
}

public sealed record AgentLiteDelivery
{
    public required string Envelope { get; init; }
    public required string Text { get; init; }
}

// The target host's acknowledgement: the request id it saw, its outcome (delivered or refused,
// with a code) and its kill epoch.
public sealed record AgentLiteAck(string Request, string Outcome, string? Code = null, long KillEpoch = 0);

public sealed record AgentLiteSendResult(string Outcome, string? Code = null, string? Detail = null);
