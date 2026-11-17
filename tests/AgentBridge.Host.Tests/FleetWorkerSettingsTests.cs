using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed class FleetWorkerSettingsTests : IAsyncLifetime
{
    const string Op = "host.workspace.fleetWorkers";
    readonly string folder = Path.Combine(Path.GetTempPath(), "abfw-" + Guid.NewGuid().ToString("N")[..8]);
    readonly List<string> logged = [];
    readonly Callers callers = new();
    LocalServer server = null!;
    Func<IProtocolChannel, string, JsonElement, Task<object?>> handler = null!;
    string PathToConfig => Path.Combine(folder, HostSettings.FileName);
    string Workspace => Path.Combine(folder, "workspace");
    string OtherWorkspace => Path.Combine(folder, "other");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(PathToConfig, JsonSerializer.Serialize(new
        {
            version = 1, retained = 7,
            launch = new { workspaces = new object[] { new { path = Workspace, isolate = true, retained = 9 }, OtherWorkspace } },
        }));
        var control = new HostControl(new HostControlOptions
        {
            Profile = "test", Folder = folder, Version = "0.1.0",
            Machine = new MachineInfo("test", "Test", "windows", "0.1.0"),
            Running = new HostSettingsView(true),
            SignIn = (_, _, _) => throw new NotSupportedException(),
            SignedIn = (_, _) => throw new NotSupportedException(),
            Stop = () => throw new NotSupportedException(),
            Restart = () => throw new NotSupportedException(),
        }, logged.Add);
        handler = LocalApi.Handler(null!, control, callers, logged.Add);
        server = LocalServer.Start(LocalEndpoint.For(folder), handler);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await server.DisposeAsync();
        Directory.Delete(folder, recursive: true);
    }

    Task<LocalChannel> Connect() => LocalClient.ConnectAsync(LocalEndpoint.For(folder), TimeSpan.FromSeconds(10));

    [Fact]
    public async Task Default_is_off_and_reading_does_not_write_or_audit()
    {
        var before = File.ReadAllText(PathToConfig);
        await using var ch = await Connect();
        var result = await ch.CallAsync(Op, new { workspace = Workspace });
        Assert.False(result.GetProperty("value").GetBoolean());
        Assert.False(result.GetProperty("defaultValue").GetBoolean());
        Assert.Equal(before, File.ReadAllText(PathToConfig));
        Assert.Empty(logged);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task On_and_off_survive_new_connections_and_only_real_changes_are_audited(bool stringEntry)
    {
        var workspace = stringEntry ? OtherWorkspace : Workspace;
        await using (var ch = await Connect())
        {
            foreach (var value in new[] { true, true, false })
            {
                var result = await ch.CallAsync(Op, new { workspace, fleetWorkers = value });
                Assert.Equal(value, result.GetProperty("value").GetBoolean());
                Assert.False(result.GetProperty("defaultValue").GetBoolean());
                await using var verify = await Connect();
                Assert.Equal(value, (await verify.CallAsync(Op, new { workspace })).GetProperty("value").GetBoolean());
            }
        }
        await using var reader = await Connect();
        Assert.False((await reader.CallAsync(Op, new { workspace })).GetProperty("value").GetBoolean());
        Assert.Equal(2, logged.Count);
        Assert.All(logged, line => Assert.Contains(Op, line));
        var saved = JsonNode.Parse(File.ReadAllText(PathToConfig))!;
        Assert.Equal(7, saved["retained"]!.GetValue<int>());
        var workspaces = saved["launch"]!["workspaces"]!;
        Assert.True(workspaces[0]!["isolate"]!.GetValue<bool>());
        Assert.Equal(9, workspaces[0]!["retained"]!.GetValue<int>());
        Assert.False(workspaces[stringEntry ? 1 : 0]!["fleetWorkers"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(CallerKind.Agent)]
    [InlineData(CallerKind.Other)]
    [InlineData(CallerKind.HostCli)]
    public async Task Non_owner_callers_cannot_read_or_write_even_on_an_existing_app_connection(CallerKind kind)
    {
        var before = File.ReadAllText(PathToConfig);
        await using var ch = await Connect();
        await ch.CallAsync(Op, new { workspace = Workspace });
        callers.Kind = kind;
        foreach (var data in new object[] { new { workspace = Workspace }, new { workspace = Workspace, fleetWorkers = true } })
        {
            var error = await Assert.ThrowsAsync<HostErrorException>(() => ch.CallAsync(Op, data));
            Assert.Contains("only the local app", error.Message);
        }
        Assert.Equal(before, File.ReadAllText(PathToConfig));
        Assert.DoesNotContain(logged, line => line.Contains("changed", StringComparison.Ordinal));
        Assert.True(callers.Fresh);
    }

    [Fact]
    public async Task A_remote_channel_is_refused_even_when_classified_as_the_app()
    {
        var before = File.ReadAllText(PathToConfig);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler(new Remote(), Op, JsonSerializer.SerializeToElement(new { workspace = Workspace, fleetWorkers = true })));
        Assert.Contains("only the local app", error.Message);
        Assert.Equal(before, File.ReadAllText(PathToConfig));
    }

    [Theory]
    [InlineData("""{"workspace":"unknown","fleetWorkers":true}""", "unknown workspace")]
    [InlineData("""{"workspace":"$workspace","fleetWorkers":true,"agents.launch.enabled":true}""", "only workspace and fleetWorkers")]
    [InlineData("""{"workspace":"$workspace","fleetWorkers":"yes"}""", "true or false")]
    [InlineData("""{"workspace":"$workspace","fleetWorkers":null}""", "true or false")]
    public async Task Invalid_requests_are_refused_without_writing(string json, string reason)
    {
        json = json.Replace("\"$workspace\"", JsonSerializer.Serialize(Workspace), StringComparison.Ordinal);
        var before = File.ReadAllText(PathToConfig);
        await using var ch = await Connect();
        var error = await Assert.ThrowsAsync<HostErrorException>(() => ch.CallAsync(Op, JsonSerializer.Deserialize<JsonElement>(json)));
        Assert.Contains(reason, error.Message);
        Assert.Equal(before, File.ReadAllText(PathToConfig));
        Assert.Empty(logged);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("""{"version":2}""")]
    [InlineData("""{"launch":{"workspaces":false}}""")]
    public async Task Unreadable_configuration_is_not_replaced_or_reported_as_a_default(string config)
    {
        File.WriteAllText(PathToConfig, config);
        await using var ch = await Connect();
        var error = await Assert.ThrowsAsync<HostErrorException>(() => ch.CallAsync(Op, new { workspace = Workspace, fleetWorkers = true }));
        Assert.Contains("refused:", error.Message);
        Assert.Equal(config, File.ReadAllText(PathToConfig));
    }

    sealed class Callers : ICallerGuard
    {
        public CallerKind Kind { get; set; } = CallerKind.App;
        public bool Fresh { get; private set; }
        public Caller Classify(IProtocolChannel channel, bool fresh = false) { Fresh = fresh; return new(Kind, "fixture"); }
    }

    sealed class Remote : IProtocolChannel
    {
        public string PeerId => "remote";
        public string PeerName => "Remote";
        public string PeerKind => "app";
        public bool IsOpen => true;
        public Task Closed => Task.CompletedTask;
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
