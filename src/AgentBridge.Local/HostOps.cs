using AgentBridge.Protocol;

namespace AgentBridge.Local;

// The host's management ops, served on the local pipe only (never through the relay): what a
// desktop app needs to run the host for the person at the machine. docs/protocol/local-pipe.md.
public static class HostOps
{
    // In hello's capabilities on the pipe, beside the sessions ones.
    public const string Capability = "host.manage";
    public const string AgentReadinessCapability = "host.agents";
    public const string AgentsRefresh = "host.agents.refresh";
    public const string AgentSetupCapability = "host.agents.setup";
    public const string AgentsSetup = "host.agents.setup";
    public const string AgentInstallCapability = "host.agents.install";
    public const string AgentsInstall = "host.agents.install";
    public const string AgentsInstallCancel = "host.agents.install.cancel";
    public const string AgentVerificationCapability = "host.agents.verify";
    public const string AgentsVerify = "host.agents.verify";
    public const string AgentsVerifyCancel = "host.agents.verify.cancel";
    public const string AgentSignInCapability = "host.agents.signIn";
    public const string AgentsSignIn = "host.agents.signIn";

    public const string Status = "host.status";
    // Answers with the status, then sends a host.status event whenever it changes.
    public const string Subscribe = "host.subscribe";
    public const string StatusEvent = "host.status";
    public const string SignIn = "host.signIn";
    public const string SignInCancel = "host.signIn.cancel";
    public const string Settings = "host.settings";
    public const string Restart = "host.restart";
    public const string Stop = "host.stop";
    // The account this host signed in to, token included, so the app can reach the account's
    // other machines through the relay as a device of its own without a second sign-in. For
    // the app alone, like every management op: never an agent session's to read.
    public const string RelayAccount = "host.relay.account";
    // The app tells its host what its updater is doing, for the host to serve to the account's
    // other devices (update.*, docs/protocol/v1-update.md), and which of the host's asks
    // (HostStatus.UpdateAsks) it has taken on. For the app alone, like every management op.
    public const string UpdateReport = "host.update.report";
    // The app is about to stop the host, or replace it with an update (#238): the host starts no
    // usage probe whose process would go with it for the next while, and says whether one is still
    // running or in its grace. The app waits until none is before it stops the host. For the app
    // alone, like every management op. An older host answers "unknown op": it runs no such probe.
    public const string ProbeHold = "host.probes.hold";
    // Update now's departure (#1159, docs/protocol/v1-update.md, Going, step 3): the app, its
    // download checked and its probe hold over, asks the host to leave for the press it carries.
    // The host takes #805's quiet lease as purpose Update, compares what is busy with what the
    // person confirmed, announces and commits (UpdateService.DepartAsync), and answers committed
    // (the app then stops it and installs), waiting (the download kept for the next quiet moment)
    // or refused. For the app alone, like every management op. This is the departure step #870's
    // lane owns: wired here, minimally, to the contract the design gives it. An older host answers
    // "unknown op", and the press ends refused.
    public const string UpdateDepart = "host.update.depart";
}

// host.update.depart's request: the press (an Update now's request) the app is carrying.
public sealed record UpdateDepartRequest(string Request);

// host.update.depart's answer: Committed (the host is fenced until it exits; Transition names the
// departure), Waiting (Code is the waiting code: turn-running, question-waiting or unknown) or
// Refused (Code says why, as UpdateCommandResult.Code). An unknown outcome reads as Refused.
[UnknownAs(Refused)]
public enum UpdateDepartOutcome { Committed, Waiting, Refused }

public sealed record UpdateDepartResult(UpdateDepartOutcome Outcome, string? Code = null, string? Transition = null);

// host.probes.hold's answer. Busy: a probe whose process goes with the host is running or in its
// grace; Why says which, for the app's log.
public sealed record ProbeHoldResult(bool Busy, string? Why = null);

// A device's press of Check or Install, waiting for this machine's app: carried in the host's
// status, which the app follows, until the app says it has taken it (UpdateReport.Taken). By:
// the device, as the host's log names it ("pwa 1a2b3c4d"). Now: an Update now (#1159), an Install
// that skips the app's wait for a quiet machine; it is handed only to an app that says it carries
// one (UpdateReport.CarriesUpdateNow), so an older app never reads it as a plain Install.
public sealed record UpdateAsk(string Request, UpdateAskKind Kind, string By, DateTimeOffset At, UpdateNowAsk? Now = null);

