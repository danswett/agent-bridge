using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Restore;

// What became of a session an earlier run of the host saw live.
public enum RestoreVerdict { Running, Ended, Died, Waiting, Uncertain }

// What the worktree reclaimer asks (RestoreWorktreeUsage): the folders of the sessions waiting to be
// restored or still being judged, which no reclaim may take; or null when they cannot be known, and
// then every worktree is kept.
public interface IRestorePending
{
    IReadOnlyList<string>? Folders();
}

// A session to resume, idle, through the host's existing resume path (LaunchService): its id and
// agent, and what a launch through this host started it with, if one did. A session started by hand
// has none: it is resumed with the machine's default settings and asks permission (never Allow all).
// By: who asked, for the log ("host-restore").
public sealed record ResumeAsk(string Id, AgentKind Agent, RestoreLaunch? Launch, string By);

// The resume path: one launch at a time, never a prompt (so the session opens idle), folder trust
// and Bypass Permissions never answered, refused or busy as any launch is (G5, launch.enabled, held
// discovery). LaunchService.ResumeAsync.
public interface ISessionResumer
{
    Task<LaunchResult> ResumeAsync(ResumeAsk ask, CancellationToken cancellationToken = default);
}

// Where a session's restored mark is shown (SessionSource): set when it is restored, null to take the
// badge away once it has been shown long enough. The restore itself stays a boundary for as long as
// the session is live: what the interrupted run left (its turn, its question) is never shown again.
// The source clears midTurn itself once a prompt arrives after the restore.
public interface IRestoreMarks
{
    void Mark(string id, SessionRestored? mark);

    // The restore's boundary without the badge, until it is shown with Mark (the same mark): from
    // before the launch, so what the interrupted run left is never shown on the restored session,
    // while restored and Continue wait for its window to be seen ready.
    void Hold(string id, SessionRestored mark);

    // An attempt that started nothing takes back the mark it showed: the mark before it comes back as
    // it was then (Continue gone if a turn had started since), or none. Nothing if another mark has
    // been shown since.
    void Revert(string id, SessionRestored mark);

    // A boundary an earlier host run kept, with the interrupted run's questions it found (their ids),
    // so they are told by identity rather than by time; null when it found none to say.
    void Hold(string id, SessionRestored mark, IReadOnlyCollection<string>? interrupted) => Hold(id, mark);

    // The interrupted run's questions this run has set aside on a restored session, by id, for the
    // record; null before it has been read with its boundary.
    IReadOnlyList<string>? Interrupted(string id) => null;
}

// Resuming a session that is not running, idle, as a restore does, for a caller other than the
// start-up queue (#800: sessions an agent's expired sign-in stopped, once it is renewed). Through the
// same queue, so still one launch at a time; refused while the machine is read-only. Marked
// restored with the cause given, and offered Continue when MidTurn: nothing is ever typed.
public sealed record RestoreAsk
{
    public required string Session { get; init; }
    public required RestoreCause Cause { get; init; }
    // When it went, for Continue's words; now when absent.
    public DateTimeOffset? Since { get; init; }
    public bool MidTurn { get; init; }
    public bool QuestionLost { get; init; }
    // Who asked, for the log, e.g. "host-signin".
    public required string By { get; init; }
}

public interface ISessionRestorer
{
    Task<LaunchResult> RestoreAsync(RestoreAsk ask, CancellationToken cancellationToken = default);
}

public static class RestoreJudge
{
    // Boot times read against the wall clock move with a clock change; two boots closer than this
    // are one. A machine that ran less than this before it restarted reads as a host crash.
    public static readonly TimeSpan BootTolerance = TimeSpan.FromMinutes(2);

    // freshLogon: the person's logon session began since the host last ran (ILogonMarker): a sign-out,
    // or a Windows shutdown with Fast Startup, which ends every agent but keeps the boot time.
    public static RestoreCause CauseOf(RestoreFile previous, DateTimeOffset boot, bool freshLogon = false) =>
        freshLogon || (previous.Boot - boot).Duration() > BootTolerance ? RestoreCause.Reboot
        : previous.Stopped is not null ? RestoreCause.Restart
        : RestoreCause.Crash;

    // One session an earlier run saw live:
    //   * running now (discovery lists it): adopted as it is, never restored;
    //   * its agent's discovery held: waiting, judged once it can be, never read as absent (#43);
    //   * on another boot: died, whatever it wrote as it went (a shutdown ends every session, and
    //     an ending written then is the shutdown's; so is one the host saw go just before it);
    //   * on the same boot, gone while that run watched: it ended under the host's eyes;
    //   * its agent's endings not yet settled (a hook's SessionEnd still in the intake's spool):
    //     waiting, as for a hold, so an ending written while the host was down is not missed;
    //   * ended normally, as its agent records it: never restored;
    //   * a Claude or Codex session whose registration is gone: uncertain. agent-ha-bridge's daemon
    //     prunes an ended registration within a minute, and Claude's once it is StaleAfter old, so
    //     its absence says nothing either way (ISessionEnds). Neither offered nor said to have died,
    //     its folder kept as an offer's is (Engineering on #813);
    //   * otherwise it died with the host.
    // A session a person closed while the host was down, on the same boot, with nothing its agent
    // records, reads as died: it is offered, never typed into (#780, known edge).
    public static RestoreVerdict Judge(RestoreEntry entry, RestoreCause cause, bool running, bool held, Func<bool?> endedNormally, bool endsSettled = true)
    {
        if (running) { return RestoreVerdict.Running; }
        if (held) { return RestoreVerdict.Waiting; }
        if (cause == RestoreCause.Reboot) { return RestoreVerdict.Died; }
        if (entry.Gone is not null) { return RestoreVerdict.Ended; }
        if (!endsSettled) { return RestoreVerdict.Waiting; }
        return endedNormally() switch
        {
            true => RestoreVerdict.Ended,
            null when entry.Agent is AgentKind.Claude or AgentKind.Codex => RestoreVerdict.Uncertain,
            _ => RestoreVerdict.Died,
        };
    }
}

public sealed record RestoreOptions
{
    // restore.json in the profile's folder (RestoreFiles.FileName).
    public required string Path { get; init; }
    // machine.instance: this run of the host.
    public required string Instance { get; init; }
    public required ISessionEnds Ends { get; init; }
    // Whether every ending an agent's hooks wrote so far has been taken in (the hook intake's spool
    // is drained), by agent; none says always. Until then that agent's sessions wait to be judged.
    public Func<AgentKind, bool>? EndsSettled { get; init; }
    // sessions.restoreOnStart (docs/host-config.md): off, what died is only said, never kept nor started.
    public bool RestoreOnStart { get; init; } = true;
    // Off only because config.json could not be read: nothing is started, but what died is kept to
    // offer, as when it is on (HostSettings.RestoreKept).
    public bool RestoreKept { get; init; }
    // Where a restored session's mark is shown (SessionSource).
    public IRestoreMarks? Marks { get; init; }
    // The machine's time zone, for the time Continue's words give; the local one when none.
    public TimeZoneInfo? Zone { get; init; }
    // Why the machine takes no launches from other devices now (design G5), or null.
    public Func<string?> ReadOnly { get; init; } = () => null;
    public IBootClock? Boot { get; init; }
    // Whether the logon session is new since the host last ran in it; none says it never is.
    public ILogonMarker? Logon { get; init; }
    public TimeProvider? Time { get; init; }
    public Action<string>? Log { get; init; }
}

// The host's record of the sessions it saw live (#780), and what it found at start of those an
// earlier run saw. What this run found died is restored, idle, through the resume path, one at a time
// (Restore), unless the machine is read-only or sessions.restoreOnStart is off; what is not restored is
// offered, by hello's machine.restore and one push, and kept from the worktree reclaimer while it
// waits. Nothing is ever typed into a restored session: Continue is a client's ordinary reply.
//
// The record is written on every change that matters (a session appears or goes, its turn or its
// question changes), at most every Coalesce, whole and flushed before it replaces the last. A session
// that goes is kept GoneKept more: a machine shutting down ends its agents before it ends the host,
// and the next start must still know they were live.
public sealed class RestoreRecorder : IRestorePending, ISessionRestorer, IAsyncDisposable
{
    public static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan GoneKept = TimeSpan.FromSeconds(60);
    // While anything is still being judged, it is looked at again this often.
    public static readonly TimeSpan JudgeEvery = TimeSpan.FromSeconds(5);
    // A session offered and never resumed is let go this long after it was offered, so its worktree
    // can go too. Never one still being judged: what it is cannot be told, so its worktree is kept.
    public static readonly TimeSpan OwedFor = TimeSpan.FromDays(7);
    // The push waits for every session to be judged and restored, or this long after start,
    // whichever is first.
    public static readonly TimeSpan NoticeAfter = TimeSpan.FromMinutes(10);
    // A time in the record more than this ahead of the clock (one set back: a bad RTC, a VM restored
    // from a snapshot, a year corrected) is read as now. Kept, never let go for it, and never left to
    // put a deadline weeks out.
    public static readonly TimeSpan AheadTolerance = TimeSpan.FromMinutes(2);
    // No timer is set further out than this: a later deadline is looked at again when it fires. The
    // system's timers refuse anything past about 49.7 days, and that refusal stopped the host.
    public static readonly TimeSpan LongestWait = TimeSpan.FromDays(1);
    // A restored session that dies before it has stayed live this long is not restored again: a
    // session that kills its agent, or a machine that keeps falling over, would otherwise loop.
    public static readonly TimeSpan LoopWithin = TimeSpan.FromMinutes(10);
    // A restore the launcher says is busy (a folder-trust question waiting for a person, discovery
    // held) is tried again after this.
    public static readonly TimeSpan BusyRetry = TimeSpan.FromSeconds(15);
    // A restore's stamp due to go while its agent's discovery is held is looked at again this often.
    public static readonly TimeSpan HeldRetry = TimeSpan.FromMinutes(1);
    public const string RestorePrincipal = "host-restore";
    // What the record keeps of launches whose sessions have not been seen (bounded, never growing).
    const int LaunchesKept = 256;

