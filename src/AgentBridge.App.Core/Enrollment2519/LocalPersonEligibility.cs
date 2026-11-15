using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// These enums model policy inputs only; EnrollmentCaller is not authenticated Host.CallerKind evidence.
internal enum EnrollmentOrigin
{
    Unknown = 0,
    Local,
    Remote,
}

internal enum EnrollmentCaller
{
    Unknown = 0,
    App,
    Agent,
    Other,
    HostCli,
}

internal static class LocalPersonEligibility
{
    internal static bool CanEnroll(EnrollmentOrigin origin, AgentKind sessionKind, EnrollmentCaller caller, bool readOnly)
        => origin == EnrollmentOrigin.Local
            && IsSupportedSessionKind(sessionKind)
            && caller == EnrollmentCaller.App
            && !readOnly;

    static bool IsSupportedSessionKind(AgentKind sessionKind) => sessionKind switch
    {
        AgentKind.Copilot => true,
        AgentKind.Claude => true,
        AgentKind.Codex => true,
        AgentKind.Unknown => false,
        _ => false,
    };
}
