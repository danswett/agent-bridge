namespace AgentBridge.Protocol;

// Disabled until an independently reviewed, private agent-owned auth entrypoint exists.
public static class AgentSignIn
{
    public const bool EnabledByDefault = false;
    public const string Capability = "sessions.signin";
    public const string Start = "signin.start";
    public const string Code = "signin.code";
    public const string Done = "signin.done";
    public const string Cancel = "signin.cancel";
}
