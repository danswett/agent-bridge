namespace AgentBridge.Host;

// The verdict of a rate check. A refusal is always 'rate-limited' with the whole seconds until
// the oldest hit leaves its window, so an agent is told when to try again and never just "no".
public readonly record struct AgentLimitVerdict(bool Allowed, string? Reason, int RetryAfterSeconds)
{
    public const string RateLimited = "rate-limited";

    public static AgentLimitVerdict Admit { get; } = new(true, null, 0);
}

// Sliding in-memory rate windows for agent-to-agent traffic to local targets (#720 S4b, design
// 5.5). Pure and unwired: nothing calls it yet, and the limits are constructor arguments. The
// windows live in memory on purpose, so a restart resets them. Keys are opaque session ids; they
// are never logged or returned. Every check is all-or-nothing: a refusal records no hit in any
// window, so a refused message cannot use up a slot it was never given.
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
    readonly Window machineLaunches = new(Hour);

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
            var now = clock();
            var own = Get(originLaunches, origin, Hour, now);
            if (own is null) { return Refuse(3600); }
            var wait = Math.Max(own.WaitFor(now, launchesPerOrigin), machineLaunches.WaitFor(now, launchesPerMachine));
            if (wait > 0) { return Refuse(wait); }
            own.Hit(now);
            machineLaunches.Hit(now);
            return AgentLimitVerdict.Admit;
        }
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

        // Zero when another hit is admitted; otherwise the seconds until the oldest hit leaves.
        public int WaitFor(DateTimeOffset now, int limit)
        {
            // A clock stepped back leaves hits in the future; count them as now, so the wait is
            // never longer than one span.
            if (hits.Any(h => h > now)) { var kept = hits.Select(h => h > now ? now : h).ToArray(); hits.Clear(); foreach (var h in kept) { hits.Enqueue(h); } }
            while (hits.Count > 0 && now - hits.Peek() >= span) { hits.Dequeue(); }
            if (hits.Count < limit) { return 0; }
            var seconds = (int)Math.Ceiling((hits.Peek() + span - now).TotalSeconds);
            return Math.Max(1, seconds);
        }

        public bool IsIdle(DateTimeOffset now) => WaitFor(now, int.MaxValue) == 0 && hits.Count == 0;

        public void Hit(DateTimeOffset now) => hits.Enqueue(now);
    }
}
