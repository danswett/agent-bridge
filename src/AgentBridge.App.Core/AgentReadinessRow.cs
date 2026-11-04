using AgentBridge.Local;

namespace AgentBridge.App.Core;

public sealed class AgentReadinessRow(AgentReadiness agent, bool canManage = false)
{
    public string Id => agent.Id;
    public string Label => agent.Label;
    public bool CanSetUp => canManage && agent.Installation.State == ReadinessState.Working
        && agent.Hooks.State != ReadinessState.Blocked;
    public bool CanRemove => canManage && agent.Hooks.State != ReadinessState.Blocked;
    public string Installation => agent.Installation.State == ReadinessState.Working
        ? agent.Version is { } version ? $"Installed, version {version}" : "Installed, version not verified"
        : agent.Installation.Detail;
    public string? Executable => agent.Executable;
    public string Home => $"Agent home: {agent.Home}";
    public string Checks => $"Version: {agent.VersionCheck.Detail}\nHooks: {agent.Hooks.Detail}\nSign-in: {agent.Authentication.Detail}\nTrust and launch: {agent.Activation.Detail}";
    public string HookActivity => agent.LastHookHandledAt is { } at
        ? $"Last hook handled at {at.ToLocalTime():g}{(agent.Id == "agency" ? " (shared Copilot hooks)" : "")}. This is not a new-session readiness test."
        : "No hook observed by this detector during this host run.";
}
