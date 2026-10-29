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
    readonly Dictionary<string, (DateTime Created, int Pid, DateTimeOffset Started)> _codexLockHolders = new(StringComparer.OrdinalIgnoreCase);
    // Sessions the host is ending, and since when. Under _gate.
    readonly Dictionary<string, DateTimeOffset> _ending = new(StringComparer.Ordinal);
    readonly List<FileSystemWatcher> _watchers = [];
    readonly CancellationTokenSource _stop = new();
    readonly Task _dispatch;
    Task? _loop;
    long _revision;
    volatile bool _discoverRequested;
    DateTimeOffset _nextDiscovery;

    sealed class Entry(SessionTracker tracker)
    {
        public SessionTracker Tracker { get; } = tracker;
        public Session ListForm = null!;
        public Session GetForm = null!;
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

    public SessionSource(SessionSourceOptions options, IProcessInspector? processes = null, TimeProvider? time = null)
    {
        _options = options;
        _processes = processes ?? new SystemProcessInspector();
        _time = time ?? TimeProvider.System;
        _dispatch = Task.Run(DispatchAsync);
    }

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
        lock (_gate)
        {
            sessions = [.. Sorted().Select(e => e.ListForm)];
            revision = _revision;
        }
        if (start >= sessions.Count) { return new SessionList([], revision); }
        var page = Paging.Pages(sessions.Skip(start)).First();
        var next = start + page.Count < sessions.Count ? (start + page.Count).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return new SessionList(page, revision, next);
    }

    public Session? Get(string id)
    {
        lock (_gate) { return _entries.TryGetValue(id, out var e) ? e.GetForm : null; }
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
            subscription.Channel.Writer.TryWrite(new SessionEvent(Events.SessionsSynced, new SessionsSynced(_revision)));
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
        if (discover)
        {
            found = [.. Discover()];
            var live = found.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
            List<string> gone;
            lock (_gate) { gone = [.. _entries.Keys.Where(id => !live.Contains(id))]; }
            foreach (var id in gone) { Remove(id); }
        }
        else
        {
            lock (_gate) { found = [.. _entries.Values.Select(e => e.Tracker.Discovered)]; }
        }

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
                // next pass.
                continue;
            }
            Commit(entry ?? new Entry(tracker), full);
        }
    }

    IEnumerable<DiscoveredSession> Discover()
    {
        foreach (var s in CopilotDiscovery.Find(_options.CopilotHome, _processes.GetAgentProcesses(AgentKind.Copilot))) { yield return s; }
        // Listing processes is the expensive part; without a registration folder or Claude's
        // own sessions folder there is nothing it could match.
        var claudeSessions = Directory.Exists(Path.Combine(_options.ClaudeHome, "sessions"));
        if (claudeSessions || _options.ClaudeRegistrationRoots.Any(Directory.Exists))
        {
            var processes = _processes.GetAgentProcesses(AgentKind.Claude);
            var registered = RegistrationDiscovery.Find(AgentKind.Claude, _options.ClaudeRegistrationRoots, processes, FindClaudeTranscript);
            foreach (var s in registered) { yield return s; }
            if (claudeSessions)
            {
                // agent-ha-bridge's registration, where there is one, carries what its hooks
                // saw (status, permission prompts) and wins. One CLI owns one session.
                var ids = registered.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
                var pids = registered.Select(s => s.ProcessId).ToHashSet();
                foreach (var s in ClaudeSessionDiscovery.Find(_options.ClaudeHome, processes, FindClaudeTranscript))
                {
                    if (!ids.Contains(s.Id) && !pids.Contains(s.ProcessId)) { yield return s; }
                }
            }
        }
        var codexLocks = Directory.Exists(Path.Combine(_options.CodexHome, "thread-writer-locks"));
        if (codexLocks || _options.CodexRegistrationRoots.Any(Directory.Exists))
        {
            var processes = _processes.GetAgentProcesses(AgentKind.Codex);
            var registered = RegistrationDiscovery.Find(AgentKind.Codex, _options.CodexRegistrationRoots, processes, FindCodexTranscript);
            foreach (var s in registered) { yield return s; }
            if (codexLocks)
            {
                // agent-ha-bridge's registration, where there is one, carries what its hooks
                // saw (status, the approval marker) and wins. One CLI owns one session.
                var ids = registered.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var pids = registered.Select(s => s.ProcessId).ToHashSet();
                foreach (var s in CodexLockDiscovery.Find(_options.CodexHome, processes, _processes.FileHolders, _codexLockHolders, FindCodexTranscript))
                {
                    if (!ids.Contains(s.Id) && !pids.Contains(s.ProcessId)) { yield return s; }
                }
            }
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

    void Commit(Entry entry, Session full)
    {
        entry.Full = full;
        DateTimeOffset? endingSince;
        lock (_gate) { endingSince = _ending.TryGetValue(full.Id, out var at) ? at : null; }
        var shown = entry.Touched is { } touched && touched > full.UpdatedAt ? full with { UpdatedAt = touched } : full;
        if (endingSince is { } since) { shown = shown with { Status = SessionStatus.Ending, StatusSince = since }; }
        var getForm = SessionBudget.Fit(shown);
        var listForm = SessionBudget.Fit(shown.WithoutHistory());
        var json = JsonSerializer.Serialize(getForm, ProtocolJson.Options);
        lock (_gate)
        {
            if (json == entry.Json && _entries.ContainsKey(full.Id)) { return; }
            entry.GetForm = getForm;
            entry.ListForm = listForm;
            entry.Json = json;
            _entries[full.Id] = entry;
            _revision++;
            entry.Dirty = true;
            ScheduleEmit(entry);
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