    readonly ISessionSource source;
    readonly RestoreOptions options;
    readonly TimeProvider time;
    readonly Action<string> log;
    readonly DateTimeOffset boot;
    readonly bool freshLogon;
    // A fresh logon's marker, still to be made: once a record of this start's judgement is written.
    bool logonPending;
    readonly string? logon;
    // Sessions this run judged from the last run's live ones, as against those it carried over
    // owed: only these are news, only they make the push, and only they are restored.
    readonly HashSet<string> judgedNow = new(StringComparer.Ordinal);
    // Gone between Start's subscribing and its snapshot being merged: not taken from the snapshot.
    readonly HashSet<string> goneBeforeStart = new(StringComparer.Ordinal);
    readonly Lock gate = new();
    readonly Dictionary<string, RestoreEntry> live = new(StringComparer.Ordinal);
    readonly Dictionary<string, RestoreLaunch> launched = new(StringComparer.Ordinal);
    // Sessions of earlier runs: judged to have died (kept to offer), or still being judged.
    readonly List<RestoreOwed> owed = [];
    // Judged to have died while sessions.restoreOnStart is off: said in the notice, never kept.
    readonly List<RestoreOwed> said = [];
    // Restored (stamped before its launch), by when, until each has stayed live LoopWithin.
    readonly Dictionary<string, DateTimeOffset> restoredAt = new(StringComparer.Ordinal);
    // Each restored session's boundary (RestoreEntry.Boundary): its latest restore, for as long as the
    // record keeps it, whatever becomes of its stamp.
    readonly Dictionary<string, DateTimeOffset> boundaries = new(StringComparer.Ordinal);
    // The interrupted run's questions set aside at each boundary (RestoreEntry.Interrupted), as the
    // source last said or the record kept; none until known.
    readonly Dictionary<string, IReadOnlyList<string>> interruptedAt = new(StringComparer.Ordinal);
    // When each stamped session was first seen live: the ten minutes count from there, not from the
    // stamp, so a restore that waits at folder trust keeps its stamp until it has really lived. On the
    // monotonic clock: on the wall clock, one set forward cleared a stamp after seconds of real life,
    // and a crash then was restored again (Copilot on #837). The stamps themselves stay wall-clock
    // times, as the record keeps them.
    readonly Dictionary<string, long> liveSince = new(StringComparer.Ordinal);
    // The marks shown on sessions restored in this run, each taken away after ShownFor.
    // Each with its generation: a callback already under way for a timer since replaced takes
    // nothing away.
    readonly Dictionary<string, (ITimer Timer, long Gen)> marks = new(StringComparer.Ordinal);
    long markGen;
    // And the marks themselves, with when each goes, so an attempt that started nothing can put an
    // earlier one back for what was left of its time.
    readonly Dictionary<string, (SessionRestored Mark, DateTimeOffset Until)> shown = new(StringComparer.Ordinal);
    // One resume at a time, the start-up queue's and RestoreAsync's alike.
    readonly SemaphoreSlim one = new(1, 1);
    // Tried this run and refused or failed: offered, not tried again.
    readonly HashSet<string> tried = new(StringComparer.Ordinal);
    // Restored by this run: not offered, whatever the clock says of their stamps.
    readonly HashSet<string> restoredThisRun = new(StringComparer.Ordinal);
    // Restored by this start's queue, with what each lost (went mid-turn, or with a question
    // waiting), for the notice and the push. Counted only while not offered or being judged
    // (Counted): a session is never both restored and a loop.
    readonly Dictionary<string, RestoreSession?> restoredBy = new(StringComparer.Ordinal);
    // Restored by this run and died while it watched: judged as a crash (an ending its agent records
    // still counts), but no cause of the notice's.
    readonly HashSet<string> inRun = new(StringComparer.Ordinal);
    // Died while an attempt to restore it was under way (registered, then gone, before its result):
    // that attempt does not count it restored. Per attempt, unlike inRun, so a later restore that
    // succeeds is shown restored.
    readonly HashSet<string> diedDuring = new(StringComparer.Ordinal);
    // Being resumed now, by the queue or on request: never a second attempt at the same session.
    readonly HashSet<string> inFlight = new(StringComparer.Ordinal);
    // Restores on request that the record holds no owed entry of, with the folder each runs in (null
    // when not known): kept from any reclaim (Folders) until it registers, starts nothing, or its
    // stamp lapses. Before, its worktree could be reclaimed before the launch reserved it.
    readonly Dictionary<string, string?> requested = new(StringComparer.Ordinal);
    // Restores on request still under way: shutdown waits for them as for the queue's (DisposeAsync).
    readonly HashSet<Task> requests = [];
    // Answered Unconfirmed (started, its window not yet seen ready), by the stamp of the attempt that
    // was: seen ready later (LaunchService.SeenReadyLate), counted and shown restored then, if that
    // attempt is still the session's. Seen ready while the attempt's answer was still on its way:
    // readyLate, read as that answer arrives.
    readonly Dictionary<string, Unconfirmed> unconfirmed = new(StringComparer.Ordinal);
    readonly HashSet<string> readyLate = new(StringComparer.Ordinal);

    sealed record Unconfirmed(DateTimeOffset Stamp, RestoreSession? Loss, bool Queue);

    // Cancelled as the host stops: a restore still waiting its turn starts nothing.
    readonly CancellationTokenSource stopping = new();
    Task active = Task.CompletedTask;
    ISessionResumer? resumer;
    bool resuming;
    DateTimeOffset retryAt;

    // Why and since when, as this start found it, for a notice once every session it found is restored.
    RestoreCause startCause = RestoreCause.Crash;
    DateTimeOffset? startSince;
    DateTimeOffset started;
    // Started, on the monotonic clock: how long the notice is shown and the hold's wait before the
    // push are elapsed time. Measured on the wall clock, one set back days kept both going for days.
    long startedStamp;
    bool running;
    bool stopped;
    // The earlier record could not be read: what waits to be restored is unknown for this run.
    bool unreadable;
    // Start failed: what is owed is unknown for this run, so every worktree is kept.
    bool failed;
    bool told;
    RestoreNotice? notice;
    ITimer? writeTimer;
    // When the pending write was armed, on the monotonic clock, and how long after: the timer runs
    // on elapsed time, so its deadline is kept the same way. On the wall clock, one set forward
    // during a gone session's GoneKept left a new change waiting most of a minute (Copilot on #813).
    long writeArmed;
    TimeSpan writeAfter;
    ITimer? judgeTimer;
    bool judgingEvery;
    string? writeProblem;

    // Raised once, with the notice to push, when what died is known (or NoticeAfter has passed).
    public event Action<RestoreNotice>? NoticeReady;

    public RestoreRecorder(ISessionSource source, RestoreOptions options)
    {
        this.source = source;
        this.options = options;
        time = options.Time ?? TimeProvider.System;
        log = options.Log ?? (_ => { });
        boot = (options.Boot ?? new SystemBootClock(time)).Boot();
        freshLogon = options.Logon?.Fresh() == true;
        logonPending = freshLogon;
        logon = options.Logon?.Logon();
    }

    // What died is kept to offer (and, with restoreOnStart on, to restore).
    bool Keeps => options.RestoreOnStart || options.RestoreKept;

    // hello's machine.restore: for RestoreProtocol.ShownFor after the host started. Why is read as
    // it is now, so a machine that takes launches again (agent-ha-bridge stopped) says so.
    public RestoreNotice? Notice
    {
        get
        {
            RestoreNotice? n;
            lock (gate) { n = notice is not null && time.GetElapsedTime(startedStamp) < RestoreProtocol.ShownFor ? notice : null; }
            if (n is null || n.Why == RestoreWhy.Off) { return n; }
            var why = options.ReadOnly() is null ? RestoreWhy.Pending : RestoreWhy.ReadOnly;
            return n.Why == why ? n : n with { Why = why };
        }
    }

    // Whether a Claude or Codex session of an earlier run is still being judged without its ending
    // read: the hook intake prunes nothing meanwhile (HookIntakeOptions.KeepEndings), since an
    // ended session's registration, pruned first, would be read as a death.
    public bool JudgingEndings
    {
        get
        {
            lock (gate) { return owed.Any(w => !w.Decided && w.EndedSeen is null && w.Entry.Agent is AgentKind.Claude or AgentKind.Codex); }
        }
    }

