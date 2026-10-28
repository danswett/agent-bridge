using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Usage;

// Asks each agent for its allowance at the pace its sessions set, and says when what it would
// show has changed. The pacing is agent-ha-bridge's Sync-DaemonUsage:
//   * every Interval while a session of that agent is open on this machine, every
//     IdleInterval while none is;
//   * a session opening or closing brings the next ask forward, never within MinimumGap of
//     the last one;
//   * a clock that went backwards makes an ask due rather than holding every ask off.
// One difference: while agent-ha-bridge's daemon runs for this user it is already asking the
// same vendors for the same accounts, so this host asks only at the idle pace.
//
// A reading is announced (Changed) when anything but its time changed, or when the last
// announcement of it is Republish old, so a client can tell a host still confirming an
// unchanged figure from one that stopped.
public sealed class UsageMonitor : IAsyncDisposable
{
    readonly IReadOnlyList<IUsageCollector> collectors;
    readonly ISessionSource sessions;
    readonly UsageOptions options;
    readonly TimeProvider time;
    readonly Func<string?> agentHaBridge;
    readonly Action<string> log;
    readonly Dictionary<AgentKind, State> states = [];
    readonly SemaphoreSlim pass = new(1, 1);
    readonly object sync = new();
    readonly CancellationTokenSource stop = new();
    UsageReport report = UsageReport.Empty;
    long version;
    Task? loop;
    bool? deferring;
    DateTimeOffset deferCheckedAt;

    public UsageMonitor(IEnumerable<IUsageCollector> collectors, ISessionSource sessions, UsageOptions options, TimeProvider? time = null,
        Func<string?>? agentHaBridge = null, Action<string>? log = null)
    {
        this.collectors = [.. collectors];
        this.sessions = sessions;
        this.options = options;
        this.time = time ?? TimeProvider.System;
        this.agentHaBridge = agentHaBridge ?? (() => null);
        this.log = log ?? (_ => { });
        foreach (var c in this.collectors) { states[c.Agent] = new State(); }
    }

    // Raised after a pass that changed what a client shows, with the report and its version.
    public event Action<UsageReport, long>? Changed;

    // The latest reading of every agent that has said anything, newest figures included, and
    // the version of the last announcement.
    public (UsageReport Report, long Version) Current
    {
        get { lock (sync) { return (report, version); } }
    }

    public static IReadOnlyList<IUsageCollector> Collectors(UsageOptions options, IUsageHttp http, ICommandRunner commands, TimeProvider? time = null, Action<string>? log = null)
    {
        var store = OperatingSystem.IsWindows() ? new WindowsCredentialStore() : null;
        var credentials = new CopilotCredentials(options, commands, store, time: time, log: log);
        return [new CopilotUsageCollector(options, http, credentials, time), new ClaudeUsageCollector(options, http, time), new CodexUsageCollector(options)];
    }

