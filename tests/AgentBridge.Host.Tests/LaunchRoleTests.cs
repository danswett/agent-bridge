using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Fleet;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed class LaunchRoleTests : LaunchFlowFixture
{
    private protected override bool UsesRelay => false;

    [Theory]
    [InlineData("one-shot", false)]
    [InlineData("lead", true)]
    [InlineData("preserved", false)]
    public async Task The_launch_role_survives_restart_and_session_metadata_cannot_change_it(string role, bool patch)
    {
        Directory.CreateDirectory(f.CopilotHome);
        File.WriteAllText(Path.Combine(f.CopilotHome, "mcp-config.json"), """{"mcpServers":{"keep":{"command":"fake"},"drop":{"command":"fake"}}}""");
        var launcher = Launcher(Config(c => c with { WorkerMcp = new Dictionary<string, IReadOnlyList<string>> { ["worker-mcp"] = ["keep"] } }));
        var request = JsonSerializer.Deserialize<LaunchRequest>(JsonSerializer.Serialize(new
        {
            request = Guid.NewGuid().ToString(), agent = "copilot", workspace = "App", permissions = "ask", role = "worker-mcp", sessionRole = role,
        }), ProtocolJson.Options)!;
        Assert.Null(AgentLaunchRequestCheck.Check(JsonSerializer.SerializeToElement(request, ProtocolJson.Options), []));
        var result = await launcher.LaunchAsync(request, "fixture");
        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.Contains("drop", Assert.Single(starter.Specs).Arguments);
        Assert.DoesNotContain("keep", Assert.Single(starter.Specs).Arguments);
        var file = Path.Combine(f.CopilotHome, "session-state", result.Session!, "workspace.yaml");
        File.AppendAllText(file, "\nrole: forged\n");
        await source.RefreshAsync();
        var restarted = Launcher(Config());
        var service = new SessionService(source, new("fixture", "Fixture", "windows", "test"), launcher: restarted);
        var page = JsonSerializer.SerializeToElement(await service.HandleAsync(null!, Ops.SessionsList, default), ProtocolJson.Options);
        Assert.Equal(role, Assert.Single(page.GetProperty("sessions").EnumerateArray()).GetProperty("role").GetString());
        var channel = new RoleChannel();
        var subscription = Assert.IsType<ChannelReply>(await service.HandleAsync(channel, Ops.SessionsSubscribe, JsonSerializer.SerializeToElement(new { patch })));
        var feed = subscription.Then(channel);
        try { Assert.Equal(role, (await channel.Upsert.Task.WaitAsync(TimeSpan.FromSeconds(5))).Role); }
        finally { channel.End.TrySetResult(); await feed.WaitAsync(TimeSpan.FromSeconds(5)); }
        var publisher = new FleetPublisher(source, "fixture", role: restarted.RoleOf);
        Assert.True(await publisher.PublishAsync((record, _) =>
        {
            Assert.Equal(role, Assert.Single(record.GetProperty("sessions").EnumerateArray()).GetProperty("role").GetString());
            return Task.CompletedTask;
        }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("one-shot")]
    [InlineData("unknown")]
    public async Task A_missing_session_role_never_inherits_the_MCP_role(string? role)
    {
        var launcher = Launcher(Config());
        var result = await launcher.LaunchAsync(new LaunchRequest
        {
            Request = Guid.NewGuid().ToString(), Agent = "copilot", Workspace = "App", Role = role,
        }, "fixture");
        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        var service = new SessionService(source, new("fixture", "Fixture", "windows", "test"), launcher: Launcher(Config()));
        var page = JsonSerializer.SerializeToElement(await service.HandleAsync(null!, Ops.SessionsList, default), ProtocolJson.Options);
        Assert.False(Assert.Single(page.GetProperty("sessions").EnumerateArray()).TryGetProperty("role", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("One-Shot")]
    public void Unknown_session_roles_are_refused_at_the_launch_boundary(string role) =>
        Assert.NotNull(LaunchProtocol.Check(JsonSerializer.Deserialize<LaunchRequest>(
            JsonSerializer.Serialize(new { request = Guid.NewGuid().ToString(), sessionRole = role }), ProtocolJson.Options)!));

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    public async Task Unavailable_provenance_omits_the_advisory_role_without_breaking_reads(string corrupt)
    {
        var result = await Launcher(Config()).LaunchAsync(new LaunchRequest
        {
            Request = Guid.NewGuid().ToString(), Agent = "copilot", Workspace = "App",
        }, "fixture");
        File.WriteAllText(Path.Combine(f.Root, "profile", "launch-provenance.json"), corrupt);
        var launcher = Launcher(Config());
        var service = new SessionService(source, new("fixture", "Fixture", "windows", "test"), launcher: launcher);
        var page = Assert.IsType<SessionList>(await service.HandleAsync(null!, Ops.SessionsList, default));
        Assert.Null(Assert.Single(page.Sessions).Role);
        Assert.Null(Assert.IsType<Session>(await service.HandleAsync(null!, Ops.SessionGet,
            JsonSerializer.SerializeToElement(new { id = result.Session }))).Role);
        Assert.True(await new FleetPublisher(source, "fixture", role: launcher.RoleOf).PublishAsync((record, ct) =>
        {
            Assert.False(Assert.Single(record.GetProperty("sessions").EnumerateArray()).TryGetProperty("role", out _));
            return Task.CompletedTask;
        }));
    }

    [Fact]
    public void Legacy_unbound_or_reused_processes_never_acquire_a_role()
    {
        var path = Path.Combine(f.Root, "provenance.json");
        var request = Guid.NewGuid();
        var process = new SessionProcess(123, DateTimeOffset.UtcNow);
        var provenance = new LaunchProvenance(path, _ => { });
        provenance.Preparing(request, "session", role: "one-shot");
        Assert.Null(provenance.RoleOf("session", process));
        provenance.Bound(request, "session", process);
        var reopened = new LaunchProvenance(path, _ => { });
        Assert.Equal("one-shot", reopened.RoleOf("session", process));
        Assert.Null(reopened.RoleOf("session", process with { Id = 124 }));
        Assert.Null(reopened.RoleOf("session", process with { StartedAt = process.StartedAt!.Value.AddSeconds(1) }));
        var legacy = Guid.NewGuid();
        reopened.Preparing(legacy, "legacy");
        reopened.Bound(legacy, "legacy", process);
        Assert.Null(new LaunchProvenance(path, _ => { }).RoleOf("legacy", process));
    }

    [Fact]
    public async Task A_source_cannot_supply_its_own_role_without_launch_metadata()
    {
        var discovered = new FleetPublisherTests.Source();
        discovered.Sessions = [discovered.Sessions[0] with { Role = "one-shot" }];
        var service = new SessionService(discovered, new("fixture", "Fixture", "windows", "test"));
        var page = JsonSerializer.SerializeToElement(await service.HandleAsync(null!, Ops.SessionsList, default), ProtocolJson.Options);
        Assert.False(Assert.Single(page.GetProperty("sessions").EnumerateArray()).TryGetProperty("role", out _));
        Assert.True(await new FleetPublisher(discovered, "fixture").PublishAsync((record, ct) =>
        {
            Assert.False(Assert.Single(record.GetProperty("sessions").EnumerateArray()).TryGetProperty("role", out _));
            return Task.CompletedTask;
        }));
    }

    [Theory]
    [InlineData(CallerKind.Agent, "session.stop", true)]
    [InlineData(CallerKind.App, "session.stop", false)]
    [InlineData(CallerKind.Agent, "agent.endSelf", false)]
    [InlineData(CallerKind.Agent, "session.endSelf", false)]
    [InlineData(CallerKind.Agent, "EndSelf", false)]
    public async Task Stop_and_unavailable_EndSelf_paths_never_read_the_advisory_role(CallerKind kind, string op, bool refused)
    {
        var actions = new DisplayOnlyRole();
        var service = new SessionService(source, new("fixture", "Fixture", "windows", "test"), launcher: actions, stops: actions);
        var handler = LocalApi.Handler(service, null!, new FixedCallerGuard(new Caller(kind, "fixture")));
        var body = JsonSerializer.SerializeToElement(new { id = "target", request = Guid.NewGuid().ToString(), role = "one-shot" });
        if (refused) { await Assert.ThrowsAsync<InvalidOperationException>(() => handler(null!, op, body)); }
        else if (op == Ops.SessionStop) { Assert.Equal(StopOutcome.Ended, Assert.IsType<StopResult>(await handler(null!, op, body)).Outcome); }
        else { Assert.Null(await handler(null!, op, body)); }
        Assert.Equal(kind == CallerKind.App ? 1 : 0, actions.Stops);
    }

    sealed class DisplayOnlyRole : ISessionLauncher, ISessionStopper
    {
        public int Stops;
        public bool Available => true;
        public string? RoleOf(string session, SessionProcess? process) =>
            throw new InvalidOperationException("Advisory persisted roles must never authorize a stop.");
        public Task<LaunchOptions> OptionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LaunchResult> LaunchAsync(LaunchRequest request, string peer, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StopResult> StopAsync(StopRequest request, CancellationToken cancellationToken = default)
        {
            Stops++;
            return Task.FromResult(new StopResult(StopOutcome.Ended));
        }
    }

    sealed class RoleChannel : IProtocolChannel
    {
        public readonly TaskCompletionSource<Session> Upsert = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource End = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string PeerId => "fixture";
        public string PeerName => "fixture";
        public string PeerKind => "fixture";
        public bool IsOpen => !End.Task.IsCompleted;
        public Task Closed => End.Task;
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default)
        {
            if (data is Session session) { Upsert.TrySetResult(session); }
            return Task.FromResult(0L);
        }
    }
}
