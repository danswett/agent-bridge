using System.Text.Json;
using AgentBridge.Host.Fleet;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class ScriptCredentialScopeTests : IDisposable
{
    readonly Fixture fixture = new();
    readonly FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
    readonly AgentCredentials credentials;
    readonly Channel channel = new();
    readonly List<string> logs = [];

    public ScriptCredentialScopeTests() =>
        credentials = new(fixture.Root, "machine1", clock, logs.Add);

    public void Dispose() => fixture.Dispose();

    static JsonElement Body(object value) => JsonSerializer.SerializeToElement(value);

    Func<IProtocolChannel, string, JsonElement, Task<object?>> Handler() =>
        LocalApi.Handler(null!, null!, new FixedCallerGuard(new(CallerKind.App, "fixture app")),
            credentials: credentials, log: logs.Add, fleet: new FleetList(credentials, new Roster()),
            audit: new AgentAuditLog(fixture.Root));

    [Theory]
    [InlineData("fleet.list")]
    [InlineData("fleet.find")]
    public async Task A_script_can_read_the_fleet_without_gaining_the_scopes_claimed_in_its_request(string op)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var issued = credentials.MintScript("collector").Credential;
        var hello = credentials.Accept(channel, issued);
        Assert.Equal(AgentProtocol.ScopeRead, hello.Scope);
        var result = await Handler()(channel, op, Body(new
        {
            sessionId = "agent_bridge_1234567890abcdef",
            scope = AgentProtocol.ScopeReadReplyLaunch,
            principal = "person:local",
        }));
        Assert.NotNull(result);
        Assert.Equal(issued, credentials.Of(channel));
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Handler()(channel, "agent.send", Body(new { })));
        Assert.Contains("fleet.list and fleet.find", refusal.Message);
    }

    [Theory]
    [InlineData("agent.send")]
    [InlineData("session.reply")]
    [InlineData("session.launch")]
    [InlineData("session.answer")]
    [InlineData("host.config.set")]
    [InlineData("scripts.add")]
    [InlineData("agent.adopt")]
    [InlineData("agent.hello")]
    [InlineData("future.operation")]
    public async Task A_script_is_refused_every_non_read_operation_even_when_classified_as_the_app(string op)
    {
        var issued = credentials.MintScript("collector").Credential;
        credentials.Accept(channel, issued);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Handler()(channel, op, Body(new { scope = AgentProtocol.ScopeReadReplyLaunch })));
        Assert.Contains("fleet.list and fleet.find", error.Message);
        Assert.Equal(issued, credentials.Of(channel));
        Assert.Contains(logs, line => line.Contains("script:machine1/collector", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("live")]
    [InlineData("expired")]
    [InlineData("revoked")]
    public void A_script_connection_cannot_be_rebound_to_a_session_credential_with_wider_scope(string state)
    {
        var script = credentials.MintScript("collector", TimeSpan.FromMinutes(1)).Credential;
        credentials.Accept(channel, script);
        if (state == "expired") { clock.Advance(TimeSpan.FromMinutes(1)); }
        if (state == "revoked") { credentials.RevokeScript("collector"); }
        var started = clock.GetUtcNow();
        var token = credentials.Mint("session1", "launch1", 100, started, AgentProtocol.ScopeReadReplyLaunch);
        var replacement = Assert.IsType<AgentCredential>(credentials.Check(token,
            new ProcessAncestry([new PeerProcess(100, started, "agent.exe")], true)).Accepted);

        var error = Assert.Throws<InvalidOperationException>(() => credentials.Accept(channel, replacement));

        Assert.Contains("fleet.list and fleet.find", error.Message);
        Assert.Equal(state == "live" ? script : null, credentials.Of(channel));
        Assert.True(credentials.IsLive(replacement));
    }

    [Theory]
    [InlineData("fleet.list", false)]
    [InlineData("fleet.list", true)]
    [InlineData("fleet.find", false)]
    [InlineData("fleet.find", true)]
    public async Task A_script_loses_even_its_read_operations_after_expiry_or_revocation(string op, bool revoke)
    {
        credentials.Accept(channel, credentials.MintScript("collector", TimeSpan.FromMinutes(1)).Credential);
        if (revoke) { credentials.RevokeScript("collector"); }
        else { clock.Advance(TimeSpan.FromMinutes(1)); }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Handler()(channel, op, Body(new { name = "worker" })));
        Assert.Contains("no longer live", error.Message);
    }

    sealed class Roster : IScopedFleetRoster
    {
        public Task<IReadOnlyList<FleetMachine>> ReadAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FleetMachine>>([new("fixture", true, [new("agent_bridge_1234567890abcdef", "worker", "idle", DateTimeOffset.UtcNow)], Verified: true)]);
        public async Task<FleetRosterSnapshot> ReadScopedAsync(CancellationToken ct) =>
            new("account-a", await ReadAsync(ct));
    }

    sealed class Channel : IProtocolChannel
    {
        public string PeerId => "fixture";
        public string PeerName => "fixture";
        public string PeerKind => "fixture";
        public bool IsOpen => true;
        public Task Closed => Task.CompletedTask;
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) =>
            Task.FromResult(0L);
    }
}
