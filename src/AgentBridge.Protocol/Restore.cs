// Restoring sessions after a machine reboot or a host crash (#780; docs/protocol/v1-sessions.md,
// hello and Notifications; docs/host-launch.md, Restoring after a reboot or a crash). A host that
// offers sessions.restore says in hello's machine.restore what went when it last stopped: the
// sessions that died with it, which it offers to restore, and how many it is still judging.
namespace AgentBridge.Protocol;

public static class RestoreProtocol
{
    // hello's machine.restore, a session's restored field, and the restored push.
    public const string Capability = "sessions.restore";

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

// Why sessions that died are offered rather than started again. Pending: the host keeps them for a
// person to resume from Launch. ReadOnly: the machine takes no launches now (design G5), and keeps
// them until it does. Off: sessions.restoreOnStart is off on this machine, and they are only said.
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
}

// A session's restored field: it was restored by its host after a reboot or a crash, left idle.
// Continue: the exact words of the reply a client offers to send for one that went mid-turn, sent
// through session.reply like any reply, and only when a person presses it. No host sends it.
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
    // Started again (restore itself, a later slice of #780): none yet.
    public int Restored { get; init; }
    // Restored once and died again within ten minutes, so not restored again: none yet.
    public int Loops { get; init; }
    // Still being judged: their agent's session discovery is held, so whether they are still
    // running cannot be told yet (§6.3 invariant, #43). Nothing is said of them until it can.
    public int Waiting { get; init; }
    public RestoreWhy? Why { get; init; }
    public IReadOnlyList<RestoreSession> Offered { get; init; } = [];
    public int? OfferedMore { get; init; }
}
