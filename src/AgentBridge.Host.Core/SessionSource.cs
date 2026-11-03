using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AgentBridge.Host.Core.Discovery;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core;

// Discovers live Copilot, Claude and Codex sessions and keeps a contract Session for
// each, strictly read-only: it opens agent files for shared reading and never writes,
// locks, renames or deletes anything, and never lists agent-ha-bridge's hook spool.
public sealed class SessionSource : ISessionSource
{
    static readonly TimeSpan TranscriptSearchCache = TimeSpan.FromSeconds(30);
    static readonly TimeSpan WakeDebounce = TimeSpan.FromMilliseconds(50);

    readonly SessionSourceOptions _options;
    readonly IProcessInspector _processes;
    readonly TimeProvider _time;
    readonly SemaphoreSlim _pass = new(1, 1);
    readonly Lock _gate = new();
    readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    readonly List<SessionSubscription> _subscribers = [];
    readonly Channel<SessionChange> _changes = Channel.CreateUnbounded<SessionChange>(new UnboundedChannelOptions { SingleReader = true });
    readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    readonly Dictionary<string, (string? Path, DateTimeOffset At)> _transcriptSearch = new(StringComparer.Ordinal);
    // Who holds each Codex thread lock, as the OS last said. Only touched under _pass.
    readonly Dictionary<string, CodexLockHolding> _codexLockHolders = new(StringComparer.OrdinalIgnoreCase);
    // Sessions the host is ending, and since when. Under _gate.
    readonly Dictionary<string, DateTimeOffset> _ending = new(StringComparer.Ordinal);
    // Who drives each session (Session.Driver), shared with the Home Assistant connector; null for
    // a source that does not say.
    readonly SessionDrivers? _drivers;
    readonly List<FileSystemWatcher> _watchers = [];
    readonly CancellationTokenSource _stop = new();
    readonly Task _dispatch;
    Task? _loop;
    long _revision;
    volatile bool _discoverRequested;
    DateTimeOffset _nextDiscovery;
    // Under _gate.
    DiscoveryHeld? _held;
    // Only on the pass, under _pass.
    readonly Unaccounted _unaccounted;
    string? _heldLogged;
    DateTimeOffset _heldLoggedAt;
    // Only on the pass: whether the first discovery pass has looked at StateFile (Restore), what it
    // read there (for that pass only), and what was last written to it.
    bool _restored;
    List<DiscoveredSession>? _remembered;
    string? _saved;
    string? _saveProblem;

    // How often a hold that has not changed is said again in the log: it is a standing
    // condition, and a line written only when it began scrolls away long before anyone comes
    // looking for why nothing was resumed or reclaimed.
    static readonly TimeSpan HeldLogEvery = TimeSpan.FromMinutes(10);

    sealed class Entry(SessionTracker tracker)
    {
        public SessionTracker Tracker { get; } = tracker;
        public Session ListForm = null!;
        public Session GetForm = null!;
        // GetForm before SessionBudget.Fit (GetUnbudgeted), and its JSON when Fit changed it.
        public Session Unbudgeted = null!;
        public string? UnbudgetedJson;
        // What the transcript says, before the ending override; what SetEndingAsync shows
        // again when a stop fails.
        public Session Full = null!;
        // When the ending override last changed, which a client sees, so UpdatedAt never
        // goes back behind it.
        public DateTimeOffset? Touched;
        public string Json = "";
        public DateTimeOffset? LastEmit;
        public ITimer? Timer;
        public bool Dirty;
    }

    public SessionSource(SessionSourceOptions options, IProcessInspector? processes = null, TimeProvider? time = null, SessionDrivers? drivers = null)
    {
        _options = options;
        _processes = processes ?? new SystemProcessInspector();
        _time = time ?? TimeProvider.System;
        _unaccounted = new Unaccounted(_time, Log);
        _drivers = drivers;
        if (_drivers is not null)
        {
            _drivers.SourceObserves = true;
            _drivers.Changed += OnDriverChanged;
        }
        _dispatch = Task.Run(DispatchAsync);
    }

    void Log(string message) => _options.Log?.Invoke(message);

    public DiscoveryHeld? Held { get { lock (_gate) { return _held; } } }

    // The current user's sessions on the real machine, with default roots.
    public static SessionSource ForCurrentUser() => new(SessionSourceOptions.ForCurrentUser());

    public long Revision { get { lock (_gate) { return _revision; } } }

    public event EventHandler<SessionChange>? Changed;

    public IReadOnlyList<Session> Snapshot()
    {
        lock (_gate) { return [.. Sorted().Select(e => e.ListForm)]; }
    }

