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
}
