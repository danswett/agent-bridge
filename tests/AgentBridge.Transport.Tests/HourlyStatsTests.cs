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
        Assert.Equal(600000, HourlyStats.Percentile(Buckets((HourlyStats.EdgesMs.Length, 3)), 0.9));
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

    static byte[] Body(params string[] samples) => Encoding.UTF8.GetBytes("{\"samples\":[" + string.Join(",", samples) + "]}");
    static string Sample(string app, long ms, long at, string route = "send-to-settled") => $"{{\"app\":\"{app}\",\"route\":\"{route}\",\"ms\":{ms},\"at\":{at}}}";

    [Fact]
    public async Task App_timings_count_per_app_drop_stale_and_future_samples_and_clamp_ms()
    {
        var (s, store, _) = Make();
        var now = Noon.ToUnixTimeSeconds();

        var r = s.AddTimings(Body(Sample("PWA", 30_000, now - 5), Sample("PWA", 9_999_999, now - 5), Sample("Windows", 40, now - 7200), Sample("Phone", 40, now + 60)));
        await s.FlushAsync();

        Assert.Equal((2, 2), r);
        var hours = await s.ReadAsync(1, default);
        var row = Assert.Single(hours[0].Rows);
        Assert.Equal("app-send-to-settled-pwa", row.Group);
        Assert.Equal(2, row.Count);
        Assert.Equal(60000, row.P50Ms);
        Assert.Equal(600000, row.P90Ms);
        Assert.Equal(0, row.C4xx);
    }

    [Theory]
    [InlineData("{\"samples\":[]}")]
    [InlineData("not json")]
    [InlineData("{\"samples\":[{\"app\":\"PWA\",\"route\":\"send-to-settled\",\"ms\":1.5,\"at\":1}]}")]
    public void A_malformed_post_is_refused_whole(string body)
    {
        Assert.Null(Make().Stats.AddTimings(Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public void One_bad_app_or_route_or_too_many_samples_refuses_the_whole_post()
    {
        var (s, _, _) = Make();
        var now = Noon.ToUnixTimeSeconds();
        Assert.Null(s.AddTimings(Body(Sample("PWA", 1, now), Sample("Laptop-17", 1, now))));
        Assert.Null(s.AddTimings(Body(Sample("PWA", 1, now, "other"))));
        Assert.Null(s.AddTimings(Body(Enumerable.Repeat(Sample("PWA", 1, now), HourlyStats.MaxSamples + 1).ToArray())));
        Assert.NotNull(s.AddTimings(Body(Enumerable.Repeat(Sample("PWA", 1, now), HourlyStats.MaxSamples).ToArray())));
    }

    [Fact]
    public async Task An_hour_written_with_the_old_edges_is_read_with_its_slow_requests_in_the_top_bucket()
    {
        var (s, store, _) = Make();
        var old = new long[14]; old[2] = 1; old[13] = 3;
        var doc = new Dictionary<string, object> { ["v"] = 1, ["groups"] = new Dictionary<string, object> { ["api"] = new { n = 4, b = old, c4 = 0, c5 = 0 } } };
        await store.WriteAsync("stats/v1/2026110912.json", JsonSerializer.SerializeToUtf8Bytes(doc), null, default);

        var row = Assert.Single((await s.ReadAsync(1, default))[0].Rows);

        Assert.Equal(4, row.Count);
        Assert.Equal(600000, row.P90Ms);

        s.Record("api", TimeSpan.FromMilliseconds(3), 200);
        await s.FlushAsync();
        Assert.Equal(5, Assert.Single((await s.ReadAsync(1, default))[0].Rows).Count);
        Assert.NotNull(await store.ReadAsync("stats/v2/2026110912.json", default));
        Assert.Equal(old.Length, Parse14(await store.ReadAsync("stats/v1/2026110912.json", default)));

        static int Parse14(StoredRoster? b) => JsonDocument.Parse(b!.Body).RootElement.GetProperty("groups").GetProperty("api").GetProperty("b").GetArrayLength();
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Browser_timing_posts_allow_the_account_header_only_from_allowlisted_origins()
    {
        await using var relay = await TestRelay.StartAsync(o => o.Stats.Store = new MemoryRosterStore());
        foreach (var origin in new[] { "https://pwa.example", "https://evil.example" })
        {
            using var preflight = new HttpRequestMessage(HttpMethod.Options, new Uri(relay.Url, "/v1/stats/app-timings"));
            preflight.Headers.Add("Origin", origin);
            preflight.Headers.Add("Access-Control-Request-Method", "POST");
            preflight.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
            using var response = await relay.Http.SendAsync(preflight);
            if (origin == "https://evil.example")
            {
                Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
                Assert.False(response.Headers.Contains("Access-Control-Allow-Headers"));
                Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"));
                continue;
            }
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
            Assert.Equal("POST", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Methods")));
            var headers = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers"));
            Assert.Contains("authorization", headers, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("content-type", headers, StringComparison.OrdinalIgnoreCase);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(relay.Url, "/v1/stats/app-timings"));
        request.Headers.Add("Origin", "https://pwa.example");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", relay.TokenFor(1, "test"));
        request.Content = new ByteArrayContent(Body(Sample("PWA", 5, DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var posted = await relay.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        Assert.Equal("https://pwa.example", Assert.Single(posted.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task The_timings_endpoint_needs_a_token_lands_in_the_hourly_read_and_limits_size_and_rate_with_no_ids_stored()
    {
        var store = new MemoryRosterStore();
        await using var relay = await TestRelay.StartAsync(o => { o.Stats.Store = store; o.Stats.ReadKey = "sre-key"; });
        var token = relay.TokenFor(424242, "private-login");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(null, Body(Sample("PWA", 5, now)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post("bad", Body(Sample("PWA", 5, now)))).StatusCode);
        var ok = await Post(token, Body(Sample("PWA", 2500, now), Sample("Windows", 70000, now)));
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        Assert.Equal(2, JsonDocument.Parse(await ok.Content.ReadAsStringAsync()).RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(token, Body(Sample("Toaster", 5, now)))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Post(token, Body(Sample("PWA", 5, now)))).StatusCode);

        var other = relay.TokenFor(7, "someone-else");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Post(other, new byte[HourlyStats.MaxPostBytes + 1])).StatusCode);

        await relay.App.Services.GetRequiredService<HourlyStats>().FlushAsync();
        var req = new HttpRequestMessage(HttpMethod.Get, new Uri(relay.Url, "/v1/stats/hourly?hours=2"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "sre-key");
        var json = await (await relay.Http.SendAsync(req)).Content.ReadAsStringAsync();
        Assert.Contains("app-send-to-settled-pwa", json);
        Assert.Contains("app-send-to-settled-windows", json);
        Assert.DoesNotContain("app-send-to-settled-phone", json);
        // The stored hour and the read rows hold no account id or login.
        var stored = string.Concat(await Task.WhenAll(new[] { 0, 1 }.Select(async h => Encoding.UTF8.GetString((await store.ReadAsync($"stats/v2/{DateTimeOffset.UtcNow.AddHours(-h):yyyyMMddHH}.json", default))?.Body ?? []))));
        foreach (var text in new[] { json, stored }) { Assert.DoesNotContain("424242", text); Assert.DoesNotContain("private-login", text); }

        Task<HttpResponseMessage> Post(string? bearer, byte[] body)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, new Uri(relay.Url, "/v1/stats/app-timings")) { Content = new ByteArrayContent(body) };
            r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (bearer is not null) { r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer); }
            return relay.Http.SendAsync(r);
        }
    }
}
