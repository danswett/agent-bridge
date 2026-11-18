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
            new FixedCallerGuard(new(CallerKind.HostCli, "fixture")), credentials, new AgentAuditLog(fixture.Root)), ProtocolJson.Options);
        var bytes = File.ReadAllBytes(result.GetProperty("path").GetString()!);
        var clear = OperatingSystem.IsWindows() ? ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser) : bytes;
        try
        {
            var accepted = credentials.Check(Encoding.ASCII.GetString(clear),
                new ProcessAncestry([new PeerProcess(100, fixture.Time.GetUtcNow(), "scheduler.exe")], true)).Accepted!;
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
