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
    readonly HashSet<string> _undeleted = new(StringComparer.Ordinal);
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
    // since this intake last (re)took the spool.
    bool _reconciled;

    public HookIntake(HookIntakeOptions options, IProcessInspector processes, TimeProvider? time = null, Action<string>? log = null)
    {
        _options = options;
        _processes = processes;
        _time = time ?? TimeProvider.System;
        _log = log ?? (_ => { });
        _handlers = new HookHandlers(options.RuntimeRoot, options.ClaudeHome, processes, _time);
        _spool = SpoolContract.SpoolDirectory(options.RuntimeRoot);
        _heartbeat = SpoolContract.HeartbeatPath(options.RuntimeRoot);
        _prunedAt = _time.GetUtcNow();
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
        lock (_gate)
        {
            if (_paused is not null) { _log("agent-ha-bridge has stopped; the host takes the hook spool again"); }
            _paused = null;
        }
        if (Beat() && !_reconciled)
        {
            ReconcileApprovals();
            _reconciled = true;
        }
        EnsureWatcher();
        var handled = Consume();
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
        }
        return handled;
    }

    void StandBack(string why)
    {
        lock (_gate)
        {
            if (_paused is null) { _log($"leaving the hook spool to agent-ha-bridge: {why}"); }
            _paused = why;
        }
        // The heartbeat is the daemon's now; the watcher would only wake passes that do nothing.
        _heartbeatOwned = false;
        _reconciled = false;
        _watcher?.Dispose();
        _watcher = null;
    }

    // The daemon writes its pid; a hook reads only the time. This host's pid tells a reader
    // whose it is, and lets this host take back only its own when it stops.
    bool Beat()
    {
        try
        {
            Directory.CreateDirectory(_options.RuntimeRoot);
            File.WriteAllText(_heartbeat, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _heartbeatOwned = true;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log($"could not keep the hook heartbeat ({e.Message}); hooks give their fixed reply until it is fresh again");
            _reconciled = false;
            return false;
        }
    }

    // A Codex hook that ran while nobody took events gave its fixed reply and went nowhere, so
    // the PreToolUse or Stop that ended an approval may never have arrived: the person answered
    // at the terminal while the host was down. Its marker would then offer an answered approval
    // to be answered again, typed into whatever the terminal shows now. So each approval asked
    // before the last such run is withdrawn when the intake takes the spool again; one asked
    // after it is still pending. The hook log is the record of those runs (both hooks write it).
    void ReconcileApprovals()
    {
        string[] markers;
        try { markers = Directory.Exists(_handlers.CodexRoot) ? Directory.GetFiles(_handlers.CodexRoot, "*.approval.json") : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        if (markers.Length == 0) { return; }
        if (HookStats.LastUnspooled(_options.RuntimeRoot, "codex") is not { } unheard) { return; }
        foreach (var marker in markers)
        {
            DateTimeOffset? created = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(marker));
                if (Json.Str(doc.RootElement, "Created") is { } text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at)) { created = at; }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
            if (created is { } c && c > unheard) { continue; }
            try
            {
                File.Delete(marker);
                _log($"withdrew Codex approval {Path.GetFileName(marker)}: a Codex hook ran at {unheard:o} with nobody taking events, so it may have been answered at the terminal");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

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
            if (!Directory.Exists(_spool)) { return 0; }
            files = Directory.GetFiles(_spool, "*.json").Where(f => f.EndsWith(".json", StringComparison.Ordinal)).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
        if (files.Length == 0) { return 0; }
        Array.Sort(files, StringComparer.Ordinal);

        var handled = 0;
        foreach (var path in files)
        {
            // agent-ha-bridge may have started part-way through: from here the files are its.
            if (_options.AgentHaBridgeActive() is { } active)
            {
                StandBack(active);
                break;
            }
            var name = Path.GetFileName(path);
            // Handled already; only its deletion failed. Its handler must not run twice: a Codex
            // PermissionRequest replayed after the PreToolUse that answered it would re-arm it.
            if (_undeleted.Contains(name))
            {
                if (Delete(path)) { _undeleted.Remove(name); }
                continue;
            }
            try
            {
                var bytes = ReadCopy(path);
                if (bytes is null) { continue; }
                var spooled = SpoolContract.Parse(bytes, name) ?? throw new InvalidDataException("not a spooled event");
                var notice = _handlers.Handle(spooled);
                handled++;
                Handled++;
                _attempts.Remove(name);
                if (!Delete(path)) { _undeleted.Add(name); }
                Publish(notice);
            }
            // Whatever went wrong with one event, it costs that event, never the pass.
            catch (Exception e)
            {
                var attempts = _attempts.GetValueOrDefault(name) + 1;
                if (attempts >= 2)
                {
                    _log($"dropped spooled hook {name} after {attempts} attempts: {e.Message}");
                    _attempts.Remove(name);
                    Dropped++;
                    if (!Delete(path)) { _undeleted.Add(name); }
                }
                else
                {
                    _attempts[name] = attempts;
                    _log($"spooled hook {name} failed, will retry: {e.Message}");
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
        _undeleted.RemoveWhere(n => !files.Any(f => f.EndsWith(n, StringComparison.Ordinal)));
        if (_attempts.Count > 0) { Wake(); }
        return handled;
    }

    // The file's bytes, read through a handle that shares delete, so whoever deletes it is
    // never refused because this host was reading (the shadow-read rule of G5). Null when it
    // has already gone.
    internal static byte[]? ReadCopy(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > SpoolContract.MaxEventBytes + 64 * 1024) { throw new InvalidDataException("larger than any hook writes"); }
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
            var live = claude.Processes.Select(p => p.Id).ToHashSet();
            foreach (var file in HookHandlers.SafeFiles(_handlers.ClaudeRoot))
            {
                using var doc = Read(file);
                if (doc is null) { continue; }
                var pid = Json.Int(doc.RootElement, "ProcessId") ?? 0;
                var updated = Json.Time(doc.RootElement, "Updated");
                var stale = updated is { } u && now - u > _options.StaleAfter;
                if (!(pid > 0 && live.Contains(pid)) && stale) { Delete(file); }
            }
        }
        var codex = _processes.Observe(AgentKind.Codex);
        if (codex.Known)
        {
            var live = codex.Processes.Select(p => p.Id).ToHashSet();
            foreach (var file in HookHandlers.SafeFiles(_handlers.CodexRoot))
            {
                if (file.EndsWith(".approval.json", StringComparison.OrdinalIgnoreCase)) { continue; }
                using var doc = Read(file);
                if (doc is null || Json.Str(doc.RootElement, "SessionId") is null) { continue; }
                var pid = Json.Int(doc.RootElement, "ProcessId") ?? 0;
                var ended = Json.Bool(doc.RootElement, "Ended") == true;
                var stale = Json.Time(doc.RootElement, "Updated") is { } u && now - u > _options.StaleAfter;
                if (ended || (pid > 0 && !live.Contains(pid)) || (pid <= 0 && stale)) { Delete(file); }
            }
        }
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
        // A heartbeat left behind would have hooks spool to nobody for up to a minute. Only
        // this host's own: a daemon that has taken over keeps its.
        if (_heartbeatOwned)
        {
            try
            {
                if (File.Exists(_heartbeat) && File.ReadAllText(_heartbeat).Trim() == Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)) { File.Delete(_heartbeat); }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        lock (_gate)
        {
            foreach (var s in _subscribers) { s.Writer.TryComplete(); }
            _subscribers.Clear();
        }
        _wake.Dispose();
    }
}