    public IReadOnlyList<string>? Folders()
    {
        lock (gate)
        {
            // A record set aside keeps every worktree until a person has looked at it and removed
            // it, on this start and every later one: unknown is never read as nothing owed (#780).
            if (failed || unreadable || !running || RestoreFiles.SetAsideExists(options.Path)) { return null; }
            var folders = new List<string>();
            foreach (var w in owed)
            {
                // A session that may be restored, in a folder nobody knows: any worktree may be its.
                if (w.Entry.Cwd is not { Length: > 0 } cwd) { return null; }
                folders.Add(cwd);
            }
            foreach (var (id, folder) in requested)
            {
                if (owed.Any(w => w.Entry.Id == id)) { continue; }
                if (folder is not { Length: > 0 } cwd) { return null; }
                folders.Add(cwd);
            }
            return folders;
        }
    }

    // After the source's first discovery pass (SessionSource.StartAsync). Whatever stops it is said,
    // and leaves this run keeping every worktree: what is owed is unknown then, and unknown is never
    // read as nothing owed. The host goes on serving: a record that stopped it at start would stop it
    // at every start.
    public void Start()
    {
        try { StartOnce(); }
        catch (Exception e) when (e is not OutOfMemoryException) { Fail("start", e); }
    }

    void Fail(string what, Exception e)
    {
        lock (gate) { failed = true; }
        log($"restore: could not {what} ({e.GetType().Name}: {e.Message}); what waits to be restored is unknown for this run, so no worktree is reclaimed until the host starts again");
    }

    void StartOnce()
    {
        var (previous, problem) = RestoreFiles.Read(options.Path);
        if (problem is not null)
        {
            log($"restore: the record of the sessions live when the host last ran could not be read ({problem}); it is set aside as {RestoreFiles.FileName}.unreadable, nothing is offered from it, and no worktree is reclaimed until that file is removed");
            RestoreFiles.SetAside(options.Path);
        }
        else if (RestoreFiles.SetAsideExists(options.Path))
        {
            log($"restore: {RestoreFiles.FileName}.unreadable is still there; no worktree is reclaimed until it is removed");
        }
        // What the sessions still live were launched with, so adopting them keeps it; and when those
        // restored lately were, so one that dies soon after is not restored again.
        lock (gate)
        {
            // Owed ones too: one still being judged when the host stopped may be found running now.
            // Every one: LaunchesKept bounds launches not yet seen (Launched). Capped here too, a session
            // past it was written again without its settings, which were lost.
            foreach (var e in (previous?.Sessions ?? []).Concat((previous?.Owed ?? []).Select(w => w.Entry)))
            {
                if (e.Launch is { } l) { launched.TryAdd(e.Id, l); }
                if (e.Restored is { } r) { restoredAt.TryAdd(e.Id, r); }
            }
            // A restore started and not yet registered is stamped on its owed entry; one with no
            // entry, in Stamps. Either, found running now, is adopted stamped.
            foreach (var w in previous?.Owed ?? [])
            {
                if (w.Restored is { } r) { restoredAt[w.Entry.Id] = r; }
            }
            foreach (var (id, r) in previous?.Stamps ?? new Dictionary<string, DateTimeOffset>())
            {
                restoredAt.TryAdd(id, r);
            }
            // Held on the source before its first pass (HoldBoundaries), and written on with them.
            if (previous is not null)
            {
                foreach (var (id, at) in Boundaries(previous)) { boundaries[id] = at; }
                foreach (var (id, ids) in InterruptedAt(previous)) { interruptedAt[id] = ids; }
            }
        }
        // Heard from now, so nothing between the snapshot and this is missed; the snapshot is taken
        // outside the lock, as the source's own events are.
        source.Changed += OnChanged;
        var snapshot = source.Snapshot();
        RestoreNotice? ready;
        lock (gate)
        {
            var now = time.GetUtcNow();
            started = now;
            startedStamp = time.GetTimestamp();
            running = true;
            unreadable = problem is not null;
            foreach (var s in snapshot)
            {
                if (!live.ContainsKey(s.Id) && !goneBeforeStart.Contains(s.Id) && From(s, now) is { } e) { live[e.Id] = e; }
            }
            goneBeforeStart.Clear();
            // Restored lately and still running: its ten minutes start again from now.
            foreach (var id in live.Keys.Where(restoredAt.ContainsKey)) { liveSince.TryAdd(id, time.GetTimestamp()); }
            // A restore on request that had not registered when the host went: still a request, its
            // folder unknown, so every worktree is kept until it registers or its stamp lapses. Before,
            // a restart dropped it, and its worktree could be reclaimed before it registered.
            foreach (var id in previous?.Stamps?.Keys ?? [])
            {
                if (!(live.TryGetValue(id, out var found) && found.Gone is null)) { requested.TryAdd(id, null); }
            }
            if (previous is not null)
            {
                // A marker missing in the logon the record was written in is not a new logon: it
                // was removed, or never made (sec-813).
                var sameLogon = logon is not null && previous.Logon == logon;
                var cause = RestoreJudge.CauseOf(previous, boot, freshLogon && !sameLogon);
                (startCause, startSince) = (cause, previous.Written);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                // A session that run also kept owed (a restored one that died while it watched, still
                // written live for GoneKept) is carried as owed: read as live, it would read as ended
                // under that run's eyes, and be neither offered nor its worktree kept.
                var alsoOwed = previous.Owed.Select(w => w.Entry.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var e in previous.Sessions)
                {
                    if (alsoOwed.Contains(e.Id) || !seen.Add(e.Id)) { continue; }
                    owed.Add(new RestoreOwed(e, cause, previous.Written, now, Decided: false));
                    judgedNow.Add(e.Id);
                }
                // Sessions earlier runs left owed. Those offered already are not news, and are not
                // restored now: a person resumes them from Launch. Those still being judged are, and
                // are judged as of now: a reboot since makes them died. With sessions.restoreOnStart
                // off none is kept.
                foreach (var w in previous.Owed)
                {
                    if (!Keeps || (w.Decided && now - w.At >= OwedFor) || !seen.Add(w.Entry.Id)) { continue; }
                    // Stamped, and not found running (adopted then): its restore started and it went
                    // again before it had lived, with the host. A loop, never offered as a plain death.
                    if (w.Decided) { owed.Add(w.Restored is not null && w.Loop != true ? w with { Loop = true } : w); continue; }
                    owed.Add(cause == RestoreCause.Reboot ? w with { Cause = cause } : w);
                    judgedNow.Add(w.Entry.Id);
                }
                log($"restore: the host last ran on {(cause == RestoreCause.Reboot ? "an earlier boot" : "this boot")} ({cause.ToString().ToLowerInvariant()}); {previous.Sessions.Count} session(s) were live then");
            }
            ready = AdvanceLocked(now);
            WriteLocked(now);
            ArmLocked(now);
            PumpLocked(now);
        }
        if (ready is not null) { Tell(ready); }
    }

    // An owed entry's boundary, kept for the session it becomes live again as (adopted once a hold
    // lifts, or resumed): adopted as a fresh entry, it was written with none, and the next host run
    // listed the interrupted question as asked again (Engineering, cr-837g). The later of the two.
    void CarryBoundaryLocked(RestoreEntry entry)
    {
        if (entry.Boundary is not { } b) { return; }
        if (!boundaries.TryGetValue(entry.Id, out var was) || b > was)
        {
            boundaries[entry.Id] = b;
            if (entry.Interrupted is { } ids) { interruptedAt[entry.Id] = ids; } else { interruptedAt.Remove(entry.Id); }
        }
        else if (b == was && entry.Interrupted is { } same) { interruptedAt.TryAdd(entry.Id, same); }
    }

    // What the record keeps of a boundary's interrupted questions: the newest MaxInterrupted, each
    // within MaxName. Never none for being too many: that fell back to telling them by time, and hid a
    // new question a clock set back (Engineering, cr2-837h). One left out reads as new after a restart,
    // shown rather than hidden.
    IReadOnlyList<string>? Kept(string id) =>
        interruptedAt.TryGetValue(id, out var ids)
            ? [.. ids.Where(i => System.Text.Encoding.UTF8.GetByteCount(i) <= LaunchProtocol.MaxName).TakeLast(RestoreFiles.MaxInterrupted)]
            : null;