    public void Start()
    {
        loop ??= Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { await PassAsync(stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception e) { log($"usage: a pass failed: {e.GetType().Name}"); }
                try { await Task.Delay(options.Tick, time, stop.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    // One look at what is due, and an ask of each agent that is. Returns whether anything
    // was announced.
    public async Task<bool> PassAsync(CancellationToken cancellationToken = default)
    {
        await pass.WaitAsync(cancellationToken);
        try { return await PassLockedAsync(cancellationToken); }
        finally { pass.Release(); }
    }

    async Task<bool> PassLockedAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var open = sessions.Snapshot().Select(s => s.Agent).ToHashSet();
        bool? defer = null;
        var due = new List<(IUsageCollector Collector, State State)>();
        foreach (var collector in collectors)
        {
            var state = states[collector.Agent];
            var sessionOpen = open.Contains(collector.Agent);
            // Asked only when a session would otherwise set the busy pace: the probe lists
            // named objects and scheduled tasks, which is not free.
            if (sessionOpen) { defer ??= Deferring(now); }
            var busy = sessionOpen && defer != true;
            if (!Due(state, busy, now)) { continue; }
            // Stamped before asking, so an ask that fails still waits its turn rather than
            // being repeated every tick until one works.
            state.CheckedAt = now;
            if (collector.Agent == AgentKind.Copilot && busy && !state.Busy && collector.SessionOpened(now))
            {
                log("usage: a Copilot session opened here; asking gh for the allowance again");
            }
            state.Busy = busy;
            due.Add((collector, state));
        }
        if (due.Count == 0) { return false; }

        var announce = false;
        foreach (var (collector, state) in due)
        {
            AgentUsage? read;
            try { read = await collector.CollectAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                read = UsageRead.Failure(collector.Agent, $"Usage for {collector.Agent.ToString().ToLowerInvariant()} could not be collected: {UsageFetch.Describe(e)}", now);
            }
            // Nothing to say: whatever was read before stands and ages.
            if (read is null) { continue; }
            read = UsageProtocol.FitAgent(read);
            Say(collector.Agent, state, read);
            lock (sync) { state.Latest = read; }
            var signature = Signature(read);
            if (signature == state.Signature && now - state.PublishedAt < options.Republish) { continue; }
            state.Signature = signature;
            state.PublishedAt = now;
            announce = true;
        }

        UsageReport current;
        long v;
        lock (sync)
        {
            report = UsageProtocol.Fit(new UsageReport([.. collectors.Select(c => states[c.Agent].Latest).OfType<AgentUsage>()]));
            if (announce) { version++; }
            (current, v) = (report, version);
        }
        if (announce) { Changed?.Invoke(current, v); }
        return announce;
    }

    bool Due(State state, bool busy, DateTimeOffset now)
    {
        var elapsed = now - state.CheckedAt;
        if (elapsed < TimeSpan.Zero) { return true; }
        if (elapsed >= (busy ? options.Interval : options.IdleInterval)) { return true; }
        return busy != state.Busy && elapsed >= Min(options.Interval, options.MinimumGap);
    }

    static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    // At most once a minute: whether agent-ha-bridge started or stopped matters to the pace,
    // which is minutes, and the probe should not run on every tick.
    bool Deferring(DateTimeOffset at)
    {
        if (deferring is { } known && at - deferCheckedAt < TimeSpan.FromMinutes(1) && at >= deferCheckedAt) { return known; }
        deferCheckedAt = at;
        string? reason;
        try { reason = agentHaBridge(); }
        catch (Exception) { reason = "unknown"; }
        var now = reason is not null;
        if (deferring != now && (deferring is not null || now))
        {
            log(now ? "usage: agent-ha-bridge is asking for this user's allowances too; asking only every "
                + $"{options.IdleInterval.TotalMinutes:0} minutes from here" : "usage: agent-ha-bridge has stopped; asking at the usual pace");
        }
        deferring = now;
        return now;
    }

    // Said when an agent's state changes, not every poll, and never with an account in it.
    void Say(AgentKind agent, State state, AgentUsage read)
    {
        var said = read.Windows.Count > 0
            ? $"read from {read.Source.ToString().ToLowerInvariant()}{(read.Error is null ? "" : $" ({read.Error})")}"
            : $"could not be read: {read.Error}";
        if (said == state.Said) { return; }
        state.Said = said;
        log($"usage: {agent.ToString().ToLowerInvariant()} {said}");
    }

    // Everything about a reading but when it was taken: the same windows under another
    // account are another reading.
    static string Signature(AgentUsage a) =>
        string.Join('|', a.Agent, a.Account, a.AccountKey, a.Plan, a.Source, a.Error,
            string.Join(',', a.Windows.Select(w => $"{w.Key}={w.Percent.ToString(System.Globalization.CultureInfo.InvariantCulture)}@{w.ResetsAt?.ToUnixTimeSeconds()}")));

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

    sealed class State
    {
        public DateTimeOffset CheckedAt = DateTimeOffset.MinValue;
        public bool Busy;
        public string Signature = "";
        public DateTimeOffset PublishedAt = DateTimeOffset.MinValue;
        public AgentUsage? Latest;
        public string? Said;
    }
}
