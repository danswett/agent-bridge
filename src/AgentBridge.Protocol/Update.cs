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

    // A request id is the device's UUID for one press, as session.answer's.
    public static bool IsRequest(string? request) => request is { Length: > 0 and <= RequestId } && Guid.TryParse(request, out _);

    public static UpdateState Fit(UpdateState s) => s with
    {
        Current = Limits.KeepStart(s.Current, Version),
        Available = Limits.KeepStart(s.Available, Version),
        Notes = Limits.KeepStart(s.Notes, Notes),
        Waiting = Limits.KeepStart(s.Waiting, Reason),
        Unavailable = Limits.KeepStart(s.Unavailable, Reason),
        Request = IsRequest(s.Request) ? s.Request : null,
        Progress = s.Progress is { } p ? Math.Clamp(p, 0, 100) : null,
        Last = s.Last is { } l ? l with
        {
            From = Limits.KeepStart(l.From, Version),
            To = Limits.KeepStart(l.To, Version),
            Reason = Limits.KeepStart(l.Reason, Reason),
            Request = IsRequest(l.Request) ? l.Request : null,
        } : null,
    };
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
}

// update.check and update.install: Request is the device's UUID for this press.
public sealed record UpdateCommand(string Request);

// Accepted: the machine's app has been asked; how it ends is in the state (Last, with this
// request). Refused: nothing was asked, and Reason says why.
[UnknownAs(Refused)]
public enum UpdateAck { Accepted, Refused }

public sealed record UpdateCommandResult(UpdateAck Outcome, string? Reason = null)
{
    public UpdateState? State { get; init; }
}
