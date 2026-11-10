using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed class FleetNamesTests : IAsyncLifetime
{
    const string Id = "88888888-8888-4888-8888-888888888888";
    readonly Fixture fixture = new();
    readonly Callers callers = new();
    readonly Channel channel = new();
    SessionSource source = null!;
    SessionService service = null!;
    string PathToNames => Path.Combine(fixture.Root, "fleet-names.json");
    Dictionary<string, string> Saved() => JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(PathToNames))!;

    public async Task InitializeAsync()
    {
        fixture.CopilotSession(Id, 100, name: "Served session");
        fixture.Processes.Run(AgentKind.Copilot, 100);
        source = new SessionSource(fixture.Options(), fixture.Processes, TimeProvider.System);
        await source.RefreshAsync();
        service = MakeService();
    }

    SessionService MakeService() => new(source, new MachineInfo("host", "Fixture", "windows", "0.1.0"));

    async Task<JsonElement> CallAsync(object data)
    {
        var result = await LocalApi.Handler(service, Control(), callers)(channel, "session.rename", JsonSerializer.SerializeToElement(data));
        if (result is null) { throw new InvalidOperationException("unknown op: session.rename"); }
        return JsonSerializer.SerializeToElement(result, ProtocolJson.Options);
    }

    HostControl Control() => new(new HostControlOptions
    {
        Profile = "test", Folder = fixture.Root, Version = "0.1.0",
        Machine = new("host", "Fixture", "windows", "0.1.0"), Running = new(false),
        SignIn = (_, _, _) => throw new NotSupportedException(), SignedIn = (_, _) => { },
        Stop = () => { }, Restart = () => { },
    });

    public async Task DisposeAsync() { await source.DisposeAsync(); fixture.Dispose(); }

    [Theory]
    [InlineData(CallerKind.App)]
    [InlineData(CallerKind.HostCli)]
    public async Task Fleet_names_are_person_set_host_state_not_session_titles(CallerKind kind)
    {
        callers.Current = new FixedCallerGuard(new Caller(kind, "person at the machine"));
        Assert.False(File.Exists(PathToNames));
        var result = await CallAsync(new { id = Id, name = "  SG Quality Lead  " });
        Assert.Equal("SG Quality Lead", result.GetProperty("name").GetString());
        Assert.Equal("SG Quality Lead", Saved()[Id]);
        Assert.Equal("Served session", source.Get(Id)!.Title);
        Assert.DoesNotContain("Served session", File.ReadAllText(PathToNames), StringComparison.Ordinal);
        await CallAsync(new { id = Id, name = " " });
        Assert.Empty(Saved());
    }

    [Theory]
    [InlineData(CallerKind.Agent)]
    [InlineData(CallerKind.Other)]
    public async Task Fleet_name_changes_refuse_agents_and_untraced_callers_without_writing(CallerKind kind)
    {
        callers.Current = new FixedCallerGuard(new Caller(kind, "unapproved caller"));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync(new { id = Id, name = "SG Quality Lead" }));
        Assert.Contains("only the app or host command line", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(PathToNames));
    }

    [Fact]
    public async Task Fleet_name_changes_recheck_the_caller_on_an_already_open_pipe()
    {
        await CallAsync(new { id = Id, name = "Original" });
        callers.Current = new FreshAgentCaller();
        await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync(new { id = Id, name = "Replacement" }));
        Assert.Equal("Original", Saved()[Id]);
    }

    [Theory]
    [InlineData("SG_Quality.+()- 123", true)]
    [InlineData("A", true)]
    [InlineData("bad|name", false)]
    [InlineData("bad<name>", false)]
    [InlineData("bad\nname", false)]
    [InlineData("bad\tname", false)]
    [InlineData("caf\u00e9", false)]
    [InlineData("bad/name", false)]
    public async Task Fleet_name_filter_accepts_only_the_documented_ASCII_characters(string name, bool valid)
    {
        if (valid)
        {
            var result = await CallAsync(new { id = Id, name });
            Assert.Equal(name, result.GetProperty("name").GetString());
        }
        else
        {
            var refused = await Assert.ThrowsAsync<ArgumentException>(() => CallAsync(new { id = Id, name }));
            Assert.Contains("1 to 64", refused.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(PathToNames));
        }
    }

    [Theory]
    [InlineData(64)]
    [InlineData(65)]
    public async Task Fleet_name_filter_enforces_the_trimmed_length(int length)
    {
        var name = new string('A', length);
        if (length == 64) { await CallAsync(new { id = Id, name = " " + name + " " }); }
        else
        {
            var refused = await Assert.ThrowsAsync<ArgumentException>(() => CallAsync(new { id = Id, name }));
            Assert.Contains("1 to 64", refused.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("""{"name":"SG Quality Lead"}""", "needs an id and name")]
    [InlineData("""{"id":"missing","name":"SG Quality Lead"}""", "session is not listed")]
    [InlineData("""{"id":"$id","name":null}""", "needs an id and name")]
    [InlineData("""{"id":"$id","name":7}""", "needs an id and name")]
    public async Task Fleet_name_changes_refuse_missing_sessions_and_malformed_requests(string json, string reason)
    {
        var data = JsonSerializer.Deserialize<JsonElement>(json.Replace("$id", Id, StringComparison.Ordinal));
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => CallAsync(data));
        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(PathToNames));
    }

    [Fact]
    public async Task Fleet_names_survive_a_host_restart_without_renaming_other_sessions()
    {
        await CallAsync(new { id = Id, name = "Original" });
        const string other = "99999999-9999-4999-8999-999999999999";
        fixture.CopilotSession(other, 101, name: "Another title");
        fixture.Processes.Run(AgentKind.Copilot, 101);
        await source.RefreshAsync();
        service = MakeService();
        await CallAsync(new { id = other, name = "Other" });
        Assert.Equal("Original", Saved()[Id]);
        Assert.Equal("Other", Saved()[other]);
        Assert.Equal(2, Saved().Count);
    }

    [Fact]
    public async Task Unreadable_host_name_state_is_not_treated_as_an_empty_store()
    {
        await CallAsync(new { id = Id, name = "Original" });
        File.WriteAllText(PathToNames, "{broken");
        Assert.Throws<JsonException>(() => MakeService());
        Assert.Equal("{broken", File.ReadAllText(PathToNames));
    }

    sealed class Callers : ICallerGuard
    {
        public ICallerGuard Current { get; set; } = new FixedCallerGuard(new Caller(CallerKind.App, "app"));
        public Caller Classify(IProtocolChannel channel, bool fresh = false) => Current.Classify(channel, fresh);
    }
    sealed class FreshAgentCaller : ICallerGuard
    {
        public Caller Classify(IProtocolChannel channel, bool fresh = false) =>
            new(fresh ? CallerKind.Agent : CallerKind.App, "caller now inside a session");
    }
    sealed class Channel : IProtocolChannel
    {
        public string PeerId => "fixture";
        public string PeerName => "fixture";
        public string PeerKind => "local";
        public bool IsOpen => true;
        public Task Closed => Task.CompletedTask;
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }
}
