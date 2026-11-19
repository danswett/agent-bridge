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
        var publisher = new FleetPublisher(source, "fixture", role: restarted.RoleOf, relaySupportsRoles: true);
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

    [Fact]
    public async Task Registry_roles_are_omitted_until_relay_support_is_explicit()
    {
        var discovered = new FleetPublisherTests.Source { Binding = _ => new(123, Fixture.T0) };
        await new FleetPublisher(discovered, "fixture", role: (_, _) => "lead").PublishAsync((record, ct) =>
        {
            Assert.All(record.GetProperty("sessions").EnumerateArray(), s => Assert.False(s.TryGetProperty("role", out _)));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Registry_publication_uses_one_process_binding_per_session()
    {
        var reads = 0;
        var discovered = new FleetPublisherTests.Source { Binding = _ => new(++reads, Fixture.T0.AddSeconds(reads)) };
        discovered.Sessions = [discovered.Sessions[0]];
        await new FleetPublisher(discovered, "fixture", role: (_, _) => "lead", relaySupportsRoles: true).PublishAsync((record, _) =>
        {
            Assert.Equal(Fixture.T0.AddSeconds(1), record.GetProperty("sessions")[0].GetProperty("startedAt").GetDateTimeOffset());
            Assert.Equal("lead", record.GetProperty("sessions")[0].GetProperty("role").GetString());
            Assert.Equal(1, reads);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task An_advisory_role_never_displaces_an_existing_budgeted_notice()
    {
        var launcher = Launcher(Config());
        var launched = await launcher.LaunchAsync(new() { Request = Guid.NewGuid().ToString(), Agent = "copilot", Workspace = "App", SessionRole = "lead" }, "fixture");
        var part = new HistoryPart { Id = "part", At = Fixture.T0, Kind = HistoryPartKind.Response, Text = new string('n', 16000) };
        var session = source.Get(launched.Session!)! with { Activity = new Activity
        {
            Response = new string('r', 6000), Reasoning = "",
            Notice = new HistoryTurn { Id = "notice", At = Fixture.T0, Parts = [part, part with { Id = "part2" }] },
        } };
        session = session with { Activity = session.Activity with { Reasoning = new string('q', SessionBudget.MaxSessionBytes - ProtocolJson.ByteCount(session)) } };
        Assert.Equal(40960, ProtocolJson.ByteCount(session));
        var discovered = new FleetPublisherTests.Source { Sessions = [session], Binding = source.ProcessOf };
        var service = new SessionService(discovered, new("fixture", "Fixture", "windows", "test"), launcher: launcher);
        var actual = Assert.IsType<Session>(await service.HandleAsync(null!, Ops.SessionGet, JsonSerializer.SerializeToElement(new { id = session.Id })));
        Assert.Equal(session.Activity.Notice, actual.Activity.Notice);
        Assert.Null(actual.Role);
    }

    [Fact]
    public async Task Role_binding_is_revisioned_and_a_queued_snapshot_keeps_its_original_incarnation()
    {
        var launcher = Launcher(Config());
        using var changes = source.Subscribe();
        var launch = launcher.LaunchAsync(new() { Request = Guid.NewGuid().ToString(), Agent = "copilot", Workspace = "App", SessionRole = "lead" }, "fixture");
        SessionEvent first;
        do { first = await changes.Events.ReadAsync().AsTask().WaitAsync(Patience); } while (first.Data is not Session);
        var launched = await launch;
        Assert.True(source.Revision > first.Revision, "Binding the role must advance the source revision.");
        using var resumed = source.Subscribe(first.Revision);
        Assert.Equal(1, resumed.Result.Count);
        var service = new SessionService(source, new("fixture", "Fixture", "windows", "test"), launcher: launcher);
        var channel = new RoleChannel();
        var subscription = Assert.IsType<ChannelReply>(await service.HandleAsync(channel, Ops.SessionsSubscribe, default));
        var process = source.ProcessOf(launched.Session!)!;
        f.Processes.Run(AgentKind.Copilot, process.Id, $"copilot --session-id {launched.Session}", process.StartedAt!.Value.AddSeconds(10));
        await source.RefreshAsync();
        var feed = subscription.Then(channel);
        try { Assert.Equal("lead", (await channel.Upsert.Task.WaitAsync(Patience)).Role); }
        finally { channel.End.TrySetResult(); await feed.WaitAsync(Patience); }
    }

    [Fact]
    public async Task An_initially_unreadable_start_time_does_not_permanently_lose_the_launch_role()
    {
        f.Processes.Queried = agent =>
        {
            if (agent == AgentKind.Copilot) { foreach (var pid in starter.Pids) { f.Processes.RunUndated(agent, pid); } }
        };
        var launcher = Launcher(Config());
        var launched = await launcher.LaunchAsync(new() { Request = Guid.NewGuid().ToString(), Agent = "copilot", Workspace = "App", SessionRole = "lead" }, "fixture");
        Assert.Null(source.ProcessOf(launched.Session!)!.StartedAt);
        var original = Assert.Single(launcher.LaunchedProcesses());
        f.Processes.Queried = null;
        f.Processes.Run(AgentKind.Copilot, original.ProcessId, $"copilot --session-id {launched.Session}", original.StartedAt.AddMilliseconds(10));
        await source.RefreshAsync();
        Assert.Null(launcher.RoleOf(launched.Session!, source.ProcessOf(launched.Session!)));
        f.Processes.Run(AgentKind.Copilot, original.ProcessId, $"copilot --session-id {launched.Session}", original.StartedAt);
        await source.RefreshAsync();
        await WaitAsync(() => Task.FromResult(launcher.RoleOf(launched.Session!, source.ProcessOf(launched.Session!)) == "lead"));
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
