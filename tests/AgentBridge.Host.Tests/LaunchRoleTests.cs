using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Fleet;
using AgentBridge.Protocol;
using AgentBridge.Relay;
using Xunit;

public sealed class LaunchRoleTests : LaunchFlowFixture
{
    private protected override bool UsesRelay => false;

    [Theory]
    [InlineData("one-shot")]
    [InlineData("lead")]
    [InlineData("preserved")]
    public async Task The_launch_role_survives_restart_and_session_metadata_cannot_change_it(string role)
    {
        var launcher = Launcher(Config());
        var result = await launcher.LaunchAsync(new LaunchRequest
        {
            Request = Guid.NewGuid().ToString(), Agent = "copilot", Workspace = "App", Role = role,
        }, "fixture");
        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        var file = Path.Combine(f.CopilotHome, "session-state", result.Session!, "workspace.yaml");
        File.AppendAllText(file, "\nrole: forged\n");
        await source.RefreshAsync();
        var restarted = Launcher(Config());
        var service = new SessionService(source, new("fixture", "Fixture", "windows", "test"), launcher: restarted);
        var page = JsonSerializer.SerializeToElement(await service.HandleAsync(null!, Ops.SessionsList, default), ProtocolJson.Options);
        Assert.Equal(role, Assert.Single(page.GetProperty("sessions").EnumerateArray()).GetProperty("role").GetString());
        var publisher = new FleetPublisher(source, "fixture", role: restarted.RoleOf);
        Assert.True(await publisher.PublishAsync((record, _) =>
        {
            Assert.Equal(role, Assert.Single(record.GetProperty("sessions").EnumerateArray()).GetProperty("role").GetString());
            Assert.Null(RosterFields.Check(record, "fixture", out var epoch, out var sequence, out var at));
            return Task.CompletedTask;
        }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unknown")]
    public async Task A_missing_or_unknown_launch_role_never_inherits_a_session_supplied_role(string? role)
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
    public async Task A_source_cannot_supply_its_own_role_without_launch_authority()
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
}
