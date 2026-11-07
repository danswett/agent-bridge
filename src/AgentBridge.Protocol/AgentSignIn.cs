namespace AgentBridge.Protocol;

// Off unless the machine's own permissions.phoneSignIn turns it on (#972), and then only for an
// agent with a registered private adapter: there is no build-time default that enables it.
public static class AgentSignIn
{
    public const string Capability = "sessions.signin";
    public const string Start = "signin.start";
    public const string Code = "signin.code";
    public const string Done = "signin.done";
    public const string Cancel = "signin.cancel";
}