// An Update now's terms, as the person signed them: the release they were shown, what they
// confirmed it may go over (absent on a quiet machine), and the end of the press's life (15 minutes
// from its signature), after which the app drops it expired.
public sealed record UpdateNowAsk(string Release, UpdateBusy? Accept, DateTimeOffset Until);

// An unknown kind reads as Check, which changes nothing on the machine.
[UnknownAs(Check)]
public enum UpdateAskKind { Check, Install }

// host.update.report: the app's updater now, and the asks it has taken on. CarriesUpdateNow: the app
// can carry an Update now (#1159); an older one says nothing, and the devices are told old-app.
public sealed record UpdateReport(UpdateState State, IReadOnlyList<string>? Taken = null, bool CarriesUpdateNow = false);

// host.relay.account: the relay that issued the account, and the account. Token is a bearer
// credential for that relay, as private as the device key.
public sealed record RelayAccount(string Relay, string Token, string AccountId, string Login);

// host.relay.account's request: the app's own device id, when it says it (#801). The host then
// refuses manage.* through the relay from that key: the app manages its own machine from Settings,
// and an agent here could use its key as the same user. Absent from an older app.
public sealed record RelayAccountRequest(string? Device = null);

[UnknownAs(Off)]
public enum RelayState { Off, SignedOut, Connecting, Connected, Refused }

[UnknownAs(Idle)]
public enum SignInState { Idle, Pending, Failed }

public sealed record HostAccount(string Login, string AccountId);

// Off: this host never goes to the relay (--local-only). SignedOut: nobody has signed in on
// this profile. Refused: the relay turned the account down; sign in again.
public sealed record RelayStatus(RelayState State, string? Url = null, string? Detail = null, DateTimeOffset? Since = null);

// A device-flow sign-in the host is running: the code to enter, and where.
public sealed record SignInStatus(SignInState State, string? UserCode = null, string? VerificationUri = null, DateTimeOffset? ExpiresAt = null, string? Error = null);

// What the host's config.json says now. A change takes effect when the host restarts, which
// RestartRequired on the status says.
// RemoteFolderTrust: launch.remoteFolderTrust, folder-trust questions answered from a person's devices.
// RemoteFolderTrustDevices: launch.remoteFolderTrustDevices, the devices (by id) approved to answer
// them; null when none is.
// DevBoxKeepAwake: devBox.keepAwake, keeping this Dev Box from hibernating on disconnect.
// EscapedApproval: permissions.escapedApproval, confirm or machine (#803); null from a host before it.
// EscapedApprovalDevices: permissions.escapedApprovalDevices, the devices that may confirm one; null
// while absent, when the folder-trust devices may, and only while folder trust is on.
// PhoneSignIn: permissions.phoneSignIn, signing an agent in from a person's device (#972).
public sealed record HostSettingsView(bool RemoteApproval, string? Name = null, bool RemoteFolderTrust = false, IReadOnlyList<string>? RemoteFolderTrustDevices = null,
    bool DevBoxKeepAwake = false, string? EscapedApproval = null, IReadOnlyList<string>? EscapedApprovalDevices = null, bool PhoneSignIn = false)
{
    // By the devices in it, not the list object, so a restart is not asked for because two reads
    // of the same file made two lists.
    public bool Equals(HostSettingsView? other) =>
        other is not null && RemoteApproval == other.RemoteApproval && Name == other.Name && RemoteFolderTrust == other.RemoteFolderTrust
        && (RemoteFolderTrustDevices ?? []).SequenceEqual(other.RemoteFolderTrustDevices ?? [], StringComparer.Ordinal)
        && DevBoxKeepAwake == other.DevBoxKeepAwake && EscapedApproval == other.EscapedApproval
        && (EscapedApprovalDevices is null) == (other.EscapedApprovalDevices is null)
        && (EscapedApprovalDevices ?? []).SequenceEqual(other.EscapedApprovalDevices ?? [], StringComparer.Ordinal)
        && PhoneSignIn == other.PhoneSignIn;

    public override int GetHashCode() => HashCode.Combine(RemoteApproval, Name, RemoteFolderTrust, RemoteFolderTrustDevices?.Count ?? 0, DevBoxKeepAwake);
}

