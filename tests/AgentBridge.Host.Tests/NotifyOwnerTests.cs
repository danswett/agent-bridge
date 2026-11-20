using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed class NotifyOwnerTests : IDisposable
{
    readonly Fixture fixture = new();
    readonly AgentCredentials credentials;
    readonly Channel channel = new();
    readonly Func<IProtocolChannel, string, JsonElement, Task<object?>> handle;
    static readonly Guid Request = Guid.Parse("88888888-8888-4888-8888-888888888888");
    static JsonElement Body(string audience = "owner", string target = "policy", string body = "private page") =>
        JsonSerializer.SerializeToElement(new { request = Request, audience, target, title = "Attention", body });

    public NotifyOwnerTests()
    {
        credentials = new(fixture.Root, "machine1", fixture.Time);
        handle = Handler(new AgentAuditLog(fixture.Root, fixture.Time));
    }

    public void Dispose() => fixture.Dispose();
    Func<IProtocolChannel, string, JsonElement, Task<object?>> Handler(AgentAuditLog? audit) =>
        LocalApi.Handler(null!, null!, new FixedCallerGuard(new(CallerKind.HostCli, "fixture")),
            credentials: credentials, audit: audit, auditTime: fixture.Time);

    void Grant(bool enabled = true)
    {
        var result = JsonSerializer.SerializeToElement(AgentBridge.Host.Cli.ScriptCredentialCommands.Handle(
            new Channel(), "scripts.add", JsonSerializer.SerializeToElement(new { name = "desk", notifyOwner = enabled }),
            new FixedCallerGuard(new(CallerKind.HostCli, "fixture")), credentials, new AgentAuditLog(fixture.Root, fixture.Time)), ProtocolJson.Options);
        var bytes = File.ReadAllBytes(result.GetProperty("path").GetString()!);
        var clear = OperatingSystem.IsWindows() ? ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser) : bytes;
        try
        {
            var token = Encoding.ASCII.GetString(clear);
            var ancestry = new ProcessAncestry([new PeerProcess(100, fixture.Time.GetUtcNow(), "scheduler.exe")], true);
            var accepted = credentials.Check(token, ancestry).Accepted!;
            Assert.Equal(enabled, JsonSerializer.SerializeToElement(new AgentCredentials(fixture.Root, "machine1", fixture.Time)
                .Check(token, ancestry).Accepted, ProtocolJson.Options).GetProperty("notifyOwner").GetBoolean());
            credentials.Accept(channel, accepted);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    static void Refused(object? result, string reason)
    {
        var json = JsonSerializer.SerializeToElement(result, ProtocolJson.Options);
        var clients = json.GetProperty("clients").EnumerateArray().ToArray();
        Assert.Equal(["app", "pwa"], clients.Select(c => c.GetProperty("client").GetString()));
        Assert.All(clients, c =>
        {
            Assert.Equal("refused", c.GetProperty("outcome").GetString());
            Assert.Equal(reason, c.GetProperty("reason").GetString());
        });
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("read-only")]
    [InlineData("launch:fleet")]
    [InlineData("session")]
    public async Task Paging_does_not_inherit_person_read_reply_or_launch_authority(string caller)
    {
        if (caller is "read-only" or "launch:fleet")
        {
            Grant(false);
            if (caller == "launch:fleet")
            {
                File.WriteAllText(Path.Combine(fixture.Root, HostSettings.FileName), """{"agents":{"enabled":true}}""");
                credentials.SetFleetLaunch("desk", true);
            }
        }
        if (caller == "session")
        {
            var at = fixture.Time.GetUtcNow();
            var token = credentials.Mint("session1", "launch1", 100, at, AgentProtocol.ScopeReadReply);
            credentials.Accept(channel, credentials.Check(token, new ProcessAncestry([new PeerProcess(100, at, "agent.exe")], true)).Accepted!);
        }
        Refused(await handle(channel, "notify-owner", Body()), "callerNotAuthorized");
    }

    [Fact]
    public async Task Explicit_paging_authority_is_durable_revocable_and_never_claims_unwired_delivery()
    {
        Grant();
        Assert.Equal(Request, JsonSerializer.SerializeToElement(await handle(channel, "notify-owner", Body()), ProtocolJson.Options)
            .GetProperty("request").GetGuid());
        Refused(await handle(channel, "notify-owner", Body()), "deliveryUnavailable");
        credentials.RevokeScript("desk");
        Refused(await handle(channel, "notify-owner", Body()), "callerNotAuthorized");
        var audit = string.Join("\n", Directory.GetFiles(Path.Combine(fixture.Root, "audit"), "*.jsonl").SelectMany(File.ReadAllLines));
        Assert.Contains("notify-owner", audit);
        Assert.DoesNotContain("private page", audit);
        Assert.DoesNotContain("Attention", audit);
    }

    [Theory]
    [InlineData("other", "policy", "page", "audienceRefused")]
    [InlineData("owner", "someone@example.invalid", "page", "targetRefused")]
    [InlineData("owner", "policy", "", "badRequest")]
    [InlineData("owner", "policy", "\u001b[31m", "badRequest")]
    public async Task Audience_target_and_malformed_text_are_refused_explicitly(string audience, string target, string body, string reason)
    {
        Grant();
        Refused(await handle(channel, "notify-owner", Body(audience, target, body)), reason);
    }

    [Theory]
    [InlineData("""{"request":"88888888-8888-4888-8888-888888888888","audience":"owner","audience":"owner"}""")]
    [InlineData("""{"owner":"arbitrary recipient"}""")]
    [InlineData("""{"title":"\uD800"}""")]
    public async Task Duplicate_unknown_and_invalid_unicode_fields_cannot_broaden_the_request(string json)
    {
        Grant();
        Refused(await handle(channel, "notify-owner", JsonDocument.Parse(json).RootElement), "badRequest");
    }

    [Fact]
    public async Task Oversize_rate_limit_expiry_and_unwritable_audit_fail_closed()
    {
        Grant();
        Refused(await handle(channel, "notify-owner", Body(body: new string('x', 4097))), "payloadTooLarge");
        for (var i = 0; i < 6; i++) { Refused(await handle(channel, "notify-owner", Body()), "deliveryUnavailable"); }
        Refused(await handle(channel, "notify-owner", Body()), "rateLimited");
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        Refused(await Handler(null)(channel, "notify-owner", Body()), "auditUnwritable");
        fixture.Time.Advance(TimeSpan.FromDays(31));
        Refused(await handle(channel, "notify-owner", Body()), "callerNotAuthorized");
    }

    [Theory]
    [InlineData("\"delivered\"", true, "\"delivered\"")]
    [InlineData("\"queued\"", true, "\"queued\"")]
    [InlineData("\"refused\"", true, "\"refused\"")]
    [InlineData("\"future\"", true, "\"refused\"")]
    [InlineData("99", true, "\"refused\"")]
    [InlineData("null", true, "\"refused\"")]
    [InlineData("\"app\"", false, "\"app\"")]
    [InlineData("\"pwa\"", false, "\"pwa\"")]
    [InlineData("\"future\"", false, "\"unknown\"")]
    [InlineData("99", false, "\"unknown\"")]
    [InlineData("null", false, "\"unknown\"")]
    public void Wire_ack_enums_roundtrip_without_inventing_delivery(string json, bool outcome, string expected)
    {
        var type = outcome ? typeof(OwnerNotifyOutcome) : typeof(OwnerClientKind);
        Assert.Equal(expected, JsonSerializer.Serialize(JsonSerializer.Deserialize(json, type, ProtocolJson.Options), type, ProtocolJson.Options));
    }

    [Fact]
    public void Absent_ack_fields_are_not_evidence_of_delivery()
    {
        var ack = JsonSerializer.Deserialize<OwnerClientAck>("{}", ProtocolJson.Options)!;
        Assert.False(ack.Client is OwnerClientKind.App or OwnerClientKind.Pwa);
        Assert.Equal(OwnerNotifyOutcome.Refused, ack.Outcome);
    }

    [Theory]
    [InlineData(OwnerNotifyOutcome.Delivered, 0, 2, OwnerClientKind.App)]
    [InlineData(OwnerNotifyOutcome.Queued, 0, 2, OwnerClientKind.App)]
    [InlineData(OwnerNotifyOutcome.Refused, 1, 2, OwnerClientKind.App)]
    [InlineData((OwnerNotifyOutcome)99, 1, 2, OwnerClientKind.App)]
    [InlineData(OwnerNotifyOutcome.Delivered, 1, 1, OwnerClientKind.App)]
    [InlineData(OwnerNotifyOutcome.Delivered, 1, 2, (OwnerClientKind)99)]
    public async Task The_cli_requires_acknowledgment_for_both_client_kinds(OwnerNotifyOutcome outcome, int exit, int count, OwnerClientKind client)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var pwa = new OwnerClientAck(OwnerClientKind.Pwa,
            outcome == OwnerNotifyOutcome.Refused ? OwnerNotifyOutcome.Refused : OwnerNotifyOutcome.Queued, "pwa detail");
        await using var server = LocalServer.Start(LocalEndpoint.For(fixture.Root), (_, _, data) => Task.FromResult<object?>(
            new NotifyOwnerResult(data.GetProperty("request").GetGuid(), new OwnerClientAck[] { new(client, outcome, "app detail"),
                pwa }.Take(count).ToArray())));
        await using var local = await LocalClient.ConnectAsync(LocalEndpoint.For(fixture.Root));
        Assert.Equal(exit, await NotifyOwnerCommands.RunAsync(["notify-owner", "Attention", "private page"], fixture.Root, output, error,
            request => local.CallAsync<NotifyOwnerResult>(NotifyOwnerProtocol.Op, request)));
        var clients = JsonDocument.Parse(output.ToString()).RootElement.GetProperty("clients").EnumerateArray().ToArray();
        Assert.Equal(count, clients.Length);
        Assert.Equal(client == OwnerClientKind.App ? "app" : "unknown", clients[0].GetProperty("client").GetString());
        Assert.Equal(outcome is OwnerNotifyOutcome.Delivered or OwnerNotifyOutcome.Queued ? outcome.ToString().ToLowerInvariant() : "refused",
            clients[0].GetProperty("outcome").GetString());
        Assert.Equal("app detail", clients[0].GetProperty("reason").GetString());
        if (count == 2) { Assert.Equal(JsonSerializer.Serialize(pwa, ProtocolJson.Options), clients[1].GetRawText()); }
        Assert.DoesNotContain("private page", output.ToString() + error);
    }

    [Fact]
    public async Task An_invalid_cli_profile_is_refused_before_any_connection()
    {
        Assert.Equal(2, await NotifyOwnerCommands.RunAsync(["notify-owner", "title", "body", "--profile", ".."],
            fixture.Root, new StringWriter(), new StringWriter(), _ => throw new InvalidOperationException("must not connect")));
    }

    sealed class Channel : IProtocolChannel
    {
        public string PeerId => "fixture";
        public string PeerName => "fixture";
        public string PeerKind => "fixture";
        public bool IsOpen => true;
        public Task Closed => Task.Delay(Timeout.Infinite);
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }
}
