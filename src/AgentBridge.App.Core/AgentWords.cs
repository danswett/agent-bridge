using AgentBridge.Local;

namespace AgentBridge.App.Core;

// What a Settings agent row says of what its host reported, from a known set (Creative's rulings on
// #784 and #705: 6673094602, 6674345563). Never an exception's, a command's or an installer's own
// text: that named paths and read as a crash ("Hook ownership could not be checked: Access to the
// path '…' is denied."), so it goes only to the app's log (SettingsViewModel.Log, AgentReadinessRow.Raw).
public static class AgentWords
{
    public const string NotInstalled = "Not installed";
    public const string InstallHere = "Not installed · install it on this machine";
    public const string Installing = "Installing…";
    // `hookSetup` unknown with hooks a current host would have said it of: only a host from before
    // the field (#807) sends that.
    public const string OlderHost = "Can't check · this machine's bridge is older";
    public const string CantCheck = "Can't check";
    public const string TookTooLong = "It took too long, and some hooks may have changed. Check again before trying again.";

    const string OwnershipFailed = "Hook ownership could not be checked";
    const string DetectionFailed = "Detection failed:";
    const string VersionFailed = "The version command failed:";

    // Why a file couldn't be read, from an exception's message: one of three.
    public static string FileReason(string raw) =>
        Has(raw, "denied") ? "access denied"
        : Has(raw, "used by another process") ? "file in use"
        : "couldn't read";

    // The older bridge that runs a machine. A person never sees "host" (Creative, 6692247358, over
    // 6675472997).
    const string OlderBridge = "this machine's bridge is older";

    // Hooks the host holds back from this app: "Read-only · why".
    public static string ReadOnly(string detail) => "Read-only · " + (
        Has(detail, "agent-ha-bridge") ? OlderBridge
        : Has(detail, "Another bridge profile") ? "another bridge profile owns its hooks"
        // Whether set read-only or read-only for a problem in its config, it's the host's settings
        // (HostSettings.ReadOnlyReason, ProblemReason), and "Read-only · this host is read-only" said
        // it twice (Creative, 6675018272).
        : "set in this bridge's settings");

    // Hooks the host found broken in a way it can't repair: an ownership record it couldn't read or
    // doesn't recognise, or this profile's intake record it couldn't use (AgentReadinessService.Hooks).
    public static string HooksProblem(string detail) =>
        detail.StartsWith(OwnershipFailed, StringComparison.Ordinal) ? $"Hook ownership couldn't be checked · {FileReason(detail)}"
        : Has(detail, "hook ownership record is not recognized") ? "Hook ownership couldn't be checked · couldn't read"
        : Has(detail, "hook intake is off") ? $"Hook intake couldn't be checked · {FileReason(detail)}"
        : CantCheck;

    // A readiness detail as the row's checks list may show it: the host's own sentences as they are,
    // and one carrying an exception's text or a path by its reason.
    public static string Plain(string detail) =>
        detail.StartsWith(OwnershipFailed, StringComparison.Ordinal) || IntakeProblem(detail) ? HooksProblem(detail)
        : detail.StartsWith(DetectionFailed, StringComparison.Ordinal) ? $"Detection failed · {FileReason(detail)}"
        : detail.StartsWith(VersionFailed, StringComparison.Ordinal) ? $"The version command failed · {FileReason(detail)}"
        // A read-only host's reasons name what holds it in its own terms; the row says them as
        // Creative's Read-only words do, wherever they're said.
        : detail.StartsWith(HooksReadOnly, StringComparison.Ordinal) || Has(detail, "Another bridge profile") ? ReadOnly(detail)
        : detail.StartsWith(VersionReadOnly, StringComparison.Ordinal) ? $"Version not checked · {ReadOnly(detail)}"
        : detail;
    const string HooksReadOnly = "Hook setup is unavailable while this host is read-only";
    const string VersionReadOnly = "Version command not run while the host is read-only";

    // IntakeState.Load's problems start with the record's path.
    static bool IntakeProblem(string detail) => Has(detail, "hook intake is off") && Path.IsPathFullyQualified(detail.Split(' ')[0]);

    // Why a CLI installation failed, or null when what the host said is not a failure.
    public static string? InstallFailure(string detail) =>
        detail.StartsWith("An existing executable was found", StringComparison.Ordinal)
            || detail.StartsWith("The CLI executable is now available", StringComparison.Ordinal)
            || detail.StartsWith("Installing the CLI", StringComparison.Ordinal) ? null
        : Has(detail, "cancelled") ? "cancelled"
        : Has(detail, "timed out") ? "it took too long"
        : Has(detail, "Windows Package Manager is unavailable") ? "Windows Package Manager isn't available"
        : Has(detail, "Windows Package Manager could not start") ? "Windows Package Manager couldn't start"
        : Has(detail, "Windows Package Manager exited") ? "Windows Package Manager couldn't install it"
        : Has(detail, "launch executable was not found") ? "the CLI wasn't found afterwards"
        : Has(detail, "configured executable is missing") ? "the configured path is missing"
        : Has(detail, "cleanup could not be confirmed") ? "the installer's processes may still be running"
        : Has(detail, "launch settings") ? "this profile's launch settings have a problem"
        : Has(detail, "Another CLI installation") ? "another installation was running"
        : Has(detail, "read-only") || Has(detail, "agent-ha-bridge") ? "this bridge is read-only"
        : "the machine couldn't install it";

    // What a setup, repair or removal left, said on its row.
    public static string SetupResult(bool remove, string? verb, AgentSetupResult result) => result.Completed
        ? remove ? "Removed · sessions already running keep their hooks until they end"
            : $"{(verb == "Repair" ? "Repaired" : "Set up")} · {(result.RestartRequired ? "restart the bridge, then start a new session to check it" : "start a new session to check it")}"
        : SetupFailed(remove, verb, result.Detail);

    // A press the app refused because this bridge predates agent setup.
    public static string SetupUnsupported(bool remove, string? verb) =>
        $"{(remove ? "Removal" : verb == "Repair" ? "Repair" : "Setup")} failed · " + OlderBridge + "; update the app and restart the bridge";

    // A setup that finished but left the row offering the same verb: nothing the host registered
    // changed what it reports, so "Set up · start a new session" would be a result the row contradicts.
    public static string SetupChangedNothing(string? verb) =>
        $"Nothing changed · the bridge still reports this agent needs {(verb == "Repair" ? "repair" : "setup")}. Check again, then start a new session";

    // A setup the host refused or that failed part-way, by why (StandaloneHooks.Preflight's refusals).
    public static string SetupFailed(bool remove, string? verb, string raw) =>
        $"{(remove ? "Removal" : verb == "Repair" ? "Repair" : "Setup")} failed · " + (
            // A legacy file's path can itself name agent-ha-bridge, so what's wrong with it comes first.
            Has(raw, "legacy registration") ? "an older bridge's hooks are there; migrate them first"
            : Has(raw, "agent-ha-bridge") ? OlderBridge
            : Has(raw, "belongs to another installation") || Has(raw, "Another bridge profile") ? "another bridge profile owns its hooks"
            : Has(raw, "no hook registration owned by this profile") ? "this profile set up nothing to remove"
            : Has(raw, "migration") ? "this profile is being migrated"
            : Has(raw, "read-only") ? "this bridge is read-only"
            : Has(raw, "native hook is missing") ? "the app's hook is missing; reinstall the app"
            : Has(raw, "denied") ? "access denied"
            : Has(raw, "used by another process") ? "file in use"
            : "the machine couldn't finish it");

    static bool Has(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);
}
