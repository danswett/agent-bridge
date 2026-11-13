using System.Text.Json;

namespace AgentBridge.Protocol;

// Validated native requests, not a wire DTO that can bypass Read.
public sealed class ApnsPushRequest
{
    public const string Capability = "push.apns";
    public const string Subscribe = "push.apns.subscribe";
    public const string Unsubscribe = "push.apns.unsubscribe";
    public const string List = "push.apns.list";
    public string Operation { get; } = "";
    public string? Target { get; }
    public string? Token { get; }
    public string? KeyId { get; }
    public PushKeys? Keys { get; }
    public string? Device { get; }

    ApnsPushRequest() { }

    public static ApnsPushRequest Read(string operation, JsonElement data) =>
        throw new NotImplementedException("native push contract parser is not implemented");
}
