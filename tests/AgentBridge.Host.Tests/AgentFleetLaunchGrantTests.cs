using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Admission = System.Func<AgentBridge.Protocol.LaunchRequest, string, System.Func<System.Func<string?>, System.Threading.Tasks.Task<AgentBridge.Protocol.LaunchResult>>, System.Threading.Tasks.Task<AgentBridge.Protocol.LaunchResult>>;

public sealed class AgentFleetLaunchGrantTests : IAsyncLifetime
{
    const string Op = "host.agents.fleetLaunchGrant";
    readonly string folder = Directory.CreateTempSubdirectory("ab-agent-fleet-grant-").FullName;
    readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 18, 7, 0, 0, TimeSpan.Zero));
    readonly Callers callers = new();
    AgentCredentials credentials = null!;
    AgentCredential script = null!;
    AgentCredential agent = null!;
    readonly LaunchProbe launcher = new();
    bool agentConnection;
    bool scriptConnection;
    Admission? caps;
    LocalServer server = null!;
    Func<IProtocolChannel, string, JsonElement, Task<object?>> handler = null!;

    public Task InitializeAsync()
    {
        credentials = new AgentCredentials(folder, "machine", time);
        (script, _) = credentials.MintScript("fixture");
        var started = time.GetUtcNow();
        var token = credentials.Mint("fixture-session", "fixture-launch", 42, started, AgentProtocol.ScopeRead);
        agent = credentials.Check(token, new ProcessAncestry([new PeerProcess(42, started, "agent.exe")], true)).Accepted!;
        RestartControl();
        server = LocalServer.Start(LocalEndpoint.For(folder), (channel, op, data) =>
        {
            if (scriptConnection) { credentials.Accept(channel, script); }
            if (agentConnection) { credentials.Accept(channel, agent); }
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
        handler = LocalApi.Handler(new SessionService(null!, new("machine", "Fixture", "windows", "0.1.0"),
            launcher: launcher) { FleetLaunchAdmission = caps }, control, callers, credentials: credentials, audit: new AgentAuditLog(folder, time));
    }

    public async Task DisposeAsync()
    {
        await server.DisposeAsync();
        Directory.Delete(folder, recursive: true);
    }

    async Task<JsonElement> Call(object data, string op = Op)
    {
        await using var channel = await LocalClient.ConnectAsync(LocalEndpoint.For(folder), TimeSpan.FromSeconds(10));
        return await channel.CallAsync(op, data);
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

    LaunchRequest Launch() => new()
    {
        Request = Guid.NewGuid().ToString("D"), Agent = "copilot", Workspace = "Fixture", Role = "builder",
        FleetName = "SG Fixture", Permissions = LaunchProtocol.AllowAll, Prompt = "private fixture prompt",
        Model = "fixture-model", Effort = "high", Context = "default",
    };

    void ConfigureFleet()
    {
        File.WriteAllText(Path.Combine(folder, HostSettings.FileName), JsonSerializer.Serialize(new
        {
            agents = new { enabled = true, fleet = new { workspaces = new[] { "Fixture" } } },
            launch = new { enabled = true, workspaces = new[] { new { label = "Fixture", path = folder, isolate = true } },
                workerMcp = new Dictionary<string, string[]> { ["builder"] = [] } },
        }));
        launcher.Config = new() { Workspaces = [new("Fixture", folder, true)] };
        agentConnection = true;
        callers.Kind = CallerKind.Agent;
    }

    JsonElement[] Attempts() => Directory.GetFiles(Path.Combine(folder, "audit"), "*.jsonl")
        .SelectMany(File.ReadLines).Select(s => JsonSerializer.Deserialize<JsonElement>(s))
        .Where(r => r.TryGetProperty("action", out var action) && action.GetString() == "fleet.launch.attempt").ToArray();

    [Theory]
    [InlineData("none", "none")]
    [InlineData("expired", "expired")]
    [InlineData("revoked", "revoked")]
    [InlineData("corrupt", "read safely")]
    public async Task Fleet_launch_requires_a_readable_active_person_grant_and_audits_each_refusal(string state, string reason)
    {
        if (state != "none")
        {
            var issued = await Call(Issue());
            if (state == "expired") { time.Advance(TimeSpan.FromHours(1)); }
            if (state == "revoked") { await Call(new { action = "revoke", grant = issued.GetProperty("grant").GetProperty("id").GetString() }); }
            if (state == "corrupt") { File.WriteAllText(Path.Combine(folder, "agent-fleet-launch-grant.json"), "{broken"); }
        }
        ConfigureFleet();
        var error = await Assert.ThrowsAsync<HostErrorException>(() => Call(Launch(), LaunchProtocol.Launch));
        Assert.Contains(reason, error.Message);
        var row = Assert.Single(Attempts());
        Assert.Equal("fleet-launcher", row.GetProperty("principal").GetString());
        Assert.Equal("refused", row.GetProperty("outcome").GetString());
        Assert.Equal(0, launcher.Reads);
    }

    [Fact]
    public async Task A_durable_exact_key_and_grant_are_audited_before_dispatch_and_replay_stays_refused_after_a_crash()
    {
        var issued = await Call(Issue());
        var id = issued.GetProperty("grant").GetProperty("id").GetString();
        ConfigureFleet();
        var request = Launch();
        launcher.BeforeConfig = () =>
        {
            var row = Assert.Single(Attempts());
            Assert.Equal(id, row.GetProperty("grant").GetString());
            Assert.Equal(request.Request, row.GetProperty("request").GetString());
            Assert.Equal(LaunchService.RequestBinding(request), row.GetProperty("target").GetString());
            Assert.Equal("reserved", row.GetProperty("outcome").GetString());
            Assert.Equal("agent:machine/fixture-session", Assert.Single(row.GetProperty("chain").EnumerateArray()).GetString());
            Assert.DoesNotContain("private fixture", row.GetRawText());
            throw new InvalidOperationException("fixture crash after durable admission");
        };
        var crash = await Assert.ThrowsAsync<HostErrorException>(() => Call(request, LaunchProtocol.Launch));
        Assert.Contains("fixture crash", crash.Message);
        RestartControl();
        launcher.BeforeConfig = null;
        var replay = await Assert.ThrowsAsync<HostErrorException>(() => Call(request, LaunchProtocol.Launch));
        Assert.Contains("replay", replay.Message);
        Assert.Equal(1, launcher.Reads);
        Assert.Equal(2, Attempts().Length);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("workspace")]
    [InlineData("model")]
    [InlineData("effort")]
    [InlineData("context")]
    [InlineData("permissions")]
    [InlineData("prompt")]
    [InlineData("agent")]
    [InlineData("fleetName")]
    public async Task A_consumed_request_cannot_be_reused_with_any_changed_launch_field(string field)
    {
        await Call(Issue());
        ConfigureFleet();
        var request = Launch();
        var first = await Call(request, LaunchProtocol.Launch);
        Assert.Contains("admission", first.GetProperty("reason").GetString());
        var changed = JsonSerializer.SerializeToNode(request, ProtocolJson.Options)!.AsObject();
        changed[field] = field == "permissions" ? LaunchProtocol.Ask : "different";
        var error = await Assert.ThrowsAsync<HostErrorException>(() => Call(changed, LaunchProtocol.Launch));
        Assert.Contains("request key", error.Message);
        Assert.Equal(1, launcher.Reads);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("workspace")]
    [InlineData("extraArgs")]
    public async Task Unknown_roles_workspaces_and_request_fields_never_reach_dispatch(string field)
    {
        await Call(Issue());
        ConfigureFleet();
        var request = JsonSerializer.SerializeToNode(Launch(), ProtocolJson.Options)!.AsObject();
        request[field] = "unknown";
        await Assert.ThrowsAsync<HostErrorException>(() => Call(request, LaunchProtocol.Launch));
        Assert.Equal("refused", Assert.Single(Attempts()).GetProperty("outcome").GetString());
        Assert.Equal(0, launcher.Reads);
    }

    [Theory]
    [InlineData(Ops.SessionAnswer)]
    [InlineData(Ops.SessionReply)]
    [InlineData("launch.trust.answer")]
    [InlineData(Ops.SessionStop)]
    public async Task An_active_fleet_grant_never_authorizes_person_only_operations(string op)
    {
        await Call(Issue());
        ConfigureFleet();
        var data = new { request = Guid.NewGuid().ToString("D"), id = "target" };
        if (op == "launch.trust.answer") { Assert.Equal("refused", (await Call(data, op)).GetProperty("outcome").GetString()); }
        else { await Assert.ThrowsAsync<HostErrorException>(() => Call(data, op)); }
        Assert.Equal(0, launcher.Reads);
    }

    [Fact]
    public async Task An_unwritable_audit_refuses_admission_before_any_launch_work()
    {
        await Call(Issue());
        ConfigureFleet();
        File.WriteAllText(Path.Combine(folder, "audit"), "blocked");
        var error = await Assert.ThrowsAsync<HostErrorException>(() => Call(Launch(), LaunchProtocol.Launch));
        Assert.Contains("audit", error.Message);
        Assert.Equal(0, launcher.Reads);
    }

    [Fact]
    public async Task Granted_launches_reach_the_hourly_cap_through_the_real_pipe_and_audit()
    {
        await Call(Issue());
        ConfigureFleet();
        caps = FleetLaunchCaps.Create(folder, new AgentAuditLog(folder, time),
            () => new(time.GetUtcNow(), new(1, null, null, RunQueue: 7),
                new Dictionary<string, AgentBridge.Host.Core.Load.SessionLoad>()), _ => false, time);
        RestartControl();
        for (var i = 0; i < 8; i++)
        { Assert.Equal("started", (await Call(Launch(), LaunchProtocol.Launch)).GetProperty("outcome").GetString()); }
        RestartControl();
        var refused = await Call(Launch(), LaunchProtocol.Launch);
        Assert.Contains("hourly-limit", refused.GetProperty("reason").GetString());
        Assert.Equal(8, launcher.Starts);
        var config = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(folder, HostSettings.FileName)))!;
        config["agents"]!["enabled"] = false;
        File.WriteAllText(Path.Combine(folder, HostSettings.FileName), config.ToJsonString());
        var disabled = await Assert.ThrowsAsync<HostErrorException>(() => Call(Launch(), LaunchProtocol.Launch));
        Assert.Contains("agents.enabled", disabled.Message);
        Assert.Contains(Attempts(), row => row.GetProperty("reason").GetString()!.Contains("agents.enabled", StringComparison.Ordinal));
    }

    sealed class LaunchProbe : ISessionLauncher
    {
        public int Reads { get; private set; }
        public int Starts { get; private set; }
        public Action? BeforeConfig { get; set; }
        public LaunchConfig Config { get; set; } = new();
        public bool Available => true;
        public LaunchConfig FleetConfig { get { Reads++; BeforeConfig?.Invoke(); return Config; } }
        public Task<LaunchOptions> OptionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LaunchResult> LaunchAsync(LaunchRequest request, string peer, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LaunchResult> LaunchFleetAsync(LaunchRequest request, string principal, Func<Guid, Action<string>?> prepare,
            CancellationToken cancellationToken = default)
        {
            if (prepare(Guid.NewGuid()) is not { } registered) { return Task.FromResult(new LaunchResult(LaunchOutcome.Refused)); }
            var session = Guid.NewGuid().ToString("D");
            registered(session);
            Starts++;
            return Task.FromResult(new LaunchResult(LaunchOutcome.Started) { Session = session });
        }
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
