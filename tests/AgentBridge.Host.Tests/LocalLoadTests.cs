using AgentBridge.Host;
using AgentBridge.Host.Core.Load;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class LocalLoadTests
{
    [Theory]
    [InlineData(true, 2, "partial")]
    [InlineData(true, 16, "stale")]
    [InlineData(true, -1, "unavailable")]
    [InlineData(false, 2, "")]
    public async Task The_authenticated_pipe_serves_current_load_but_never_subscriptions(bool enabled, int seconds, string state)
    {
        using var fixture = new Fixture();
        await using var source = fixture.Source();
        var time = new FakeTimeProvider(Fixture.T0);
        ProcessLoadReport? report = seconds < 0 ? null : new(time.GetUtcNow(),
            new(4, null, null, RunQueueAvg: 8.5), new Dictionary<string, AgentBridge.Host.Core.Load.SessionLoad>());
        time.Advance(TimeSpan.FromSeconds(Math.Max(0, seconds)));
        var service = new SessionService(source, new("fixture-id", "Fixture", "windows", "0.1.0"))
        {
            LoadEnabled = () => enabled,
            Load = new LoadService(source, () => report, () => enabled, time: time),
        };
        var endpoint = LocalEndpoint.For(fixture.Root);
        await using var server = LocalServer.Start(endpoint, LocalApi.Handler(service, null!,
            new FixedCallerGuard(new(CallerKind.HostCli, "fixture caller"))));
        await using var pipe = await LocalClient.ConnectAsync(endpoint, TimeSpan.FromSeconds(10));
        await pipe.HelloAsync();
        var response = await pipe.RequestAsync("load.get", new { });
        Assert.Equal(enabled, response.GetProperty("ok").GetBoolean());
        if (enabled)
        {
            var record = response.GetProperty("result");
            Assert.Equal(state, record.GetProperty("state").GetString());
            if (seconds >= 0) { Assert.Equal(seconds * 1000L, record.GetProperty("ageMs").GetInt64()); }
            Assert.Equal(state == "partial", record.TryGetProperty("runQueueAvg", out var queue));
            if (state == "partial") { Assert.Equal(8.5, queue.GetDouble()); }
        }
        foreach (var op in new[] { "load.subscribe", "load.unsubscribe" })
            Assert.False((await pipe.RequestAsync(op, new { })).GetProperty("ok").GetBoolean());
    }

    // #3120 review P1 (ruling: trim). The local pipe's reply is the four figures launch admission needs and nothing else: no session list, nothing
    // per session, no CPU or memory totals, no sample time. Every caller class that may call it gets exactly that.
    [Theory]
    [InlineData(CallerKind.App, 2)]
    [InlineData(CallerKind.HostCli, 2)]
    [InlineData(CallerKind.Agent, 2)]
    [InlineData(CallerKind.Other, 2)]
    [InlineData(CallerKind.App, 16)]
    [InlineData(CallerKind.HostCli, 16)]
    [InlineData(CallerKind.Agent, 16)]
    [InlineData(CallerKind.Other, 16)]
    public async Task Every_caller_class_gets_exactly_the_four_figures_and_nothing_per_session(CallerKind kind, int seconds)
    {
        using var fixture = new Fixture();
        await using var source = fixture.Source();
        var time = new FakeTimeProvider(Fixture.T0);
        // A machine with CPU and memory readings and a session of its own, all of which the relay record carries and this reply must not.
        var report = new ProcessLoadReport(time.GetUtcNow(),
            new(4, 55.0, 2_000_000_000, RunQueueAvg: 8.5),
            new Dictionary<string, AgentBridge.Host.Core.Load.SessionLoad> { ["session-one"] = new(default, 2, 12.5, 123_456_789) });
        time.Advance(TimeSpan.FromSeconds(seconds));
        var service = new SessionService(source, new("fixture-id", "Fixture", "windows", "0.1.0"))
        {
            LoadEnabled = () => true,
            Load = new LoadService(source, () => report, () => true, time: time),
        };
        var endpoint = LocalEndpoint.For(fixture.Root);
        await using var server = LocalServer.Start(endpoint, LocalApi.Handler(service, null!, new FixedCallerGuard(new(kind, "fixture caller"))));
        await using var pipe = await LocalClient.ConnectAsync(endpoint, TimeSpan.FromSeconds(10));
        await pipe.HelloAsync();
        var response = await pipe.RequestAsync("load.get", new { });
        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString());
        var record = response.GetProperty("result");
        var names = record.EnumerateObject().Select(p => p.Name).ToArray();
        string[] allowed = ["state", "ageMs", "runQueueAvg", "staleAfterMs"];
        Assert.Contains("state", names);
        Assert.Empty(names.Except(allowed));
        Assert.Equal(seconds * 1000L, record.GetProperty("ageMs").GetInt64());
        Assert.Equal(seconds <= 15, record.TryGetProperty("runQueueAvg", out var queue));   // a stale sample has no figure, never a 0
        if (seconds <= 15) { Assert.Equal(8.5, queue.GetDouble()); }
        Assert.DoesNotContain("session-one", record.GetRawText());
    }}
