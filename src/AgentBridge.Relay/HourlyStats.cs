using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBridge.Relay;

public sealed class StatsOptions
{
    // Bearer key for GET /v1/stats/hourly. Unset: the endpoint answers 404. An app setting
    // supplied by the deployment, never kept in the repo.
    public string? ReadKey { get; set; }
    public TimeSpan FlushEvery { get; set; } = TimeSpan.FromMinutes(1);
    public TimeProvider Time { get; set; } = TimeProvider.System;
    public IRosterStore? Store { get; set; }
}

// One route group in one hour: counts only; latency as bucket counts so replicas can add them.
public sealed class StatsCell
{
    [JsonPropertyName("n")] public long Count { get; set; }
    [JsonPropertyName("b")] public long[] Buckets { get; set; } = new long[HourlyStats.EdgesMs.Length + 1];
    [JsonPropertyName("c4")] public long Client { get; set; }
    [JsonPropertyName("c5")] public long Server { get; set; }

    public void Add(StatsCell o)
    {
        Count += o.Count; Client += o.Client; Server += o.Server;
        for (var i = 0; i < Buckets.Length && i < o.Buckets.Length; i++) { Buckets[i] += o.Buckets[i]; }
    }
}

public sealed class StatsHour
{
    [JsonPropertyName("v")] public int V { get; set; } = 1;
    [JsonPropertyName("groups")] public Dictionary<string, StatsCell> Groups { get; set; } = [];
}

// The row SRE reads: the only fields there are. No path, account, device or machine.
public sealed record StatsRow(string Group, long Count, int? P50Ms, int? P90Ms, long C4xx, long C5xx);

