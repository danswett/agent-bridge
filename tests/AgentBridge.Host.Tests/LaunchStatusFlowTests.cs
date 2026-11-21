using AgentBridge.Host;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;
public sealed class LaunchStatusFlowTests : LaunchFlowFixture
{
    private protected override bool UsesRelay => false;
    [Theory, InlineData(true), InlineData(false)]
    public async Task Published_status_survives_memory_eviction_and_registration_uses_only_the_exact_live_id(bool registered)
    {
        var folder = Path.Combine(f.Root, "status");
        var launcher = Launcher(Config(), statusFolder: folder, rememberFor: TimeSpan.Zero);
        var request = new LaunchRequest { Request = Guid.NewGuid().ToString("D"), Agent = "copilot", Workspace = "App", Permissions = "allowAll" };
        var external = Guid.NewGuid().ToString("D");
        launcher.PublishAlias(external, request, new LaunchResult(LaunchOutcome.Unconfirmed));
        var result = await launcher.LaunchAuditedAsync(request, "fixture", _ => _ => { });
        Assert.True(result.Outcome == LaunchOutcome.Started, result.Reason);
        clock.Run();
        await Task.Delay(TimeSpan.FromMilliseconds(25), clock);
        Assert.Null(launcher.ReplayAuditedAsync(request, "fixture"));
        if (!registered) { starter.Exit(starter.Pids[0]); await source.RefreshAsync(); }
        var rebuilt = Launcher(Config(), statusFolder: folder);
        var handler = LocalApi.Handler(new SessionService(source, new MachineInfo("host", "fixture", "windows", "test"), launcher: rebuilt) { Time = clock },
            null!, new FixedCallerGuard(new Caller(CallerKind.Agent, "fixture")));
        var endpoint = LocalEndpoint.For(Path.Combine(f.Root, "pipe"));
        await using var pipe = LocalServer.Start(endpoint, handler);
        await using var channel = await LocalClient.ConnectAsync(endpoint);
        await channel.HelloAsync();
        var row = await channel.ReadAsync<LaunchStatus>("launches.status", new { request = external });
        Assert.Equal("started", row.Outcome);
        Assert.Equal(Guid.Parse(result.Session!).ToString("N")[..16], row.Session);
        Assert.Equal("allowAll", row.Permissions);
        Assert.Equal(registered, row.Registered);
        Assert.Equal("unknown", (await channel.ReadAsync<LaunchStatus>("launches.status", new { request = Guid.NewGuid().ToString("D") })).Outcome);
        Assert.Single(await channel.ReadAsync<LaunchStatus[]>("launches.list", new { since = 15 }));
        Assert.Single(starter.Specs);
        Assert.Empty(screens.Typed);
    }
    [Fact]
    public async Task Unconfirmed_is_retained_without_claiming_registration()
    {
        starter.Registers = false;
        var launcher = Launcher(Config(), wait: TimeSpan.FromMilliseconds(100), follow: TimeSpan.FromMilliseconds(200), statusFolder: Path.Combine(f.Root, "status"));
        var result = await launcher.LaunchAsync(new LaunchRequest { Request = Guid.NewGuid().ToString("D"), Agent = "copilot", Workspace = "App" }, "fixture");
        Assert.Equal(LaunchOutcome.Unconfirmed, result.Outcome);
        Assert.Equal("unconfirmed", Assert.Single(launcher.LaunchStatuses()).Outcome);
    }
}
