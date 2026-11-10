// Restoring sessions after a machine reboot or a host crash (#780; docs/protocol/v1-sessions.md,
// hello and Notifications; docs/host-launch.md, Restoring after a reboot or a crash). A host that
// offers sessions.restore says in hello's machine.restore what went when it last stopped: the
// sessions that died with it, which it offers to restore, and how many it is still judging.
namespace AgentBridge.Protocol;

public static class RestoreProtocol
{
    // hello's machine.restore, a session's restored field, and the restored push.
    public const string Capability = "sessions.restore";

    // restore.run (docs/protocol/restore-run.md): restore what the notice offers, from a person's device.
    public const string RunCapability = "sessions.restore.run";
    public const string RunOp = "restore.run";

    // A run names 1 to this many sessions: the notice names at most MaxOffered. The host's restore.run
    // enforces the count and the no-repeats rule (restore-run.md); the record, like most requests,
    // reads any list.
    public const int MaxRunSessions = MaxOffered;

    // At most this many sessions are named in a notice; the rest are counted in offeredMore.
    public const int MaxOffered = 20;

    // How long a notice stays in hello after the host started: this run, and never more than a
    // day, so a host that runs for weeks stops saying what went the morning it started. One
    // constant until Design says otherwise (#780).
    public static readonly TimeSpan ShownFor = TimeSpan.FromHours(24);

    // machine.restore as sent, well inside a message beside the rest of hello. Its fields are bounded
    // (MaxOffered names, each title Limits.Title), but their JSON can be larger (12 bytes for each
    // emoji); a notice past this was made unsendable, and hello with it (Engineering on #837).
    public const int MaxNoticeBytes = 8 * 1024;
    // What Fit cuts each title to first.
    public const int FitTitleBytes = 60;

    // A notice within maxBytes: titles cut, then gone, then the last names counted in offeredMore
    // instead, never the counts or the cause.
    public static RestoreNotice Fit(RestoreNotice n, int maxBytes = MaxNoticeBytes)
    {
        bool Fits(RestoreNotice x) => ProtocolJson.ByteCount(x) <= maxBytes;
        if (Fits(n)) { return n; }
        n = n with { Offered = [.. n.Offered.Select(o => o with { Title = Limits.KeepStart(o.Title, FitTitleBytes) })] };
        if (Fits(n)) { return n; }
        n = n with { Offered = [.. n.Offered.Select(o => o with { Title = null })] };
        while (!Fits(n) && n.Offered.Count > 0)
        {
            n = n with { Offered = [.. n.Offered.Take(n.Offered.Count - 1)], OfferedMore = (n.OfferedMore ?? 0) + 1 };
        }
        return n;
    }
}

// Why the sessions went. Reboot: the machine started again, or the person signed out, so every
// agent went with it. Crash: the host stopped without saying so, in the same logon. Restart: the
// host stopped cleanly (an update, a stop or a restart) in the same logon, and sessions went while
// it was away. A cause a client does not know reads as Crash, which says the least about why.
[UnknownAs(Crash)]
public enum RestoreCause { Reboot, Crash, Restart }

// Why sessions that died are offered rather than restored. Pending: the host keeps them for a person
// to resume from Launch (it could not restore them, or they died again too soon after a restore).
// ReadOnly: the machine takes no launches now (design G5), and keeps them until it does. Off:
// sessions.restoreOnStart is off on this machine, and they are only said.
[UnknownAs(Pending)]
public enum RestoreWhy { Pending, ReadOnly, Off }

// One session that died with the host. MidTurn: it was working when it went. QuestionLost: a question
// or approval was waiting in it, and went with it; the title names it, never the question's words.
public sealed record RestoreSession
{
    // What a client resumes it by (Launch): read as "" when missing, it would be offered under no
    // session at all.
    [Strict(Strictness.Identity)]
    public required string Id { get; init; }
    public required AgentKind Agent { get; init; }
    public string? Title { get; init; }
    public bool? MidTurn { get; init; }
    public bool? QuestionLost { get; init; }
    // It died again within ten minutes of a restore (one of the notice's Loops): a confirmation says
    // so before a person restores it once more. Absent otherwise, and ignored by a client that does not know it.
    public bool? Loop { get; init; }
}

// A session's restored field: it was restored by its host after a reboot or a crash, left idle.
// MidTurn: it went while working, and has had no turn since (the host takes it away once a turn starts
// after the restore). Continue: the exact words of the reply a client offers to send for it, with
// MidTurn only, sent through session.reply like any reply, and only when a person presses it. No host
// sends it, and a reply with these words is given no more trust than any other.
public sealed record SessionRestored
{
    public required DateTimeOffset At { get; init; }
    public required RestoreCause Cause { get; init; }
    public bool? MidTurn { get; init; }
    public bool? QuestionLost { get; init; }
    public string? Continue { get; init; }
}

public sealed record RestoreNotice
{
    // When this run of the host started and compared.
    public required DateTimeOffset At { get; init; }
    public required RestoreCause Cause { get; init; }
    // When the host last wrote down what was live before it stopped: about when the sessions went.
    public DateTimeOffset? Since { get; init; }
    // Restored by the host, idle, through its resume path: how many.
    public int Restored { get; init; }
    // Restored once and died again within ten minutes, so not restored again, only offered.
    public int Loops { get; init; }
    // Still being judged: their agent's session discovery is held, so whether they are still
    // running cannot be told yet (§6.3 invariant, #43). Nothing is said of them until it can.
    public int Waiting { get; init; }
    public RestoreWhy? Why { get; init; }
    public IReadOnlyList<RestoreSession> Offered { get; init; } = [];
    public int? OfferedMore { get; init; }
}

// restore.run (#948): restore, idle, every session the notice names, through the start-up restore's own
// path. Request is the client's UUID for this press, with session.launch's semantics. Offer is the
// notice's At, so a host that started again since refuses the run whole. Sessions are the ids the
// confirmation listed (1 to MaxRunSessions, in the offer's order, no repeats). Progress asks for a frame
// as each session's attempt ends.
public sealed record RestoreRunRequest
{
    public required string Request { get; init; }
    public required DateTimeOffset Offer { get; init; }
    public required IReadOnlyList<string> Sessions { get; init; }
    public bool? Progress { get; init; }
}

// One session's attempt, with session.launch's outcomes and meaning. Reason is present for anything
// but Started. An outcome a client does not know reads as Unconfirmed: something may have started.
public sealed record RestoreRunSession
{
    [Strict(Strictness.Identity)]
    public required string Id { get; init; }
    public required LaunchOutcome Outcome { get; init; }
    public string? Reason { get; init; }
}

// The whole run, read first row that holds: Refused (nothing tried), Busy (nothing tried), Restored (every
// session started), Partial (something started, so any unconfirmed counts), None (no agent started).
// An outcome a client does not know reads as Partial.
[UnknownAs(Partial)]
public enum RestoreRunOutcome { Restored, Partial, None, Busy, Refused }

public sealed record RestoreRunResult
{
    public required RestoreRunOutcome Outcome { get; init; }
    public string? Reason { get; init; }
    // [Strict]: an entry left out would read as a session the run never touched.
    [Strict(Strictness.Identity)]
    public IReadOnlyList<RestoreRunSession> Sessions { get; init; } = [];
}

// A frame sent as each session's attempt ends, before the response, when the request asked for them.
public sealed record RestoreRunProgress
{
    public required string Session { get; init; }
    public required LaunchOutcome Outcome { get; init; }
    public required int Done { get; init; }
    public required int Of { get; init; }
}
