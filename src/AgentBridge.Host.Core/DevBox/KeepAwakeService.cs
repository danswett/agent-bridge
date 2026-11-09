namespace AgentBridge.Host.Core.DevBox;

// One pass at a time, across every profile and process of this user: two hosts (a profile beside
// the default one, say) each moving the same Dev Box's schedule would be two writers to it. A
// lock file rather than agent-ha-bridge's named mutex, because a mutex belongs to the thread that
// took it and a pass awaits across threads; on Windows the host also holds agent-ha-bridge's own
// pass mutexes (AgentHaBridgeProbe.HoldKeepAwake), so a pass of its task started meanwhile, by a
// rollback say, exits without a cloud call. Taken without waiting: a pass that finds one running
// has nothing left to do.
public interface IKeepAwakeLock
{
    IDisposable? TryTake();
}

public sealed class FileKeepAwakeLock(string path) : IKeepAwakeLock
{
    // Null only while another pass holds it. A lock that cannot be had at all (a folder that cannot
    // be made, a file it may not open) throws, so the pass fails loudly instead of reading as busy
    // for ever.
    public IDisposable? TryTake()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None); }
        catch (IOException e) when (Held(e)) { return null; }
    }

    // As UserLock reads it: ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION on Windows, the flock's
    // EWOULDBLOCK (Linux 11, macOS 35) elsewhere.
    static bool Held(IOException e) => OperatingSystem.IsWindows()
        ? e.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)
        : e.HResult is 11 or 35;
}

// Several locks taken as one: all of them, or none (those taken are let go), in order.
public sealed class KeepAwakeLocks(params IKeepAwakeLock[] locks) : IKeepAwakeLock
{
    public IDisposable? TryTake()
    {
        var taken = new List<IDisposable>();
        try
        {
            foreach (var l in locks)
            {
                if (l.TryTake() is not { } held)
                {
                    Release(taken);
                    return null;
                }
                taken.Add(held);
            }
        }
        catch
        {
            Release(taken);
            throw;
        }
        return new All(taken);
    }

    static void Release(List<IDisposable> taken)
    {
        for (var i = taken.Count - 1; i >= 0; i--) { taken[i].Dispose(); }
        taken.Clear();
    }

    sealed class All(List<IDisposable> taken) : IDisposable
    {
        public void Dispose() => Release(taken);
    }
}

public sealed class DelegateKeepAwakeLock(Func<IDisposable?> take) : IKeepAwakeLock
{
    public IDisposable? TryTake() => take();
}

// What the keep-awake needs from the machine, so tests stand in for all of it.
public sealed class KeepAwakeOptions
{
    public required DevBoxSettings Settings { get; init; }
    // This machine's Dev Box identity, read again for every pass (a reprovision moves it), with why
    // there is none when there is a settings file it could not use.
    public required Func<(DevBoxIdentity? Identity, string? Problem)> FindIdentity { get; init; }
    public required IDevCenterToken Token { get; init; }
    public required IDevCenterHttp Http { get; init; }
    public required IKeepAwakeLock Lock { get; init; }
    // Why agent-ha-bridge's own keep-awake owns this Dev Box's schedule now (its task is enabled,
    // or a pass of it runs, or that could not be told), or null when it does not. Asked before
    // every pass: two writers to one schedule is the double-writer problem G5 exists for.
    public Func<KeepAwakeOwner?> AgentHaBridgeKeepAwake { get; init; } = () => null;
    // How soon after the host starts the first pass runs: soon, since a host started on a Dev Box
    // nobody is connected to may have a stop already pending.
    public TimeSpan FirstPass { get; init; } = TimeSpan.FromSeconds(30);
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

// The keep-awake inside the host (design §6.1 Host.DevBox: agent-ha-bridge's behaviour, scheduled
// inside the host instead of by a task of its own): a short first pass, then one every
// devBox.intervalMinutes while devBox.keepAwake is on. Every pass is logged, no-action included,
// and a failure loudly, since a keep-awake that fails quietly looks exactly like a Dev Box that
// hibernated for no reason.
public sealed class KeepAwakeService : IAsyncDisposable
{
    readonly KeepAwakeOptions options;
    readonly Action<string> log;
    readonly CancellationTokenSource stop = new();
    readonly object sync = new();
    // Completed to bring the next pass forward (keep-awake just turned on); replaced once seen.
    TaskCompletionSource woken = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile bool enabled;
    Task? loop;
    (DateTimeOffset At, KeepAwakeResult Result)? last;
    DateTimeOffset? next;
    bool isDevBox;
    string? identityProblem;

