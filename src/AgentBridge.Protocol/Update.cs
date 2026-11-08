using System.Text;

namespace AgentBridge.Protocol;

// A machine's updates, seen and started from another of the person's devices
// (docs/protocol/v1-update.md, #51). The Windows app on that machine does the work, as it does
// for its own automatic updates (AutoUpdater): it checks, downloads, waits for the host to be
// idle, stops the host and restarts both into the new release. The host only carries what the
// app reports and what a device asks.
public static class UpdateProtocol
{
    public const string Capability = "update";
    public const string Get = "update.get";
    // The state now, then an update.state event whenever it changes.
    public const string Subscribe = "update.subscribe";
    public const string Event = "update.state";
    public const string Check = "update.check";
    public const string Install = "update.install";

    // Field limits, in UTF-8 bytes of the text.
    public const int Version = 64;
    public const int Reason = 512;
    public const int Notes = 4096;
    public const int RequestId = 64;
    // A press the machine's app has not taken this long after it was made lapses: the host lets
    // go of it, and an app that comes to it later (from a status it was slow to read) refuses it.
    public static readonly TimeSpan Lapses = TimeSpan.FromMinutes(10);

    // Update now (#1159): update.install carrying { now: SignedCommand } and nothing else. The
    // signed body's action is NowAction, its args NowArgs; it lives ManageProtocol.MaxLifetime
    // from issuedAt, and the press it starts NowLife from that same issuedAt.
    public const string NowAction = "update.install.now";
    public static readonly TimeSpan NowLife = TimeSpan.FromMinutes(15);
    // The busy sessions UpdateBusy lists, approvals first; past this, two counts say what it left out.
    public const int BusyListed = 32;
    // The most any of an accept's counts may say (working, waiting, moreApprovals, moreOther): far
    // past the sessions any machine runs, and small enough that no sum of them overflows.
    public const int BusyMost = 1_000_000;
    // A code (UpdateCommandResult.Code, UpdateResult.Code, waitingCode, updateNowWhy) is a short
    // lowercase word or words joined by '-'; anything else is dropped as the app reports it.
    public const int Code = 32;

    // A request id is the device's UUID for one press, as session.answer's.
    public static bool IsRequest(string? request) => request is { Length: > 0 and <= RequestId } && Guid.TryParse(request, out _);

    public static bool IsCode(string? code) => code is { Length: > 0 and <= Code } && code.All(c => c is >= 'a' and <= 'z' or '-');

    // What the app reports, within the limits. canUpdateNow, updateNowWhy, updateNowAt and busy
    // are the host's alone, from its own view, so whatever the app put there is dropped.
    public static UpdateState Fit(UpdateState s) => s with
    {
        Current = Limits.KeepStart(s.Current, Version),
        Available = Limits.KeepStart(s.Available, Version),
        Notes = Limits.KeepStart(s.Notes, Notes),
        Waiting = Limits.KeepStart(s.Waiting, Reason),
        Unavailable = Limits.KeepStart(s.Unavailable, Reason),
        Request = IsRequest(s.Request) ? s.Request : null,
        Progress = s.Progress is { } p ? Math.Clamp(p, 0, 100) : null,
        WaitingCode = IsCode(s.WaitingCode) ? s.WaitingCode : null,
        CanUpdateNow = false,
        UpdateNowWhy = null,
        UpdateNowAt = null,
        Busy = null,
        Last = s.Last is { } l ? l with
        {
            From = Limits.KeepStart(l.From, Version),
            To = Limits.KeepStart(l.To, Version),
            Reason = Limits.KeepStart(l.Reason, Reason),
            Request = IsRequest(l.Request) ? l.Request : null,
            Code = IsCode(l.Code) ? l.Code : null,
        } : null,
    };
}

// Why Update now can't be pressed on a machine with an update ready (UpdateState.UpdateNowWhy).
// A client words each itself (docs/protocol/v1-update.md, "What the person sees").
public static class UpdateNowWhy
{
    public const string Disabled = "disabled";
    public const string ReadOnly = "read-only";
    public const string OldApp = "old-app";
    public const string Unavailable = "unavailable";
    public const string SignIn = "sign-in";
    public const string Busy = "busy";
    public const string RestartCap = "restart-cap";
}

// Update now's own codes for a press (UpdateCommandResult.Code) and how it ended (UpdateResult.Code),
// beside manage.*'s (ManageReason), which it shares for everything the signed-command gate refuses.
public static class UpdateCode
{
    public const string ReleaseChanged = "release-changed";
    public const string OtherProfile = "other-profile";
    public const string OldApp = "old-app";
    public const string RestartCap = "restart-cap";
    public const string SignIn = "sign-in";
    public const string UsageCheck = "usage-check";
}

// Why an Update now waits (UpdateState.WaitingCode, with phase waiting).
public static class UpdateWaitingCode
{
    public const string TurnRunning = "turn-running";
    public const string QuestionWaiting = "question-waiting";
    public const string Unknown = "unknown";
    public const string UsageCheck = "usage-check";
}