// The Dev Box keep-awake (devBox.*, docs/host-config.md), present when this machine is a Microsoft
// Dev Box: when the Dev Box agent is installed, whether or not the host can use its settings.
// KeepAwake: whether passes run now. Last: the last pass, its outcome in agent-ha-bridge's words
// (skipped, delayed, safe, no-action, blocked, failed) or the host's own (stood-back, busy,
// not-a-devbox), and what it did; Alarming when a person should look (failed, blocked, or a
// stand-back with no evidence agent-ha-bridge keeps it awake: the Dev Box may hibernate). NextAt:
// when the next pass is due, while on. Problem: why the agent's settings could not be used, when
// they could not, so the Dev Box cannot be kept awake.
public sealed record DevBoxStatus(bool KeepAwake, int IntervalMinutes, DevBoxPass? Last = null, DateTimeOffset? NextAt = null, string? Problem = null);

public sealed record DevBoxPass(DateTimeOffset At, string Outcome, string Detail, bool Alarming = false);

public sealed record HostStatus
{
    public required string Version { get; init; }
    public bool Dev { get; init; }
    public required string Profile { get; init; }
    public required string Folder { get; init; }
    public required int ProcessId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public bool Elevated { get; init; }
    public required MachineInfo Machine { get; init; }
    public HostAccount? Account { get; init; }
    public required RelayStatus Relay { get; init; }
    public required SignInStatus SignIn { get; init; }
    // Why delivery is off: agent-ha-bridge is running for this user (design G5). Null when it
    // is not, so answers, replies and stops are this host's to deliver.
    public string? AgentHaBridge { get; init; }
    // Why the Home Assistant connector is not reaching Home Assistant: it rejected the token, or
    // has banned this machine's address, with what to do. Null when nothing is refused.
    public string? HomeAssistant { get; init; }
    public required HostSettingsView Settings { get; init; }
    public bool RestartRequired { get; init; }
    // The host's log file, for an app to show the end of.
    public string? Log { get; init; }
    // Presses of Check and Install from the account's other devices, for the app to act on.
    public IReadOnlyList<UpdateAsk>? UpdateAsks { get; init; }
    // Why the host acts on nothing from other devices now (delivery.readOnly, or agent-ha-bridge
    // running), absent when it does: an install a device asked for before is not carried out.
    public string? ReadOnly { get; init; }
    // The Dev Box keep-awake, absent when this machine is not a Dev Box.
    public DevBoxStatus? DevBox { get; init; }
    // Read-only setup evidence; absent on older hosts and until the first scan completes.
    public AgentReadinessReport? Agents { get; init; }
    public AgentInstallProgress? AgentInstallation { get; init; }
    // Who can sign an agent in on this machine from their device (#972), read-only, from the
    // machine's person-device roster: none until #55's roster lands. Absent from an older host.
    public SignInDevices? SignInDevices { get; init; }
    // Current-host-run tests, latest per selected launcher; no persisted ready flags.
    public IReadOnlyList<AgentVerificationReport>? AgentVerifications { get; init; }
}

// host.signIn: start GitHub's device flow through this relay (or the one the profile used).
public sealed record SignInRequest(string? Relay = null);

// host.settings: any of these; absent ones are left as they are. Name "" clears it.
// RemoteFolderTrustDevices replaces the whole list ([] approves none). RemoteFolderTrustDevicesWas:
// the list that one was made from; when the host's differs (another app, or a person's terminal,
// changed it meanwhile), nothing is changed and the result says Conflict, so a device revoked
// elsewhere is never approved again by a list made before. DevBoxKeepAwake: devBox.keepAwake,
// applied at once. PhoneSignIn: permissions.phoneSignIn, at the next start.
public sealed record SettingsRequest(bool? RemoteApproval = null, string? Name = null, bool? RemoteFolderTrust = null, IReadOnlyList<string>? RemoteFolderTrustDevices = null,
    IReadOnlyList<string>? RemoteFolderTrustDevicesWas = null, bool? DevBoxKeepAwake = null, bool? PhoneSignIn = null);

public sealed record SettingsResult(HostSettingsView Settings, bool RestartRequired, bool Conflict = false);

public sealed record AgentSetupRequest(string Agent, string Action);

// A failure can leave owned, repairable partial work. Neither completion nor a restart is
// evidence of authentication, hook trust or a successful fresh session.
public sealed record AgentSetupResult(bool Completed, string Detail, bool RestartRequired);

public sealed record AgentInstallRequest(string Agent, bool AcceptAgreements = false);
public sealed record AgentInstallResult(bool Completed, string Detail, string? Executable = null);
public sealed record AgentInstallProgress(string Agent, bool InProgress, string Detail, bool Blocked = false);
public sealed record AgentSignInRequest(string Agent, bool Confirmed = false);
public sealed record AgentSignInResult(bool Opened, string Detail);
