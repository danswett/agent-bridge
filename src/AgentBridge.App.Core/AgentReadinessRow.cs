using AgentBridge.Local;

namespace AgentBridge.App.Core;

// One agent under Settings › Agents on this machine. Each row's buttons are named for its agent and
// have their own AutomationIds, and its setup's result sits on the row, so Narrator and UI tests can
// tell which agent a button or a result is about (#678; #670's design review).
public sealed class AgentReadinessRow(AgentReadiness agent, bool canManage = false, bool canInstall = false, AgentSetupNote? note = null)
{
    public string Id => agent.Id;
    public string Label => agent.Label;
    // Offered only while the CLI is missing: an installed agent has nothing to install, so no
    // button rather than a disabled one (ui.md §9.1 A7).
    public bool ShowInstall => agent.Id is "copilot" or "claude" or "codex" && agent.Installation.State == ReadinessState.Missing;
    public bool CanInstall => canInstall && ShowInstall && agent.Hooks.State != ReadinessState.Blocked;
    public string InstallName => $"Install CLI for {Label}";
    public string InstallId => $"InstallAgent_{Id}";
    // One verb for the state the hooks are in, never "Set up / Repair" (ui.md §9.1 A7): "Set up" while
    // they aren't set up (or this profile's intake is off), "Repair" only when they're broken, and none
    // when nothing needs doing: working, held by another profile or a read-only host, or set up and
    // not tested yet. An agent that isn't installed has nothing to set up.
    // A host can't yet say hooks work, so a healthy set-up agent reads Unknown, and an Unknown that
    // offered Repair put Repair on every healthy row (#705 review). Unknown is set up only when the
    // host found this profile's owned setup record, which its detail says (AgentReadinessService.Hooks;
    // AgentReadinessTests holds those words); without one, intake is on but this agent was never set up.
    public string? SetUpVerb => agent.Installation.State != ReadinessState.Working ? null : agent.Hooks.State switch
    {
        ReadinessState.Missing or ReadinessState.NeedsAction => "Set up",
        ReadinessState.Unknown when !OwnedRecord => "Set up",
        ReadinessState.Failed => "Repair",
        _ => null,
    };
    bool OwnedRecord => agent.Hooks.Detail.Contains("owned setup record", StringComparison.OrdinalIgnoreCase);
    // Set up, with nothing to do until a new session shows the hooks work.
    public bool NotTested => agent.Installation.State == ReadinessState.Working && agent.Hooks.State == ReadinessState.Unknown && OwnedRecord;
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
    public string Status => note?.Text ?? (NotTested ? "Not tested yet" : "");
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