// One aggregate per hour per route group, kept in the relay's store so no machine holds it.
// Runs on every replica; a leaving replica flushes, a crash loses at most FlushEvery. State: stats/ in the store.
public sealed class HourlyStats
{
    // Upper edges of the latency buckets. A percentile is the edge of the bucket it falls in, so
    // it is an upper bound; 10000 means ten seconds or more.
    public static readonly int[] EdgesMs = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000];
    const int MaxAttempts = 5;
    readonly StatsOptions o;
    readonly TimeSpan storeTimeout;
    readonly IRosterStore? store;
    readonly ILogger<HourlyStats> log;
    readonly object gate = new();
    Dictionary<long, Dictionary<string, StatsCell>> pending = [];

    public HourlyStats(RelayOptions options, ILogger<HourlyStats> log)
    {
        o = options.Stats;
        storeTimeout = options.Roster.StoreTimeout;
        // A test that hands the roster its own store keeps stats out of it.
        store = o.Store ?? (options.Roster.Store is null ? MachineRoster.CreateStore(options.Roster) : new MemoryRosterStore());
        this.log = log;
    }

    public bool Enabled => store is not null;
    public bool Readable => Enabled && !string.IsNullOrEmpty(o.ReadKey);
    public TimeSpan FlushEvery => o.FlushEvery;

    // A fixed list, never the raw path (it can carry an id). Null for sockets (their time is the
    // connection's life) and for this endpoint.
    public static string? GroupOf(PathString path, bool webSocket)
    {
        if (webSocket || path.StartsWithSegments("/v1/stats")) { return null; }
        if (path.StartsWithSegments("/healthz") || path.StartsWithSegments("/readyz")) { return "health"; }
        if (path.StartsWithSegments("/v1/auth")) { return "auth"; }
        if (path.StartsWithSegments("/v1/push")) { return "push"; }
        if (path.StartsWithSegments(LocalBlobBackend.PathPrefix.TrimEnd('/'))) { return "blobs"; }
        return path.StartsWithSegments("/v1") ? "api" : "other";
    }

    public static int Bucket(double ms)
    {
        for (var i = 0; i < EdgesMs.Length; i++) { if (ms <= EdgesMs[i]) { return i; } }
        return EdgesMs.Length;
    }

    // The smallest bucket edge with at least ceil(p * n) requests at or below it. Null when empty.
    public static int? Percentile(long[] buckets, double p)
    {
        var n = buckets.Sum();
        if (n == 0) { return null; }
        var rank = (long)Math.Ceiling(p * n); long seen = 0;
        for (var i = 0; i < buckets.Length; i++)
        {
            seen += buckets[i];
            if (seen >= rank) { return EdgesMs[Math.Min(i, EdgesMs.Length - 1)]; }
        }
        return EdgesMs[^1];
    }

    public static StatsRow RowOf(string group, StatsCell c) =>
        new(group, c.Count, Percentile(c.Buckets, 0.5), Percentile(c.Buckets, 0.9), c.Client, c.Server);
    static long HourOf(DateTimeOffset t) => t.ToUnixTimeSeconds() / 3600;
    static string NameOf(long hour) => $"stats/v1/{DateTimeOffset.FromUnixTimeSeconds(hour * 3600):yyyyMMddHH}.json";

    public void Record(string group, TimeSpan elapsed, int status)
    {
        if (!Enabled) { return; }
        var hour = HourOf(o.Time.GetUtcNow());
        lock (gate)
        {
            if (!pending.TryGetValue(hour, out var groups)) { pending[hour] = groups = []; }
            if (!groups.TryGetValue(group, out var c)) { groups[group] = c = new StatsCell(); }
            c.Count++;
            c.Buckets[Bucket(elapsed.TotalMilliseconds)]++;
            if (status is >= 400 and < 500) { c.Client++; }
            else if (status >= 500) { c.Server++; }
        }
    }

    // Merges what this replica has counted into the stored hours. A hour that cannot be written
    // is kept for the next flush.
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (store is null) { return; }
        Dictionary<long, Dictionary<string, StatsCell>> taken;
        lock (gate) { taken = pending; pending = []; }
        var written = new HashSet<long>();
        try { foreach (var (hour, groups) in taken) { if (await MergeAsync(store, hour, groups, ct)) { written.Add(hour); } } }
        finally { Requeue(taken.Where(t => !written.Contains(t.Key))); }
    }

    // Puts back what was not written, also when a stop cancels the flush part way.
    void Requeue(IEnumerable<KeyValuePair<long, Dictionary<string, StatsCell>>> unwritten)
    {
        lock (gate)
        {
            foreach (var (hour, groups) in unwritten)
            {
                if (!pending.TryGetValue(hour, out var again)) { pending[hour] = groups; continue; }
                foreach (var (g, c) in groups) { if (again.TryGetValue(g, out var have)) { have.Add(c); } else { again[g] = c; } }
            }
        }
    }

    async Task<bool> MergeAsync(IRosterStore s, long hour, Dictionary<string, StatsCell> groups, CancellationToken ct)
    {
        try
        {
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(storeTimeout);
                var stored = await s.ReadAsync(NameOf(hour), timeout.Token);
                var doc = Parse(stored?.Body) ?? new StatsHour();
                foreach (var (g, c) in groups)
                {
                    if (doc.Groups.TryGetValue(g, out var have)) { have.Add(c); } else { doc.Groups[g] = c; }
                }
                if (await s.WriteAsync(NameOf(hour), JsonSerializer.SerializeToUtf8Bytes(doc), stored?.ETag, timeout.Token) is not null) { return true; }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Not the exception's message: a storage error can quote the account URL.
            log.LogWarning("Hourly stats were not written, will retry ({Error})", e.GetType().Name);
        }
        return false;
    }

    static StatsHour? Parse(byte[]? body)
    {
        if (body is null || body.Length == 0) { return null; }
        try
        {
            var d = JsonSerializer.Deserialize<StatsHour>(body);
            return d is { V: 1 } && d.Groups.Values.All(c => c.Buckets.Length == EdgesMs.Length + 1) ? d : null;
        }
        catch (JsonException) { return null; }
    }

    // The last `hours` hours, oldest first; an hour with no traffic has no rows.
    public async Task<IReadOnlyList<(DateTimeOffset Hour, IReadOnlyList<StatsRow> Rows)>> ReadAsync(int hours, CancellationToken ct)
    {
        var result = new List<(DateTimeOffset, IReadOnlyList<StatsRow>)>();
        if (store is null) { return result; }
        var now = HourOf(o.Time.GetUtcNow());
        for (var h = now - hours + 1; h <= now; h++)
        {
            var stored = await store.ReadAsync(NameOf(h), ct);
            var rows = Parse(stored?.Body)?.Groups.OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => RowOf(g.Key, g.Value)).ToList() ?? [];
            result.Add((DateTimeOffset.FromUnixTimeSeconds(h * 3600), rows));
        }
        return result;
    }

    public bool KeyMatches(string? header)
    {
        if (!Readable || header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) { return false; }
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(header[7..].Trim())),
            SHA256.HashData(Encoding.UTF8.GetBytes(o.ReadKey!)));
    }
}

public sealed class StatsWriter(HourlyStats stats) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(stats.FlushEvery);
        try { while (await timer.WaitForNextTickAsync(stoppingToken)) { await stats.FlushAsync(stoppingToken); } }
        catch (OperationCanceledException) { }
    }

    // What this replica has counted goes to the store before it leaves.
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        using var last = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await stats.FlushAsync(last.Token);
    }
}
