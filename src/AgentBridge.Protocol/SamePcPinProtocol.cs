namespace AgentBridge.Protocol;

// Same-PC trust (#3450 ruling item 3): the signed-in app hands the host on its own machine the
// roster root it has verified, over the user-only pipe. App callers only; never the relay.
public static class SamePcPinProtocol
{
    public const string Op = "host.roster.pin";
    public const string Pinned = "pinned";
}

// Hashes and heads are base64url of 32 bytes; Device is the app's own device id in that roster.
public sealed record SamePcPinRequest(string Account, string Generation, string Genesis, string[] Heads, string Device);

public sealed record SamePcPinResult(string State);
