using System.Threading.Channels;
using AgentBridge.Hooks;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Intake;

public sealed class HookIntakeOptions
{
    // The installation's runtime folder: agent-ha-bridge's <bridgeHome>\runtime on a machine
    // migrated from it, where both its hook and this product's spool and look for the
    // heartbeat (SpoolContract).
    public required string RuntimeRoot { get; init; }

    // ~/.claude, for a Claude transcript the hook event did not name.
    public required string ClaudeHome { get; init; }

    // Design G5: why agent-ha-bridge's daemon counts as running for this user, or null. While
    // it does, the intake touches nothing: no heartbeat, and no spool file is read, consumed or
    // deleted. The daemon owns them.
    public Func<string?> AgentHaBridgeActive { get; init; } = () => null;

    // The daemon's sweep: a watcher only shortens the wait, since watchers drop events.
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(2);

    // How often registrations whose session is over are deleted, as the daemon's reconcile
    // does (Get-ClaudeSessionRegistrations, Get-CodexSessionRegistrations).
    public TimeSpan PruneInterval { get; init; } = TimeSpan.FromSeconds(60);

    // A Claude registration whose process has gone is deleted once it is this old; a Codex one
    // with no process recorded likewise. ($script:ClaudeSessionStaleMinutes, CodexSessionStaleMinutes.)
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromMinutes(240);

    public bool UseFileSystemWatcher { get; init; } = true;

    // Whether an event the size hooks spool would fit in the runtime folder: null when it does,
    // else why not. The heartbeat alone proves nothing on a full disk (its few bytes live in the
    // file's own MFT record), while events, and the log lines that would say they were lost, fail.
    public Func<string, string?> SpaceProbe { get; init; } = HookIntake.ProbeSpace;
}