    public SessionList List(string? cursor = null)
    {
        var start = 0;
        if (cursor is not null && (!int.TryParse(cursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out start) || start < 0))
        {
            throw new ArgumentException("Unknown cursor.", nameof(cursor));
        }
        List<Session> sessions;
        long revision;
        DiscoveryHeld? held;
        lock (_gate)
        {
            sessions = [.. Sorted().Select(e => e.ListForm)];
            revision = _revision;
            held = _held;
        }
        if (start >= sessions.Count) { return new SessionList([], revision, null, held); }
        var page = Paging.Pages(sessions.Skip(start)).First();
        var next = start + page.Count < sessions.Count ? (start + page.Count).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return new SessionList(page, revision, next, held);
    }

    public Session? Get(string id)
    {
        lock (_gate) { return _entries.TryGetValue(id, out var e) ? e.GetForm : null; }
    }

    public Session? GetUnbudgeted(string id)
    {
        lock (_gate) { return _entries.TryGetValue(id, out var e) ? e.Unbudgeted : null; }
    }

    public (Session Budgeted, Session Unbudgeted)? GetForms(string id)
    {
        lock (_gate) { return _entries.TryGetValue(id, out var e) ? (e.GetForm, e.Unbudgeted) : null; }
    }

    public event EventHandler<string>? UnbudgetedChanged;

