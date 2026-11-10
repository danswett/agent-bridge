using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentBridge.Relay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentBridge.Transport.Tests;

public class HourlyStatsTests
{
    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    static readonly DateTimeOffset Noon = new(2026, 11, 9, 12, 10, 0, TimeSpan.Zero);
    static (HourlyStats Stats, MemoryRosterStore Store, Clock Clock) Make(string? key = "k")
    {
        var clock = new Clock(Noon);
        var store = new MemoryRosterStore();
        var o = new RelayOptions { Stats = { Store = store, Time = clock, ReadKey = key } };
        return (new HourlyStats(o, NullLogger<HourlyStats>.Instance), store, clock);
    }

    static long[] Buckets(params (int Bucket, long Count)[] fill)
    {
        var b = new long[HourlyStats.EdgesMs.Length + 1];
        foreach (var (i, n) in fill) { b[i] = n; }
        return b;
    }

    [Fact]
    public void The_median_and_p90_are_the_edge_of_the_bucket_holding_that_rank()
    {
        // 10 requests: 5 at <=10 ms, 4 at <=100 ms, 1 at <=1000 ms. Rank 5 and rank 9.
        var b = Buckets((3, 5), (6, 4), (9, 1));

        Assert.Equal(10, HourlyStats.Percentile(b, 0.5));
        Assert.Equal(100, HourlyStats.Percentile(b, 0.9));
    }

    [Fact]
    public void A_request_slower_than_the_last_edge_reports_that_edge_and_an_empty_hour_reports_nothing()
    {
        Assert.Equal(10000, HourlyStats.Percentile(Buckets((HourlyStats.EdgesMs.Length, 3)), 0.9));
        Assert.Null(HourlyStats.Percentile(Buckets(), 0.5));
    }

    [Fact]
    public async Task Two_flushes_and_two_replicas_add_into_one_row_per_group_and_hour()
    {
        var (a, store, clock) = Make();
        var b = new HourlyStats(new RelayOptions { Stats = { Store = store, Time = clock, ReadKey = "k" } }, NullLogger<HourlyStats>.Instance);
        a.Record("auth", TimeSpan.FromMilliseconds(8), 200);
        a.Record("auth", TimeSpan.FromMilliseconds(8), 401);
        await a.FlushAsync();
        b.Record("auth", TimeSpan.FromMilliseconds(80), 503);
        b.Record("push", TimeSpan.FromMilliseconds(3), 200);
        await b.FlushAsync();

        var hours = await a.ReadAsync(1, default);

        var rows = Assert.Single(hours).Rows;
        Assert.Equal(new StatsRow("auth", 3, 10, 100, 1, 1), rows[0]);
        Assert.Equal(new StatsRow("push", 1, 5, 5, 0, 0), rows[1]);
    }

    [Fact]
    public async Task An_hour_without_traffic_is_listed_with_no_rows()
    {
        var (stats, _, clock) = Make();
        stats.Record("api", TimeSpan.FromMilliseconds(1), 200);
        await stats.FlushAsync();
        clock.Now = Noon.AddHours(2);

        var hours = await stats.ReadAsync(3, default);

        Assert.Equal([1, 0, 0], hours.Select(h => h.Rows.Count));
        Assert.Equal(Noon.AddHours(2).AddMinutes(-10), hours[^1].Hour);
    }

    [Fact]
    public async Task A_row_holds_only_counts_and_times_whatever_the_path_carried()
    {
        var (stats, store, _) = Make();
        var group = HourlyStats.GroupOf("/v1/accounts/987654321/devices/laptop-7", false);
        Assert.Equal("api", group);
        Assert.Null(HourlyStats.GroupOf("/v1/ws", true));
        Assert.Null(HourlyStats.GroupOf("/v1/stats/hourly", false));
        stats.Record(group!, TimeSpan.FromMilliseconds(4), 404);
        await stats.FlushAsync();

        var stored = Encoding.UTF8.GetString((await store.ReadAsync(store.Names.Single(), default))!.Body);

        Assert.DoesNotContain("987654321", stored);
        Assert.DoesNotContain("laptop", stored);
        var row = JsonSerializer.SerializeToElement((await stats.ReadAsync(1, default))[0].Rows[0]);
        Assert.Equal(["Group", "Count", "P50Ms", "P90Ms", "C4xx", "C5xx"], row.EnumerateObject().Select(p => p.Name));
    }

