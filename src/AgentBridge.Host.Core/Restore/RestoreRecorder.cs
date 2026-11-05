using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Restore;

// What became of a session an earlier run of the host saw live.
public enum RestoreVerdict { Running, Ended, Died, Waiting }

// What the worktree reclaimer asks (RestoreWorktreeUsage): the folders of the sessions waiting to be
// restored or still being judged, which no reclaim may take; or null when they cannot be known, and
// then every worktree is kept.
public interface IRestorePending
{
    IReadOnlyList<string>? Folders();
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
        return endedNormally() == true ? RestoreVerdict.Ended : RestoreVerdict.Died;
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
    // sessions.restoreOnStart (docs/host-config.md): off, what died is only said, never kept.
    public bool RestoreOnStart { get; init; } = true;
    // Why the machine takes no launches from other devices now (design G5), or null.
    public Func<string?> ReadOnly { get; init; } = () => null;
    public IBootClock? Boot { get; init; }
    // Whether the logon session is new since the host last ran in it; none says it never is.
    public ILogonMarker? Logon { get; init; }
    public TimeProvider? Time { get; init; }
    public Action<string>? Log { get; init; }
}

// The host's record of the sessions it saw live (#780), and what it found at start of those an
// earlier run saw. It starts nothing: what died is offered, by hello's machine.restore and one push,
// and kept from the worktree reclaimer while it waits.
//
// The record is written on every change that matters (a session appears or goes, its turn or its
// question changes), at most every Coalesce, whole and flushed before it replaces the last. A session
// that goes is kept GoneKept more: a machine shutting down ends its agents before it ends the host,
// and the next start must still know they were live.
public sealed class RestoreRecorder : IRestorePending, IAsyncDisposable
{
    public static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan GoneKept = TimeSpan.FromSeconds(60);
    // While anything is still being judged, it is looked at again this often.
    public static readonly TimeSpan JudgeEvery = TimeSpan.FromSeconds(5);
    // A session offered and never resumed is let go this long after it was offered, so its worktree
    // can go too. Never one still being judged: what it is cannot be told, so its worktree is kept.
    public static readonly TimeSpan OwedFor = TimeSpan.FromDays(7);
    // The push waits for every session to be judged, or this long after start, whichever is first.
    public static readonly TimeSpan NoticeAfter = TimeSpan.FromMinutes(10);
    // A time in the record more than this ahead of the clock (one set back: a bad RTC, a VM restored
    // from a snapshot, a year corrected) is read as now. Kept, never let go for it, and never left to
    // put a deadline weeks out.
    public static readonly TimeSpan AheadTolerance = TimeSpan.FromMinutes(2);
    // No timer is set further out than this: a later deadline is looked at again when it fires. The
    // system's timers refuse anything past about 49.7 days, and that refusal stopped the host.
    public static readonly TimeSpan LongestWait = TimeSpan.FromDays(1);
    // What the record keeps of launches whose sessions have not been seen (bounded, never growing).
    const int LaunchesKept = 256;