// A busy session's state in UpdateBusy: a turn (or delegated work) running, a question waiting, or
// an approval waiting, named as one so a confirmation never calls an approval anything else.
public static class UpdateBusyState
{
    public const string Working = "working";
    public const string Question = "question";
    public const string Approval = "approval";

    public static bool Is(string? state) => state is Working or Question or Approval;
}

// One busy session, by id and state only: never its title, prompt, question or answer.
public sealed record UpdateBusySession(string Id, string State);

// What Update now would go over (UpdateState.Busy), from the host's own session snapshot; and, in a
// press's args (accept), what the person confirmed. Working: sessions with a turn or delegated work
// running. Waiting: sessions with a question or approval waiting. Unknown: the host can't tell
// whether one is busy. Sessions: at most UpdateProtocol.BusyListed of them, approvals first, then
// questions, then turns; past that MoreApprovals and MoreOther count the ones left out.
public sealed record UpdateBusy
{
    public int Working { get; init; }
    public int Waiting { get; init; }
    public bool? Unknown { get; init; }
    public IReadOnlyList<UpdateBusySession>? Sessions { get; init; }
    public int? MoreApprovals { get; init; }
    public int? MoreOther { get; init; }
}

// The signed args of an Update now press: now true, the release the person was shown, and what
// they confirmed it may go over (absent: the one-press case, valid only on a quiet machine).
public sealed record UpdateNowArgs
{
    public bool Now { get; init; }
    public string? Release { get; init; }
    public UpdateBusy? Accept { get; init; }
}

// What the machine's updater is doing. An unknown phase reads as Idle: nothing a client would
// wait on.
[UnknownAs(Idle)]
public enum UpdatePhase { Idle, Checking, Downloading, Waiting, Installing }

// How the last update asked for (or the last one installed) ended. Updated: running the new
// release now. Current: there was nothing newer. Refused: not attempted, and why. Failed:
// attempted, and still the old release. An unknown one reads as Failed, never as Updated.
[UnknownAs(Failed)]
public enum UpdateOutcome { Updated, Current, Refused, Failed }

public sealed record UpdateResult
{
    public required UpdateOutcome Outcome { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    public string? Reason { get; init; }
    // The press it answers, when a device asked for it; absent for an automatic update.
    public string? Request { get; init; }
    // For an Update now: why it ended so, as UpdateCommandResult.Code. A client words it itself.
    public string? Code { get; init; }
    public required DateTimeOffset At { get; init; }
}

// update.get's result, update.subscribe's result and every update.state.
public sealed record UpdateState
{
    // The release installed; null when this copy is not one an updater can update.
    public string? Current { get; init; }
    // Whether an updater is here to act on a request: the Windows app, running and installed.
    public bool Installer { get; init; }
    // Why nothing can be asked of this machine now, when that is so (no app running, say).
    public string? Unavailable { get; init; }
    // A newer release than Current, once a check has found one, with its notes.
    public string? Available { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset? CheckedAt { get; init; }
    public UpdatePhase Phase { get; init; }
    // 0-100 while downloading.
    public int? Progress { get; init; }
    // While waiting: what for (a session busy, the host not running, another profile running).
    public string? Waiting { get; init; }
    // The press being carried out now, if one is.
    public string? Request { get; init; }
    // Whether the app also installs updates without being asked.
    public bool Automatic { get; init; }
    public UpdateResult? Last { get; init; }
    // #1159, the host's own: whether Update now can be pressed here, and when it can't but an
    // update is ready, why (UpdateNowWhy) and, for restart-cap, when the first slot frees.
    public bool CanUpdateNow { get; init; }
    public string? UpdateNowWhy { get; init; }
    public DateTimeOffset? UpdateNowAt { get; init; }
    // Only with CanUpdateNow: what Update now would go over, from the host's session snapshot.
    public UpdateBusy? Busy { get; init; }
    // The app's: the press in Request is an Update now, and with phase waiting, why it waits
    // (UpdateWaitingCode).
    public bool RequestNow { get; init; }
    public string? WaitingCode { get; init; }
}

// update.check and update.install: Request is the device's UUID for this press. For Update now,
// update.install carries Now instead, and the request only inside it: a host that doesn't know Now
// finds no request and refuses the press, never taking it as a plain Install.
public sealed record UpdateCommand(string? Request = null)
{
    [Strict(Strictness.Security)]
    public SignedCommand? Now { get; init; }
}

// Accepted: the machine's app has been asked; how it ends is in the state (Last, with this
// request). Refused: nothing was asked, and Reason says why. Confirm (Update now): what is busy
// now is more than the person confirmed; nothing was asked, and State.Busy says what is busy.
[UnknownAs(Refused)]
public enum UpdateAck { Accepted, Refused, Confirm }

public sealed record UpdateCommandResult(UpdateAck Outcome, string? Reason = null)
{
    public UpdateState? State { get; init; }
    // Only for a press with Now: why it was refused, as a code (ManageReason's and UpdateCode's).
    public string? Code { get; init; }
}
