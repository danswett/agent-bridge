using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class HaStartGateTests
{
    static HostSettings Configure(string folder, bool? enabled = null)
    {
        var ha = new JsonObject
        {
            ["baseUrl"] = "http://ha.fixture.invalid:8123", ["disposable"] = true,
            ["machineName"] = "ha-gate-" + Guid.NewGuid().ToString("N"),
        };
        if (enabled is { } value) { ha["enabled"] = value; }
        File.WriteAllText(Path.Combine(folder, HostSettings.FileName), new JsonObject { ["homeAssistant"] = ha }.ToJsonString());
        HaTokenStore.Save(folder, "fixture-ha-token");
        return HostSettings.Load(folder).Settings;
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public async Task Default_on_and_explicit_on_start_the_connector_through_the_existing_guards(bool? enabled)
    {
        using var fixture = new Fixture();
        var settings = Configure(fixture.Root, enabled);
        Assert.True(settings.HomeAssistant.Enabled);
        await using var source = fixture.Source();
        using var http = new HeldTransport();
        await using var running = HaRunning.Start(settings, fixture.Root, source, null, () => null,
            true, "fixture", _ => { }, transport: http);

        Assert.NotNull(running);
        var request = await http.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/services/mqtt/publish", request.RequestUri!.AbsolutePath);
        Assert.Equal("ha.fixture.invalid", request.RequestUri.Host);
    }

    [Theory]
    [InlineData("""{"homeAssistant":{"enabled":false}}""")]
    [InlineData("""{"homeAssistant":false}""")]
    [InlineData("""{"homeAssistant":{"enabled":"yes"}}""")]
    [InlineData("{broken")]
    [InlineData("""{"version":999}""")]
    public async Task Off_or_invalid_configuration_creates_no_connector_and_sends_no_requests(string json)
    {
        using var fixture = new Fixture();
        Configure(fixture.Root);
        File.WriteAllText(Path.Combine(fixture.Root, HostSettings.FileName), json);
        var settings = HostSettings.Load(fixture.Root).Settings;
        await using var source = fixture.Source();
        using var http = new HeldTransport();
        await using var running = HaRunning.Start(settings, fixture.Root, source, null, () => null,
            true, "fixture", _ => { }, transport: http);

        Assert.False(settings.HomeAssistant.Enabled);
        Assert.Null(running);
        Assert.Equal(0, http.Requests);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "ha-connector.json")));
        Assert.Equal("fixture-ha-token", HaTokenStore.Load(fixture.Root));
    }

    [Fact]
    public async Task An_audited_runtime_off_stops_the_connector_and_keeps_it_off_at_the_next_start()
    {
        using var fixture = new Fixture();
        var settings = Configure(fixture.Root);
        await using var source = fixture.Source();
        using var http = new HeldTransport();
        var clock = new FakeTimeProvider();
        await using var running = HaRunning.Start(settings, fixture.Root, source, null, () => null,
            true, "fixture", _ => { }, transport: http, time: clock);
        Assert.NotNull(running);
        await http.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var error = new StringWriter();
        Assert.Equal(0, HaCommands.Run(["ha", "off"], fixture.Root, () => throw new InvalidOperationException(),
            new StringWriter(), error, true, () => null));
        Assert.Empty(error.ToString());
        clock.Advance(TimeSpan.FromSeconds(1));
        await running.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(http.Cancelled.Task.IsCompletedSuccessfully);
        Assert.Equal(1, http.Requests);
        var (rows, complete) = GateAuditProjection.Load(fixture.Root);
        Assert.True(complete);
        Assert.Equal(["requested", "configured-off"], rows.Select(row => row.Outcome));
        Assert.All(rows, row => Assert.Equal("host.homeAssistant", row.Action));
        Assert.Equal(rows[0].Request, rows[1].Request);
        using var restartedHttp = new HeldTransport();
        await using var restarted = HaRunning.Start(HostSettings.Load(fixture.Root).Settings, fixture.Root,
            source, null, () => null, true, "fixture", _ => { }, transport: restartedHttp);
        Assert.Null(restarted);
        Assert.Equal(0, restartedHttp.Requests);
        Assert.Equal("fixture-ha-token", HaTokenStore.Load(fixture.Root));
    }

    [Fact]
    public async Task A_newly_unreadable_configuration_stops_an_already_running_connector()
    {
        using var fixture = new Fixture();
        var settings = Configure(fixture.Root);
        await using var source = fixture.Source();
        using var http = new HeldTransport();
        var clock = new FakeTimeProvider();
        await using var running = HaRunning.Start(settings, fixture.Root, source, null, () => null,
            true, "fixture", _ => { }, transport: http, time: clock);
        Assert.NotNull(running);
        await http.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        File.Delete(Path.Combine(fixture.Root, HostSettings.FileName));
        Directory.CreateDirectory(Path.Combine(fixture.Root, HostSettings.FileName));

        clock.Advance(TimeSpan.FromSeconds(1));
        await running.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(http.Cancelled.Task.IsCompletedSuccessfully);
        Assert.Equal(1, http.Requests);
    }

    sealed class HeldTransport : HttpMessageHandler
    {
        int requests;
        public int Requests => Volatile.Read(ref requests);
        public TaskCompletionSource<HttpRequestMessage> Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            Requested.TrySetResult(request);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The fake request must end by cancellation.");
            }
            finally { Cancelled.TrySetResult(); }
        }
    }
}
