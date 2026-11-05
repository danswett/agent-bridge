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

// Where a session's restored mark is shown (SessionSource): set when it is restored, null to take it
// away. The source clears midTurn itself once a turn starts after the restore.
public interface IRestoreMarks
{
    void Mark(string id, SessionRestored? mark);
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
    // Restored, by when, until each has stayed live LoopWithin.
    readonly Dictionary<string, DateTimeOffset> restoredAt = new(StringComparer.Ordinal);
    // The marks shown on sessions restored in this run, each taken away after ShownFor.
    readonly Dictionary<string, ITimer> marks = new(StringComparer.Ordinal);
    // One resume at a time, the start-up queue's and RestoreAsync's alike.
    readonly SemaphoreSlim one = new(1, 1);
    // Tried this run and refused or failed: offered, not tried again.
    readonly HashSet<string> tried = new(StringComparer.Ordinal);
    // Restored by this run: not offered, whatever the clock says of their stamps.
    readonly HashSet<string> restoredThisRun = new(StringComparer.Ordinal);
    ISessionResumer? resumer;
    bool resuming;
    DateTimeOffset retryAt;
    int restoredNow;
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
            if (previous is not null)
            {
                // A marker missing in the logon the record was written in is not a new logon: it
                // was removed, or never made (sec-813).
                var sameLogon = logon is not null && previous.Logon == logon;
                var cause = RestoreJudge.CauseOf(previous, boot, freshLogon && !sameLogon);
                (startCause, startSince) = (cause, previous.Written);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var e in previous.Sessions)
                {
                    if (!seen.Add(e.Id)) { continue; }
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
                    if (w.Decided) { owed.Add(w); continue; }
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
                // Resumed since (from Launch): live again, so no longer owed.
                var resumed = owed.RemoveAll(w => w.Entry.Id == s.Id) + said.RemoveAll(w => w.Entry.Id == s.Id) > 0;
                if (resumed) { BuildNoticeLocked(); }
                goneBeforeStart.Remove(s.Id);
                if (From(s, now) is not { } e) { return; }
                if (live.TryGetValue(e.Id, out var was) && was.Gone is null && Same(was, e) && !resumed) { return; }
                live[e.Id] = e;
                if (running) { ScheduleWriteLocked(Coalesce); }
            }
            else if (change.Kind == SessionChangeKind.Removed && !running && !live.ContainsKey(change.Id)) { goneBeforeStart.Add(change.Id); }
            else if (change.Kind == SessionChangeKind.Removed && live.TryGetValue(change.Id, out var gone) && gone.Gone is null)
            {
                live[change.Id] = gone with { Gone = now };
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
        owed.Where(w => Offered(w) && w.Loop != true && w.Restored is null && judgedNow.Contains(w.Entry.Id) && !tried.Contains(w.Entry.Id));

    // Whether the queue may start one now: a resume path, restoreOnStart on, and a machine that takes
    // launches (G5, launch.enabled, a config that could be read). Read-only, they wait, offered.
    bool CanRestoreLocked() => resumer is not null && options.RestoreOnStart && options.ReadOnly() is null;

    // Still restoring: one under way, or one that could start now.
    bool RestoringLocked() => resuming || (CanRestoreLocked() && Candidates().Any());

    // Starts the next restore, newest first, if one may start now. Only one is ever under way.
    void PumpLocked(DateTimeOffset now)
    {
        if (stopped || !running || resuming || now < retryAt || !CanRestoreLocked()) { return; }
        if (Candidates().OrderByDescending(w => w.Entry.Seen).FirstOrDefault() is not { } next) { return; }
        resuming = true;
        // Off this thread: the launcher takes locks of its own, and calls back into this one.
        _ = Task.Run(async () =>
        {
            try { await RestoreNextAsync(next); }
            catch (Exception e) when (e is not OutOfMemoryException) { Fail("restore a session", e); }
        });
    }

    async Task RestoreNextAsync(RestoreOwed w)
    {
        var entry = w.Entry;
        var result = await ResumeOneAsync(new ResumeAsk(entry.Id, entry.Agent, LaunchOf(entry), RestorePrincipal));
        RestoreNotice? ready;
        lock (gate)
        {
            resuming = false;
            if (stopped) { return; }
            var now = time.GetUtcNow();
            var i = owed.FindIndex(x => x.Entry.Id == entry.Id);
            switch (result.Outcome)
            {
                case LaunchOutcome.Started or LaunchOutcome.Unconfirmed:
                    restoredNow++;
                    if (i >= 0) { owed[i] = owed[i] with { Restored = now }; }
                    restoredThisRun.Add(entry.Id);
                    MarkLocked(entry.Id, now, w.Cause, w.Since ?? w.At, entry.Turn == RestoreTurn.Working, entry.QuestionWaiting);
                    log($"restore: session {Short(entry.Id)} restored, idle{(result.Outcome == LaunchOutcome.Unconfirmed ? $" (not confirmed: {result.Reason})" : "")}");
                    break;
                case LaunchOutcome.Busy:
                    // Waiting on a person at the machine (folder trust), or on discovery: tried again.
                    retryAt = now + BusyRetry;
                    log($"restore: session {Short(entry.Id)} waits ({result.Reason})");
                    break;
                default:
                    tried.Add(entry.Id);
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

    // What a session's restore resumes it with: what this host launched it with, else nothing (the
    // machine's defaults, asking permission).
    RestoreLaunch? LaunchOf(RestoreEntry entry) => entry.Launch ?? (launched.TryGetValue(entry.Id, out var l) ? l : null);

    async Task<LaunchResult> ResumeOneAsync(ResumeAsk ask)
    {
        ISessionResumer? path;
        lock (gate) { path = resumer; }
        if (path is null) { return new LaunchResult(LaunchOutcome.Refused, "this host cannot resume sessions"); }
        await one.WaitAsync();
        try { return await path.ResumeAsync(ask); }
        catch (Exception e) when (e is not OutOfMemoryException) { return new LaunchResult(LaunchOutcome.Failed, e.Message); }
        finally { one.Release(); }
    }

    // #800: a session an agent's expired sign-in stopped, resumed once it is renewed, as a restore is.
    public async Task<LaunchResult> RestoreAsync(RestoreAsk ask, CancellationToken cancellationToken = default)
    {
        AgentKind agent;
        RestoreLaunch? launch;
        lock (gate)
        {
            if (stopped) { return new LaunchResult(LaunchOutcome.Refused, "the host is stopping"); }
            if (options.ReadOnly() is { } why) { return new LaunchResult(LaunchOutcome.Refused, why); }
            var known = live.GetValueOrDefault(ask.Session) ?? owed.FirstOrDefault(w => w.Entry.Id == ask.Session)?.Entry;
            agent = known?.Agent ?? AgentKind.Unknown;
            launch = known is null ? launched.GetValueOrDefault(ask.Session) : LaunchOf(known);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var result = await ResumeOneAsync(new ResumeAsk(ask.Session, agent, launch, ask.By));
        if (result.Outcome is LaunchOutcome.Started or LaunchOutcome.Unconfirmed)
        {
            lock (gate)
            {
                if (stopped) { return result; }
                var now = time.GetUtcNow();
                var i = owed.FindIndex(x => x.Entry.Id == ask.Session);
                if (i >= 0) { owed[i] = owed[i] with { Restored = now }; }
                restoredThisRun.Add(ask.Session);
                MarkLocked(ask.Session, now, ask.Cause, ask.Since ?? now, ask.MidTurn, ask.QuestionLost);
                ScheduleWriteLocked(Coalesce);
            }
        }
        log($"restore: session {Short(ask.Session)} asked by {ask.By}: {result.Outcome.ToString().ToLowerInvariant()}{(result.Reason is null ? "" : $" ({result.Reason})")}");
        return result;
    }

    // The restored mark, shown on the session for this host run and at most ShownFor; Continue's words
    // only for one that went mid-turn. Stamped too, so one that dies within LoopWithin is not restored
    // again.
    void MarkLocked(string id, DateTimeOffset now, RestoreCause cause, DateTimeOffset went, bool midTurn, bool questionLost)
    {
        restoredAt[id] = now;
        var mark = new SessionRestored
        {
            At = now,
            Cause = cause,
            MidTurn = midTurn ? true : null,
            QuestionLost = questionLost ? true : null,
            Continue = midTurn ? ContinueText(cause, went, options.Zone ?? TimeZoneInfo.Local) : null,
        };
        options.Marks?.Mark(id, mark);
        if (marks.Remove(id, out var earlier)) { earlier.Dispose(); }
        marks[id] = time.CreateTimer(_ => Guarded(() => Unmark(id)), null, RestoreProtocol.ShownFor, Timeout.InfiniteTimeSpan);
    }

    void Unmark(string id)
    {
        lock (gate)
        {
            if (stopped || !marks.Remove(id, out var timer)) { return; }
            timer.Dispose();
        }
        options.Marks?.Mark(id, null);
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
        if (found.Count == 0 && restoredNow == 0) { return null; }
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
        if (offered.Count == 0 && waiting == 0 && restoredNow == 0)
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
        var latest = about.MaxBy(w => w.At);
        // Within its budget in hello and the push, whatever the titles hold (RestoreProtocol.Fit).
        return RestoreProtocol.Fit(new RestoreNotice
        {
            At = started,
            Cause = latest?.Cause ?? startCause,
            Since = latest is null ? startSince : latest.Since,
            Restored = restoredNow,
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

    void WriteLocked(DateTimeOffset now, DateTimeOffset? stoppedAt = null)
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
                restoredAt.Remove(id);
            }
            else { Next(gone + GoneKept); }
        }
        // A restored session that has stayed live LoopWithin is no longer one that died soon after
        // its restore; one that went sooner keeps its stamp for as long as it is kept at all.
        foreach (var (id, r) in restoredAt.ToList())
        {
            if (now - r < LoopWithin) { Next(r + LoopWithin); continue; }
            if (live.TryGetValue(id, out var e) && e.Gone is { } went && went - r < LoopWithin) { continue; }
            restoredAt.Remove(id);
            if (live.TryGetValue(id, out var still) && still.Restored is not null) { live[id] = still with { Restored = null }; }
        }
        var file = new RestoreFile
        {
            Boot = boot,
            Logon = logon,
            Instance = options.Instance,
            Written = now,
            Stopped = stoppedAt,
            Sessions = [.. live.Values.OrderBy(e => e.Id, StringComparer.Ordinal)],
            Owed = [.. owed],
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
            return;
        }
        // Written: a crash from here leaves this start's judgement, so the logon can be marked seen.
        if (logonPending)
        {
            logonPending = false;
            options.Logon?.Commit();
        }
        if (next is { } at) { ScheduleWriteLocked(at - now > Coalesce ? at - now : Coalesce); }
    }

    // A clean stop: the record says so, so the next run on this boot knows the host was stopped, not
    // lost. The sessions stay in it: they are still live, and the next run adopts them.
    public ValueTask DisposeAsync()
    {
        source.Changed -= OnChanged;
        lock (gate)
        {
            if (stopped) { return ValueTask.CompletedTask; }
            stopped = true;
            writeTimer?.Dispose();
            writeTimer = null;
            judgeTimer?.Dispose();
            judgeTimer = null;
            foreach (var t in marks.Values) { t.Dispose(); }
            marks.Clear();
            if (running)
            {
                var now = time.GetUtcNow();
                try { WriteLocked(now, stoppedAt: now); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        return ValueTask.CompletedTask;
    }
}
