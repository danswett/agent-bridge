using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

[Trait("Category", "Network")]
public sealed class AgentSettingsStatusTests : IAsyncLifetime
{
    const string Op = "host.agentSettings.status";
    readonly string folder = Path.Combine(Path.GetTempPath(), "ab-as-" + Guid.NewGuid().ToString("N")[..8]);
    readonly Callers callers = new();
    HostControl control = null!;
    LocalServer server = null!;
    LocalChannel channel = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(folder);
        control = new(new HostControlOptions
        {
            Profile = "test", Folder = folder, Version = "0.1.0",
            Machine = new("host", "Fixture", "windows", "0.1.0"), Running = new(false),
            SignIn = (_, _, _) => throw new NotSupportedException(), SignedIn = (_, _) => { },
            Stop = () => { }, Restart = () => { },
        });
        server = LocalServer.Start(LocalEndpoint.For(folder), LocalApi.Handler(null!, control, callers));
        channel = await LocalClient.ConnectAsync(LocalEndpoint.For(folder), TimeSpan.FromSeconds(10));
    }

    public async Task DisposeAsync()
    {
        await channel.DisposeAsync();
        await server.DisposeAsync();
        Directory.Delete(folder, true);
    }

    void Config(string text) => File.WriteAllText(Path.Combine(folder, HostSettings.FileName), text);
    Task<JsonElement> Read() => channel.CallAsync(Op);
    static JsonElement Entry(JsonElement result, string key) => result.GetProperty("settings").GetProperty(key);

    [Fact]
    public async Task Absent_settings_show_all_defaults_and_ranges_without_writing()
    {
        var result = await Read();
        Assert.Equal("ok", result.GetProperty("state").GetString());
        var settings = result.GetProperty("settings");
        Assert.Equal(11, settings.EnumerateObject().Count());
        var enabled = Entry(result, "agents.launch.enabled");
        Assert.False(enabled.GetProperty("value").GetBoolean());
        Assert.False(enabled.GetProperty("default").GetBoolean());
        var limit = Entry(result, "agents.limits.launchesPerOriginPerHour");
        Assert.Equal(3, limit.GetProperty("value").GetInt32());
        Assert.Equal(3, limit.GetProperty("default").GetInt32());
        Assert.Equal(1, limit.GetProperty("min").GetInt32());
        Assert.Equal(3, limit.GetProperty("max").GetInt32());
        Assert.False(File.Exists(Path.Combine(folder, HostSettings.FileName)));
    }

    [Fact]
    public async Task A_set_value_is_shown_with_its_default_and_range_on_the_next_read()
    {
        await Read();
        const string text = """{"agents":{"limits":{"launchesPerOriginPerHour":2,"maxDepth":1}}}""";
        Config(text);
        var result = await Read();
        var limit = Entry(result, "agents.limits.launchesPerOriginPerHour");
        Assert.Equal((2, 3, 1, 3), (limit.GetProperty("value").GetInt32(), limit.GetProperty("default").GetInt32(),
            limit.GetProperty("min").GetInt32(), limit.GetProperty("max").GetInt32()));
        Assert.Equal(1, Entry(result, "agents.limits.maxDepth").GetProperty("value").GetInt32());
        Assert.Equal(text, File.ReadAllText(Path.Combine(folder, HostSettings.FileName)));
    }

    [Fact]
    public async Task An_invalid_limit_is_refused_not_reported_as_the_default_in_force()
    {
        Config("""{"agents":{"limits":{"launchesPerOriginPerHour":99}}}""");
        var result = await Read();
        Assert.Equal("refused", result.GetProperty("state").GetString());
        Assert.Contains("agents.limits.launchesPerOriginPerHour", result.GetProperty("reason").GetString());
        var limit = Entry(result, "agents.limits.launchesPerOriginPerHour");
        Assert.Equal(JsonValueKind.Null, limit.GetProperty("value").ValueKind);
        Assert.Equal(3, limit.GetProperty("default").GetInt32());
    }

    [Fact]
    public async Task An_unreadable_file_is_reported_without_claiming_default_values()
    {
        Directory.CreateDirectory(Path.Combine(folder, HostSettings.FileName));
        var result = await Read();
        Assert.Equal("unreadable", result.GetProperty("state").GetString());
        Assert.Contains("could not be read", result.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, Entry(result, "agents.limits.maxDepth").GetProperty("value").ValueKind);
    }

    [Theory]
    [InlineData(CallerKind.Agent)]
    [InlineData(CallerKind.Other)]
    public async Task Agents_and_unknown_local_callers_are_refused(CallerKind kind)
    {
        callers.Kind = kind;
        var e = await Assert.ThrowsAnyAsync<Exception>(() => Read());
        Assert.Contains("refused", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Caller_is_rechecked_and_nonlocal_channels_are_refused()
    {
        await Read();
        callers.FreshKind = CallerKind.Agent;
        var e = await Assert.ThrowsAnyAsync<Exception>(() => Read());
        Assert.Contains("refused", e.Message, StringComparison.OrdinalIgnoreCase);
        callers.FreshKind = CallerKind.App;
        var handler = LocalApi.Handler(null!, control, callers);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler(new Remote(), Op, default));
    }

    [Fact]
    public async Task The_persons_host_command_line_can_read_but_a_credentialed_connection_cannot()
    {
        callers.Kind = CallerKind.HostCli;
        Assert.Equal("ok", (await Read()).GetProperty("state").GetString());
        var credentials = new AgentCredentials(folder, "machine");
        var token = credentials.Mint("parent", "launch", 100, DateTimeOffset.UtcNow, AgentProtocol.ScopeRead);
        var identity = credentials.Check(token,
            new ProcessAncestry([new PeerProcess(100, DateTimeOffset.UtcNow, "agent.exe")], true)).Accepted!;
        credentials.Accept(channel, identity);
        var handler = LocalApi.Handler(null!, control, callers, credentials: credentials);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler(channel, Op, default));
    }

    sealed class Callers : ICallerGuard
    {
        public CallerKind Kind { get; set; } = CallerKind.App;
        public CallerKind? FreshKind { get; set; }
        public Caller Classify(IProtocolChannel channel, bool fresh = false) => new(fresh ? FreshKind ?? Kind : Kind, "fixture");
    }

    sealed class Remote : IProtocolChannel
    {
        public string PeerId => "remote";
        public string PeerName => "remote";
        public string PeerKind => "remote";
        public bool IsOpen => true;
        public Task Closed => Task.Delay(Timeout.Infinite);
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }
}