    // A store whose writes fail once, either by refusing or by a stop cancelling the flush.
    sealed class FlakyStore(MemoryRosterStore inner, CancellationTokenSource? stop = null, bool conflict = false) : IRosterStore
    {
        public bool Fail = true;
        public Task<StoredRoster?> ReadAsync(string name, CancellationToken ct) => inner.ReadAsync(name, ct);
        public Task<bool> DeleteAsync(string name, string etag, CancellationToken ct) => inner.DeleteAsync(name, etag, ct);
        public Task<string?> WriteAsync(string name, byte[] body, string? etag, CancellationToken ct)
        {
            if (!Fail) { return inner.WriteAsync(name, body, etag, ct); }
            Fail = false;
            if (conflict) { return Task.FromResult<string?>(null); }
            if (stop is null) { throw new IOException("refused"); }
            stop.Cancel();
            ct.ThrowIfCancellationRequested();
            return inner.WriteAsync(name, body, etag, ct);
        }
    }

    [Fact]
    public async Task A_conflicting_write_is_read_again_and_merged_once_in_the_same_flush()
    {
        var mem = new MemoryRosterStore();
        var other = new HourlyStats(new RelayOptions { Stats = { Store = mem, Time = new Clock(Noon), ReadKey = "k" } }, NullLogger<HourlyStats>.Instance);
        other.Record("api", TimeSpan.FromMilliseconds(5), 200);
        await other.FlushAsync();
        var stats = new HourlyStats(new RelayOptions { Stats = { Store = new FlakyStore(mem, null, true), Time = new Clock(Noon), ReadKey = "k" } }, NullLogger<HourlyStats>.Instance);
        stats.Record("api", TimeSpan.FromMilliseconds(5), 200);

        await stats.FlushAsync();

        Assert.Equal(2, Assert.Single(await stats.ReadAsync(1, default)).Rows.Single().Count);
        await stats.FlushAsync();
        Assert.Equal(2, Assert.Single(await stats.ReadAsync(1, default)).Rows.Single().Count);
    }

    [Fact]
    public async Task Counts_survive_a_refused_write_and_a_flush_cancelled_by_a_stop_and_land_on_the_next_flush()
    {
        foreach (var cancelled in new[] { false, true })
        {
            var cts = new CancellationTokenSource();
            var mem = new MemoryRosterStore();
            var clock = new Clock(Noon);
            var stats = new HourlyStats(new RelayOptions { Stats = { Store = new FlakyStore(mem, cancelled ? cts : null), Time = clock, ReadKey = "k" } }, NullLogger<HourlyStats>.Instance);
            stats.Record("api", TimeSpan.FromMilliseconds(5), 200);
            clock.Now = Noon.AddHours(1);
            stats.Record("api", TimeSpan.FromMilliseconds(5), 500);

            if (cancelled) { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stats.FlushAsync(cts.Token)); } else { await stats.FlushAsync(); }
            await stats.FlushAsync();

            var hours = await stats.ReadAsync(2, default);
            Assert.Equal([1L, 1L], hours.Select(h => h.Rows.Sum(r => r.Count)));
            Assert.Equal(1, hours[1].Rows[0].C5xx);
        }
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task The_endpoint_is_off_without_a_key_refuses_a_wrong_key_and_serves_the_hour_to_the_right_one()
    {
        var store = new MemoryRosterStore();
        await using var off = await TestRelay.StartAsync(o => o.Stats.Store = store);
        Assert.Equal(HttpStatusCode.NotFound, (await off.Http.GetAsync(new Uri(off.Url, "/v1/stats/hourly"))).StatusCode);

        await using var relay = await TestRelay.StartAsync(o => { o.Stats.Store = store; o.Stats.ReadKey = "sre-key"; });
        await relay.Http.GetAsync(new Uri(relay.Url, "/healthz"));
        await relay.Http.GetAsync(new Uri(relay.Url, "/v1/nothing-here"));
        await relay.App.Services.GetRequiredService<HourlyStats>().FlushAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("wrong")).StatusCode);
        var ok = await Get("sre-key");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var groups = JsonDocument.Parse(await ok.Content.ReadAsStringAsync()).RootElement.GetProperty("hours").EnumerateArray().SelectMany(h => h.GetProperty("groups").EnumerateArray()).ToDictionary(g => g.GetProperty("group").GetString()!);
        Assert.Equal(1, groups["health"].GetProperty("count").GetInt64());
        Assert.Equal(1, groups["api"].GetProperty("c4xx").GetInt64());

        Task<HttpResponseMessage> Get(string key)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, new Uri(relay.Url, "/v1/stats/hourly?hours=2"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return relay.Http.SendAsync(req);
        }
    }
}