    public KeepAwakeService(KeepAwakeOptions options, Action<string>? log = null)
    {
        this.options = options;
        this.log = log ?? (_ => { });
        enabled = options.Settings.KeepAwake;
        (var identity, identityProblem) = options.FindIdentity();
        isDevBox = identity is not null || identityProblem is not null;
    }

    public event Action? Changed;

    // Whether passes run: devBox.keepAwake, applied at once when the app changes it.
    public bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value) { return; }
            enabled = value;
            log($"keep-awake is {(value ? "on" : "off")} (devBox.keepAwake, set by the app; applies now)");
            // Turned on: a pass now, rather than up to an interval from now.
            if (value) { lock (sync) { woken.TrySetResult(); } }
            Changed?.Invoke();
        }
    }

    public TimeSpan Interval => TimeSpan.FromMinutes(options.Settings.IntervalMinutes);

    // What the app and status show: whether this is a Dev Box at all, the last pass and the next.
    public KeepAwakeState State
    {
        get { lock (sync) { return new KeepAwakeState(isDevBox, identityProblem, enabled, options.Settings.IntervalMinutes, last?.At, last?.Result, enabled ? next : null); } }
    }

    public void Start()
    {
        if (isDevBox && identityProblem is { } unusable && enabled)
        {
            log($"keep-awake is on (devBox.keepAwake), but this Dev Box's agent settings could not be used ({unusable}); every pass will fail until they can -- THIS DEV BOX MAY HIBERNATE WHEN DISCONNECTED");
        }
        else if (isDevBox)
        {
            log(enabled ? $"keep-awake is on (devBox.keepAwake): a pass in {options.FirstPass.TotalSeconds:F0} s, then every {options.Settings.IntervalMinutes} min"
                : "keep-awake is off (devBox.keepAwake): unless agent-ha-bridge's own keep-awake runs here, this Dev Box may hibernate when its last session disconnects");
        }
        else if (enabled)
        {
            log("keep-awake is on (devBox.keepAwake) but this machine is not a Dev Box (it has no Dev Box agent settings); it is looked for again before every pass");
        }
        // Started whether or not this looks like a Dev Box now: the agent's settings file can be
        // unreadable for a moment (the agent rewriting or updating it), and one bad read at logon
        // must not leave the Dev Box to hibernate until the host restarts. agent-ha-bridge's task
        // read it afresh on every run too.
        loop ??= Task.Run(async () =>
        {
            var wait = options.FirstPass;
            while (!stop.IsCancellationRequested)
            {
                DateTimeOffset due;
                lock (sync) { next = due = options.Time.GetUtcNow() + wait; }
                Changed?.Invoke();
                try { await WaitAsync(due); }
                catch (OperationCanceledException) { break; }
                try
                {
                    if (enabled) { await PassAsync(dryRun: false, stop.Token); }
                    // Off, it still looks, so the app offers the switch once this is seen to be a Dev Box.
                    else { Identify(); }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception e) { Record(new KeepAwakeResult(KeepAwakeResult.Failed, $"the pass failed: {e.GetType().Name}: {e.Message}")); }
                wait = Interval;
            }
        });
    }

    // This machine's identity now. Its Dev Box agent's settings being there at all, usable or not,
    // is what makes it a Dev Box for the status, so a file the host cannot use shows as an alarm in
    // the app rather than the setting disappearing.
    DevBoxIdentity? Identify()
    {
        var (identity, problem) = options.FindIdentity();
        var present = identity is not null || problem is not null;
        bool changed;
        lock (sync)
        {
            changed = isDevBox != present || identityProblem != problem;
            isDevBox = present;
            identityProblem = problem;
        }
        if (changed) { Changed?.Invoke(); }
        return identity;
    }

    // Until the pass is due or brought forward, on the injected clock. Measured to when it is due,
    // not for a fixed time from now, so a clock that moved on meanwhile does not put it off again.
    async Task WaitAsync(DateTimeOffset due)
    {
        Task signal;
        lock (sync) { signal = woken.Task; }
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        // The clock can move between reading it and the timer being registered: a timer made after
        // that move is due its delay from the later time, so it fires late or, on a clock nobody
        // advances again, never. Reading the clock once more after registering shows whether it
        // moved; if so the timer is made again from the later time. Once it did not move, any
        // later move reaches the registered timer.
        while (true)
        {
            var before = options.Time.GetUtcNow();
            var remaining = due - before;
            if (remaining <= TimeSpan.Zero) { break; }
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(waiting.Token);
            var delay = Task.Delay(remaining, options.Time, attempt.Token);
            if (options.Time.GetUtcNow() != before) { attempt.Cancel(); continue; }
            await Task.WhenAny(signal, delay);
            break;
        }
        waiting.Cancel();
        stop.Token.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (woken.Task.IsCompleted) { woken = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        }
    }

    // One pass now: with dryRun, only reads Dev Center and says what it would do.
    public async Task<KeepAwakeResult> PassAsync(bool dryRun = false, CancellationToken cancellationToken = default)
    {
        var identity = Identify();
        // The Dev Box agent's settings are here but cannot be used (a shape not known, a file being
        // rewritten, an address refused): this is a Dev Box that cannot be kept awake, which is a
        // failure to raise every pass, not "not a Dev Box" said once.
        if (identity is null && State.Problem is { } unusable)
        {
            return Record(new(KeepAwakeResult.Failed, $"this Dev Box's agent settings could not be used, so it cannot be kept awake: {unusable}"), dryRun);
        }
        if (identity is null) { return Record(new(KeepAwakeResult.NotADevBox, "this machine is not a Dev Box"), dryRun); }
        // A dry run changes nothing, so agent-ha-bridge's keep-awake running beside it does not
        // matter, nor does another pass. Standing back only because Task Scheduler could not be
        // read is an alarm: nothing may be keeping the Dev Box awake.
        if (!dryRun && options.AgentHaBridgeKeepAwake() is { } owner)
        {
            return Record(new(KeepAwakeResult.StoodBack, owner.Reason) { Unsure = !owner.Known }, dryRun);
        }
        IDisposable? held = null;
        if (!dryRun)
        {
            try { held = options.Lock.TryTake(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Record(new(KeepAwakeResult.Failed, $"the keep-awake lock could not be taken ({e.Message}); no Dev Center call was made"));
            }
            if (held is null) { return Record(new(KeepAwakeResult.Busy, "another keep-awake pass is running (this host's or agent-ha-bridge's); no Dev Center call was made")); }
        }
        using (held)
        {
            var result = await KeepAwakePass.RunAsync(identity, options.Token, options.Http, options.Time.GetUtcNow(), dryRun, log, cancellationToken);
            return Record(result, dryRun);
        }
    }

    KeepAwakeResult Record(KeepAwakeResult result, bool dryRun = false)
    {
        var line = $"keep-awake{(dryRun ? " (dry run)" : "")}: {result.Outcome}: {result.Detail}";
        // On a machine that is not a Dev Box with keep-awake on anyway, said once, not every pass.
        bool repeated;
        lock (sync) { repeated = !dryRun && result.Outcome == KeepAwakeResult.NotADevBox && last?.Result == result; }
        if (!repeated) { log(result.Alarming ? $"{line} -- THIS DEV BOX MAY HIBERNATE WHEN DISCONNECTED" : line); }
        if (dryRun) { return result; }
        lock (sync) { last = (options.Time.GetUtcNow(), result); }
        Changed?.Invoke();
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (loop is not null)
        {
            try { await loop; }
            catch (OperationCanceledException) { }
        }
        stop.Dispose();
    }
}

// The keep-awake's state for the host's status (HostStatus.DevBox). IsDevBox: the Dev Box agent's
// settings file is here, usable or not, so the app shows the setting and any alarm.
public sealed record KeepAwakeState(bool IsDevBox, string? Problem, bool Enabled, int IntervalMinutes, DateTimeOffset? LastAt, KeepAwakeResult? Last, DateTimeOffset? NextAt);

// Who keeps this Dev Box awake instead of the host: agent-ha-bridge's own keep-awake. Known: there
// is positive evidence (its task enabled or running, a pass of it holding its mutex); false when
// Task Scheduler could not be read, so standing back is only caution, and nothing may be keeping
// the Dev Box awake at all.
public sealed record KeepAwakeOwner(string Reason, bool Known = true);
