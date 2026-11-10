using System.Net;
using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.App.Tests;

public class AppTimingsTests
{
    [Fact]
    public async Task Settled_turns_post_their_settlement_time_without_identifiers()
    {
        var time = new FakeTimeProvider(Make.T0);
        var clock = new TurnClock(time);
        clock.Sent(); clock.Seen(SessionStatus.Working);
        time.Advance(TimeSpan.FromMilliseconds(4200)); clock.Seen(SessionStatus.Idle);
        using var http = new HttpClient(new Handler(async r =>
        {
            Assert.Equal("https://relay.example/v1/stats/app-timings", r.RequestUri!.ToString());
            Assert.Equal("Bearer account-token", r.Headers.Authorization!.ToString());
            Assert.Equal("application/json", r.Content!.Headers.ContentType!.MediaType);
            using var json = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            var sample = Assert.Single(json.RootElement.GetProperty("samples").EnumerateArray());
            Assert.Equal(["app", "route", "ms", "at"], sample.EnumerateObject().Select(p => p.Name));
            Assert.Equal("Windows", sample.GetProperty("app").GetString());
            Assert.Equal("send-to-settled", sample.GetProperty("route").GetString());
            Assert.Equal(4200, sample.GetProperty("ms").GetInt32());
            Assert.Equal(time.GetUtcNow().ToUnixTimeSeconds(), sample.GetProperty("at").GetInt64());
            return new(HttpStatusCode.Accepted);
        }));
        await new AppTimings(http, time).PostAsync(new("https://relay.example"), "account-token", clock.Drain);
        Assert.Empty(clock.Drain());
    }

    [Fact]
    public async Task Refusals_are_dropped_and_do_not_drain_another_batch_for_a_minute()
    {
        var time = new FakeTimeProvider(Make.T0);
        var requests = 0;
        var drains = 0;
        var logs = new List<string>();
        using var http = new HttpClient(new Handler(_ => { requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)); }));
        var reporter = new AppTimings(http, time, logs.Add);
        IReadOnlyList<TurnTiming> Drain() { drains++; return [new(1, time.GetUtcNow().ToUnixTimeSeconds())]; }
        await reporter.PostAsync(new("https://relay.example"), "token", Drain);
        time.Advance(TimeSpan.FromMilliseconds(59999));
        await reporter.PostAsync(new("https://relay.example"), "token", Drain);
        Assert.Equal(1, drains);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await reporter.PostAsync(new("https://relay.example"), "token", Drain);
        Assert.Equal(2, requests);
        Assert.Equal(2, drains);
        Assert.Contains("429", logs[0]);
    }

    [Fact]
    public async Task Batches_fit_both_limits_and_network_failures_do_not_escape()
    {
        var logs = new List<string>();
        using var http = new HttpClient(new Handler(async r =>
        {
            var bytes = await r.Content!.ReadAsByteArrayAsync();
            Assert.InRange(bytes.Length, 1, 4096);
            using var json = JsonDocument.Parse(bytes);
            var samples = json.RootElement.GetProperty("samples");
            Assert.InRange(samples.GetArrayLength(), 1, 50);
            Assert.All(samples.EnumerateArray(), s => Assert.Equal(600000, s.GetProperty("ms").GetInt32()));
            throw new HttpRequestException("private network details");
        }));
        await new AppTimings(http, log: logs.Add).PostAsync(new("https://relay.example"), "token",
            () => Enumerable.Repeat(new TurnTiming(700000, 1700000000), 100).ToArray());
        Assert.Equal(["app timings: request failed; batch dropped"], logs);
    }

    [Fact]
    public async Task A_slow_request_cannot_overlap_another_upload_and_disconnected_turns_are_discarded()
    {
        var time = new FakeTimeProvider(Make.T0);
        var finished = new TaskCompletionSource<HttpResponseMessage>();
        using var http = new HttpClient(new Handler(_ => finished.Task));
        var reporter = new AppTimings(http, time);
        var drains = 0;
        IReadOnlyList<TurnTiming> Drain() { drains++; return [new(1, 1700000000)]; }
        await reporter.PostAsync(null, null, Drain);
        Assert.Equal(1, drains);
        var pending = reporter.PostAsync(new("https://relay.example"), "token", Drain);
        time.Advance(TimeSpan.FromMinutes(1));
        await reporter.PostAsync(new("https://relay.example"), "token", Drain);
        Assert.Equal(2, drains);
        finished.SetResult(new(HttpStatusCode.Accepted));
        await pending;
    }

    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}
