using AgentBridge.Local;

namespace AgentBridge.App.Core;

// One agent under Settings › Agents on this machine. Each row's buttons are named for its agent and
// have their own AutomationIds, and its setup's result sits on the row, so Narrator and UI tests can
// tell which agent a button or a result is about (#678; #670's design review).
public sealed class AgentReadinessRow(AgentReadiness agent, bool canManage = false, bool canInstall = false, AgentSetupNote? note = null, HookSetup? setup = null, AgentSetupNote? shared = null,
    AgentInstallProgress? install = null, bool installOffered = true)
{
    // Agency runs on Copilot CLI's hooks: the host sets them up and removes them as Copilot's
    // (AgentSetupService), so what a setup on either row left is evidence for both (#705 review).
    public static string HooksOf(string id) => id == "agency" ? "copilot" : id;
    // The last setup's result for this row's hooks, from whichever row ran it; the row's own note
    // says only what its own setup said.
    AgentSetupNote? Result => shared ?? note;
    public string Id => agent.Id;
    // Settings rows name the product being installed and set up ("Claude Code", "Copilot CLI");
    // sessions, pushes and banners name the agent ("Claude"), as the host's label does (Creative,
    // 6675018272).
    public string Label => agent.Id switch
    {
        "copilot" => "Copilot CLI",
        "claude" => "Claude Code",
        _ => agent.Label,
    };
    // Whether this profile's setup finished, from the host's `hookSetup` (Unknown from an older host).
    public HookSetup Setup { get; } = setup ?? HookSetups.Of(agent);
    bool Installed => agent.Installation.State == ReadinessState.Working;
    public bool IsInstalled => Installed;
    // A hook from a session started since the bridge began, which is the host's only evidence that
    // the agent's hooks work (not a finished setup).
    public bool HooksWorking => agent.Hooks.State == ReadinessState.Working || agent.LastHookHandledAt is not null;
    bool Blocked => agent.Hooks.State == ReadinessState.Blocked;

    // A row the app can't act on has its own words and no verb, in the neutral tone, never Ready,
    // Not set up or Needs repair, and nothing on it is a button that can't work (Creative's rulings
    // on #784 and #705): hooks held back from this app (Read-only · why), hooks a current host found
    // broken in a way it can't repair (Hook ownership couldn't be checked · why), and hooks an older
    // host can't say the setup of (Can't check · …). A current host sends `hookSetup` unknown only
    // with blocked or failed hooks (AgentReadinessService.Hooks), so with any other hooks it is a
    // host from before the field. Its fallbacks on the detail's words offered Set up or Repair the
    // host might refuse (#705 Engineering review), and they're gone.
    bool CannotRepair => agent.Hooks.State == ReadinessState.Failed && Setup == HookSetup.Unknown;
    bool OlderHost => Setup == HookSetup.Unknown && !Blocked && agent.Hooks.State != ReadinessState.Failed;

    // Offered only while the CLI is missing, and only where it can work: an installed agent has
    // nothing to install, and on hooks held back from this app, a host that doesn't install, or
    // Agency (installed through the person's organization), there's no button rather than a
    // disabled one (ui.md §9.1 A7; Creative's ruling on #784).
    public bool InstallSupported => agent.Id is "copilot" or "claude" or "codex";
    public bool ShowInstall => InstallSupported && installOffered && agent.Installation.State == ReadinessState.Missing && !Blocked;
    // This row's CLI installation, from the host's progress (or the app's request before it says).
    public bool Installing => install is { InProgress: true } i && i.Agent == Id;
    public bool CanInstall => canInstall && ShowInstall && !Installing;
    // While it runs, Install CLI shows its ring and keeps focus (ui.md §9.1 A4): it stays enabled,
    // and a press does nothing until the outcome.
    public bool InstallEnabled => CanInstall || Installing;
    public string InstallName => $"Install CLI for {Label}";
    public string InstallId => $"InstallAgent_{Id}";
    public string CancelInstallName => "Cancel CLI installation";
    public string CancelInstallId => $"CancelAgentInstall_{Id}";
    // Said of Install CLI while its installation runs, so a screen reader hears it's busy (ui.md §9.1 A4).
    public string InstallItemStatus => Installing ? AgentWords.Installing : "";
    // Why this row's last installation failed, from a known set, while the CLI is still missing.
    public string? InstallFailure => !Installing && agent.Installation.State == ReadinessState.Missing
        && install is { InProgress: false } i && i.Agent == Id ? AgentWords.InstallFailure(i.Detail) : null;

    // One verb for the state the hooks are in, never "Set up / Repair" (ui.md §9.1 A7): "Set up" while
    // they aren't set up (or this profile's intake is off), "Repair" only when the setup didn't finish,
    // and none when nothing needs doing or the app can't act: working, set up and not tested yet, or
    // any row above with words of its own. An agent that isn't installed has nothing to set up. A host
    // can't yet say hooks work, so a healthy set-up agent reads Unknown, and an Unknown that offered
    // Repair put Repair on every healthy row (#705 review). The host's record is saved before any
    // registration, so incomplete is a setup that failed part-way: Repair, whatever intake says.
    public string? SetUpVerb => !Installed ? null : (agent.Hooks.State, Setup) switch
    {
        (ReadinessState.Blocked, _) or (_, HookSetup.Unknown) => null,
        // Removing Copilot CLI's connection can leave its owned record, when other entries share the
        // file (StandaloneHooks.RemoveAsync), which a host reads as a setup that didn't finish. A
        // removal of this row's hooks, which a fresh check keeps, says it isn't (#705 review).
        (_, HookSetup.Incomplete) when Result is { Removed: true } => "Set up",
        (_, HookSetup.Incomplete) or (ReadinessState.Failed, _) => "Repair",
        (ReadinessState.Missing or ReadinessState.NeedsAction, _) => "Set up",
        (ReadinessState.Unknown, HookSetup.NotSetUp) => "Set up",
        _ => null,
    };
    // Set up, with nothing to do until a new session shows the hooks work. Never "Ready": a finished
    // setup isn't evidence that hooks work (#783 D2).
    public bool NotTested => Installed && agent.Hooks.State == ReadinessState.Unknown && Setup == HookSetup.Completed;

    // An agent installed after the bridge (#231), from the host's typed evidence alone (never its
    // Detail text): installed, and its profile's hook setup says it was never set up. A removal
    // that left a record is Incomplete, and one that left none is the person's own doing, which
    // SettingsViewModel records so it is never offered back. The card says the version, so an agent
    // whose version could not be read is not offered one (the row keeps its Set up). Agency shares
    // Copilot CLI's hooks and is not one of the three the card announces.
    public string? Version => string.IsNullOrWhiteSpace(agent.Version) ? null : agent.Version;
    public bool CanBeOffered => Id is "copilot" or "claude" or "codex" && Installed && !Blocked && Setup == HookSetup.NotSetUp && Version is not null;
    // While the card for this agent shows it holds the agent's only Set up, so the row draws none
    // (ui.md §6.9: nothing is drawn twice); once Not now hides the card, the row's Set up returns.
    public bool Offered { get; internal set; }
    // What of this row's last setup a fresh check keeps (SettingsViewModel.CheckAgentsAsync): a
    // removal the host reads as a setup that didn't finish.
    internal bool Keeps(AgentSetupNote note) => note.Removed && Setup == HookSetup.Incomplete;
    public bool ShowSetUp => SetUpVerb is not null && !Offered;
    public bool CanSetUp => canManage && ShowSetUp;
    public string SetUpName => $"{SetUpVerb ?? "Set up"} {Label}";
    public string SetUpId => $"SetUpAgent_{Id}";
    // Remove undoes this profile's own setup, so it needs the owned record the host's removal starts
    // from: without one StandaloneHooks.Preflight refuses, and the refusal still asked for a restart
    // (#705 Engineering review). Not shown at all where the host would refuse it, or where it can't
    // say whether there's one: no button that can't work.
    public bool ShowRemove => !Blocked && Setup is HookSetup.Completed or HookSetup.Incomplete;
    public bool CanRemove => canManage && ShowRemove;
    public string RemoveName => $"Remove {Label}'s connection";
    public string RemoveId => $"RemoveAgent_{Id}";
    // The confirmation before either: named for the agent, and for what Remove leaves alone.
    public string SetUpQuestion => $"{SetUpVerb ?? "Set up"} {Label}?";
    public string RemoveQuestion => $"Remove {Label}'s connection?";
    // The last setup's progress or result on this row, or what the row can't act on and why: a
    // polite live region in the view.
    public string Status => note?.Text
        ?? (NotTested && SetUpVerb is null ? "Not tested yet"
            : Blocked ? AgentWords.ReadOnly(agent.Hooks.Detail)
            : CannotRepair ? AgentWords.HooksProblem(agent.Hooks.Detail)
            : OlderHost ? AgentWords.OlderHost
            : "");
    // Its accessible name says whose it is: a live region is read out on its own, away from the
    // agent's heading (#705 review).
    public string StatusName => Status.Length == 0 ? "" : $"{Label}: {Status}";
    public Tone StatusTone => note?.Tone ?? Tone.None;
    public string StatusId => $"AgentStatus_{Id}";
    public string Installation => Installed
        ? agent.Version is { } version ? $"Installed, version {version}" : "Installed, version not verified"
        : Installing ? AgentWords.Installing
        : InstallFailure is { } why ? $"Install failed · {why}"
        : agent.Installation.State == ReadinessState.Missing ? ShowInstall ? AgentWords.NotInstalled : AgentWords.InstallHere
        : AgentWords.Plain(agent.Installation.Detail);
    public string? Executable => agent.Executable;
    public string Home => $"Agent home: {agent.Home}";
    public string Checks => $"Version: {Fact(AgentWords.Plain(agent.VersionCheck.Detail))}\nHooks: {Fact(AgentWords.Plain(agent.Hooks.Detail))}\nSign-in: {agent.Authentication.Detail}\nTrust and launch: {agent.Activation.Detail}";
    // A check the row's status line already gives the reason for says only the fact: the reason
    // once, on the status line (Creative, 6675472997).
    string Fact(string words) => Status.Length > 0 && words.EndsWith(Status, StringComparison.Ordinal) ? "Not checked" : words;
    public string HookActivity => agent.LastHookHandledAt is { } at
        ? $"Last hook handled at {at.ToLocalTime():g}{(agent.Id == "agency" ? " (shared Copilot CLI hooks)" : "")}. This is not a new-session readiness test."
        : "No hook seen since the bridge started.";

    // What the host said that the row says in other words, for the app's log: the raw text stays
    // there, never on the row.
    public IEnumerable<string> Raw()
    {
        foreach (var detail in new[] { agent.Installation.Detail, agent.VersionCheck.Detail, agent.Hooks.Detail })
        {
            if (AgentWords.Plain(detail) != detail || (detail == agent.Hooks.Detail && (CannotRepair || Blocked))) { yield return detail; }
        }
        if (InstallFailure is not null && install is { } i) { yield return i.Detail; }
    }
}
// What a setup on one row last said: "Setting up…" while it runs, then its outcome in a known set's
// words (AgentWords), in the Danger role when it failed.
public sealed record AgentSetupNote(string Text, Tone Tone, bool Removed = false);