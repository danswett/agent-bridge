using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class LocalApiLaunchStopAuditTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "abaudit-" + Guid.NewGuid().ToString("N"));
    readonly AgentCredentials credentials;
    readonly Chan channel = new();
    readonly Actions actions = new();
    readonly FakeTimeProvider clock = new();
    const string Request = "11111111-1111-4111-8111-111111111111";
    const string Target = "22222222-2222-4222-8222-222222222222";
    const string Secret = "private prompt \u00e9, token and path";
    const string Reason = "answering, replying, ending and launching sessions are for the person, from the app or a phone";

    public LocalApiLaunchStopAuditTests()
    {
        Directory.CreateDirectory(root);
        credentials = new AgentCredentials(root, "machine1");
        var started = DateTimeOffset.UtcNow;
        var token = credentials.Mint("session1", "launch1", 100, started, AgentProtocol.ScopeRead, chain: ["agent:upstream/parent"]);
        credentials.Accept(channel, credentials.Check(token,
            new AgentBridge.Local.ProcessAncestry([new AgentBridge.Local.PeerProcess(100, started, "agent.exe")], true)).Accepted!);
    }

    public void Dispose() => Directory.Delete(root, true);

    Func<IProtocolChannel, string, JsonElement, Task<object?>> Handler(AgentAuditLog? audit, CallerKind kind = CallerKind.Agent) =>
        LocalApi.Handler(new SessionService(null!, new MachineInfo("machine1", "Test", "windows", "0.1.0"),
            launcher: actions, stops: actions), null!, new FixedCallerGuard(new Caller(kind, "fixture")), credentials: credentials, audit: audit, auditTime: clock);

    static JsonElement Body() => JsonSerializer.SerializeToElement(new
    {
        request = Request, id = Target, prompt = Secret, principal = "owner", chain = new[] { "owner" }, confirm = Secret,
    });

    JsonElement Line(string op) => JsonSerializer.Deserialize<JsonElement>(Assert.Single(File.ReadAllLines(
        Assert.Single(Directory.GetFiles(Path.Combine(root, AgentAuditLog.FolderName), op == LaunchProtocol.Launch ? "launches-*.jsonl" : "agents-*.jsonl"))).Skip(1)));

    [Theory]
    [InlineData(LaunchProtocol.Launch)]
    [InlineData(Ops.SessionStop)]
    public async Task A_refused_agent_launch_or_stop_writes_one_credential_attributed_record_without_private_text(string op)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(new AgentAuditLog(root))(channel, op, Body()));

        Assert.Contains(Reason, error.Message);
        Assert.Equal(0, actions.Calls);
        var line = Line(op);
        Assert.Equal("agent:machine1/session1", line.GetProperty("principal").GetString());
        Assert.Equal("agent:upstream/parent", Assert.Single(line.GetProperty("chain").EnumerateArray()).GetString());
        Assert.Equal("local", line.GetProperty("direction").GetString());
        Assert.Equal(Request, line.GetProperty("request").GetString());
        Assert.Equal(op, line.GetProperty("action").GetString());
        Assert.Equal(op == LaunchProtocol.Launch ? "local" : Target, line.GetProperty("target").GetString());
        Assert.Equal("refused", line.GetProperty("outcome").GetString());
        Assert.Equal(Reason, line.GetProperty("reason").GetString());
        Assert.True(line.GetProperty("at").TryGetDateTimeOffset(out _));
        if (op == LaunchProtocol.Launch)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(Secret);
            Assert.Equal(bytes.Length, line.GetProperty("textLength").GetInt32());
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
                line.GetProperty("dataSha256").GetString());
        }
        else { Assert.Equal(JsonValueKind.Null, line.GetProperty("textLength").ValueKind); }
        Assert.DoesNotContain("private", line.GetRawText());
        Assert.DoesNotContain("owner", line.GetRawText());
    }

    [Theory]
    [InlineData(LaunchProtocol.Launch, false)]
    [InlineData(LaunchProtocol.Launch, true)]
    [InlineData(Ops.SessionStop, false)]
    [InlineData(Ops.SessionStop, true)]
    public async Task A_missing_or_failed_audit_refuses_the_agent_attempt_without_dispatch(string op, bool wired)
    {
        File.WriteAllText(Path.Combine(root, AgentAuditLog.FolderName), "blocked");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Handler(wired ? new AgentAuditLog(root) : null)(channel, op, Body()));

        Assert.Equal("refused: auditUnwritable: the agent audit log could not be written; nothing was launched or stopped", error.Message);
        Assert.Equal(0, actions.Calls);
        Assert.Equal("blocked", File.ReadAllText(Path.Combine(root, AgentAuditLog.FolderName)));
    }

    [Theory]
    [InlineData(CallerKind.App)]
    [InlineData(CallerKind.Other)]
    [InlineData(CallerKind.HostCli)]
    public async Task Person_launches_and_stops_are_not_changed_or_written_to_the_agent_log(CallerKind kind)
    {
        var handler = Handler(null, kind);
        Assert.Equal(LaunchOutcome.Started, Assert.IsType<LaunchResult>(await handler(channel, LaunchProtocol.Launch, Body())).Outcome);
        Assert.Equal(StopOutcome.Ended, Assert.IsType<StopResult>(await handler(channel, Ops.SessionStop, Body())).Outcome);
        Assert.Equal(2, actions.Calls);
        Assert.False(Directory.Exists(Path.Combine(root, AgentAuditLog.FolderName)));
    }

    [Theory]
    [InlineData(LaunchProtocol.Launch)]
    [InlineData(Ops.SessionStop)]
    public async Task A_refused_attempt_does_not_copy_unvalidated_request_or_target_text_into_the_log(string op)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(new AgentAuditLog(root))(channel, op,
            JsonSerializer.SerializeToElement(new { request = Secret, id = Secret })));
        Assert.Contains(Reason, error.Message);
        var line = Line(op);
        Assert.True(Guid.TryParse(line.GetProperty("request").GetString(), out _));
        Assert.Equal(op == LaunchProtocol.Launch ? "local" : "unknown", line.GetProperty("target").GetString());
        Assert.DoesNotContain("private", line.GetRawText());
    }

    [Theory]
    [InlineData(LaunchProtocol.Launch)]
    [InlineData(Ops.SessionStop)]
    public async Task An_unauthenticated_or_revoked_agent_stays_refused_without_inventing_a_principal(string op)
    {
        var handler = Handler(new AgentAuditLog(root));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler(new Chan(), op, Body()));
        credentials.RevokeSession("session1", "test");
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler(channel, op, Body()));
        Assert.Equal(0, actions.Calls);
        Assert.False(Directory.Exists(Path.Combine(root, AgentAuditLog.FolderName)));
    }

    [Theory]
    [InlineData(LaunchProtocol.Launch, """{"request":"\uD800"}""")]
    [InlineData(Ops.SessionStop, """{"id":"\uD800"}""")]
    public async Task Undecodable_identifiers_still_leave_one_refused_record(string op, string json)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(new AgentAuditLog(root))(channel, op, JsonDocument.Parse(json).RootElement));
        Assert.Equal("refused", Line(op).GetProperty("outcome").GetString());
        Assert.Equal(0, actions.Calls);
    }

    [Theory]
    [InlineData(LaunchProtocol.Launch)]
    [InlineData(Ops.SessionStop)]
    public async Task Refusal_audit_is_limited_per_principal_with_counted_minute_summaries_and_a_fixed_hour_reset(string op)
    {
        var handler = Handler(new AgentAuditLog(root));
        async Task Refused(Chan who, string action)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => handler(who, action, Body()));
            Assert.Contains(Reason, error.Message);
        }
        JsonElement[] Rows() => Directory.GetFiles(Path.Combine(root, AgentAuditLog.FolderName), "*.jsonl")
            .SelectMany(f => File.ReadAllLines(f).Skip(1)).Select(s => JsonSerializer.Deserialize<JsonElement>(s)).ToArray();
        await Task.WhenAll(Enumerable.Range(0, 60).Select(_ => Task.Run(() => Refused(channel, op))));
        Assert.Equal(60, Rows().Length);
        await Refused(channel, op);
        await Refused(channel, op == LaunchProtocol.Launch ? Ops.SessionStop : LaunchProtocol.Launch);
        Assert.Equal(60, Rows().Length);
        clock.Advance(TimeSpan.FromSeconds(59));
        await Refused(channel, op);
        Assert.Equal(60, Rows().Length);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Refused(channel, op);
        Assert.Equal(61, Rows().Length);
        Assert.Equal("4 additional launch or stop attempts were refused", Rows().Single(r => r.GetProperty("reason").GetString() != Reason).GetProperty("reason").GetString());
        await Refused(channel, op);
        clock.Advance(TimeSpan.FromSeconds(59));
        await Refused(channel, op);
        Assert.Equal(61, Rows().Length);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Refused(channel, op);
        Assert.Equal(62, Rows().Length);
        Assert.Contains(Rows(), r => r.GetProperty("reason").GetString() == "3 additional launch or stop attempts were refused");
        var other = new Chan();
        var started = DateTimeOffset.UtcNow;
        var token = credentials.Mint("session2", "launch2", 200, started, AgentProtocol.ScopeRead);
        credentials.Accept(other, credentials.Check(token,
            new AgentBridge.Local.ProcessAncestry([new AgentBridge.Local.PeerProcess(200, started, "agent.exe")], true)).Accepted!);
        await Refused(other, op);
        Assert.Single(Rows(), r => r.GetProperty("principal").GetString() == "agent:machine1/session2");
        clock.Advance(TimeSpan.FromMinutes(58));
        await Refused(channel, op);
        Assert.Equal(64, Rows().Length);
        Assert.Equal(61, Rows().Count(r => r.GetProperty("principal").GetString() == "agent:machine1/session1" && r.GetProperty("reason").GetString() == Reason));
        Assert.Equal(0, actions.Calls);
    }

    [Theory]
    [InlineData(LaunchProtocol.Launch)]
    [InlineData(Ops.SessionStop)]
    public async Task A_failed_summary_append_stays_fail_closed_and_preserves_its_count_for_retry(string op)
    {
        var handler = Handler(new AgentAuditLog(root));
        for (var i = 0; i < 60; i++) { await Assert.ThrowsAsync<InvalidOperationException>(() => handler(channel, op, Body())); }
        clock.Advance(TimeSpan.FromMinutes(1));
        var folder = Path.Combine(root, AgentAuditLog.FolderName);
        var held = Path.Combine(root, "held-audit");
        Directory.Move(folder, held);
        File.WriteAllText(folder, "blocked");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => handler(channel, op, Body()));
        Assert.Contains("auditUnwritable", failure.Message);
        File.Delete(folder);
        Directory.Move(held, folder);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler(channel, op, Body()));
        var lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(folder, "*.jsonl"))).Skip(1).ToArray();
        Assert.Equal(61, lines.Length);
        Assert.Equal("2 additional launch or stop attempts were refused", JsonSerializer.Deserialize<JsonElement>(lines[^1]).GetProperty("reason").GetString());
        Assert.Equal(0, actions.Calls);
    }

    sealed class Actions : ISessionLauncher, ISessionStopper
    {
        public int Calls;
        public bool Available => true;
        public Task<LaunchOptions> OptionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LaunchResult> LaunchAsync(LaunchRequest request, string peer, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new LaunchResult(LaunchOutcome.Started)); }
        public Task<StopResult> StopAsync(StopRequest request, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new StopResult(StopOutcome.Ended)); }
    }

    sealed class Chan : IProtocolChannel
    {
        public string PeerId => "fixture";
        public string PeerName => "fixture";
        public string PeerKind => "fixture";
        public bool IsOpen => true;
        public Task Closed => Task.CompletedTask;
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }
}
