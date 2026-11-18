using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class AgentFleetLaunchGrantTests : IAsyncLifetime
{
    const string Op = "host.agents.fleetLaunchGrant";
    readonly string folder = Directory.CreateTempSubdirectory("ab-agent-fleet-grant-").FullName;
    readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 18, 7, 0, 0, TimeSpan.Zero));
    readonly Callers callers = new();
    AgentCredentials credentials = null!;
    AgentCredential script = null!;
    bool scriptConnection;
    LocalServer server = null!;
    Func<IProtocolChannel, string, JsonElement, Task<object?>> handler = null!;

    public Task InitializeAsync()
    {
        credentials = new AgentCredentials(folder, "machine");
        (script, _) = credentials.MintScript("fixture");
        RestartControl();
        server = LocalServer.Start(LocalEndpoint.For(folder), (channel, op, data) =>
        {
            if (scriptConnection) { credentials.Accept(channel, script); }
            return handler(channel, op, data);
        });
        return Task.CompletedTask;
    }

    void RestartControl()
    {
        var control = new HostControl(new HostControlOptions
        {
            Profile = "fixture", Folder = folder, Version = "0.1.0", Time = time,
            Machine = new MachineInfo("machine", "Fixture", "windows", "0.1.0"),
            Running = new HostSettingsView(true),
            SignIn = (_, _, _) => throw new NotSupportedException(),
            SignedIn = (_, _) => throw new NotSupportedException(),
            Stop = () => throw new NotSupportedException(),
            Restart = () => throw new NotSupportedException(),
        });
        handler = LocalApi.Handler(null!, control, callers, credentials: credentials);
    }

    public async Task DisposeAsync()
    {
        await server.DisposeAsync();
        Directory.Delete(folder, recursive: true);
    }

    async Task<JsonElement> Call(object data)
    {
        await using var channel = await LocalClient.ConnectAsync(LocalEndpoint.For(folder), TimeSpan.FromSeconds(10));
        return await channel.CallAsync(Op, data);
    }

    object Issue(string scope = "session.launch", string role = "fleet", string principal = "fleet-launcher") =>
        new { action = "issue", scope, role, principal, expiresAt = time.GetUtcNow().AddHours(1) };

    [Fact]
    public async Task The_grant_is_absent_until_the_person_issues_one_and_survives_a_host_restart()
    {
        Assert.Equal("none", (await Call(new { action = "read" })).GetProperty("state").GetString());
        var issued = await Call(Issue());
        Assert.Equal("active", issued.GetProperty("state").GetString());
        var grant = issued.GetProperty("grant");
        Assert.Equal("session.launch", grant.GetProperty("scope").GetString());
        Assert.Equal("fleet", grant.GetProperty("role").GetString());
        Assert.Equal("fleet-launcher", grant.GetProperty("principal").GetString());
        Assert.True(Guid.TryParse(grant.GetProperty("id").GetString(), out _));
        RestartControl();
        Assert.Equal(grant.GetRawText(), (await Call(new { action = "read" })).GetProperty("grant").GetRawText());
    }

    [Theory]
    [InlineData("read")]
    [InlineData("issue")]
    [InlineData("revoke")]
    public async Task Remote_devices_cannot_manage_the_grant_even_when_classified_as_the_app(string action)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler(new Remote(), Op, JsonSerializer.SerializeToElement(new { action })));
    }

    [Theory]
    [InlineData(CallerKind.Agent, false)]
    [InlineData(CallerKind.Other, false)]
    [InlineData(CallerKind.HostCli, false)]
    [InlineData(CallerKind.App, true)]
    public async Task An_agent_or_script_cannot_issue_or_revoke_a_persons_grant(CallerKind kind, bool scriptCaller)
    {
        var issued = await Call(Issue());
        var id = issued.GetProperty("grant").GetProperty("id").GetString();
        callers.Kind = kind;
        scriptConnection = scriptCaller;
        foreach (var request in new[] { Issue(), new { action = "revoke", grant = id } })
        {
            var error = await Assert.ThrowsAsync<HostErrorException>(() => Call(request));
            Assert.Contains("only the local app", error.Message);
        }
        Assert.True(callers.Fresh);
        callers.Kind = CallerKind.App;
        scriptConnection = false;
        Assert.Equal(id, (await Call(new { action = "read" })).GetProperty("grant").GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("session.reply", "fleet", "fleet-launcher")]
    [InlineData("session.launch", "person", "fleet-launcher")]
    [InlineData("session.launch", "fleet", "another-principal")]
    public async Task Another_scope_role_or_principal_cannot_be_granted(string scope, string role, string principal)
    {
        var error = await Assert.ThrowsAsync<HostErrorException>(() => Call(Issue(scope, role, principal)));
        Assert.Contains("refused:", error.Message);
        Assert.Equal("none", (await Call(new { action = "read" })).GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_second_active_grant_is_refused_and_only_the_exact_grant_can_be_revoked()
    {
        var issued = await Call(Issue());
        var id = issued.GetProperty("grant").GetProperty("id").GetString();
        await Assert.ThrowsAsync<HostErrorException>(() => Call(Issue()));
        await Assert.ThrowsAsync<HostErrorException>(() => Call(new { action = "revoke", grant = Guid.NewGuid().ToString("D") }));
        Assert.Equal("active", (await Call(new { action = "read" })).GetProperty("state").GetString());
        Assert.Equal("revoked", (await Call(new { action = "revoke", grant = id })).GetProperty("state").GetString());
        RestartControl();
        Assert.Equal("revoked", (await Call(new { action = "read" })).GetProperty("state").GetString());
        var replacement = await Call(Issue());
        Assert.NotEqual(id, replacement.GetProperty("grant").GetProperty("id").GetString());
        await Assert.ThrowsAsync<HostErrorException>(() => Call(new { action = "revoke", grant = id }));
    }

    [Fact]
    public async Task Expiry_is_observed_on_the_next_read_and_an_expired_issue_is_refused()
    {
        var request = Issue();
        await Call(request);
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal("expired", (await Call(new { action = "read" })).GetProperty("state").GetString());
        await Assert.ThrowsAsync<HostErrorException>(() => Call(request));
        Assert.Equal("expired", (await Call(new { action = "read" })).GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("read", "{broken")]
    [InlineData("issue", "{broken")]
    [InlineData("revoke", "{broken")]
    [InlineData("read", "{}")]
    [InlineData("issue", """{"id":"first","id":"second"}""")]
    public async Task Unreadable_durable_state_is_refused_and_never_replaced(string action, string saved)
    {
        var path = Path.Combine(folder, "agent-fleet-launch-grant.json");
        File.WriteAllText(path, saved);
        var error = await Assert.ThrowsAsync<HostErrorException>(() => Call(action == "issue" ? Issue() : new { action }));
        Assert.Contains("refused:", error.Message);
        Assert.Equal(saved, File.ReadAllText(path));
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
        public string PeerName => "Remote app";
        public string PeerKind => "app";
        public bool IsOpen => true;
        public Task Closed => Task.CompletedTask;
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
