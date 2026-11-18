using System.Text.Json;
using AgentBridge.Host.Fleet;
using AgentBridge.Host.Lite;
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
    readonly Sender sender = new();

    public ScriptCredentialScopeTests() =>
        credentials = new(fixture.Root, "machine1", clock, logs.Add);

    public void Dispose() => fixture.Dispose();

    static JsonElement Body(object value) => JsonSerializer.SerializeToElement(value);

    Func<IProtocolChannel, string, JsonElement, Task<object?>> Handler() =>
        LocalApi.Handler(null!, null!, new FixedCallerGuard(new(CallerKind.App, "fixture app")),
            credentials: credentials, log: logs.Add, fleet: new FleetList(credentials, new Roster()), lite: sender,
            audit: new AgentAuditLog(fixture.Root));

    [Theory]
    [InlineData("fleet.list")]
    [InlineData("fleet.find")]
    public async Task A_script_can_read_the_fleet_without_gaining_the_scopes_claimed_in_its_request(string op)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var issued = credentials.MintScript("collector").Credential;
        var hello = credentials.Accept(channel, issued);
        Assert.Equal(AgentProtocol.ScopeReadReply, hello.Scope);
        var result = await Handler()(channel, op, Body(new
        {
            name = "worker",
            scope = AgentProtocol.ScopeReadReplyLaunch,
            principal = "person:local",
        }));
        Assert.NotNull(result);
        Assert.Equal(issued, credentials.Of(channel));
        Assert.Empty(sender.Calls);
    }

    [Theory]
    [InlineData("session.reply")]
    [InlineData("session.launch")]
    [InlineData("session.answer")]
    [InlineData("host.config.set")]
    [InlineData("scripts.add")]
    [InlineData("scripts.revoke")]
    [InlineData("scripts.grant-fleet")]
    [InlineData("scripts.revoke-fleet")]
    [InlineData("agent.adopt")]
    [InlineData("agent.revoke")]
    [InlineData("agent.credential")]
    [InlineData("agent.hello")]
    [InlineData("future.operation")]
    public async Task A_script_is_refused_operations_outside_read_and_reply_even_when_classified_as_the_app(string op)
    {
        var issued = credentials.MintScript("collector").Credential;
        credentials.Accept(channel, issued);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Handler()(channel, op, Body(new { scope = AgentProtocol.ScopeReadReplyLaunch })));
        Assert.Contains("read+reply", error.Message);
        Assert.Contains("fleet.list, fleet.find and agent.send", error.Message);
        Assert.Equal(issued, credentials.Of(channel));
        Assert.False(credentials.Of(channel)!.FleetLaunch);
        Assert.Equal(1, credentials.Count);
        Assert.Empty(sender.Calls);
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

        Assert.Contains("read+reply", error.Message);
        Assert.Equal(state == "live" ? script : null, credentials.Of(channel));
        Assert.True(credentials.IsLive(replacement));
    }

    [Theory]
    [InlineData("fleet.list", false)]
    [InlineData("fleet.list", true)]
    [InlineData("fleet.find", false)]
    [InlineData("fleet.find", true)]
    [InlineData("agent.send", false)]
    [InlineData("agent.send", true)]
    public async Task A_script_loses_read_and_reply_after_expiry_or_revocation(string op, bool revoke)
    {
        credentials.Accept(channel, credentials.MintScript("collector", TimeSpan.FromMinutes(1)).Credential);
        if (revoke) { credentials.RevokeScript("collector"); }
        else { clock.Advance(TimeSpan.FromMinutes(1)); }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Handler()(channel, op, Body(new { name = "worker" })));
        Assert.Contains("no longer live", error.Message);
        Assert.Empty(sender.Calls);
    }

    [Theory]
    [InlineData(CallerKind.App)]
    [InlineData(CallerKind.HostCli)]
    [InlineData(CallerKind.Other)]
    public async Task A_script_can_send_a_message_as_itself_without_raising_its_scope(CallerKind classification)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var issued = credentials.MintScript("collector").Credential;
        credentials.Accept(channel, issued);
        var handler = LocalApi.Handler(null!, null!, new FixedCallerGuard(new(classification, "fixture")),
            credentials: credentials, lite: sender, audit: new AgentAuditLog(fixture.Root));

        var result = Assert.IsType<LiteSendReply>(await handler(channel, LiteAgentSend.Op, Body(new
        {
            machine = "target",
            session = "agent_bridge_1111111111111111",
            text = "please read this",
            scope = AgentProtocol.ScopeReadReplyLaunch,
            principal = "person:local",
            fleetLaunch = true,
        })));

        Assert.Equal("delivered", result.Outcome);
        var sent = Assert.Single(sender.Calls);
        Assert.Equal(issued, sent.Caller);
        Assert.Equal(new LiteSendInput("target", "agent_bridge_1111111111111111", "please read this"), sent.Input);
        Assert.Equal(issued, credentials.Of(channel));
        Assert.False(credentials.Of(channel)!.FleetLaunch);
        var audit = Directory.GetFiles(Path.Combine(fixture.Root, AgentAuditLog.FolderName), "*.jsonl")
            .SelectMany(File.ReadAllLines).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(row => row.TryGetProperty("action", out _)).ToArray();
        Assert.Equal(new[] { "attempted", "delivered" }, audit.Select(row => row.GetProperty("outcome").GetString()));
        Assert.All(audit, row =>
            Assert.Equal(issued.Principal, row.GetProperty("principal").GetString()));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler(channel, "scripts.grant-fleet", Body(new { name = "collector" })));
        Assert.Contains("read+reply", error.Message);
        Assert.False(credentials.Of(channel)!.FleetLaunch);
    }

    [Fact]
    public async Task A_script_reply_is_refused_without_an_audit_before_anything_is_sent()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        credentials.Accept(channel, credentials.MintScript("collector").Credential);
        var handler = LocalApi.Handler(null!, null!, new FixedCallerGuard(new(CallerKind.Other, "fixture")),
            credentials: credentials, lite: sender);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => handler(channel, LiteAgentSend.Op,
            Body(new { machine = "target", session = "agent_bridge_1111111111111111", text = "hello" })));
        Assert.Contains("auditUnwritable", error.Message);
        Assert.Empty(sender.Calls);
    }

    sealed class Sender : ILiteSender
    {
        public readonly List<(AgentCredential Caller, LiteSendInput Input)> Calls = [];
        public Task<LiteOutcome> SendAsync(AgentCredential caller, LiteSendInput input, CancellationToken ct)
        {
            Calls.Add((caller, input));
            return Task.FromResult(new LiteOutcome(LiteKind.Delivered, null, "fixture-request"));
        }
    }

    sealed class Roster : IScopedFleetRoster
    {
        public Task<IReadOnlyList<FleetMachine>> ReadAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FleetMachine>>([]);
        public Task<FleetRosterSnapshot> ReadScopedAsync(CancellationToken ct) =>
            Task.FromResult(new FleetRosterSnapshot("account-a", []));
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
