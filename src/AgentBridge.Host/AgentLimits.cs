namespace AgentBridge.Host;

// The verdict of a rate check. A refusal is always 'rate-limited' with the whole seconds until
// enough hits leave its window, so an agent is told when to try again and never just "no".
public readonly record struct AgentLimitVerdict(bool Allowed, string? Reason, int RetryAfterSeconds, bool Duplicate = false)
{
    public const string RateLimited = "rate-limited";

    public static AgentLimitVerdict Admit { get; } = new(true, null, 0);
}

// Sliding in-memory windows; a restart resets them. Message/read limits are constructor
// arguments; the launch gate supplies freshly read policy. Refusals spend no launch slot,
// but distinct requests and mismatched retries spend the separate attempt budget.
public sealed class AgentLimits
{
    static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    const int MaxKeys = 1024;

    readonly Func<DateTimeOffset> clock;
    readonly int perPair, perTarget, launchesPerOrigin, launchesPerMachine, readsPerOrigin;
    readonly object gate = new();
    readonly Dictionary<(string, string), Window> pairs = [];
    readonly Dictionary<string, Window> targets = [], originLaunches = [], reads = [];
    readonly Dictionary<string, Window> launchAttempts = [];
    readonly Dictionary<(string Origin, string Request), (string Hash, DateTimeOffset At)> launchRequests = [];
    readonly Window machineLaunches = new(Hour);
    DateTimeOffset? lastLaunchCheck;

    public AgentLimits(
        Func<DateTimeOffset>? clock = null,
        int messagesPerPairPerMinute = 6,
        int messagesPerTargetPerMinute = 12,
        int launchesPerOriginPerHour = 3,
        int launchesPerMachinePerHour = 10,
        int readsPerOriginPerMinute = 60)
    {
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        perPair = Positive(messagesPerPairPerMinute);
        perTarget = Positive(messagesPerTargetPerMinute);
        launchesPerOrigin = Positive(launchesPerOriginPerHour);
        launchesPerMachine = Positive(launchesPerMachinePerHour);
        readsPerOrigin = Positive(readsPerOriginPerMinute);
    }