    readonly ISessionSource source;
    readonly RestoreOptions options;
    readonly TimeProvider time;
    readonly Action<string> log;
    readonly DateTimeOffset boot;
    readonly bool freshLogon;
    // Sessions this run judged from the last run's live ones, as against those it carried over
    // owed: only these are news, and only they make the push.
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
    DateTimeOffset writeDue;
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
    }

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
        // What the sessions still live were launched with, so adopting them keeps it.
        lock (gate)
        {
            // Owed ones too: one still being judged when the host stopped may be found running now.
            // Every one: LaunchesKept bounds launches not yet seen (Launched). Capped here too, a session
            // past it was written again without its settings, which were lost.
            foreach (var e in (previous?.Sessions ?? []).Concat((previous?.Owed ?? []).Select(w => w.Entry)))
            {
                if (e.Launch is { } l) { launched.TryAdd(e.Id, l); }
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
                var cause = RestoreJudge.CauseOf(previous, boot, freshLogon);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var e in previous.Sessions)
                {
                    if (!seen.Add(e.Id)) { continue; }
                    owed.Add(new RestoreOwed(e, cause, previous.Written, now, Decided: false));
                    judgedNow.Add(e.Id);
                }
                // Sessions earlier runs left owed. Those offered already are not news. Those still
                // being judged are, and are judged as of now: a reboot since makes them died. With
                // sessions.restoreOnStart off none is kept.
                foreach (var w in previous.Owed)
                {
                    if (!options.RestoreOnStart || (w.Decided && now - w.At >= OwedFor) || !seen.Add(w.Entry.Id)) { continue; }
                    if (w.Decided) { owed.Add(w); continue; }
                    owed.Add(cause == RestoreCause.Reboot ? w with { Cause = cause } : w);
                    judgedNow.Add(w.Entry.Id);
                }
                log($"restore: the host last ran on {(cause == RestoreCause.Reboot ? "an earlier boot" : "this boot")} ({cause.ToString().ToLowerInvariant()}); {previous.Sessions.Count} session(s) were live then");
            }
            ready = AdvanceLocked(now);
            WriteLocked(now);
            ArmLocked(now);
        }
        if (ready is not null) { Tell(ready); }
    }

    // A session this host launched, with what it was launched with: kept with its entry, so a restore
    // resumes it as it was started (LaunchService.SessionStarted).
    public void Launched(string sessionId, LaunchedWith with)
    {
        var launch = new RestoreLaunch(with.Agent, with.Workspace, with.Worktree, with.Profile, with.Model, with.Effort, with.Context, with.Permissions);
        lock (gate)
        {
            if (stopped) { return; }
            if (launched.Count >= LaunchesKept)
            {
                foreach (var id in launched.Keys.Where(id => !live.ContainsKey(id)).ToList()) { launched.Remove(id); }
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
        }
    }

    // Never before now, never past LongestWait: what fires early only looks again.
    static TimeSpan Wait(TimeSpan due) => due < TimeSpan.Zero ? TimeSpan.Zero : due > LongestWait ? LongestWait : due;

    // While anything is still being judged, every JudgeEvery; then once, when the first offer comes
    // to OwedFor (at most LongestWait ahead, then again), so a host that runs for weeks lets it go
    // (and its worktree) on time.
    void ArmLocked(DateTimeOffset now)
    {
        AheadLocked(now);
        var judging = owed.Any(w => !w.Decided);
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
        return $"{n.Offered.Count + (n.OfferedMore ?? 0)} session(s) went with the {(n.Cause == RestoreCause.Reboot ? "reboot" : "host")} and are offered ({why}){held}";
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
            // lifts, and its absence then would read as a death.
            if (held.Contains(w.Entry.Agent) && settled && w.EndedSeen is null && w.Cause != RestoreCause.Reboot
                && w.Entry.Agent is AgentKind.Claude or AgentKind.Codex && options.Ends.Ended(w.Entry.Agent, w.Entry.Id) == true)
            {
                owed[i] = w = w with { EndedSeen = true };
            }
            var seen = w.EndedSeen;
            var entry = w.Entry;
            var verdict = RestoreJudge.Judge(entry, w.Cause, running: false, held: held.Contains(entry.Agent), () => seen == true || options.Ends.Ended(entry.Agent, entry.Id) == true,
                endsSettled: settled);
            switch (verdict)
            {
                case RestoreVerdict.Ended:
                case RestoreVerdict.Running:
                    owed.RemoveAt(i);
                    break;
                case RestoreVerdict.Died when options.RestoreOnStart:
                    // Offered from now: a session held undecided for days still gets its full week.
                    owed[i] = w with { Decided = true, At = now };
                    break;
                case RestoreVerdict.Died:
                    owed.RemoveAt(i);
                    said.Add(w with { Decided = true, At = now });
                    break;
            }
        }
        BuildNoticeLocked();
        if (told || notice is null) { return null; }
        var waiting = owed.Count(w => !w.Decided);
        if (waiting > 0 && time.GetElapsedTime(startedStamp) < NoticeAfter) { return null; }
        // The push says only what this start found: an offer an earlier one pushed is in hello alone.
        var found = owed.Where(w => w.Decided).Concat(said).Where(w => judgedNow.Contains(w.Entry.Id)).OrderByDescending(w => w.Entry.Seen).ToList();
        if (found.Count == 0) { return null; }
        told = true;
        return NoticeOf(found, waiting, found);
    }

    void BuildNoticeLocked()
    {
        var offered = owed.Where(w => w.Decided).Concat(said).OrderByDescending(w => w.Entry.Seen).ToList();
        var waiting = owed.Count(w => !w.Decided);
        if (offered.Count == 0 && waiting == 0)
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
        var latest = about.MaxBy(w => w.At)!;
        return new RestoreNotice
        {
            At = started,
            Cause = latest.Cause,
            Since = latest.Since,
            Waiting = waiting,
            Why = !options.RestoreOnStart ? RestoreWhy.Off : options.ReadOnly() is not null ? RestoreWhy.ReadOnly : RestoreWhy.Pending,
            Offered = [.. offered.Take(RestoreProtocol.MaxOffered).Select(w => new RestoreSession
            {
                Id = w.Entry.Id,
                Agent = w.Entry.Agent,
                Title = w.Entry.Title,
                MidTurn = w.Entry.Turn == RestoreTurn.Working ? true : null,
                QuestionLost = w.Entry.QuestionWaiting ? true : null,
            })],
            OfferedMore = offered.Count > RestoreProtocol.MaxOffered ? offered.Count - RestoreProtocol.MaxOffered : null,
        };
    }

    string Signature() => string.Join(";", owed.Select(w => $"{w.Entry.Id}:{w.Decided}:{w.EndedSeen}"));

    RestoreEntry? From(Session s, DateTimeOffset now)
    {
        if (s.Agent == AgentKind.Unknown) { return null; }
        launched.TryGetValue(s.Id, out var launch);
        return new RestoreEntry
        {
            Id = s.Id,
            Agent = s.Agent,
            Title = s.Title,
            Cwd = s.Cwd,
            Model = s.Model,
            Turn = s.Status switch
            {
                SessionStatus.Working => RestoreTurn.Working,
                SessionStatus.Waiting => RestoreTurn.Waiting,
                _ => RestoreTurn.Idle,
            },
            QuestionWaiting = s.Question is not null || s.WaitingOn is not null,
            Launch = launch,
            Seen = now,
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
        var due = time.GetUtcNow() + after;
        if (writeTimer is not null)
        {
            if (writeDue <= due) { return; }
            writeDue = due;
            writeTimer.Change(after, Timeout.InfiniteTimeSpan);
            return;
        }
        writeDue = due;
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
        DateTimeOffset? nextGone = null;
        foreach (var (id, e) in live.ToList())
        {
            if (e.Gone is not { } gone) { continue; }
            if (now - gone >= GoneKept)
            {
                live.Remove(id);
                launched.Remove(id);
            }
            else if (nextGone is null || gone + GoneKept < nextGone) { nextGone = gone + GoneKept; }
        }
        var file = new RestoreFile
        {
            Boot = boot,
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
        if (nextGone is { } at) { ScheduleWriteLocked(at - now > Coalesce ? at - now : Coalesce); }
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
