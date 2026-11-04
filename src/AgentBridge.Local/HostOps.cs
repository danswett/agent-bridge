using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    public const string LaunchSettingsCapability = "host.launch.settings";

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
}

// A device's press of Check or Install, waiting for this machine's app: carried in the host's
// status, which the app follows, until the app says it has taken it (UpdateReport.Taken). By:
// the device, as the host's log names it ("pwa 1a2b3c4d").
public sealed record UpdateAsk(string Request, UpdateAskKind Kind, string By, DateTimeOffset At);

// An unknown kind reads as Check, which changes nothing on the machine.
[UnknownAs(Check)]
public enum UpdateAskKind { Check, Install }

// host.update.report: the app's updater now, and the asks it has taken on.
public sealed record UpdateReport(UpdateState State, IReadOnlyList<string>? Taken = null);

// host.relay.account: the relay that issued the account, and the account. Token is a bearer
// credential for that relay, as private as the device key.
public sealed record RelayAccount(string Relay, string Token, string AccountId, string Login);

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
public sealed record HostSettingsView(bool RemoteApproval, string? Name = null, bool RemoteFolderTrust = false, IReadOnlyList<string>? RemoteFolderTrustDevices = null,
    bool DevBoxKeepAwake = false, HostLaunchSettings? Launch = null, string? LaunchProblem = null)
{
    // By the devices in it, not the list object, so a restart is not asked for because two reads
    // of the same file made two lists.
    public bool Equals(HostSettingsView? other) =>
        other is not null && RemoteApproval == other.RemoteApproval && Name == other.Name && RemoteFolderTrust == other.RemoteFolderTrust
        && (RemoteFolderTrustDevices ?? []).SequenceEqual(other.RemoteFolderTrustDevices ?? [], StringComparer.Ordinal)
        && DevBoxKeepAwake == other.DevBoxKeepAwake && (Launch ?? HostLaunchSettings.Default) == (other.Launch ?? HostLaunchSettings.Default)
        && LaunchProblem == other.LaunchProblem;

    public override int GetHashCode() => HashCode.Combine(RemoteApproval, Name, RemoteFolderTrust, RemoteFolderTrustDevices?.Count ?? 0, DevBoxKeepAwake, Launch ?? HostLaunchSettings.Default, LaunchProblem);
}

public sealed record ApprovedWorkspace(string Label, string Path, bool Isolate = false);

public sealed record HostLaunchSettings(IReadOnlyList<ApprovedWorkspace> Workspaces, string DefaultAgent = "auto",
    string? DefaultWorkspace = null, string Permissions = LaunchProtocol.Ask, bool Enabled = true)
{
    public static readonly HostLaunchSettings Default = new([]);

    [JsonIgnore]
    public string Baseline => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this, ProtocolJson.Options)));

    [JsonIgnore]
    public bool FitsMessage => JsonSerializer.SerializeToUtf8Bytes(new { workspaces = Workspaces }, ProtocolJson.Options).Length <= LaunchProtocol.MaxOptionsBytes;

    public void CheckMessageBudget()
    {
        if (!FitsMessage)
        {
            throw new ArgumentException("The workspace list is too large for launch settings. Choose fewer folders or shorter labels and paths; nothing was saved.");
        }
    }

    public bool Equals(HostLaunchSettings? other) => other is not null && DefaultAgent == other.DefaultAgent
        && DefaultWorkspace == other.DefaultWorkspace && Permissions == other.Permissions && Enabled == other.Enabled
        && (Workspaces ?? []).SequenceEqual(other.Workspaces ?? []);

    public override int GetHashCode() => HashCode.Combine(DefaultAgent, DefaultWorkspace, Permissions, Enabled, Workspaces?.Count ?? 0);
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
}

// host.signIn: start GitHub's device flow through this relay (or the one the profile used).
public sealed record SignInRequest(string? Relay = null);

// host.settings: any of these; absent ones are left as they are. Name "" clears it.
// RemoteFolderTrustDevices replaces the whole list ([] approves none). RemoteFolderTrustDevicesWas:
// the list that one was made from; when the host's differs (another app, or a person's terminal,
// changed it meanwhile), nothing is changed and the result says Conflict, so a device revoked
// elsewhere is never approved again by a list made before. DevBoxKeepAwake: devBox.keepAwake,
// applied at once.
public sealed record SettingsRequest(bool? RemoteApproval = null, string? Name = null, bool? RemoteFolderTrust = null, IReadOnlyList<string>? RemoteFolderTrustDevices = null,
    IReadOnlyList<string>? RemoteFolderTrustDevicesWas = null, bool? DevBoxKeepAwake = null,
    HostLaunchSettings? Launch = null, string? LaunchBaseline = null, bool LaunchConfirmed = false);

public sealed record SettingsResult(HostSettingsView Settings, bool RestartRequired, bool Conflict = false);

public sealed record AgentSetupRequest(string Agent, string Action);

// A failure can leave owned, repairable partial work. Neither completion nor a restart is
// evidence of authentication, hook trust or a successful fresh session.
public sealed record AgentSetupResult(bool Completed, string Detail, bool RestartRequired);

public sealed record AgentInstallRequest(string Agent, bool AcceptAgreements = false);
public sealed record AgentInstallResult(bool Completed, string Detail, string? Executable = null);
public sealed record AgentInstallProgress(string Agent, bool InProgress, string Detail, bool Blocked = false);