    // Read between passes, as a pass is what changes a tracker's state.
    public async Task<Subagent?> GetAgentAsync(string id, string agent, CancellationToken cancellationToken = default)
    {
        await _pass.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Entry? entry;
            lock (_gate) { entry = _entries.GetValueOrDefault(id); }
            return entry?.Tracker.Agent(agent);
        }
        finally { _pass.Release(); }
    }

    public SessionProcess? ProcessOf(string id)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out var e) && e.Tracker.Discovered is { ProcessId: { } pid } d ? new SessionProcess(pid, d.ProcessStartedAt) : null;
        }
    }

    public SessionFiles? Files(string id)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var e)) { return null; }
            var d = e.Tracker.Discovered;
            return new SessionFiles(d.Agent, d.TranscriptPath, d.Registration?.Path);
        }
    }

    public SessionSubscription Subscribe()
    {
        lock (_gate)
        {
            var sorted = Sorted();
            var subscription = new SessionSubscription(new SubscribeResult(_revision, sorted.Count), Unsubscribe);
            foreach (var e in sorted) { subscription.Channel.Writer.TryWrite(new SessionEvent(Events.SessionUpsert, e.ListForm)); }
            subscription.Channel.Writer.TryWrite(new SessionEvent(Events.SessionsSynced, new SessionsSynced(_revision, _held)));
            _subscribers.Add(subscription);
            return subscription;
        }
    }

    void Unsubscribe(SessionSubscription subscription)
    {
        lock (_gate) { _subscribers.Remove(subscription); }
    }

    List<Entry> Sorted() =>
        [.. _entries.Values.OrderByDescending(e => e.ListForm.UpdatedAt).ThenBy(e => e.ListForm.Id, StringComparer.Ordinal)];

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _pass.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { Pass(discover: true); }
        finally { _pass.Release(); }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_loop is not null) { return; }
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (_options.UseFileSystemWatcher) { StartWatchers(); }
        _loop = Task.Run(() => LoopAsync(_stop.Token), CancellationToken.None);
    }

    async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var woke = false;
            using (var timeout = new CancellationTokenSource(_options.PollInterval, _time))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token))
            {
                try
                {
                    await _wake.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
                    woke = true;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                catch (OperationCanceledException) { return; }
            }
            try
            {
                // A burst of writes arrives as many notifications; one pass covers them.
                if (woke) { await Task.Delay(WakeDebounce, _time, token).ConfigureAwait(false); }
                // Discovery lists processes, which costs more than tailing: a file change
                // only re-reads the transcripts unless it was a lock or registration.
                var discover = DiscoveryDue(woke);
                await _pass.WaitAsync(token).ConfigureAwait(false);
                try { Pass(discover); }
                finally { _pass.Release(); }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // A bug in one pass must not end watching for good; the next pass retries.
            }
        }
    }

    // Discovery runs on a fixed deadline as well as when a watcher saw a lock or
    // registration change. Without the deadline, transcript writes arriving faster than
    // the poll interval kept every pass a tail-only one, so a session that ended without
    // touching its lock stayed listed and a missed creation event was never recovered.
    internal bool DiscoveryDue(bool woke)
    {
        var now = _time.GetUtcNow();
        var due = !woke || _discoverRequested || now >= _nextDiscovery;
        _discoverRequested = false;
        if (due) { _nextDiscovery = now + _options.PollInterval; }
        return due;
    }

    void Pass(bool discover)
    {
        List<DiscoveredSession> found;
        var release = false;
        DiscoveryHeld? held = null;
        if (discover)
        {
            // What the host listed when it last ran, read once, for the first discovery pass
            // (Discover's accounting and Restore).
            if (!_restored && _options.StateFile is { } state) { _remembered = Load(state); }
            var observed = Discover();
            found = [.. observed.SelectMany(o => o.Sessions)];
            var live = found.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
            var holding = observed.Where(o => o.Holds.Count > 0).Select(o => o.Agent).ToHashSet();
            if (!_restored)
            {
                _restored = true;
                found.AddRange(Restore(observed, found));
                _remembered = null;
            }
            List<string> gone;
            lock (_gate)
            {
                // An agent whose pass was incomplete keeps every session it had: one it could
                // not see this time is not one that has ended, and it keeps being read.
                foreach (var e in _entries.Values.Where(e => !live.Contains(e.ListForm.Id) && holding.Contains(e.Tracker.Discovered.Agent)))
                {
                    found.Add(e.Tracker.Discovered);
                }
                gone = [.. _entries.Keys.Where(id => !live.Contains(id) && !holding.Contains(_entries[id].Tracker.Discovered.Agent))];
            }
            foreach (var id in gone) { Remove(id); }
            held = HeldBy(observed);
            // A hold that begins is said at once. One that ends, or lets an agent go, waits until
            // the sessions this pass found are listed: a session the hold kept unseen (its
            // registration unreadable until now) is otherwise neither listed nor held for a
            // moment, and a resume of it in that moment starts a second CLI on its transcript.
            // Both at once (Copilot clears as Codex is held) says the new one now, with the old
            // agents kept until then.
            release = Releases(held);
            if (!release) { SetHeld(held); }
            else if (held is not null) { SetHeld(Widened(held)); }
        }
        else
        {
            lock (_gate) { found = [.. _entries.Values.Select(e => e.Tracker.Discovered)]; }
        }

        var unread = new List<AgentKind>();
        foreach (var discovered in found.DistinctBy(f => f.Id))
        {
            Entry? entry;
            lock (_gate) { _entries.TryGetValue(discovered.Id, out entry); }
            var tracker = entry?.Tracker ?? new SessionTracker(discovered, _options, _time);
            Session full;
            try { full = tracker.Update(discovered); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
            {
                // One unreadable session must not stall every other; it is tried again
                // next pass. One found live but never listed holds its agent meanwhile: it is
                // running, and must not be offered for resume.
                if (discover && entry is null) { unread.Add(discovered.Agent); }
                continue;
            }
            Commit(entry ?? new Entry(tracker), full);
        }
        if (unread.Count > 0) { (held, release) = (WithUnread(held, unread), true); }
        if (release) { SetHeld(held); }
        if (discover) { Save(); }
    }

    static DiscoveryHeld WithUnread(DiscoveryHeld? held, List<AgentKind> unread)
    {
        var reasons = unread.Distinct().Select(a => $"a {a.ToString().ToLowerInvariant()} session that could not be read");
        var text = Limits.KeepStart(string.Join(", ", held is null ? reasons : reasons.Prepend(held.Reason)), DiscoveryHeld.MaxReasonBytes)!;
        return new DiscoveryHeld(text, [.. (held?.Agents ?? []).Union(unread).Order()]);
    }

    bool Releases(DiscoveryHeld? next)
    {
        lock (_gate) { return _held is { } now && (next is null || now.Agents.Except(next.Agents).Any()); }
    }

    // The next hold, still covering every agent the current one does.
    DiscoveryHeld Widened(DiscoveryHeld next)
    {
        lock (_gate) { return next with { Agents = [.. (_held?.Agents ?? []).Union(next.Agents).Order()] }; }
    }

    // One agent's part of a discovery pass: what it found, and anything that kept it from
    // being sure that is all (none when complete). Observed and Ended are what a restart's
    // first pass checks a remembered session against (Restore).
    sealed record AgentDiscovery(AgentKind Agent, IReadOnlyList<DiscoveredSession> Sessions, IReadOnlyList<string> Holds,
        AgentProcessObservation Observed, IReadOnlySet<string>? Ended = null);

    List<AgentDiscovery> Discover()
    {
        Dictionary<AgentKind, List<AgentProcess>> previous;
        lock (_gate)
        {
            previous = _entries.Values.Where(e => e.Tracker.Discovered.ProcessId is not null)
                .GroupBy(e => e.Tracker.Discovered.Agent)
                .ToDictionary(g => g.Key, g => g.Select(e => new AgentProcess(e.Tracker.Discovered.ProcessId!.Value, e.Tracker.Discovered.ProcessStartedAt, null)).ToList());
        }
        // A host that has just started knew no session's process, so a runtime that cannot be read
        // now held nothing, and a session only its own unreadable runtime still showed was dropped
        // (found in review of #147). What it listed when it last ran counts as known, dated only:
        // an undated one could be any process given its pid.
        foreach (var s in _remembered ?? [])
        {
            if (s.ProcessId is not { } pid || s.ProcessStartedAt is null) { continue; }
            if (!previous.TryGetValue(s.Agent, out var list)) { previous[s.Agent] = list = []; }
            list.Add(new AgentProcess(pid, s.ProcessStartedAt, null));
        }
        var result = new List<AgentDiscovery>();

        {
            var observed = _processes.Observe(AgentKind.Copilot);
            var holds = new List<string>();
            var sessions = CopilotDiscovery.Find(_options.CopilotHome, observed.Processes, holds, observed.Unidentified);
            var accounted = sessions.Where(s => s.ProcessId is not null).Select(s => s.ProcessId!.Value).ToHashSet();
            // Scout's embedded `copilot --headless` is nobody's session and never will be.
            accounted.UnionWith(observed.Processes.Where(p => CommandLine.IsEmbedded(p.CommandLine)).Select(p => p.Id));
            Account(AgentKind.Copilot, observed, accounted, previous, holds);
            result.Add(new(AgentKind.Copilot, sessions, holds, observed));
        }
        // Listing processes is the expensive part; without a registration folder or Claude's
        // own sessions folder there is nothing it could match, and an agent that was never
        // installed is absence, not doubt.
        var claudeSessions = Installed(Path.Combine(_options.ClaudeHome, "sessions"));
        if (claudeSessions || _options.ClaudeRegistrationRoots.Any(Installed))
        {
            result.Add(Registrations(AgentKind.Claude, _options.ClaudeRegistrationRoots, FindClaudeTranscript, previous, claudeSessions));
        }
        var codexLocks = Installed(Path.Combine(_options.CodexHome, "thread-writer-locks"));
        if (codexLocks || _options.CodexRegistrationRoots.Any(Installed))
        {
            result.Add(Registrations(AgentKind.Codex, _options.CodexRegistrationRoots, FindCodexTranscript, previous, codexLocks: codexLocks));
        }
        return result;
    }

    // A root that cannot be read is not one that is missing: that agent is asked, and holds.
    static bool Installed(string root) => CopilotDiscovery.ProbeFolder(root) != CopilotDiscovery.Folder.Absent;

    AgentDiscovery Registrations(AgentKind agent, IReadOnlyList<string> roots, Func<string, string?> findTranscript, Dictionary<AgentKind, List<AgentProcess>> previous,
        bool claudeSessions = false, bool codexLocks = false)
    {
        var observed = _processes.Observe(agent);
        var holds = new List<string>();
        var accounted = new HashSet<int>();
        var ended = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<DiscoveredSession> sessions = RegistrationDiscovery.Find(agent, roots, observed.Processes, findTranscript, holds, accounted, _options.AfterRegistrationRead, observed.Unidentified, ended);
        if (codexLocks)
        {
            // agent-ha-bridge's registration, where there is one, carries what its hooks saw
            // (status, the approval marker) and wins, an ended one included. One CLI owns one
            // session. A terminal a thread lock binds to a session is accounted for by it.
            var ids = sessions.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            ids.UnionWith(ended);
            var pids = sessions.Select(s => s.ProcessId).ToHashSet();
            var locked = CodexLockDiscovery.Find(_options.CodexHome, observed.Processes, _processes.FileHolders, _codexLockHolders, findTranscript, _time.GetUtcNow(), holds)
                .Where(s => !ids.Contains(s.Id) && !pids.Contains(s.ProcessId)).ToList();
            accounted.UnionWith(locked.Where(s => s.ProcessId is not null).Select(s => s.ProcessId!.Value));
            sessions = [.. sessions, .. locked];
        }
        if (claudeSessions)
        {
            // agent-ha-bridge's registration, where there is one, carries what its hooks saw
            // (status, permission prompts) and wins. One CLI owns one session.
            var ids = sessions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            var pids = sessions.Select(s => s.ProcessId).ToHashSet();
            var own = ClaudeSessionDiscovery.Find(_options.ClaudeHome, observed.Processes, findTranscript, holds, accounted, observed.Unidentified);
            sessions = [.. sessions, .. own.Where(s => !ids.Contains(s.Id) && !pids.Contains(s.ProcessId))];
        }
        if (agent == AgentKind.Codex)
        {
            accounted.UnionWith(observed.Processes.Where(p => CommandLine.IsCodexAppServer(p.CommandLine)).Select(p => p.Id));
        }
        Account(agent, observed, accounted, previous, holds);
        return new(agent, sessions, holds, observed, ended);
    }

    // What makes an agent's pass incomplete beyond what it could not read: agent-ha-bridge's
    // Known false, as Read-DaemonAdapterRegistrations and Select-DaemonHoldingProcesses set it.
    void Account(AgentKind agent, AgentProcessObservation observed, HashSet<int> accounted, Dictionary<AgentKind, List<AgentProcess>> previous, List<string> holds)
    {
        var name = agent.ToString().ToLowerInvariant();
        // Not Known, which an unrelated unreadable runtime also clears (see AgentProcessObservation).
        if (!observed.Listed) { holds.Add($"the {name} processes could not be listed"); }
        // A runtime whose command line could not be read this time, that was a session's own
        // process last time: a failed read, not a session that ended. Every other user's node
        // is unreadable too, so only those discovery already knew are held for - by start time as
        // well as id, since a pid the session's process left behind can be reused by any runtime.
        if (previous.TryGetValue(agent, out var had) && observed.Unidentified.FirstOrDefault(p => had.Any(h => SameProcess(h, p))) is { } unread)
        {
            holds.Add($"a {name} process that could not be read (pid {unread.Id})");
        }
        var unaccounted = observed.Processes.Where(p => !accounted.Contains(p.Id)).ToList();
        var present = observed.Processes.Where(p => accounted.Contains(p.Id)).ToList();
        foreach (var p in _unaccounted.Holding(agent, unaccounted, present))
        {
            holds.Add($"an unrecognised {name} process (pid {p.Id})");
        }
    }

    // Unknown start times cannot tell two generations of a pid apart, so they are taken to match:
    // holding for a stranger costs a pass of retirement, retiring a live session costs the session.
    static bool SameProcess(AgentProcess known, AgentProcess seen) =>
        known.Id == seen.Id && (known.StartedAt is not { } a || seen.StartedAt is not { } b || (a - b).Duration() <= TimeSpan.FromSeconds(1));

    // Publishes the hold when it changes: on the source, to subscribers as discovery.changed,
    // and to the log, which also says it again now and then while it stands.
    static DiscoveryHeld? HeldBy(List<AgentDiscovery> observed)
    {
        var reasons = observed.SelectMany(o => o.Holds).Distinct(StringComparer.Ordinal).ToList();
        if (reasons.Count == 0) { return null; }
        var text = Limits.KeepStart(string.Join(", ", reasons), DiscoveryHeld.MaxReasonBytes)!;
        return new DiscoveryHeld(text, [.. observed.Where(o => o.Holds.Count > 0).Select(o => o.Agent)]);
    }

    void SetHeld(DiscoveryHeld? held)
    {
        var now = _time.GetUtcNow();
        var summary = held?.Reason;
        if (summary != _heldLogged || (summary is not null && now - _heldLoggedAt >= HeldLogEvery))
        {
            if (summary is not null) { Log($"session discovery held ({summary}); sessions of {string.Join(", ", held!.Agents.Select(a => a.ToString().ToLowerInvariant()))} are kept, and no resume of them or worktree reclaim is done until it clears"); }
            else if (_heldLogged is not null) { Log("session discovery complete again"); }
            _heldLogged = summary;
            _heldLoggedAt = now;
        }
        lock (_gate)
        {
            if (Same(_held, held)) { return; }
            _held = held;
            foreach (var s in _subscribers) { s.Channel.Writer.TryWrite(new SessionEvent(Events.DiscoveryChanged, new DiscoveryChanged(held))); }
        }
    }

    static bool Same(DiscoveryHeld? a, DiscoveryHeld? b) =>
        a is null ? b is null : b is not null && a.Reason == b.Reason && a.Agents.SequenceEqual(b.Agents);

    // A hold keeps every session an agent had, so one its pass could not see stays listed. But a
    // host that has just started had none: a session only the hold was keeping was dropped by a
    // restart, and the Home Assistant connector retired its entities, while it ran on (#147). So
    // the first discovery pass of a host that keeps StateFile brings back each session it listed
    // when it last ran, of an agent that pass holds, whose own process is still running (the same
    // pid, started at the same time) and is no other session's now. It is then kept, read and
    // judged like any other session a hold keeps: removed only by a complete pass that does not
    // find it. A complete pass is believed as ever, and a registration that says the session
    // ended is too.
    List<DiscoveredSession> Restore(List<AgentDiscovery> observed, List<DiscoveredSession> found)
    {
        var restored = new List<DiscoveredSession>();
        if (_remembered is not { Count: > 0 } remembered || !observed.Any(o => o.Holds.Count > 0)) { return restored; }
        var ids = found.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var pids = found.Where(f => f.ProcessId is not null).Select(f => (f.Agent, f.ProcessId!.Value)).ToHashSet();
        foreach (var o in observed.Where(o => o.Holds.Count > 0))
        {
            var running = o.Observed.Processes.Concat(o.Observed.Unidentified).ToList();
            foreach (var s in remembered.Where(s => s.Agent == o.Agent))
            {
                if (ids.Contains(s.Id) || o.Ended?.Contains(s.Id) == true) { continue; }
                // Only a process that can be told from a later one given its pid: a session is
                // never put back on a stranger.
                if (s.ProcessId is not { } pid || s.ProcessStartedAt is not { } started || pids.Contains((s.Agent, pid))) { continue; }
                if (!running.Any(p => p.Id == pid && p.StartedAt is { } at && (at - started).Duration() <= TimeSpan.FromSeconds(1))) { continue; }
                restored.Add(Located(s));
                ids.Add(s.Id);
                pids.Add((s.Agent, pid));
            }
        }
        if (restored.Count > 0)
        {
            Log($"kept {restored.Count} session(s) listed across the restart while discovery is held: {string.Join(", ", restored.Select(s => $"{s.Agent.ToString().ToLowerInvariant()} {s.Id[..Math.Min(8, s.Id.Length)]} (pid {s.ProcessId})"))}");
        }
        return restored;
    }

    // A remembered session with its files found as discovery finds them. Only a session's identity
    // is kept across a restart, never a path: what the file says is not trusted to say where to read.
    DiscoveredSession Located(DiscoveredSession s) => s.Agent switch
    {
        AgentKind.Copilot when Path.Combine(_options.CopilotHome, "session-state", s.Id) is var dir =>
            s with { StateDirectory = dir, TranscriptPath = Path.Combine(dir, "events.jsonl") },
        AgentKind.Claude => s with { TranscriptPath = FindClaudeTranscript(s.Id) },
        _ => s with { TranscriptPath = FindCodexTranscript(s.Id) },
    };

    // What StateFile says was listed, as identities alone; nothing when it cannot be read whole.
    List<DiscoveredSession> Load(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxStateBytes) { return []; }
            using var doc = JsonDocument.Parse(stream);
            if (Json.Arr(doc.RootElement, "sessions") is not { } list) { return []; }
            var sessions = new List<DiscoveredSession>();
            foreach (var e in list.EnumerateArray())
            {
                var id = Json.Str(e, "id");
                if (string.IsNullOrWhiteSpace(id) || RegistrationDiscovery.SafeKey(id) != id) { continue; }
                var agent = Json.Str(e, "agent") switch { "copilot" => AgentKind.Copilot, "claude" => AgentKind.Claude, "codex" => AgentKind.Codex, _ => (AgentKind?)null };
                if (agent is null) { continue; }
                sessions.Add(new DiscoveredSession(id, agent.Value, null, null, Json.Int(e, "pid"), ProcessStartedAt: Json.Time(e, "processStartedAt")));
            }
            return sessions;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Log($"could not read the sessions listed when the host last ran ({e.Message}); none is kept across the restart");
            return [];
        }
    }

    const long MaxStateBytes = 4 * 1024 * 1024;

    // StateFile, written whole and renamed into place whenever what discovery lists changes, so
    // it always says what the next start would need to keep. Only on the pass.
    void Save()
    {
        if (_options.StateFile is not { } path) { return; }
        List<DiscoveredSession> sessions;
        lock (_gate) { sessions = [.. _entries.Values.Select(e => e.Tracker.Discovered).OrderBy(d => d.Id, StringComparer.Ordinal)]; }
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteStartArray("sessions");
            foreach (var d in sessions)
            {
                w.WriteStartObject();
                w.WriteString("id", d.Id);
                w.WriteString("agent", d.Agent.ToString().ToLowerInvariant());
                if (d.ProcessId is { } pid) { w.WriteNumber("pid", pid); }
                if (d.ProcessStartedAt is { } started) { w.WriteString("processStartedAt", started.ToString("o", CultureInfo.InvariantCulture)); }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        if (json == _saved) { return; }
        // Never more than the next start would read.
        if (buffer.WrittenCount > MaxStateBytes)
        {
            if (_saveProblem != "too large") { Log($"not keeping the listed sessions for the next start: {sessions.Count} would take more than {MaxStateBytes / 1024 / 1024} MiB"); }
            _saveProblem = "too large";
            return;
        }
        var partial = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(partial, json);
            File.Move(partial, path, overwrite: true);
            _saved = json;
            _saveProblem = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (_saveProblem != e.Message) { Log($"could not keep the listed sessions for the next start ({e.Message}); trying again"); }
            _saveProblem = e.Message;
        }
        finally
        {
            try { File.Delete(partial); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // A registration written before the agent reported its transcript: Claude's is
    // projects/<slug>/<id>.jsonl, Codex's sessions/<y>/<m>/<d>/rollout-*-<id>.jsonl.
    string? FindClaudeTranscript(string id) => Search("claude:" + id, () =>
    {
        var projects = Path.Combine(_options.ClaudeHome, "projects");
        var name = RegistrationDiscovery.SafeKey(id) + ".jsonl";
        return CopilotDiscovery.SafeEnumerateDirectories(projects).Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
    });

    string? FindCodexTranscript(string id) => Search("codex:" + id, () =>
    {
        var sessions = Path.Combine(_options.CodexHome, "sessions");
        try
        {
            return Directory.Exists(sessions)
                ? Directory.EnumerateFiles(sessions, $"rollout-*-{RegistrationDiscovery.SafeKey(id)}.jsonl", SearchOption.AllDirectories).FirstOrDefault()
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    });

    string? Search(string key, Func<string?> search)
    {
        var now = _time.GetUtcNow();
        if (_transcriptSearch.TryGetValue(key, out var known) && (known.Path is not null || now - known.At < TranscriptSearchCache)) { return known.Path; }
        var path = search();
        _transcriptSearch[key] = (path, now);
        return path;
    }

    // Under _pass, like every Commit, so a pass that read the transcript before the override
    // changed cannot commit after it and undo it.
    public async Task SetEndingAsync(string id, bool ending)
    {
        await _pass.WaitAsync().ConfigureAwait(false);
        try
        {
            Entry? entry;
            lock (_gate)
            {
                if (!_entries.TryGetValue(id, out entry)) { return; }
                var now = _time.GetUtcNow();
                if (ending ? !_ending.TryAdd(id, now) : !_ending.Remove(id)) { return; }
                entry.Touched = now;
            }
            Commit(entry, entry.Full);
        }
        finally { _pass.Release(); }
    }

    // A press armed a session's driver: shown at once, as the ending override is, and dated, since
    // it is something a client shows.
    void OnDriverChanged(string id) => _ = RecommitDriverAsync(id);

    async Task RecommitDriverAsync(string id)
    {
        try
        {
            await _pass.WaitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { return; }
        try
        {
            Entry? entry;
            lock (_gate)
            {
                if (!_entries.TryGetValue(id, out entry)) { return; }
                entry.Touched = _time.GetUtcNow();
            }
            Commit(entry, entry.Full);
        }
        finally
        {
            // The source may have been disposed meanwhile; nothing waits on the pass then.
            try { _pass.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    void Commit(Entry entry, Session full)
    {
        entry.Full = full;
        DateTimeOffset? endingSince;
        lock (_gate) { endingSince = _ending.TryGetValue(full.Id, out var at) ? at : null; }
        var shown = entry.Touched is { } touched && touched > full.UpdatedAt ? full with { UpdatedAt = touched } : full;
        if (endingSince is { } since) { shown = shown with { Status = SessionStatus.Ending, StatusSince = since }; }
        if (_drivers is not null)
        {
            // From what the transcript says, not the ending override: a new turn decides who drives it.
            _drivers.For(full.Id).Observe(full, _time.GetUtcNow(), _drivers.Arm);
            var (driver, principal) = _drivers.Shown(full.Id);
            shown = shown with { Driver = driver, DriverPrincipal = principal };
        }
        var getForm = SessionBudget.Fit(shown);
        var listForm = SessionBudget.Fit(shown.WithoutHistory());
        var json = JsonSerializer.Serialize(getForm, ProtocolJson.Options);
        // Only a session Fit trimmed has another form to tell apart.
        var unbudgetedJson = ReferenceEquals(getForm, shown) ? null : JsonSerializer.Serialize(shown, ProtocolJson.Options);
        lock (_gate)
        {
            if (json == entry.Json && _entries.ContainsKey(full.Id))
            {
                if (unbudgetedJson == entry.UnbudgetedJson) { return; }
                // Only what Fit trimmed changed: no client is sent anything new, so no revision and
                // no upsert (each up to 40 KiB, a byte-identical repeat; found in review). Kept for
                // GetUnbudgeted, and said to its readers below.
                entry.Unbudgeted = shown;
                entry.UnbudgetedJson = unbudgetedJson;
            }
            else
            {
                entry.GetForm = getForm;
                entry.ListForm = listForm;
                entry.Unbudgeted = shown;
                entry.UnbudgetedJson = unbudgetedJson;
                entry.Json = json;
                _entries[full.Id] = entry;
                _revision++;
                entry.Dirty = true;
                ScheduleEmit(entry);
                return;
            }
        }
        if (UnbudgetedChanged is not { } handlers) { return; }
        // Each on its own, as Changed's are: one that throws must not keep the rest from hearing.
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<string>>())
        {
            try { handler(this, full.Id); }
            catch (Exception) { }
        }
    }

    // At most one upsert per session per CoalesceInterval: the first change goes out at
    // once, later ones within the interval wait for its end and go out as one, carrying
    // the latest state.
    void ScheduleEmit(Entry entry)
    {
        var now = _time.GetUtcNow();
        if (entry.LastEmit is not { } last || now - last >= _options.CoalesceInterval)
        {
            Emit(entry, now);
            return;
        }
        entry.Timer ??= _time.CreateTimer(_ => OnTimer(entry), null, last + _options.CoalesceInterval - now, Timeout.InfiniteTimeSpan);
    }

    void OnTimer(Entry entry)
    {
        lock (_gate)
        {
            entry.Timer?.Dispose();
            entry.Timer = null;
            if (!entry.Dirty || !_entries.TryGetValue(entry.ListForm.Id, out var current) || current != entry) { return; }
            Emit(entry, _time.GetUtcNow());
        }
    }

    void Emit(Entry entry, DateTimeOffset now)
    {
        entry.Dirty = false;
        entry.LastEmit = now;
        var session = entry.ListForm;
        foreach (var s in _subscribers) { s.Channel.Writer.TryWrite(new SessionEvent(Events.SessionUpsert, session)); }
        _changes.Writer.TryWrite(new SessionChange(SessionChangeKind.Upserted, session.Id, session, _revision));
    }

    void Remove(string id)
    {
        lock (_gate)
        {
            if (!_entries.Remove(id, out var entry)) { return; }
            _ending.Remove(id);
            _drivers?.Forget(id);
            entry.Timer?.Dispose();
            entry.Timer = null;
            _revision++;
            foreach (var s in _subscribers) { s.Channel.Writer.TryWrite(new SessionEvent(Events.SessionRemove, new SessionRemoved(id))); }
            _changes.Writer.TryWrite(new SessionChange(SessionChangeKind.Removed, id, null, _revision));
        }
    }

    async Task DispatchAsync()
    {
        await foreach (var change in _changes.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (Changed is not { } handlers) { continue; }
            // Each handler on its own: one that throws must not keep the handlers after it
            // (or later changes) from being heard.
            foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<SessionChange>>())
            {
                try { handler(this, change); }
                catch (Exception) { }
            }
        }
    }

    void StartWatchers()
    {
        Watch(Path.Combine(_options.CopilotHome, "session-state"), recursive: true);
        Watch(Path.Combine(_options.ClaudeHome, "projects"), recursive: true);
        Watch(Path.Combine(_options.ClaudeHome, "sessions"), recursive: false);
        Watch(Path.Combine(_options.CodexHome, "sessions"), recursive: true);
        Watch(Path.Combine(_options.CodexHome, "thread-writer-locks"), recursive: false);
        foreach (var root in _options.ClaudeRegistrationRoots.Concat(_options.CodexRegistrationRoots)) { Watch(root, recursive: false); }
    }

    // A watcher only shortens the wait. It may miss events (buffer overflow, network
    // drives, inotify limits), which polling catches, so its failures are ignored.
    void Watch(string directory, bool recursive)
    {
        if (!Directory.Exists(directory)) { return; }
        try
        {
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Changed += OnFileEvent;
            watcher.Created += OnFileEvent;
            watcher.Deleted += OnFileEvent;
            watcher.Renamed += OnFileEvent;
            watcher.Error += (_, _) => { _discoverRequested = true; _wake.Writer.TryWrite(true); };
            watcher.EnableRaisingEvents = true;
            lock (_gate) { _watchers.Add(watcher); }
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException) { }
    }

    void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // Appends to a transcript need only a re-read; anything else (a lock appearing, a
        // registration written, a folder made) can mean a session started or ended.
        if (!e.FullPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) { _discoverRequested = true; }
        _wake.Writer.TryWrite(true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_drivers is not null) { _drivers.Changed -= OnDriverChanged; }
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null) { await _loop.ConfigureAwait(false); }
        List<SessionSubscription> subscribers;
        lock (_gate)
        {
            foreach (var w in _watchers) { w.Dispose(); }
            _watchers.Clear();
            foreach (var e in _entries.Values) { e.Timer?.Dispose(); e.Timer = null; }
            subscribers = [.. _subscribers];
        }
        foreach (var s in subscribers) { s.Dispose(); }
        _changes.Writer.TryComplete();
        await _dispatch.ConfigureAwait(false);
        _stop.Dispose();
        _pass.Dispose();
    }
}
