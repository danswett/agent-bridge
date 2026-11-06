using System.Text.Json;

namespace AgentBridge.Protocol;

// Managing a machine from a person's other device (docs/protocol/v1-sessions.md, "Managing a machine", #801): restart the
// host and repair its hooks, from a phone, browser or Windows app, through the relay only. Stop,
// allowlisted config and the Home Assistant token have their shapes here too, but every host
// refuses them until the signed roster (#55) is enforced: there is no switch that turns them on.
public static class ManageProtocol
{
    public const string Capability = "manage";
    public const string HooksCapability = "manage.hooks";
    // Held until #55: never offered by a host before it.
    public const string StopCapability = "manage.stop";
    public const string ConfigCapability = "manage.config";
    public const string HaTokenCapability = "manage.haToken";

    public const string Get = "manage.get";
    // The state now, then a manage.state event whenever it changes.
    public const string Subscribe = "manage.subscribe";
    public const string Event = "manage.state";
    public const string Restart = "manage.restart";
    public const string HooksRepair = "manage.hooks.repair";
    public const string Stop = "manage.stop";
    public const string ConfigGet = "manage.config.get";
    public const string ConfigSet = "manage.config.set";
    public const string HaTokenSet = "manage.haToken.set";

    // Every manage op: the local pipe refuses each of them, whoever asks (#67 stays as it is).
    public static readonly IReadOnlySet<string> Ops = new HashSet<string>(StringComparer.Ordinal)
        { Get, Subscribe, Restart, HooksRepair, Stop, ConfigGet, ConfigSet, HaTokenSet };

    // The ops held until #55. They are refused before their data is read at all, so a token sent
    // to manage.haToken.set anyway is never parsed into anything.
    public static readonly IReadOnlySet<string> HeldUntilRoster = new HashSet<string>(StringComparer.Ordinal)
        { Stop, ConfigGet, ConfigSet, HaTokenSet };

    public static bool IsOp(string op) => op.StartsWith("manage.", StringComparison.Ordinal);

    // A management command lives at most this long from signing (transfers allow ten minutes):
    // a press delayed by a relay is not carried out long after the person made it.
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromSeconds(120);

    public const int Detail = 512;
    public const int Name = 64;

    // The agents a repair may name; agency shares Copilot's hooks.
    public static readonly IReadOnlyList<string> Agents = ["copilot", "claude", "codex", "agency"];
}

// The refusal and failure codes a ManageResult carries. A client reads an unknown one as a refusal
// and shows its Detail.
public static class ManageReason
{
    public const string NotPerson = "not-person";
    public const string NotPeer = "not-peer";
    public const string NotConnected = "not-connected";
    public const string LocalDevice = "local-device";
    public const string ReadOnly = "read-only";
    public const string Disabled = "disabled";
    public const string NeedsRoster = "needs-roster";
    public const string BadSignature = "bad-signature";
    public const string Expired = "expired";
    public const string Future = "future";
    public const string RateLimited = "rate-limited";
    public const string Busy = "busy";
    public const string NotQuiet = "not-quiet";
    public const string Changed = "changed";
    public const string Cancelled = "cancelled";
    public const string AnnounceFailed = "announce-failed";
    public const string Unavailable = "unavailable";
    public const string InvalidValue = "invalid-value";
    // The signed body carries arguments its action does not define (Connection, Q-C4 on #886): a
    // restart takes none, a repair only agent, and neither a contentSha256.
    public const string BadArgs = "bad-args";
}

// Every acting op's data: the command a person's device signed for this machine (design §5.5,
// SignedCommands), with its op's arguments inside the signed body (CommandBody.Args).
public sealed record ManageCommand
{
    [Strict(Strictness.Security)]
    public required SignedCommand Command { get; init; }
}

// manage.hooks.repair's arguments.
public sealed record ManageRepairArgs(string Agent);

// manage.config.set's arguments (held until #55).
public sealed record ManageConfigArgs(string Key, JsonElement Value, JsonElement Was, string Revision);

// manage.haToken.set's data (held until #55): the token travels beside the command, bound to it by
// the body's contentSha256. A host before #55 never reads it.
public sealed record ManageHaTokenRequest
{
    [Strict(Strictness.Security)]
    public required SignedCommand Command { get; init; }
    [Strict(Strictness.Security)]
    public required string Secret { get; init; }
}

// accepted: the host committed its departure (a restart) and is leaving; the outcome follows from
// the new host. done: finished here (a repair). unchanged: nothing to do. refused: not attempted.
// failed: attempted and did not work. An unknown outcome reads as failed, never done.
[UnknownAs(Failed)]
public enum ManageOutcome { Accepted, Done, Unchanged, Refused, Failed }

// An unknown phase reads as idle: nothing a client would wait on.
[UnknownAs(Idle)]
public enum ManagePhase { Idle, Restarting, Stopping, Repairing }

public sealed record ManageResult
{
    public string? Request { get; init; }
    public required ManageOutcome Outcome { get; init; }
    public string? Reason { get; init; }
    // Words for a person; never a secret or a value of one.
    public string? Detail { get; init; }
    public bool? RestartRequired { get; init; }
    // A restart's #805 planned-state transition, for the pressing device to match the offline reason.
    public string? Transition { get; init; }
    public ManageState? State { get; init; }
}

// The lifecycle press under way. By: the device's kind and short id ("pwa 1a2b3c4d").
public sealed record ManagePending(string Request, string Kind, string By, DateTimeOffset At, string? Transition = null);

// How many remote restarts the cap still allows now, and when the next one frees up.
public sealed record ManageRestarts(int Left, DateTimeOffset? NextAt = null);

// How the last press ended, across a restart.
public sealed record ManageLast(string Request, string Kind, ManageOutcome Outcome, string By, DateTimeOffset At, string? Reason = null, int? TookSeconds = null);

public sealed record ManageState
{
    public ManagePhase Phase { get; init; }
    public ManagePending? Pending { get; init; }
    public required ManageRestarts Restarts { get; init; }
    public ManageLast? Last { get; init; }
}
