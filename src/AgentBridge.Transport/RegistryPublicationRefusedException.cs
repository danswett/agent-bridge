namespace AgentBridge.Transport;

public sealed class RegistryPublicationRefusedException(string reason)
    : InvalidOperationException("registry publication refused: " + SafeCode(reason))
{
    public string ReasonCode { get; } = SafeCode(reason);

    static string SafeCode(string reason) =>
        reason is "bad-record" or "stale" or "too-large" or "not-allowed" ? reason : "unknown";
}
