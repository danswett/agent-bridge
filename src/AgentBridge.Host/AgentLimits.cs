namespace AgentBridge.Host;

// The verdict of a rate check. A refusal is always 'rate-limited' with the whole seconds until
// enough hits leave its window, so an agent is told when to try again and never just "no".
public readonly record struct AgentLimitVerdict(bool Allowed, string? Reason, int RetryAfterSeconds, bool Duplicate = false)
{
    public const string RateLimited = "rate-limited";

    public static AgentLimitVerdict Admit { get; } = new(true, null, 0);
}

// Sliding windows; launch windows can rebuild from the audit. Message/read limits are constructor
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
    readonly HashSet<(string Origin, string Request)> unresolvedRequests = [];
    readonly Window machineLaunches = new(Hour);
    DateTimeOffset? lastLaunchCheck;
    readonly AgentAuditLog? launchAudit;
    DateTimeOffset? rebuildAt;
    public bool CapRebuilding { get; private set; }

    public AgentLimits(
        Func<DateTimeOffset>? clock = null,
        int messagesPerPairPerMinute = 6,
        int messagesPerTargetPerMinute = 12,
        int launchesPerOriginPerHour = 3,
        int launchesPerMachinePerHour = 10,
        int readsPerOriginPerMinute = 60,
        AgentAuditLog? launchAudit = null)
    {
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        perPair = Positive(messagesPerPairPerMinute);
        perTarget = Positive(messagesPerTargetPerMinute);
        launchesPerOrigin = Positive(launchesPerOriginPerHour);
        launchesPerMachine = Positive(launchesPerMachinePerHour);
        readsPerOrigin = Positive(readsPerOriginPerMinute);
        this.launchAudit = launchAudit;
        if (launchAudit is not null) { RebuildLaunches(); }
    }

    void RebuildLaunches()
    {
        CapRebuilding = true;
        var now = clock();
        rebuildAt = now.AddSeconds(30);
        var (rows, complete) = GateAuditProjection.Load(launchAudit!.Profile);
        if (!complete || (rows.Count > 0 || Directory.Exists(Path.Combine(launchAudit.Profile, AgentAuditLog.FolderName)))
            && !launchAudit.VerifyExistingLaunch()) { return; }
        var launches = rows.Where(r => r.Action == "session.launch"
            && (r.RequestSha256 is not null || r.Gate?.ActorKind == "agent" || r.Principal.StartsWith("agent:", StringComparison.Ordinal))).ToArray();
        var restored = new List<(string Origin, string Request, string Hash, DateTimeOffset At, bool Charged, bool Pending)>();
        foreach (var group in launches.GroupBy(r => (r.Principal, r.Request)))
        {
            var reservations = group.Where(r => r.Outcome == "reserved").OrderBy(r => r.At).ToArray();
            var bound = group.OrderBy(r => r.At).FirstOrDefault(r => r.RequestSha256 is not null || r.Gate?.LaunchBinding is not null);
            if (bound is null)
            {
                if (group.Any(r => r.Outcome is "reserved" or "started" or "failed" or "unconfirmed" or "interrupted" or "attempted")) { return; }
                continue;
            }
            if (string.IsNullOrEmpty(group.Key.Request)) { return; }
            var hash = bound.RequestSha256 ?? bound.Gate!.LaunchBinding!;
            if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)
                || reservations.Any(r => (r.RequestSha256 ?? r.Gate?.LaunchBinding) != hash)) { return; }
            var pending = reservations.Any(r => !group.Any(t => t.Outcome is "started" or "refused" or "failed" or "busy"
                && (r.Gate is { } reserved ? t.Gate is { Phase: "terminal" } terminal && terminal.Operation == reserved.Operation
                    && terminal.LaunchBinding == hash : t.RequestSha256 == hash)));
            var at = reservations.FirstOrDefault()?.At ?? bound.At;
            if (at > now) { at = now; }
            if (pending || now - at < TimeSpan.FromDays(2))
            { restored.Add((group.Key.Principal, group.Key.Request, hash, at, reservations.Length > 0 && now - at < Hour, pending)); }
        }
        if (restored.Count > MaxKeys) { return; }
        originLaunches.Clear();
        launchAttempts.Clear();
        launchRequests.Clear();
        unresolvedRequests.Clear();
        machineLaunches.Clear();
        foreach (var row in restored.OrderBy(r => r.At))
        {
            launchRequests.Add((row.Origin, row.Request), (row.Hash, row.At));
            if (row.Pending) { unresolvedRequests.Add((row.Origin, row.Request)); }
            if (!row.Charged) { continue; }
            Get(originLaunches, row.Origin, Hour, now)!.Hit(row.At);
            machineLaunches.Hit(row.At);
        }
        foreach (var row in launches.Where(r => (r.RequestSha256 is not null && r.Outcome is "reserved" or "attempted")
            || r.RequestSha256 is null && r.Gate?.Phase == "intent").OrderBy(r => r.At))
        {
            var at = row.At > now ? now : row.At;
            if (now - at >= Hour) { continue; }
            var window = Get(launchAttempts, row.Principal, Hour, now);
            if (window is null) { return; }
            window.Hit(at);
        }
        CapRebuilding = false;
        lastLaunchCheck = now;
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
            if (launchAudit is not null) { throw new InvalidOperationException("Durable launch checks require a request binding."); }
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
            if (CapRebuilding)
            {
                if (clock() >= rebuildAt) { RebuildLaunches(); }
                if (CapRebuilding) { return new(false, "capRebuilding", 30); }
            }
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
            AgentLimitVerdict result;
            if (known) { result = new(false, "requestReused", 0); }
            else
            {
                launchRequests.Add(key, (requestSha256, now));
                result = CheckLaunch(origin, now, originLimit, machineLimit);
            }
            if (launchAudit is not null && !launchAudit.Append(new("local", request, origin, [], "session.launch", request,
                result.Allowed ? "reserved" : "attempted", RequestSha256: requestSha256)))
            {
                CapRebuilding = true;
                rebuildAt = now.AddSeconds(30);
                return new(false, "auditUnwritable", 30);
            }
            if (launchAudit is not null && result.Allowed) { unresolvedRequests.Add(key); }
            return result;
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
            if (!unresolvedRequests.Contains(entry.Key) && now - at >= (launchAudit is null ? Hour : TimeSpan.FromDays(2))) { launchRequests.Remove(entry.Key); }
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
        public void Clear() => hits.Clear();
    }
}