// Host.Intake (design §6.1): on a machine migrated from agent-ha-bridge, the host takes over
// what agent-ha-bridge's daemon did with the hook spool (hooks/daemon-hookspool.ps1).
//
// It keeps the heartbeat the hooks check, so they hand events to it in tens of milliseconds;
// takes the spool's files in name order (the order they happened), a watcher noticing a new one
// at once and a sweep every two seconds covering anything a watcher misses; records what each
// means locally (HookHandlers); deletes it; and tells subscribers. An event that fails is tried
// once more on the next pass, then dropped and logged: a bad file must not be retried forever.
//
// Only constructed for a migrated machine (IntakeState). Even then it stands back entirely
// while agent-ha-bridge's daemon runs (G5): the daemon keeps its own heartbeat and consumes the
// spool itself, and two consumers would handle each event twice. Every read is of a copy in
// memory, through a handle that lets a concurrent deleter delete.
public sealed class HookIntake : IAsyncDisposable
{
    readonly HookIntakeOptions _options;
    readonly IProcessInspector _processes;
    readonly TimeProvider _time;
    readonly Action<string> _log;
    readonly HookHandlers _handlers;
    readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);
    // Files handled whose delete failed (a reader that would not share it): deleted again, never
    // handled again.
    // Read by the answer path too (ApprovalsUnsettled), so safe to read while a pass changes it.
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _undeleted = new(StringComparer.Ordinal);
    readonly List<Channel<HookNotice>> _subscribers = [];
    readonly Lock _gate = new();
    readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    readonly string _spool;
    readonly string _heartbeat;
    FileSystemWatcher? _watcher;
    CancellationTokenSource? _stop;
    Task? _loop;
    DateTimeOffset _prunedAt;
    string? _paused;
    bool _heartbeatOwned;
    // Whether approvals have been checked against the hooks that ran while nobody took events,
    // since this intake last (re)took the spool. Read by the answer path on its own thread.
    volatile bool _reconciled;
    // Whether hooks may have run unspooled since this intake last held a fresh heartbeat: it has
    // just started, stood back, or let the heartbeat go stale. What they did then is known only
    // from the hook log, whose records are best effort (a full disk loses them too), so every
    // approval asked before the spool is taken again is withdrawn.
    bool _gap = true;
    // The cut-off approvals were last settled to, the highest one asked for since and not yet
    // settled, and the hook log they are read from.
    DateTimeOffset? _settled;
    DateTimeOffset? _pending;
    readonly HookLogWatch _codexLog;
    DateTimeOffset _beatAt;
    // Held while this intake takes the spool, so a second host (another profile migrated from
    // the same installation) never handles the same events.
    FileStream? _claim;

    public const string ClaimFile = "agent-bridge-host-intake.lock";

    public HookIntake(HookIntakeOptions options, IProcessInspector processes, TimeProvider? time = null, Action<string>? log = null)
    {
        _options = options;
        _processes = processes;
        _time = time ?? TimeProvider.System;
        _log = log ?? (_ => { });
        _handlers = new HookHandlers(options.RuntimeRoot, options.ClaudeHome, processes, _time) { Ours = Ours };
        _spool = SpoolContract.SpoolDirectory(options.RuntimeRoot);
        _codexLog = new HookLogWatch(options.RuntimeRoot, "codex", _time);
        _heartbeat = SpoolContract.HeartbeatPath(options.RuntimeRoot);
        _prunedAt = _time.GetUtcNow();
    }

    // Why a Codex approval must not be answered now, or null. Until a pass has settled the
    // markers against the hook log (the first after starting or taking the spool back, or while
    // the log cannot be read), a marker may be one already answered at the terminal, and typing
    // into it would land in whatever the terminal shows now.
    //
    // Asked as an answer is about to be typed (after the answer path's own G5 check), so what waits
    // in the spool is looked at then, not as the last pass left it: an event spooled since (a
    // PreToolUse answered at the terminal by a session still on the Go hook, a new
    // PermissionRequest) is not yet reflected in the marker. A pass running meanwhile can only
    // withdraw or replace the marker, which the answer path reads afresh after this.
    public string? ApprovalsUnsettled
    {
        get
        {
            if (Paused is { } paused) { return $"agent-ha-bridge has the hook spool ({paused}); answer at the machine"; }
            if (_queued || Waiting()) { return "the host is still handling hook events that are waiting; try again shortly"; }
            return _reconciled && !_handlers.WithdrawalsPending ? null
                : "the host has not yet checked Codex approvals against the hooks that ran while nobody took them; answer at the machine or try again shortly";
        }
    }

    // Events wait in the spool: a PermissionRequest handled from a backlog arms its marker before
    // the PreToolUse that answered it at the terminal, further down, has been reached. So no Codex
    // answer while any is queued (one whose deletion failed after it was handled aside).
    volatile bool _queued;

    // A test's way to see each listing of the spool (G5: none once agent-ha-bridge has it).
    internal Action? Listing { get; set; }

    bool Waiting()
    {
        Listing?.Invoke();
        try { return Directory.EnumerateFiles(_spool, "*.json").Any(f => f.EndsWith(".json", StringComparison.Ordinal) && !_undeleted.ContainsKey(Path.GetFileName(f))); }
        catch (DirectoryNotFoundException) when (!File.Exists(_spool)) { return false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    // The answer path's gate for Codex approvals on a profile (AnswerOptions.CodexApprovalsUnsettled),
    // given the registration whose marker would be answered: none where the profile never migrated
    // (runtimeRoot null); every approval refused while its intake.json cannot be read, since no
    // intake then runs to withdraw what an outage left; otherwise the running intake's, refused
    // until it has started. The intake settles only the markers in the runtime it took, so one in
    // another registration root discovery still reads (the default runtime, the legacy temp
    // folder) is never answerable from here: nothing withdraws it once answered at the terminal.
    public static Func<string, string?>? AnswerGate(string? runtimeRoot, string? problem, Func<HookIntake?> running)
    {
        if (problem is not null) { return _ => $"this profile's hook intake is off ({problem}), so Codex approvals left from before cannot be checked; answer at the machine"; }
        if (runtimeRoot is null) { return null; }
        var settled = Path.Combine(runtimeRoot, SpoolContract.CodexFolder);
        return registration => !InFolder(registration, settled)
            ? $"this approval is recorded outside the hook runtime this host took ({settled}), so whether it was answered at the terminal cannot be checked; answer at the machine"
            : running() is { } intake ? intake.ApprovalsUnsettled : "the host is still starting its hook intake; try again shortly";
    }

    static bool InFolder(string file, string folder)
    {
        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(file));
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return parent is not null && string.Equals(Path.TrimEndingDirectorySeparator(parent), Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), comparison);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    // Why the intake is standing back now (agent-ha-bridge is running), or null.
    public string? Paused { get { lock (_gate) { return _paused; } } }

    public long Handled { get; private set; }

    public long Dropped { get; private set; }

    public void Start(CancellationToken cancellationToken = default)
    {
        if (_loop is not null) { return; }
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _stop.Token;
        _loop = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { Pass(); }
                catch (Exception e) when (e is not OperationCanceledException) { _log($"hook intake pass failed: {e.GetType().Name}: {e.Message}"); }
                try { await _wake.WaitAsync(_options.SweepInterval, token); }
                catch (OperationCanceledException) { break; }
            }
        }, CancellationToken.None);
    }

    // Each subscriber hears of every event handled from now on. A slow one loses the oldest.
    public ChannelReader<HookNotice> Subscribe()
    {
        var channel = Channel.CreateBounded<HookNotice>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (_gate) { _subscribers.Add(channel); }
        return channel.Reader;
    }

    public void Unsubscribe(ChannelReader<HookNotice> reader)
    {
        lock (_gate) { _subscribers.RemoveAll(c => c.Reader == reader); }
    }

    // One pass: stand back if agent-ha-bridge runs; otherwise keep the heartbeat, handle what
    // is waiting, oldest first, and now and then prune registrations whose session is over.
    // Returns how many events were handled.
    public int Pass()
    {
        var active = _options.AgentHaBridgeActive();
        if (active is not null)
        {
            StandBack(active);
            return 0;
        }
        if (!Claim())
        {
            StandBack("another host is taking this spool (a second profile migrated from the same installation?)");
            return 0;
        }
        lock (_gate)
        {
            if (_paused is not null) { _log($"the host takes the hook spool again (was: {_paused})"); }
            _paused = null;
        }
        // Approvals are settled whether or not the heartbeat could be written: the events about to
        // be handled may be ones a lost hook has already overtaken.
        var beat = Beat();
        if (!ReconcileApprovals(beat)) { return 0; }
        EnsureWatcher();
        var handled = Consume();
        // Each deletion asks first: the markers are the daemon's once it has started.
        if (_handlers.WithdrawalsPending && Paused is null) { _handlers.RetryWithdrawals(); }
        var now = _time.GetUtcNow();
        if (now - _prunedAt >= _options.PruneInterval)
        {
            // The registrations are the daemon's again if it started during this pass.
            if (Paused is not null) { return handled; }
            if (_options.AgentHaBridgeActive() is { } started)
            {
                StandBack(started);
                return handled;
            }
            _prunedAt = now;
            Prune();
            if (Paused is not null) { return handled; }
        }
        return handled;
    }

    void StandBack(string why)
    {
        lock (_gate)
        {
            if (_paused is null) { _log($"standing back from the hook spool: {why}"); }
            _paused = why;
        }
        // The heartbeat is the daemon's now; the watcher would only wake passes that do nothing.
        _heartbeatOwned = false;
        _reconciled = false;
        _gap = true;
        _claim?.Dispose();
        _claim = null;
        _watcher?.Dispose();
        _watcher = null;
    }

    // Opened for this process alone and deleted when closed, so it never outlives the intake.
    bool Claim()
    {
        if (_claim is not null) { return true; }
        try
        {
            Directory.CreateDirectory(_options.RuntimeRoot);
            _claim = new FileStream(Path.Combine(_options.RuntimeRoot, ClaimFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // The daemon writes its pid; a hook reads only the time. This host's pid tells a reader
    // whose it is, and lets this host take back only its own when it stops.
    bool Beat()
    {
        var now = _time.GetUtcNow();
        // The wall clock moved back (a correction, a restored VM): times recorded since were read
        // from a clock ahead of this one, so a later event can look older than the registration
        // it should update and be passed over as a replay, leaving an approval it settled
        // answerable. Every approval is withdrawn (ReconcileApprovals withdraws markers dated
        // ahead of now too) and none answered until that is done.
        // However little: a step back of a second can still put an event before the registration
        // it should update.
        if (_clock is { } seen && now < seen)
        {
            _log($"the clock moved back ({seen:o} to {now:o}); withdrawing every Codex approval");
            Gap();
            _rewound = true;
            // Cut-offs read from the clock ahead would hold back the sweep this one asks for, and then
            // every approval after it.
            _settled = null;
            _pending = null;
            _codexLog.Forget(now);
        }
        _clock = now;
        // Hooks gave up spooling once the heartbeat was this old, whether or not it can be renewed
        // now (and mid-drain too): no Codex answer until the next pass has settled the markers.
        if (_beatAt != default && now - _beatAt >= SpoolContract.HeartbeatMaxAge) { Gap(); }
        // No room for an event: a Codex PreToolUse answering an approval at the terminal could be
        // lost together with the log line that would have said so, and its marker offered again
        // for whatever Codex asks next. So it is a gap for as long as it lasts, and hooks are told
        // at once (no heartbeat) rather than spooling into a full disk until it goes stale.
        if (_options.SpaceProbe(_options.RuntimeRoot) is { } full)
        {
            if (!_noRoom) { _log($"no room for hook events in {_options.RuntimeRoot} ({full}); hooks give their fixed reply, and no Codex approval is answered, until there is"); }
            _noRoom = true;
            Gap();
            DropHeartbeat();
            return false;
        }
        _noRoom = false;
        try
        {
            Directory.CreateDirectory(_options.RuntimeRoot);
            File.WriteAllText(_heartbeat, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _heartbeatOwned = true;
            _beatAt = now;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log($"could not keep the hook heartbeat ({e.Message}); hooks give their fixed reply until it is fresh again");
            _reconciled = false;
            return false;
        }
    }

    bool _noRoom;
    bool _spoolUnlisted;
    DateTimeOffset? _clock;
    // The clock moved back since the last complete sweep: the next withdraws every marker, whatever
    // its time (one asked a second before the step back would look asked after it).
    bool _rewound;

    // How far ahead of now a time may be before it was read from a clock since wound back (a
    // marker, a registration, a queued event), when the step itself was not seen (a restart).
    internal static readonly TimeSpan ClockSlack = TimeSpan.FromSeconds(5);

    // A realistic event's worth of space, allocated (not written) and given back at once.
    internal const long SpaceProbeBytes = 1024 * 1024;

    public static string? ProbeSpace(string runtimeRoot)
    {
        try
        {
            Directory.CreateDirectory(runtimeRoot);
            var probe = Path.Combine(runtimeRoot, $".space-probe-{Environment.ProcessId}.tmp");
            using var stream = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            stream.SetLength(SpaceProbeBytes);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return e.Message; }
    }

    // Only this host's own: a daemon that has taken over keeps its.
    void DropHeartbeat()
    {
        if (!_heartbeatOwned) { return; }
        try
        {
            if (File.Exists(_heartbeat) && string.Join("", HookStats.SharedLines(_heartbeat)).Trim() == Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)) { File.Delete(_heartbeat); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // Hooks may have run unspooled, or an event may have been lost: every approval asked before
    // the next pass is withdrawn, and none is answered until then.
    void Gap()
    {
        _gap = true;
        _reconciled = false;
    }

    // A Codex hook that ran while nobody took events gave its fixed reply and went nowhere, so
    // the PreToolUse or Stop that ended an approval may never have arrived: the person answered
    // at the terminal while the host was down. Its marker would then offer an answered approval
    // to be answered again, typed into whatever the terminal shows now. So each approval asked
    // before the last such run is withdrawn when the intake takes the spool again; one asked
    // after it is still pending. The hook log is the record of those runs (both hooks write it).
    // The same cut-off holds for a PermissionRequest still waiting in the spool from before the
    // outage: it is handled, but arms no approval. It is checked every pass, since a hook can
    // also fail to spool while the heartbeat is fresh, and settles again whenever the log shows a
    // later unspooled run. While the log cannot be read the history is unknown, so no approval
    // is offered at all; nor is one asked before the oldest log kept, once runs may have been
    // rotated out of it unread (HookLogWatch.HistoryFrom). False when agent-ha-bridge started meanwhile.
    bool ReconcileApprovals(bool beat)
    {
        var known = _codexLog.Refresh();
        var cutoff = known ? Later(_codexLog.Last, _codexLog.HistoryFrom) : _time.GetLocalNow();
        if (_gap) { cutoff = _time.GetLocalNow(); }
        // Held until settled: a later pass with a lower cut-off (the log readable again, say)
        // never lets a sweep that could not finish be forgotten.
        if (cutoff is { } wanted && (_settled is not { } done || wanted > done)) { _pending = Later(_pending, wanted); }
        // Never settled by a pass whose heartbeat failed (Beat unsettles; this must not undo it).
        if (_pending is not { } unheard) { _reconciled = known && beat; return true; }
        _handlers.ApprovalsSettledBefore = unheard;
        // A new cut-off unsettles what an earlier one settled: until its markers are gone, one of
        // them may have been answered at the terminal, so no Codex approval is answered meanwhile.
        _reconciled = false;
        // Settled only once every marker it covers is gone; otherwise the next pass tries again.
        var complete = true;
        string[] markers;
        // Listed, not Directory.Exists first: that is false too when the folder cannot be read, and
        // an unperformed sweep would count as done.
        try { markers = Directory.GetFiles(_handlers.CodexRoot, "*.approval.json"); }
        catch (DirectoryNotFoundException) when (!File.Exists(_handlers.CodexRoot)) { markers = []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { markers = []; complete = false; }
        foreach (var marker in markers)
        {
            // Read sharing write and delete: a daemon starting meanwhile must be able to replace or
            // remove the marker (File.ReadAllBytes would refuse it), and is then caught below.
            DateTimeOffset? created = null;
            using (var doc = HookHandlers.ReadJson(marker))
            {
                if (doc is not null && Json.Time(doc.RootElement, "Created") is { } at) { created = at; }
            }
            // Asked since the cut-off, unless dated ahead of now: written before the clock moved back.
            if (!_rewound && created is { } c && c > unheard && c <= _time.GetUtcNow() + ClockSlack) { continue; }
            if (_options.AgentHaBridgeActive() is { } active)
            {
                StandBack(active);
                return false;
            }
            try
            {
                File.Delete(marker);
                _log($"withdrew Codex approval {Path.GetFileName(marker)}: a Codex hook may have run at {unheard:o} with nobody taking events (or before the oldest hook log kept), so it may have been answered at the terminal");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                complete = false;
                _log($"could not withdraw Codex approval {Path.GetFileName(marker)} ({e.Message}); trying again next pass");
            }
        }
        if (complete)
        {
            _settled = unheard;
            _pending = null;
            _rewound = false;
            _reconciled = known && beat;
            // Closed once the heartbeat is fresh again, so hooks spool from here on.
            if (beat) { _gap = false; }
        }
        return true;
    }

    static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null || a >= b ? a : b;

    void EnsureWatcher()
    {
        if (!_options.UseFileSystemWatcher || _watcher is not null) { return; }
        try
        {
            Directory.CreateDirectory(_spool);
            var watcher = new FileSystemWatcher(_spool, "*.json") { NotifyFilter = NotifyFilters.FileName, InternalBufferSize = 64 * 1024 };
            // The hooks write a temporary file and rename it; Created covers a writer that
            // does not. A buffer overflow loses notifications, so it wakes a pass too.
            watcher.Renamed += (_, _) => Wake();
            watcher.Created += (_, _) => Wake();
            watcher.Error += (_, _) => Wake();
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            // Without one the two-second sweep still delivers everything, just later.
            _log($"hook spool watcher unavailable: {e.Message}");
        }
    }

    void Wake()
    {
        if (_wake.CurrentCount == 0) { _wake.Release(); }
    }

    int Consume()
    {
        string[] files;
        try
        {
            // Listed, not Directory.Exists first: that is false too when the folder cannot be read.
            files = Directory.GetFiles(_spool, "*.json").Where(f => f.EndsWith(".json", StringComparison.Ordinal)).ToArray();
            _spoolUnlisted = false;
        }
        // Not found only when nothing is there: Linux says so of a file in its place too (ENOTDIR).
        catch (DirectoryNotFoundException) when (!File.Exists(_spool)) { return 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Whatever waits in it is unseen, the PreToolUse that answered an approval at the
            // terminal among them: a gap for as long as it lasts.
            if (!_spoolUnlisted) { _log($"could not list the hook spool ({e.Message}); no Codex approval is answered until it can be"); }
            _spoolUnlisted = true;
            Gap();
            return 0;
        }
        if (files.Length == 0) { _queued = false; return 0; }
        _queued = true;
        Array.Sort(files, StringComparer.Ordinal);
        try { return Drain(files); }
        // Open again once nothing waits, including what arrived while these were handled; never
        // looked for once agent-ha-bridge has been seen to take the spool (G5: the drain asks before
        // and after every file), and answers stay closed meanwhile.
        finally { _queued = Paused is not null || Waiting(); }
    }

    int Drain(string[] files)
    {

        var handled = 0;
        foreach (var path in files)
        {
            // A host stopping leaves the rest for the next one rather than outlasting its wait
            // for the profile's lock.
            if (_stop?.IsCancellationRequested == true) { break; }
            // agent-ha-bridge may have started part-way through: from here the files are its.
            if (_options.AgentHaBridgeActive() is { } active)
            {
                StandBack(active);
                break;
            }
            // A long backlog must not let the heartbeat go stale while it drains: hooks would stop
            // spooling, and the approvals they settle would be lost.
            if (_time.GetUtcNow() - _beatAt >= TimeSpan.FromSeconds(10)) { Beat(); }
            var name = Path.GetFileName(path);
            // Handled already; only its deletion failed. Its handler must not run twice: a Codex
            // PermissionRequest replayed after the PreToolUse that answered it would re-arm it.
            if (_undeleted.ContainsKey(name))
            {
                if (Delete(path)) { _undeleted.TryRemove(name, out _); }
                continue;
            }
            SpoolEvent? spooled = null;
            try
            {
                var bytes = ReadCopy(path);
                if (bytes is null) { continue; }
                spooled = SpoolContract.Parse(bytes, name) ?? throw new InvalidDataException("not a spooled event");
                var notice = _handlers.Handle(spooled);
                // Handled, but the daemon started meanwhile: the event is its to handle (again),
                // and to publish.
                if (!Ours()) { break; }
                handled++;
                Handled++;
                _attempts.Remove(name);
                if (!Delete(path)) { _undeleted[name] = true; }
                Publish(notice);
            }
            // Not a failure: the intake has stood back, and the event waits for the daemon.
            catch (HookHandlers.StoodBack) { break; }
            // Whatever went wrong with one event, it costs that event, never the pass.
            catch (Exception e)
            {
                // Unless the daemon started meanwhile: then the file is its own, neither a failed
                // attempt of this host's nor this host's to drop.
                if (!Ours()) { break; }
                var attempts = _attempts.GetValueOrDefault(name) + 1;
                if (attempts >= 2)
                {
                    _log($"dropped spooled hook {name} after {attempts} attempts: {e.Message}");
                    _attempts.Remove(name);
                    Dropped++;
                    // It may have been the Codex event that retired an approval: as good as a hook
                    // that never spooled.
                    if (spooled is null || spooled.Agent == "codex") { Gap(); }
                    if (!Delete(path)) { _undeleted[name] = true; }
                }
                else
                {
                    _attempts[name] = attempts;
                    _log($"spooled hook {name} failed, will retry: {e.Message}");
                    // Until it has been, it may be the Codex PreToolUse that answered an approval at
                    // the terminal, whose marker would stay answerable meanwhile. So it is a gap at
                    // once, as a drop is: no answer until the sweep has settled the markers.
                    if (spooled is null || spooled.Agent == "codex") { Gap(); }
                    // Nothing after it is handled until it has been: a Codex PermissionRequest
                    // retried after the PreToolUse that answered it would re-arm an approval
                    // nobody is being asked. (The daemon carried on past a failure; order is
                    // worth the one pass this costs.)
                    break;
                }
            }
        }
        // A file that went away by other means is not waiting for a retry.
        foreach (var gone in _attempts.Keys.Where(n => !files.Any(f => f.EndsWith(n, StringComparison.Ordinal))).ToList()) { _attempts.Remove(gone); }
        foreach (var gone in _undeleted.Keys.Where(n => !files.Any(f => f.EndsWith(n, StringComparison.Ordinal))).ToList()) { _undeleted.TryRemove(gone, out _); }
        if (_attempts.Count > 0) { Wake(); }
        return handled;
    }

    // agent-ha-bridge's Go hook checks its 16 MiB before json.Marshal, which escapes <, > and &
    // (and U+2028/2029) as six bytes each, so an event it accepted can spool at up to six times that.
    internal const long MaxSpoolFileBytes = 6L * SpoolContract.MaxEventBytes + 64 * 1024;

    // The file's bytes, read through a handle that shares delete, so whoever deletes it is
    // never refused because this host was reading (the shadow-read rule of G5). Null when it
    // has already gone.
    internal static byte[]? ReadCopy(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxSpoolFileBytes) { throw new InvalidDataException("larger than any hook writes"); }
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    static bool Delete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    void Publish(HookNotice notice)
    {
        lock (_gate)
        {
            foreach (var s in _subscribers) { s.Writer.TryWrite(notice); }
        }
    }

    // As the daemon's reconcile: a registration for a session that is over is deleted, since
    // nothing else ever would, and discovery reads every one of them every second. Only on a
    // complete process list: a process that could not be seen is not a process that ended.
    void Prune()
    {
        var now = _time.GetUtcNow();
        var claude = _processes.Observe(AgentKind.Claude);
        if (claude.Known)
        {
            // A runtime whose command line could not be read may be the session's: not ended.
            var live = claude.Processes.Concat(claude.Unidentified).Select(p => p.Id).ToHashSet();
            foreach (var file in HookHandlers.SafeFiles(_handlers.ClaudeRoot))
            {
                using var doc = Read(file);
                if (doc is null) { continue; }
                var pid = Json.Int(doc.RootElement, "ProcessId") ?? 0;
                var updated = Json.Time(doc.RootElement, "Updated");
                var stale = updated is { } u && now - u > _options.StaleAfter;
                if (!(pid > 0 && live.Contains(pid)) && stale)
                {
                    if (!Ours()) { return; }
                    Delete(file);
                }
            }
        }
        var codex = _processes.Observe(AgentKind.Codex);
        if (codex.Known)
        {
            var live = codex.Processes.Concat(codex.Unidentified).Select(p => p.Id).ToHashSet();
            foreach (var file in HookHandlers.SafeFiles(_handlers.CodexRoot))
            {
                if (file.EndsWith(".approval.json", StringComparison.OrdinalIgnoreCase)) { continue; }
                using var doc = Read(file);
                if (doc is null || Json.Str(doc.RootElement, "SessionId") is null) { continue; }
                var pid = Json.Int(doc.RootElement, "ProcessId") ?? 0;
                var ended = Json.Bool(doc.RootElement, "Ended") == true;
                var stale = Json.Time(doc.RootElement, "Updated") is { } u && now - u > _options.StaleAfter;
                // An ended session's registration stays while its process runs, as the daemon's
                // ($endedCanPrune): that process may still hold the thread's lock, and without the
                // registration saying it ended, discovery would find the session through the lock.
                var running = pid > 0 && live.Contains(pid);
                if (!running && (ended || pid > 0 || stale))
                {
                    if (!Ours()) { return; }
                    Delete(file);
                }
            }
        }
    }

    // Before each delete of something the daemon would own: false (and standing back) once
    // agent-ha-bridge has started.
    bool Ours()
    {
        if (_options.AgentHaBridgeActive() is not { } active) { return true; }
        StandBack(active);
        return false;
    }

    static System.Text.Json.JsonDocument? Read(string path)
    {
        try
        {
            var bytes = ReadCopy(path);
            return bytes is null ? null : System.Text.Json.JsonDocument.Parse(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop is not null)
        {
            await _stop.CancelAsync();
            if (_loop is not null) { try { await _loop; } catch (OperationCanceledException) { } }
            _stop.Dispose();
        }
        _watcher?.Dispose();
        // A heartbeat left behind would have hooks spool to nobody for up to a minute.
        DropHeartbeat();
        lock (_gate)
        {
            foreach (var s in _subscribers) { s.Writer.TryComplete(); }
            _subscribers.Clear();
        }
        _claim?.Dispose();
        _claim = null;
        _wake.Dispose();
    }
}