    static int Positive(int value) => value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), "A limit must be at least 1.");

    public AgentLimitVerdict CheckMessage(string origin, string target)
    {
        lock (gate)
        {
            var now = clock();
            var pair = Get(pairs, (origin, target), Minute, now);
            var tgt = Get(targets, target, Minute, now);
            if (pair is null || tgt is null) { return Refuse(60); }
            var wait = Math.Max(pair.WaitFor(now, perPair), tgt.WaitFor(now, perTarget));
            if (wait > 0) { return Refuse(wait); }
            pair.Hit(now);
            tgt.Hit(now);
            return AgentLimitVerdict.Admit;
        }
    }

    public AgentLimitVerdict CheckLaunch(string origin)
    {
        lock (gate)
        {
            return CheckLaunch(origin, LaunchTime(), launchesPerOrigin, launchesPerMachine);
        }
    }

    public AgentLimitVerdict CheckLaunch(string origin, string request, string requestSha256, AgentLaunchLimits limits)
    {
        var originLimit = Positive(limits.LaunchesPerOriginPerHour);
        var machineLimit = Positive(limits.LaunchesPerMachinePerHour);
        var attemptLimit = Positive(limits.LaunchAttemptsPerOriginPerHour);
        lock (gate)
        {
            var now = LaunchTime();
            var key = (origin, request);
            var known = launchRequests.TryGetValue(key, out var remembered);
            if (known && remembered.Hash == requestSha256) { return new(false, "duplicate", 0, Duplicate: true); }
            // Never evict a live request to admit a flood of new ids.
            if (!known && launchRequests.Count >= MaxKeys)
            {
                return Refuse(Math.Max(1, (int)Math.Ceiling((launchRequests.Values.Min(r => r.At) + Hour - now).TotalSeconds)));
            }
            var attempts = Get(launchAttempts, origin, Hour, now);
            if (attempts is null) { return Refuse(3600); }
            var wait = attempts.WaitFor(now, attemptLimit);
            if (wait > 0) { return Refuse(wait); }
            attempts.Hit(now);
            if (known) { return new(false, "requestReused", 0); }
            launchRequests.Add(key, (requestSha256, now));
            return CheckLaunch(origin, now, originLimit, machineLimit);
        }
    }

    AgentLimitVerdict CheckLaunch(string origin, DateTimeOffset now, int originLimit, int machineLimit)
    {
        var own = Get(originLaunches, origin, Hour, now);
        if (own is null) { return Refuse(3600); }
        var wait = Math.Max(own.WaitFor(now, originLimit), machineLaunches.WaitFor(now, machineLimit));
        if (wait > 0) { return Refuse(wait); }
        own.Hit(now);
        machineLaunches.Hit(now);
        return AgentLimitVerdict.Admit;
    }

    DateTimeOffset LaunchTime()
    {
        var now = clock();
        if (lastLaunchCheck is { } last && now < last)
        {
            // A duplicate must observe rollback too, without recording another hit.
            foreach (var window in originLaunches.Values.Concat(launchAttempts.Values)) { window.WaitFor(now, int.MaxValue); }
            machineLaunches.WaitFor(now, int.MaxValue);
        }
        lastLaunchCheck = now;
        foreach (var entry in launchRequests.ToArray())
        {
            var at = entry.Value.At > now ? now : entry.Value.At;
            if (now - at >= Hour) { launchRequests.Remove(entry.Key); }
            else if (at != entry.Value.At) { launchRequests[entry.Key] = (entry.Value.Hash, at); }
        }
        return now;
    }

    public AgentLimitVerdict CheckRead(string origin)
    {
        lock (gate)
        {
            var now = clock();
            var window = Get(reads, origin, Minute, now);
            if (window is null) { return Refuse(60); }
            var wait = window.WaitFor(now, readsPerOrigin);
            if (wait > 0) { return Refuse(wait); }
            window.Hit(now);
            return AgentLimitVerdict.Admit;
        }
    }

    static AgentLimitVerdict Refuse(int seconds) => new(false, AgentLimitVerdict.RateLimited, seconds);

    static Window? Get<TKey>(Dictionary<TKey, Window> windows, TKey key, TimeSpan span, DateTimeOffset now) where TKey : notnull
    {
        if (!windows.TryGetValue(key, out var window))
        {
            // Forget only windows whose hits have all expired. If every one is still limiting, the
            // new key is refused rather than another key's window wiped: this fails closed.
            if (windows.Count >= MaxKeys)
            {
                foreach (var stale in windows.Where(w => w.Value.IsIdle(now)).Select(w => w.Key).ToList()) { windows.Remove(stale); }
                if (windows.Count >= MaxKeys) { return null; }
            }
            windows[key] = window = new Window(span);
        }
        return window;
    }

    sealed class Window(TimeSpan span)
    {
        readonly Queue<DateTimeOffset> hits = new();

        // A lowered limit can need several hits to expire before another is admitted.
        public int WaitFor(DateTimeOffset now, int limit)
        {
            // A clock stepped back leaves hits in the future; count them as now, so the wait is
            // never longer than one span.
            if (hits.Any(h => h > now)) { var kept = hits.Select(h => h > now ? now : h).ToArray(); hits.Clear(); foreach (var h in kept) { hits.Enqueue(h); } }
            while (hits.Count > 0 && now - hits.Peek() >= span) { hits.Dequeue(); }
            if (hits.Count < limit) { return 0; }
            var seconds = (int)Math.Ceiling((hits.ElementAt(hits.Count - limit) + span - now).TotalSeconds);
            return Math.Max(1, seconds);
        }

        public bool IsIdle(DateTimeOffset now) => WaitFor(now, int.MaxValue) == 0 && hits.Count == 0;

        public void Hit(DateTimeOffset now) => hits.Enqueue(now);
    }
}