    // Of each session's boundary in the record (Boundaries), the interrupted questions kept with it;
    // none when a later stamp is its boundary (not read with it yet).
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> InterruptedAt(RestoreFile file)
    {
        var at = Boundaries(file);
        var found = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var e in file.Sessions.Concat(file.Owed.Select(w => w.Entry)))
        {
            if (e.Boundary is { } b && e.Interrupted is { } ids && at.TryGetValue(e.Id, out var latest) && latest == b) { found.TryAdd(e.Id, ids); }
        }
        return found;
    }

    // The restores the record knows of, by session: each live session's boundary, and every stamp (a
    // restore started, registered or not). A stamp is a boundary too: found running, it is the
    // restored session.
    public static IReadOnlyDictionary<string, DateTimeOffset> Boundaries(RestoreFile file)
    {
        var found = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        void Add(string id, DateTimeOffset? at)
        {
            if (at is { } t && (!found.TryGetValue(id, out var was) || t > was)) { found[id] = t; }
        }
        foreach (var e in file.Sessions) { Add(e.Id, e.Boundary); Add(e.Id, e.Restored); }
        foreach (var w in file.Owed) { Add(w.Entry.Id, w.Entry.Boundary); Add(w.Entry.Id, w.Restored); }
        foreach (var (id, at) in file.Stamps) { Add(id, at); }
        return found;
    }

    // Before the source's first pass (Program): every restored session's boundary held on it, so a
    // host started again after a restore never publishes the interrupted run's question as asked, and
    // answerable (Engineering's second-family review of #837). Held, not shown: the badge and Continue
    // were that run's. An unreadable record holds none; Start sets it aside.
    public static void HoldBoundaries(string path, IRestoreMarks marks)
    {
        if (RestoreFiles.Read(path).File is not { } file) { return; }
        var interrupted = InterruptedAt(file);
        foreach (var (id, at) in Boundaries(file))
        {
            // Only its time is read while it is held: the cause is never shown. With the interrupted
            // questions kept, those are told by identity; without (an older record), by time.
            marks.Hold(id, new SessionRestored { At = at, Cause = RestoreCause.Crash }, interrupted.GetValueOrDefault(id));
        }
    }

    // The resume path, once the launcher exists (it is made after the recorder): what this start found
    // died is restored from now, one at a time.
    public void Restore(ISessionResumer with)
    {
        lock (gate)
        {
            if (stopped) { return; }
            resumer = with;
            if (running) { PumpLocked(time.GetUtcNow()); }
        }
    }

    // A session this host launched, with what it was launched with: kept with its entry, so a restore
    // resumes it as it was started (LaunchService.SessionStarted).
    public void Launched(string sessionId, LaunchedWith with)
    {
        var launch = new RestoreLaunch(with.Agent, with.Workspace, with.Worktree, with.Profile, with.Model, with.Effort, with.Context, with.Permissions);
        lock (gate)
        {
            if (stopped) { return; }
            // Never an owed session's: one held undecided is adopted later through From, which takes
            // its settings from here. Let go, it was written with none (Copilot on #813). Nor a live
            // one's, and those do not count to the bound: with LaunchesKept of them, every launch let
            // go of the one before it before its session was seen (Copilot on #813).
            var owedIds = owed.Select(w => w.Entry.Id).ToHashSet(StringComparer.Ordinal);
            var unseen = launched.Keys.Where(id => !live.ContainsKey(id) && !owedIds.Contains(id)).ToList();
            if (unseen.Count >= LaunchesKept)
            {
                foreach (var id in unseen) { launched.Remove(id); }
            }
            launched[sessionId] = launch;
            if (running && live.TryGetValue(sessionId, out var e) && e.Launch != launch)
            {
                live[sessionId] = e with { Launch = launch };
                ScheduleWriteLocked(Coalesce);
            }
        }
    }

    void OnChanged(object? sender, SessionChange change)
    {
        lock (gate)
        {
            // Before Start has merged its snapshot too, so a session that goes between the snapshot
            // and Start is known gone; only writing waits for Start.
            if (stopped) { return; }
            var now = time.GetUtcNow();
            if (change.Kind == SessionChangeKind.Upserted && change.Session is { } s)
            {
                // Resumed since (from Launch): live again, so no longer owed. Its boundary goes with it.
                foreach (var w in owed.Concat(said).Where(w => w.Entry.Id == s.Id)) { CarryBoundaryLocked(w.Entry); }
                var resumed = owed.RemoveAll(w => w.Entry.Id == s.Id) + said.RemoveAll(w => w.Entry.Id == s.Id) > 0;

                if (resumed) { BuildNoticeLocked(); }
                goneBeforeStart.Remove(s.Id);
                // Registered: its own session from now, kept from a reclaim as any live one is.
                requested.Remove(s.Id);
                if (From(s, now) is not { } e) { return; }
                // Stamped by a restore, and live now: its ten minutes start.
                if (restoredAt.ContainsKey(e.Id)) { liveSince.TryAdd(e.Id, time.GetTimestamp()); }
                if (live.TryGetValue(e.Id, out var was) && was.Gone is null && Same(was, e) && !resumed) { return; }
                live[e.Id] = e;
                if (running) { ScheduleWriteLocked(Coalesce); }
            }
            else if (change.Kind == SessionChangeKind.Removed && !running && !live.ContainsKey(change.Id)) { goneBeforeStart.Add(change.Id); }
            else if (change.Kind == SessionChangeKind.Removed && live.TryGetValue(change.Id, out var gone) && gone.Gone is null)
            {
                live[change.Id] = gone with { Gone = now };
                liveSince.Remove(change.Id);
                // Gone before its window was seen ready: never shown restored.
                unconfirmed.Remove(change.Id);
                // Restored, and gone before it had lived LoopWithin: judged as a death at start is
                // (its agent may record a normal end), and if it died, offered as a loop, never
                // restored again. Kept owed meanwhile, so its worktree is too, and a restart within
                // GoneKept does not read it as ended under the host's eyes.
                if (running && gone.Restored is not null && Keeps && !owed.Any(w => w.Entry.Id == change.Id))
                {
                    owed.Add(new RestoreOwed(gone with { Gone = null }, RestoreCause.Crash, now, now, Decided: false));
                    inRun.Add(change.Id);
                    if (inFlight.Contains(change.Id)) { diedDuring.Add(change.Id); }
                    restoredThisRun.Remove(change.Id);
                    BuildNoticeLocked();
                    ArmLocked(now);
                }
                if (running) { ScheduleWriteLocked(Coalesce); }
            }
        }
    }

    void Tick()
    {
        RestoreNotice? ready;
        lock (gate)
        {
            if (stopped) { return; }
            var now = time.GetUtcNow();
            var before = Signature();
            ready = AdvanceLocked(now);
            if (Signature() != before) { ScheduleWriteLocked(Coalesce); }
            ArmLocked(now);
            PumpLocked(now);
        }
        if (ready is not null) { Tell(ready); }
    }

    // A timer's callback: anything it throws would stop the host, so it is said and fails closed.
    void Guarded(Action act)
    {
        try { act(); }
        catch (Exception e) when (e is not OutOfMemoryException) { Fail("look at what waits to be restored", e); }
    }

    // Times in the record ahead of the clock by more than AheadTolerance, read as now: the clock was
    // set back. An offer dated 60 days ahead was never let go, and armed a timer past what the system
    // takes, which stopped the host at every start (Quality, #813).
    void AheadLocked(DateTimeOffset now)
    {
        for (var i = 0; i < owed.Count; i++)
        {
            if (owed[i].At - now > AheadTolerance) { owed[i] = owed[i] with { At = now }; }
        }
        foreach (var (id, e) in live.ToList())
        {
            if (e.Gone is { } gone && gone - now > AheadTolerance) { live[id] = e with { Gone = now }; }
            if (e.Restored is { } stamped && stamped - now > AheadTolerance) { live[id] = live[id] with { Restored = now }; }
        }
        // A restore stamp ahead stays until its session has stayed live LoopWithin from now; a busy
        // retry ahead would hold the queue for as long as the clock was set back.
        foreach (var (id, r) in restoredAt.ToList())
        {
            if (r - now > AheadTolerance) { restoredAt[id] = now; }
        }
        // Read as now with the stamp they were taken from, so the two still match (SeenReadyLate).
        foreach (var (id, u) in unconfirmed.ToList())
        {
            if (u.Stamp - now > AheadTolerance) { unconfirmed[id] = u with { Stamp = now }; }
        }
        if (retryAt - now > BusyRetry + AheadTolerance) { retryAt = now; }
    }

    // Never before now, never past LongestWait: what fires early only looks again.
    static TimeSpan Wait(TimeSpan due) => due < TimeSpan.Zero ? TimeSpan.Zero : due > LongestWait ? LongestWait : due;

    // While anything is still being judged or waits to be restored, every JudgeEvery; then once, when
    // the first offer comes to OwedFor (at most LongestWait ahead, then again), so a host that runs for
    // weeks lets it go (and its worktree) on time.
    void ArmLocked(DateTimeOffset now)
    {
        AheadLocked(now);
        var judging = owed.Any(w => !w.Decided) || (resumer is not null && options.RestoreOnStart && Candidates().Any());
        // Already looking every JudgeEvery, and still judging: left to run.
        if (!stopped && judging && judgeTimer is not null && judgingEvery) { return; }
        judgeTimer?.Dispose();
        judgeTimer = null;
        judgingEvery = false;
        if (stopped || owed.Count == 0) { return; }
        if (judging)
        {
            judgeTimer = time.CreateTimer(_ => Guarded(Tick), null, JudgeEvery, JudgeEvery);
            judgingEvery = true;
            return;
        }
        var due = owed.Min(w => w.At) + OwedFor - now;
        judgeTimer = time.CreateTimer(_ => Guarded(Tick), null, Wait(due > JudgeEvery ? due : JudgeEvery), Timeout.InfiniteTimeSpan);
    }

    // What this start found died and has not yet tried to restore: never one an earlier run offered
    // (a person resumes that from Launch), one already restored, or one that died again too soon.
    IEnumerable<RestoreOwed> Candidates() =>
        owed.Where(w => Offered(w) && w.Loop != true && w.Restored is null && judgedNow.Contains(w.Entry.Id) && !tried.Contains(w.Entry.Id) && !inFlight.Contains(w.Entry.Id));

    // Whether the queue may start one now: a resume path, restoreOnStart on, and a machine that takes
    // launches (G5, launch.enabled, a config that could be read). Read-only, they wait, offered.
    bool CanRestoreLocked() => resumer is not null && options.RestoreOnStart && options.ReadOnly() is null;

    // Still restoring: one under way, or one that could start now.
    bool RestoringLocked() => resuming || (CanRestoreLocked() && Candidates().Any());

    // Starts the next restore, newest first, if one may start now. Only one is ever under way. Its
    // stamp is written down before the launch: a session that registers and dies at once, with the
    // host, must still read as a loop at the next start, never be restored again.
    void PumpLocked(DateTimeOffset now)
    {
        if (stopped || stopping.IsCancellationRequested || !running || resuming || now < retryAt || !CanRestoreLocked()) { return; }
        // Not while any is still being judged (its agent's discovery held, or its hooks' endings not
        // yet taken in): every session is judged before the newest is restored, across agents
        // (Copilot on #837). The judging timer pumps again once they are.
        if (owed.Any(w => !w.Decided)) { return; }
        if (Candidates().OrderByDescending(w => w.Entry.Seen).FirstOrDefault() is not { } next) { return; }
        var entry = next.Entry;
        var undo = StampLocked(entry.Id, now, next.Cause, next.Since ?? next.At, entry.Turn == RestoreTurn.Working, entry.QuestionWaiting);
        if (!WriteLocked(now))
        {
            // Not written, so not safe to start: tried again, as for a busy launcher.
            UndoLocked(undo);
            retryAt = now + BusyRetry;
            return;
        }
        resuming = true;
        inFlight.Add(entry.Id);
        diedDuring.Remove(entry.Id);
        // Off this thread: the launcher takes locks of its own, and calls back into this one.
        active = Task.Run(async () =>
        {
            try { await RestoreNextAsync(next, undo); }
            catch (Exception e) when (e is not OutOfMemoryException) { Fail("restore a session", e); }
        });
    }

    async Task RestoreNextAsync(RestoreOwed w, Undo undo)
    {
        var entry = w.Entry;
        var result = await ResumeOneAsync(new ResumeAsk(entry.Id, entry.Agent, LaunchOf(entry), RestorePrincipal));
        RestoreNotice? ready;
        lock (gate)
        {
            resuming = false;
            inFlight.Remove(entry.Id);
            // Seen ready already, while this answer was on its way.
            var late = readyLate.Remove(entry.Id);
            if (stopped) { return; }
            var now = time.GetUtcNow();
            switch (result.Outcome is LaunchOutcome.Unconfirmed && late ? LaunchOutcome.Started : result.Outcome)
            {
                case LaunchOutcome.Started:
                    // Seen ready: restored, and only now shown so, with Continue.
                    restoredBy[entry.Id] = Loss(entry);
                    // Unless it has already registered and died: then it is judged, and if it died
                    // offered as a loop, not counted restored.
                    if (!diedDuring.Remove(entry.Id))
                    {
                        restoredThisRun.Add(entry.Id);
                        RevealLocked(entry.Id);
                    }
                    log($"restore: session {Short(entry.Id)} restored, idle");
                    break;
                case LaunchOutcome.Unconfirmed:
                    // Started, but its window not seen ready (a folder-trust question for a person,
                    // or a window not recognised): never counted restored nor shown with Continue,
                    // since a reply's Enter there would answer the question (replies are held:
                    // LaunchService.HeldForReply). Offered while it has not registered; stamped,
                    // and not tried again. Seen ready later, while still followed: counted and shown
                    // then (SeenReadyLate).
                    if (!diedDuring.Remove(entry.Id)) { unconfirmed[entry.Id] = new Unconfirmed(restoredAt.GetValueOrDefault(entry.Id), Loss(entry), Queue: true); }
                    log($"restore: session {Short(entry.Id)} started, not seen ready ({result.Reason}); not counted restored");
                    break;
                case LaunchOutcome.Busy:
                    // Waiting on a person at the machine (folder trust), or on discovery: nothing
                    // started, so no stamp, and tried again.
                    UndoLocked(undo);
                    retryAt = now + BusyRetry;
                    log($"restore: session {Short(entry.Id)} waits ({result.Reason})");
                    break;
                case LaunchOutcome.Refused:
                    // Nothing started.
                    UndoLocked(undo);
                    tried.Add(entry.Id);
                    log($"restore: session {Short(entry.Id)} could not be restored ({result.Reason}); it stays offered to resume from Launch");
                    break;
                default:
                    // Failed: it may have started, registered and gone (a window that closed before it
                    // was ready), its owed entry taken by its registration. Offered again unless it
                    // is running, as one that died straight after its restore: a loop, kept stamped.
                    tried.Add(entry.Id);
                    if (!owed.Any(x => x.Entry.Id == entry.Id) && !(live.TryGetValue(entry.Id, out var alive) && alive.Gone is null))
                    {
                        owed.Add(w with { Decided = true, At = now, Restored = restoredAt.GetValueOrDefault(entry.Id, now), Loop = true });
                    }
                    log($"restore: session {Short(entry.Id)} could not be restored ({result.Reason}); it stays offered to resume from Launch");
                    break;
            }
            BuildNoticeLocked();
            ready = AdvanceLocked(now);
            ScheduleWriteLocked(Coalesce);
            ArmLocked(now);
            PumpLocked(now);
        }
        if (ready is not null) { Tell(ready); }
    }

    static RestoreSession Lost(RestoreEntry e) => new()
    {
        Id = e.Id,
        Agent = e.Agent,
        Title = e.Title,
        MidTurn = e.Turn == RestoreTurn.Working ? true : null,
        QuestionLost = e.QuestionWaiting ? true : null,
    };

    // Restored sessions that went mid-turn or with a question waiting, for the push to name alongside
    // those offered (at most RestoreProtocol.MaxOffered).
    public IReadOnlyList<RestoreSession> RestoredLosses()
    {
        lock (gate) { return [.. restoredBy.Where(r => r.Value is not null && Counted(r.Key)).Select(r => r.Value!).Take(RestoreProtocol.MaxOffered)]; }
    }

    static RestoreSession? Loss(RestoreEntry e) => e.Turn == RestoreTurn.Working || e.QuestionWaiting ? Lost(e) : null;

    // Restored, and not since offered or being judged again (died while the host watched).
    bool Counted(string id) => !said.Any(w => w.Entry.Id == id) && (restoredThisRun.Contains(id) || !owed.Any(w => w.Entry.Id == id));

    int RestoredLocked() => restoredBy.Keys.Count(Counted);

    // What a stamp replaced, so an attempt that started nothing leaves things as they were, and never
    // takes away an earlier restore's protection.
    sealed record Undo(string Id, DateTimeOffset? At, long? Since, DateTimeOffset? Owed, DateTimeOffset? Live, (SessionRestored Mark, DateTimeOffset Until)? Shown, DateTimeOffset? Boundary, IReadOnlyList<string>? Interrupted);


    // Stamped and marked before the launch.
    Undo StampLocked(string id, DateTimeOffset now, RestoreCause cause, DateTimeOffset went, bool midTurn, bool questionLost)
    {
        var i = owed.FindIndex(x => x.Entry.Id == id);
        var undo = new Undo(id, restoredAt.TryGetValue(id, out var at) ? at : null, liveSince.TryGetValue(id, out var since) ? since : null,
            i >= 0 ? owed[i].Restored : null, live.TryGetValue(id, out var e) ? e.Restored : null, shown.TryGetValue(id, out var was) ? was : null,
            boundaries.TryGetValue(id, out var boundary) ? boundary : null, interruptedAt.GetValueOrDefault(id));
        restoredAt[id] = now;
        if (i >= 0) { owed[i] = owed[i] with { Restored = now }; }
        if (e is not null) { live[id] = e with { Restored = now }; }
        MarkLocked(id, now, cause, went, midTurn, questionLost);
        return undo;
    }

    void UndoLocked(Undo undo)
    {
        var id = undo.Id;
        if (undo.At is { } at) { restoredAt[id] = at; } else { restoredAt.Remove(id); }
        if (undo.Boundary is { } bound) { boundaries[id] = bound; } else { boundaries.Remove(id); }
        if (undo.Interrupted is { } ids) { interruptedAt[id] = ids; } else { interruptedAt.Remove(id); }
        // Registered while the attempt was under way (an earlier attempt's window, or one started at
        // the machine): live now, so its live tracking stays, and its entry carries the stamp put
        // back, never none. Cleared, a crash within ten minutes was restored again (Copilot on #837).
        var registered = live.TryGetValue(id, out var e) && e.Gone is null;
        if (undo.Since is { } since) { liveSince[id] = since; }
        else if (!(registered && restoredAt.ContainsKey(id))) { liveSince.Remove(id); }
        var i = owed.FindIndex(x => x.Entry.Id == id);
        if (i >= 0) { owed[i] = owed[i] with { Restored = undo.Owed }; }
        if (e is not null) { live[id] = e with { Restored = undo.Live ?? (registered && restoredAt.TryGetValue(id, out var back) ? back : (DateTimeOffset?)null) }; }
        if (marks.Remove(id, out var timer)) { timer.Timer.Dispose(); }
        if (shown.Remove(id, out var tried)) { options.Marks?.Revert(id, tried.Mark); }
        if (undo.Shown is { } before)
        {
            shown[id] = before;
            ArmMarkLocked(id, before.Until - time.GetUtcNow());
        }
    }

    // What a session's restore resumes it with: what this host launched it with, else nothing (the
    // machine's defaults, asking permission).
    RestoreLaunch? LaunchOf(RestoreEntry entry) => entry.Launch ?? (launched.TryGetValue(entry.Id, out var l) ? l : null);

    // caller: a request's own token. Cancelled (or the host stopping) while it waits its turn, it
    // starts nothing, and the launcher honours it until the agent is started.
    async Task<LaunchResult> ResumeOneAsync(ResumeAsk ask, CancellationToken caller = default)
    {
        ISessionResumer? path;
        lock (gate) { path = resumer; }
        if (path is null) { return new LaunchResult(LaunchOutcome.Refused, "this host cannot resume sessions"); }
        using var either = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token, caller);
        LaunchResult Withdrawn() => stopping.IsCancellationRequested ? Stopping : Cancelled;
        try { await one.WaitAsync(either.Token); }
        catch (OperationCanceledException) { return Withdrawn(); }
        try
        {
            // Withdrawn while this one waited its turn: it starts nothing.
            if (either.IsCancellationRequested) { return Withdrawn(); }
            return await path.ResumeAsync(ask, either.Token);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return new LaunchResult(LaunchOutcome.Failed, e.Message); }
        finally { one.Release(); }
    }

    static readonly LaunchResult Stopping = new(LaunchOutcome.Refused, "the host is stopping");
    static readonly LaunchResult Cancelled = new(LaunchOutcome.Refused, "the request was cancelled before anything started");

    // #800: a session an agent's expired sign-in stopped, resumed once it is renewed, as a restore is:
    // stamped before its launch too, and never a second attempt at one already running or being
    // restored.
    public async Task<LaunchResult> RestoreAsync(RestoreAsk ask, CancellationToken cancellationToken = default)
    {
        AgentKind agent;
        RestoreLaunch? launch;
        Undo undo;
        TaskCompletionSource done;
        bool hadRequest;
        string? earlierFolder;
        lock (gate)
        {
            if (stopped || stopping.IsCancellationRequested) { return Stopping; }
            if (options.ReadOnly() is { } why) { return new LaunchResult(LaunchOutcome.Refused, why); }
            if (inFlight.Contains(ask.Session) || (live.TryGetValue(ask.Session, out var current) && current.Gone is null))
            {
                return new LaunchResult(LaunchOutcome.Refused, "that session is already running or being restored");
            }
            var known = live.GetValueOrDefault(ask.Session) ?? owed.FirstOrDefault(w => w.Entry.Id == ask.Session)?.Entry;
            agent = known?.Agent ?? AgentKind.Unknown;
            launch = known is null ? launched.GetValueOrDefault(ask.Session) : LaunchOf(known);
            cancellationToken.ThrowIfCancellationRequested();
            // A recorder that never started, or failed, has no record to write the stamp in: only an
            // in-memory one, which a crash loses, and its crash-loop protection with it (Copilot on
            // #837). Nothing is stamped or marked then.
            if (!running || failed)
            {
                return new LaunchResult(LaunchOutcome.Busy, "the host's restore record is not available, so a restore cannot be written down before it starts; try again once the host has restarted");
            }
            var now = time.GetUtcNow();
            undo = StampLocked(ask.Session, now, ask.Cause, ask.Since ?? now, ask.MidTurn, ask.QuestionLost);
            // Written down first, in Stamps if the record holds no entry of it.
            if (!WriteLocked(now))
            {
                UndoLocked(undo);
                return new LaunchResult(LaunchOutcome.Busy, "the host could not write down the restore before starting it; try again");
            }
            inFlight.Add(ask.Session);
            diedDuring.Remove(ask.Session);
            // An earlier request still pending (unconfirmed, or reloaded from stamps) is put back if
            // this one starts nothing: removed, its worktree could be reclaimed while it is pending.
            hadRequest = requested.TryGetValue(ask.Session, out earlierFolder);
            if (!owed.Any(w => w.Entry.Id == ask.Session)) { requested[ask.Session] = known?.Cwd; }
            done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            requests.Add(done.Task);
        }
        try
        {
            var result = await ResumeOneAsync(new ResumeAsk(ask.Session, agent, launch, ask.By), cancellationToken);
            RestoreNotice? ready = null;
            lock (gate)
            {
                inFlight.Remove(ask.Session);
                var late = readyLate.Remove(ask.Session);
                if (!stopped)
                {
                    // Seen ready: restored, and shown so. Unless it has already registered and died: then
                    // it is judged, as a loop. Not seen ready (unconfirmed): stamped, shown once seen ready
                    // later (SeenReadyLate).
                    var died = diedDuring.Remove(ask.Session);
                    var outcome = result.Outcome is LaunchOutcome.Unconfirmed && late ? LaunchOutcome.Started : result.Outcome;
                    if (outcome is LaunchOutcome.Started && !died)
                    {
                        restoredThisRun.Add(ask.Session);
                        RevealLocked(ask.Session);
                    }
                    else if (outcome is LaunchOutcome.Unconfirmed && !died)
                    {
                        unconfirmed[ask.Session] = new Unconfirmed(restoredAt.GetValueOrDefault(ask.Session), null, Queue: false);
                    }
                    else if (outcome is LaunchOutcome.Busy or LaunchOutcome.Refused)
                    {
                        UndoLocked(undo);
                        // Started nothing: no folder of its own to keep, only an earlier request's.
                        if (hadRequest) { requested[ask.Session] = earlierFolder; }
                        else { requested.Remove(ask.Session); }
                        // Seen ready while this one was under way: an earlier unconfirmed attempt's
                        // window, whose stamp and mark are back now. Dropped, that attempt was never
                        // shown restored nor given Continue (Copilot on #837).
                        if (late) { ready = ReadyLateLocked(ask.Session); }
                    }
                    ScheduleWriteLocked(Coalesce);
                    // The start-up queue again, as after its own: a candidate this request held back
                    // (in flight, so not one) may be one again, and a tick meanwhile left only the
                    // offer-expiry timer, which stalled it for days (Copilot on #837).
                    var after = time.GetUtcNow();
                    ArmLocked(after);
                    PumpLocked(after);
                }
            }
            if (ready is not null) { Tell(ready); }
            log($"restore: session {Short(ask.Session)} asked by {ask.By}: {result.Outcome.ToString().ToLowerInvariant()}{(result.Reason is null ? "" : $" ({result.Reason})")}");
            return result;
        }
        finally
        {
            lock (gate) { requests.Remove(done.Task); }
            done.TrySetResult();
        }
    }

    // A launch answered Unconfirmed whose window was seen ready later (LaunchService.SeenReadyLate):
    // counted and shown restored now, with Continue, as if it had been seen ready in time. Only for the
    // attempt that was unconfirmed, still the session's, and while it lives.
    public void SeenReadyLate(string id)
    {
        RestoreNotice? ready;
        lock (gate)
        {
            if (stopped) { return; }
            if (inFlight.Contains(id)) { readyLate.Add(id); return; }
            ready = ReadyLateLocked(id);
        }
        if (ready is not null) { Tell(ready); }
    }

    // The unconfirmed attempt seen ready: counted and shown restored, if it is still the session's.
    // The notice to push, if this was all it waited for.
    RestoreNotice? ReadyLateLocked(string id)
    {
        if (!unconfirmed.Remove(id, out var u)) { return null; }
        // Another attempt's stamp is not this one's. No stamp at all is: it is taken away once the
        // session has lived LoopWithin, and a window at folder trust is followed for longer.
        if ((restoredAt.TryGetValue(id, out var stamp) && stamp != u.Stamp) || (live.TryGetValue(id, out var e) && e.Gone is not null)) { return null; }
        if (u.Queue) { restoredBy[id] = u.Loss; }
        restoredThisRun.Add(id);
        RevealLocked(id);
        BuildNoticeLocked();
        // The push, if this was all it waited for: a session that registered before its launch
        // answered took its owed entry and the judging with it, so nothing else would send it.
        var ready = AdvanceLocked(time.GetUtcNow());
        ScheduleWriteLocked(Coalesce);
        log($"restore: session {Short(id)} seen ready after all; restored, idle");
        return ready;
    }
    // The restored mark, for this host run and at most ShownFor; Continue's words only for one that
    // went mid-turn. Held (the boundary, no badge) from before the launch, and shown once the launch
    // has seen the window ready (RevealLocked): never on a window that may be asking whether to trust
    // its folder.
    void MarkLocked(string id, DateTimeOffset now, RestoreCause cause, DateTimeOffset went, bool midTurn, bool questionLost)
    {
        var mark = new SessionRestored
        {
            At = now,
            Cause = cause,
            MidTurn = midTurn ? true : null,
            QuestionLost = questionLost ? true : null,
            Continue = midTurn ? ContinueText(cause, went, options.Zone ?? TimeZoneInfo.Local) : null,
        };
        shown[id] = (mark, now + RestoreProtocol.ShownFor);
        boundaries[id] = now;
        // A new boundary: what it sets aside is known once the source has read with it.
        interruptedAt.Remove(id);
        options.Marks?.Hold(id, mark);
        ArmMarkLocked(id, RestoreProtocol.ShownFor);
    }

    void RevealLocked(string id)
    {
        if (shown.TryGetValue(id, out var s)) { options.Marks?.Mark(id, s.Mark); }
    }

    void ArmMarkLocked(string id, TimeSpan due)
    {
        if (marks.Remove(id, out var earlier)) { earlier.Timer.Dispose(); }
        var gen = ++markGen;
        marks[id] = (time.CreateTimer(_ => Guarded(() => Unmark(id, gen)), null, Wait(due), Timeout.InfiniteTimeSpan), gen);
    }

    // The badge goes; the source keeps the restore's boundary (what the interrupted run left is not
    // shown again). Under the lock, so a mark shown meanwhile is never the one taken away.
    void Unmark(string id, long gen)
    {
        lock (gate)
        {
            if (stopped || !marks.TryGetValue(id, out var m) || m.Gen != gen) { return; }
            marks.Remove(id);
            m.Timer.Dispose();
            shown.Remove(id);
            options.Marks?.Mark(id, null);
        }
    }

    // The reply a client offers to send for a session that went mid-turn, word for word the same on
    // every client: the host sends nothing itself. The time is the machine's own.
    public static string ContinueText(RestoreCause cause, DateTimeOffset went, TimeZoneInfo zone)
    {
        var what = cause switch
        {
            RestoreCause.Reboot => "a reboot",
            RestoreCause.Restart => "a host restart",
            _ => "a host crash",
        };
        var at = TimeZoneInfo.ConvertTime(went, zone);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"You were interrupted by {what} at {at:HH:mm}; carry on where you left off");
    }

    static string Short(string id) => id.Length > 8 ? id[..8] : id;

    void Tell(RestoreNotice ready)
    {
        log($"restore: {Describe(ready)}");
        try { NoticeReady?.Invoke(ready); }
        catch (Exception e) when (e is not OutOfMemoryException) { log($"restore: could not send the notice: {e.Message}"); }
    }

    static string Describe(RestoreNotice n)
    {
        var why = n.Why switch
        {
            RestoreWhy.ReadOnly => "the machine is read-only",
            RestoreWhy.Off => "sessions.restoreOnStart is off, so they are only said",
            _ => "to resume from Launch",
        };
        var held = n.Waiting > 0 ? $"; {n.Waiting} still being judged while discovery is held" : "";
        var loops = n.Loops > 0 ? $"; {n.Loops} not restored again, having died within {LoopWithin.TotalMinutes:0} minutes of a restore" : "";
        return $"{n.Restored} session(s) restored, idle, after the {(n.Cause == RestoreCause.Reboot ? "reboot" : "host stopped")}; {n.Offered.Count + (n.OfferedMore ?? 0)} offered ({why}){loops}{held}";
    }

    // Judges what can be judged now, and the notice from it. Returns the notice to push, once.
    RestoreNotice? AdvanceLocked(DateTimeOffset now)
    {
        AheadLocked(now);
        var held = source.Held?.Agents ?? [];
        for (var i = owed.Count - 1; i >= 0; i--)
        {
            var w = owed[i];
            if (live.TryGetValue(w.Entry.Id, out var e) && e.Gone is null) { owed.RemoveAt(i); continue; }
            // The source as it is now, not only as its events have told us: a hold that has just
            // lifted clears before the sessions it found are dispatched here. Recorded live at once,
            // so a write before its event arrives still keeps it.
            if (!w.Decided && source.Get(w.Entry.Id) is { } current && From(current, now) is { } adopted)
            {
                CarryBoundaryLocked(w.Entry);
                live[adopted.Id] = adopted;
                owed.RemoveAt(i);
                continue;
            }
            if (w.Decided && now - w.At >= OwedFor) { owed.RemoveAt(i); continue; }
            if (w.Decided) { continue; }
            var settled = options.EndsSettled?.Invoke(w.Entry.Agent) ?? true;
            // A Claude or Codex ending is read while discovery is held too, and kept: the intake
            // prunes an ended registration once its process has gone, which may be before the hold
            // lifts, and its absence then would read as a death. Newer evidence goes first: resumed
            // since, its registration says it has not ended, and what was kept goes, so the intake
            // keeps its endings again (JudgingEndings) and it is judged by what it says then
            // (Copilot on #813).
            if (held.Contains(w.Entry.Agent) && settled && w.Cause != RestoreCause.Reboot && w.Entry.Agent is AgentKind.Claude or AgentKind.Codex)
            {
                var ended = options.Ends.Ended(w.Entry.Agent, w.Entry.Id);
                if (ended == true && w.EndedSeen is null) { owed[i] = w = w with { EndedSeen = true }; }
                else if (ended == false && w.EndedSeen is not null) { owed[i] = w = w with { EndedSeen = null }; }
            }
            var seen = w.EndedSeen;
            var entry = w.Entry;
            // What its agent records now, and only once that is gone, the ending kept.
            var verdict = RestoreJudge.Judge(entry, w.Cause, running: false, held: held.Contains(entry.Agent), () => options.Ends.Ended(entry.Agent, entry.Id) ?? (seen == true ? true : null),
                endsSettled: settled);
            // Restored, and it went before it had stayed live LoopWithin (its stamp is taken away
            // only then): not restored again, only offered.
            var loop = entry.Restored is not null ? true : (bool?)null;
            switch (verdict)
            {
                case RestoreVerdict.Ended:
                case RestoreVerdict.Running:
                    owed.RemoveAt(i);
                    break;
                case RestoreVerdict.Died when Keeps:
                    // Offered from now: a session held undecided for days still gets its full week.
                    owed[i] = w with { Decided = true, At = now, Loop = loop };
                    break;
                case RestoreVerdict.Died:
                    owed.RemoveAt(i);
                    said.Add(w with { Decided = true, At = now, Loop = loop });
                    break;
                case RestoreVerdict.Uncertain when Keeps:
                    // Kept for its folder alone, as long as an offer would be: never offered or pushed.
                    owed[i] = w with { Decided = true, At = now, Uncertain = true };
                    log($"restore: session {(entry.Id.Length > 8 ? entry.Id[..8] : entry.Id)} went while the host was away and its registration is gone, so whether it ended is unknown; not offered, its folder kept for {OwedFor.TotalDays:0} days");
                    break;
                case RestoreVerdict.Uncertain:
                    owed.RemoveAt(i);
                    break;
            }
        }
        BuildNoticeLocked();
        if (told || notice is null) { return null; }
        var waiting = owed.Count(w => !w.Decided);
        // Until every one is judged and the queue has restored what it can: one push, saying it all.
        if ((waiting > 0 || RestoringLocked()) && time.GetElapsedTime(startedStamp) < NoticeAfter) { return null; }
        // The push says only what this start found: an offer an earlier one pushed is in hello alone.
        var found = Offered().Where(w => judgedNow.Contains(w.Entry.Id)).ToList();
        if (found.Count == 0 && RestoredLocked() == 0) { return null; }
        told = true;
        return NoticeOf(found, waiting, found);
    }

    // What is offered: what died and was not restored by this run. A session restored by an earlier
    // run whose session never came is offered again.
    List<RestoreOwed> Offered() =>
        [.. owed.Where(w => Offered(w) && !restoredThisRun.Contains(w.Entry.Id)).Concat(said).OrderByDescending(w => w.Entry.Seen)];

    void BuildNoticeLocked()
    {
        var offered = Offered();
        var waiting = owed.Count(w => !w.Decided);
        if (offered.Count == 0 && waiting == 0 && RestoredLocked() == 0)
        {
            notice = null;
            return;
        }
        // Why and when, of what this run found if it found anything, else of what it carried over.
        var all = owed.Concat(said).ToList();
        var fresh = all.Where(w => judgedNow.Contains(w.Entry.Id)).ToList();
        notice = NoticeOf(offered, waiting, fresh.Count > 0 ? fresh : all);
    }

    RestoreNotice NoticeOf(List<RestoreOwed> offered, int waiting, List<RestoreOwed> about)
    {
        // An in-run death is no cause of this start's: the notice keeps the reboot or crash it found.
        var latest = about.Where(w => !inRun.Contains(w.Entry.Id)).MaxBy(w => w.At);
        // Within its budget in hello and the push, whatever the titles hold (RestoreProtocol.Fit).
        return RestoreProtocol.Fit(new RestoreNotice
        {
            At = started,
            Cause = latest?.Cause ?? startCause,
            Since = latest is null ? startSince : latest.Since,
            Restored = RestoredLocked(),
            Loops = offered.Count(w => w.Loop == true),
            Waiting = waiting,
            Why = !Keeps ? RestoreWhy.Off : options.ReadOnly() is not null ? RestoreWhy.ReadOnly : RestoreWhy.Pending,
            Offered = [.. offered.Take(RestoreProtocol.MaxOffered).Select(w => new RestoreSession
            {
                Id = w.Entry.Id,
                Agent = w.Entry.Agent,
                Title = w.Entry.Title,
                MidTurn = w.Entry.Turn == RestoreTurn.Working ? true : null,
                QuestionLost = w.Entry.QuestionWaiting ? true : null,
            })],
            OfferedMore = offered.Count > RestoreProtocol.MaxOffered ? offered.Count - RestoreProtocol.MaxOffered : null,
        });
    }

    // Judged to have died, and so offered: not one kept only for its folder (RestoreOwed.Uncertain).
    static bool Offered(RestoreOwed w) => w.Decided && w.Uncertain != true;

    string Signature() => string.Join(";", owed.Select(w => $"{w.Entry.Id}:{w.Decided}:{w.EndedSeen}"));

    RestoreEntry? From(Session s, DateTimeOffset now)
    {
        if (s.Agent == AgentKind.Unknown) { return null; }
        launched.TryGetValue(s.Id, out var launch);
        return new RestoreEntry
        {
            Id = s.Id,
            Agent = s.Agent,
            // Within what a read accepts (RestoreFiles.Bounded), so a record this host wrote is never
            // set aside for its own fields.
            Title = Limits.KeepStart(s.Title, Limits.Title),
            Cwd = s.Cwd,
            Model = Limits.KeepStart(s.Model, LaunchProtocol.MaxName),
            Turn = s.Status switch
            {
                SessionStatus.Working => RestoreTurn.Working,
                SessionStatus.Waiting => RestoreTurn.Waiting,
                _ => RestoreTurn.Idle,
            },
            QuestionWaiting = s.Question is not null || s.WaitingOn is not null,
            Launch = launch,
            Seen = now,
            Restored = restoredAt.TryGetValue(s.Id, out var restored) ? restored : null,
        };
    }

    // The same as written: a change only of activity (a response streaming) is not one the record
    // keeps, so it is not written for it.
    static bool Same(RestoreEntry a, RestoreEntry b) => a == (b with { Seen = a.Seen, Gone = a.Gone });

    // A write already due sooner stands; one due later (a gone session's GoneKept) is brought
    // forward, so a change is written within Coalesce whatever else is waiting.
    void ScheduleWriteLocked(TimeSpan after)
    {
        if (stopped) { return; }
        after = Wait(after);
        if (writeTimer is not null)
        {
            if (writeAfter - time.GetElapsedTime(writeArmed) <= after) { return; }
            writeArmed = time.GetTimestamp();
            writeAfter = after;
            writeTimer.Change(after, Timeout.InfiniteTimeSpan);
            return;
        }
        writeArmed = time.GetTimestamp();
        writeAfter = after;
        writeTimer = time.CreateTimer(_ => Guarded(() =>
        {
            lock (gate)
            {
                writeTimer?.Dispose();
                writeTimer = null;
                if (!stopped) { WriteLocked(time.GetUtcNow()); }
            }
        }), null, after, Timeout.InfiniteTimeSpan);
    }

    // Whether it was written.
    bool WriteLocked(DateTimeOffset now, DateTimeOffset? stoppedAt = null)
    {
        AheadLocked(now);
        DateTimeOffset? next = null;
        void Next(DateTimeOffset at) { if (next is null || at < next) { next = at; } }
        foreach (var (id, e) in live.ToList())
        {
            if (e.Gone is not { } gone) { continue; }
            if (now - gone >= GoneKept)
            {
                live.Remove(id);
                launched.Remove(id);
                // A stamp newer than the death is a later attempt's (this session restored on
                // request): kept, in Stamps, for that attempt.
                if (!(restoredAt.TryGetValue(id, out var stamp) && (stamp > gone || inFlight.Contains(id))))
                {
                    restoredAt.Remove(id);
                    liveSince.Remove(id);
                }
            }
            else { Next(gone + GoneKept); }
        }
        // A restored session seen live for LoopWithin is no longer one that died soon after its
        // restore. One not seen live yet (waiting at folder trust) keeps its stamp, however long
        // that takes; one that went keeps it for as long as it is kept at all.
        var held = source.Held?.Agents ?? [];
        foreach (var id in restoredAt.Keys.ToList())
        {
            if (live.TryGetValue(id, out var e) && e.Gone is null && liveSince.TryGetValue(id, out var since))
            {
                var lived = time.GetElapsedTime(since);
                if (lived < LoopWithin) { Next(now + (LoopWithin - lived)); continue; }
                // While its agent's discovery is held the source keeps the sessions it listed, so
                // still listed is not still live: one that died in the hold, its stamp gone, would
                // not read as a loop once the hold lifts and it is found gone.
                if (held.Contains(e.Agent)) { Next(now + HeldRetry); continue; }
                // Nor while the source no longer lists it: a hold that has just lifted clears before
                // the removals it found are dispatched here. Its stamp cleared in that gap, the removal
                // was neither a loop nor its worktree kept (Copilot on #837).
                if (source.Get(id) is null) { Next(now + HeldRetry); continue; }
                restoredAt.Remove(id);
                liveSince.Remove(id);
                if (e.Restored is not null) { live[id] = e with { Restored = null }; }
                continue;
            }
            // Carried by no entry (a restore on request of a session the record had let go): kept,
            // in Stamps, until it registers, for a day at most if it never does; a write is due then,
            // so an otherwise idle host lets it go too.
            if (!live.ContainsKey(id) && !owed.Any(w => w.Entry.Id == id) && !inFlight.Contains(id))
            {
                if (now - restoredAt[id] < RestoreProtocol.ShownFor) { Next(restoredAt[id] + RestoreProtocol.ShownFor); continue; }
                restoredAt.Remove(id);
                liveSince.Remove(id);
                requested.Remove(id);
                unconfirmed.Remove(id);
            }
        }
        // A boundary goes with its session: once the record keeps neither it (live or owed) nor a stamp
        // of it. Owed too: a session held undecided at a start, its stamp long gone, lost its boundary
        // at the first write, and was adopted with none once the hold lifted (Engineering, cr-837g).
        var owedIds = owed.Select(w => w.Entry.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in boundaries.Keys.Where(id => !live.ContainsKey(id) && !owedIds.Contains(id) && !restoredAt.ContainsKey(id)).ToList()) { boundaries.Remove(id); }
        foreach (var id in interruptedAt.Keys.Where(id => !boundaries.ContainsKey(id)).ToList()) { interruptedAt.Remove(id); }
        // What the source has set aside on each live restored session, as it read it with the boundary.
        foreach (var id in live.Keys.Where(boundaries.ContainsKey))
        {
            if (options.Marks?.Interrupted(id) is { } found) { interruptedAt[id] = found; }
        }
        var file = new RestoreFile
        {
            Boot = boot,
            Logon = logon,
            Instance = options.Instance,
            Written = now,
            Stopped = stoppedAt,
            Sessions = [.. live.Values.OrderBy(e => e.Id, StringComparer.Ordinal).Select(e => boundaries.TryGetValue(e.Id, out var boundaryAt) ? e with { Boundary = boundaryAt, Interrupted = Kept(e.Id) } : e)],
            Owed = [.. owed.Select(w => boundaries.TryGetValue(w.Entry.Id, out var owedBoundary) ? w with { Entry = w.Entry with { Boundary = owedBoundary, Interrupted = Kept(w.Entry.Id) } } : w)],
            Stamps = restoredAt.Where(s => !live.ContainsKey(s.Key) && !owed.Any(w => w.Entry.Id == s.Key)).ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal),
        };
        try
        {
            RestoreFiles.Write(options.Path, file);
            if (writeProblem is not null) { log("restore: the record of live sessions is written again"); }
            writeProblem = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (writeProblem != e.Message) { log($"restore: could not write the record of live sessions ({e.Message}); trying again"); }
            writeProblem = e.Message;
            ScheduleWriteLocked(TimeSpan.FromSeconds(5));
            return false;
        }
        // Written: a crash from here leaves this start's judgement, so the logon can be marked seen.
        if (logonPending)
        {
            logonPending = false;
            options.Logon?.Commit();
        }
        if (next is { } at) { ScheduleWriteLocked(at - now > Coalesce ? at - now : Coalesce); }
        return true;
    }

    // A clean stop: the record says so, so the next run on this boot knows the host was stopped, not
    // lost. The sessions stay in it: they are still live, and the next run adopts them.
    // A restore under way is let finish first (a few seconds at most), so the session it starts is in
    // the last record; one still waiting its turn starts nothing.
    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        // Read after the cancel: the queue starts nothing once it is cancelled (PumpLocked), so the
        // restore under way now is the last one; so are those asked for (RestoreAsync), which are
        // refused from now. Before, a requested restore's session registered after the last write.
        Task under;
        lock (gate)
        {
            if (stopped) { return; }
            under = Task.WhenAll([active, .. requests]);
        }
        await Task.WhenAny(under, Task.Delay(StopWait)).ConfigureAwait(false);
        source.Changed -= OnChanged;
        lock (gate)
        {
            if (stopped) { return; }
            stopped = true;
            writeTimer?.Dispose();
            writeTimer = null;
            judgeTimer?.Dispose();
            judgeTimer = null;
            foreach (var m in marks.Values) { m.Timer.Dispose(); }
            marks.Clear();
            if (running)
            {
                var now = time.GetUtcNow();
                try { WriteLocked(now, stoppedAt: now); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    static readonly TimeSpan StopWait = TimeSpan.FromSeconds(5);
}
