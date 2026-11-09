using AgentBridge.Host.Core.Load;
using AgentBridge.HomeAssistant;
using Xunit;

// #1150: the process-load figures on the sessions sensor, held back until Compliance clears them.
public sealed class LoadPayloadTests : IDisposable
{
    static readonly DateTimeOffset At = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
    readonly string folder = Path.Combine(Path.GetTempPath(), "abhk-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder)) { Directory.Delete(folder, true); }
    }

    async Task<string?> SensorLoadAsync(bool publish, int sampleCalls = 1)
    {
        Directory.CreateDirectory(folder);
        var ha = new FakeHomeAssistant();
        var sessions = new FakeSessions();
        sessions.Set(FakeSessions.Copilot("6fcbab0c-1d2e-4f50-9a61-7b8c9d0e1f20"));
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(FakeSessions.T0);
        await using var c = new HaConnector(new HaConnectorOptions
        {
            MachineName = "M", Version = "0.1.0", StateFile = Path.Combine(folder, "c.json"), PublishLoad = publish,
            FieldPinDelay = TimeSpan.Zero, SelectDelay = TimeSpan.Zero, DiscoveryReset = TimeSpan.Zero,
            RegisteredDelay = TimeSpan.Zero, HelperStateDelay = TimeSpan.Zero,
        }, ha, sessions, () => null, null, null, time, load: Report);
        for (var i = 0; i < 10; i++) { time.Advance(TimeSpan.FromSeconds(1)); await c.TickAsync(); }
        var attributes = System.Text.Json.Nodes.JsonNode.Parse(ha.Retained[HaContract.GlobalAttributesTopic(HaContract.MachineSlug("M"))])!;
        return attributes["load"]?.ToJsonString();
    }

    [Fact]
    public async Task The_sensor_carries_no_load_while_publishing_it_is_off()
    {
        Assert.Null(await SensorLoadAsync(publish: false));
    }

    [Fact]
    public async Task The_sensor_carries_the_load_once_publishing_it_is_switched_on()
    {
        Assert.NotNull(await SensorLoadAsync(publish: true));
    }

    static ProcessLoadReport Report() => new(At, new MachineLoad(8, null, 4096L * 1024 * 1024), new Dictionary<string, SessionLoad>
    {
        ["session-a"] = new(LoadState.Ok, 3, 12.5, 300L * 1024 * 1024),
        ["session-b"] = new(LoadState.Unavailable, 0, null, null),
        ["not-published"] = new(LoadState.Ok, 1, 1.0, 1),
    });

    [Fact]
    public void Unknown_figures_are_null_in_the_payload_and_never_zero()
    {
        var load = HaPayloads.Load(Report(), new Dictionary<string, string> { ["session-a"] = "node_a", ["session-b"] = "node_b" });

        Assert.Null(load["machine_cpu_percent"]);
        var b = load["sessions"]!["node_b"]!;
        Assert.Null(b["cpu_percent"]);
        Assert.Null(b["working_set_bytes"]);
        Assert.Equal("unavailable", (string?)b["state"]);
        Assert.Equal(12.5, (double?)load["sessions"]!["node_a"]!["cpu_percent"]);
    }

    [Fact]
    public void The_payload_is_keyed_by_the_published_node_and_carries_no_session_ids_or_process_details()
    {
        var load = HaPayloads.Load(Report(), new Dictionary<string, string> { ["session-a"] = "node_a" });
        var json = HaContract.Serialize(load);

        Assert.Equal(["node_a"], load["sessions"]!.AsObject().Select(p => p.Key));
        Assert.DoesNotContain("session-a", json);
        Assert.DoesNotContain("not-published", json);
        foreach (var forbidden in new[] { "path", "command", "pid", "args", "name" }) { Assert.DoesNotContain($"\"{forbidden}", json); }
    }

    [Fact]
    public void The_sessions_sensor_has_no_load_unless_it_is_given_one()
    {
        var without = HaPayloads.GlobalAttributes([], "M", "m", HaPayloads.Capabilities(detailed: false, dev: true), At);
        var with = HaPayloads.GlobalAttributes([], "M", "m", HaPayloads.Capabilities(detailed: false, dev: true), At, load: HaPayloads.Load(Report(), new Dictionary<string, string>()));

        Assert.False(without.ContainsKey("load"));
        Assert.True(with.ContainsKey("load"));
    }
}
