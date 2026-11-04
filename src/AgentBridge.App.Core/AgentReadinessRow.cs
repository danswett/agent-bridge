using AgentBridge.Local;

namespace AgentBridge.App.Core;

// One agent under Settings › Agents on this machine. Each row's buttons are named for its agent and
// have their own AutomationIds, and its setup's result sits on the row, so Narrator and UI tests can
// tell which agent a button or a result is about (#678; #670's design review).
public sealed class AgentReadinessRow(AgentReadiness agent, bool canManage = false, bool canInstall = false, AgentSetupNote? note = null)
{
    public string Id => agent.Id;
    public string Label => agent.Label;
    public bool CanInstall => canInstall && agent.Id is "copilot" or "claude" or "codex"
        && agent.Installation.State == ReadinessState.Missing && agent.Hooks.State != ReadinessState.Blocked;
    public string InstallName => $"Install CLI for {Label}";
    public string InstallId => $"InstallAgent_{Id}";
    // One verb for the state the hooks are in, never "Set up / Repair": "Set up" while they aren't
    // installed (or this profile's intake is off), "Repair" when they're broken or the host can't
    // verify them, and none once they're working, or while another profile or a read-only host holds
    // them. An agent that isn't installed has nothing to set up.
    public string? SetUpVerb => agent.Installation.State != ReadinessState.Working ? null : agent.Hooks.State switch
    {
        ReadinessState.Missing or ReadinessState.NeedsAction => "Set up",
        ReadinessState.Failed or ReadinessState.Unknown => "Repair",
        _ => null,
    };
    public bool ShowSetUp => SetUpVerb is not null;
    public bool CanSetUp => canManage && ShowSetUp;
    public string SetUpName => $"{SetUpVerb ?? "Set up"} {Label}";
    public string SetUpId => $"SetUpAgent_{Id}";
    public bool CanRemove => canManage && agent.Hooks.State != ReadinessState.Blocked;
    public string RemoveName => $"Remove {Label}'s connection";
    public string RemoveId => $"RemoveAgent_{Id}";
    // The confirmation before either: named for the agent, and for what Remove leaves alone.
    public string SetUpQuestion => $"{SetUpVerb ?? "Set up"} {Label}?";
    public string RemoveQuestion => $"Remove {Label}'s connection?";
    // The last setup's progress or result on this row: a polite live region in the view.
    public string Status => note?.Text ?? "";
    public Tone StatusTone => note?.Tone ?? Tone.None;
    public string StatusId => $"AgentStatus_{Id}";
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

// What a setup on one row last said: "Setting up…" while it runs, then the host's words, in the
// Danger role when it failed.
public sealed record AgentSetupNote(string Text, Tone Tone);