using AgentBridge.Local;

namespace AgentBridge.App.Core;

// One agent under Settings › Agents on this machine. Each row's buttons are named for its agent and
// have their own AutomationIds, and its setup's result sits on the row, so Narrator and UI tests can
// tell which agent a button or a result is about (#678; #670's design review).
public sealed class AgentReadinessRow(AgentReadiness agent, bool canManage = false, bool canInstall = false, AgentSetupNote? note = null, HookSetup? setup = null, AgentSetupNote? shared = null)
{
    // Agency runs on Copilot CLI's hooks: the host sets them up and removes them as Copilot's
    // (AgentSetupService), so what a setup on either row left is evidence for both (#705 review).
    public static string HooksOf(string id) => id == "agency" ? "copilot" : id;
    // The last setup's result for this row's hooks, from whichever row ran it; the row's own note
    // says only what its own setup said.
    AgentSetupNote? Result => shared ?? note;
    public string Id => agent.Id;
    public string Label => agent.Label;
    // Whether this profile's setup finished, from the host's `hookSetup` (Unknown from an older host).
    public HookSetup Setup { get; } = setup ?? HookSetups.Of(agent);
    // Offered only while the CLI is missing: an installed agent has nothing to install, so no
    // button rather than a disabled one (ui.md §9.1 A7).
    public bool ShowInstall => agent.Id is "copilot" or "claude" or "codex" && agent.Installation.State == ReadinessState.Missing;
    public bool CanInstall => canInstall && ShowInstall && agent.Hooks.State != ReadinessState.Blocked;
    public string InstallName => $"Install CLI for {Label}";
    public string InstallId => $"InstallAgent_{Id}";
    // One verb for the state the hooks are in, never "Set up / Repair" (ui.md §9.1 A7): "Set up" while
    // they aren't set up (or this profile's intake is off), "Repair" only when they're broken or the
    // setup didn't finish, and none when nothing needs doing: working, held by another profile or a
    // read-only host, or set up and not tested yet. An agent that isn't installed has nothing to set up.
    // A host can't yet say hooks work, so a healthy set-up agent reads Unknown, and an Unknown that
    // offered Repair put Repair on every healthy row (#705 review). The host's `hookSetup` says which
    // Unknown it is: completed (set up, not tested yet) or not set up. Its record is saved before any
    // registration, so incomplete is a setup that failed part-way: Repair, whatever intake says.
    public string? SetUpVerb => agent.Installation.State != ReadinessState.Working ? null : (agent.Hooks.State, Setup) switch
    {
        // Another profile's or a read-only host's hooks are not this app's to change.
        (ReadinessState.Blocked, _) => null,
        // Removing Copilot CLI's connection can leave its owned record, when other entries share the
        // file (StandaloneHooks.RemoveAsync), which a host reads as set up, or as a setup that didn't
        // finish. A removal of this row's hooks, which a fresh check keeps, says it isn't (#705 review).
        (ReadinessState.Unknown, HookSetup.Unknown) or (_, HookSetup.Incomplete) when Result is { Removed: true } => "Set up",
        (_, HookSetup.Incomplete) or (ReadinessState.Failed, _) => "Repair",
        (ReadinessState.Missing or ReadinessState.NeedsAction, _) => "Set up",
        (ReadinessState.Unknown, HookSetup.Completed) => null,
        (ReadinessState.Unknown, HookSetup.NotSetUp) => "Set up",
        // An older host, without `hookSetup`: only its detail says whether it found this profile's
        // owned record (AgentReadinessService.Hooks; AgentReadinessTests holds those words), and a
        // setup that failed part-way reads the same as one that worked, so a failure on this row's hooks,
        // which a fresh check keeps, still offers Repair.
        (ReadinessState.Unknown, _) when !OwnedRecord => "Set up",
        (ReadinessState.Unknown, _) when Result is { Tone: Tone.Bad } => "Repair",
        _ => null,
    };
    bool OwnedRecord => agent.Hooks.Detail.Contains("owned setup record", StringComparison.OrdinalIgnoreCase);
    // Set up, with nothing to do until a new session shows the hooks work. Never "Ready": a finished
    // setup isn't evidence that hooks work (#783 D2).
    public bool NotTested => agent.Installation.State == ReadinessState.Working && agent.Hooks.State == ReadinessState.Unknown
        && Setup switch { HookSetup.Completed => true, HookSetup.Unknown => OwnedRecord, _ => false };
    // What of this row's last setup a fresh check keeps (SettingsViewModel.CheckAgentsAsync): a
    // removal the host can't tell from a setup, and on an older host a failed setup it reads as set up.
    internal bool Keeps(AgentSetupNote note) => note.Removed
        ? (Setup == HookSetup.Unknown && NotTested) || Setup == HookSetup.Incomplete
        : note.Tone == Tone.Bad && Setup == HookSetup.Unknown && NotTested;
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
    public string Status => note?.Text ?? (NotTested && SetUpVerb is null ? "Not tested yet" : "");
    // Its accessible name says whose it is: a live region is read out on its own, away from the
    // agent's heading (#705 review).
    public string StatusName => Status.Length == 0 ? "" : $"{Label}: {Status}";
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
public sealed record AgentSetupNote(string Text, Tone Tone, bool Removed = false);