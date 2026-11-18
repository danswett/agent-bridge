using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
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
    Func<IProtocolChannel, string, JsonElement, Task<object?>> Handler(AgentAuditLog? audit, SessionService? sessions = null) =>
        LocalApi.Handler(sessions!, null!, new FixedCallerGuard(new(CallerKind.App, "fixture")),
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

    [Theory]
    [InlineData(true, true, false, "delivered", "")]
    [InlineData(false, true, false, "refused", "noSubscribedClient")]
    [InlineData(true, false, false, "refused", "auditUnwritable")]
    [InlineData(true, true, true, "refused", "noSubscribedClient")]
    public async Task Owner_pages_need_an_opted_in_app_and_a_writable_intent_audit(bool optIn, bool audited, bool credentialed, string outcome, string reason)
    {
        Grant();
        await using var source = new SessionSource(fixture.Options(), fixture.Processes, fixture.Time);
        var sessions = new SessionService(source, new MachineInfo("machine1", "Test", "windows", "0.1.0")) { Time = fixture.Time };
        var route = Handler(audited ? new AgentAuditLog(fixture.Root, fixture.Time) : null, sessions);
        await using var server = LocalServer.Start(LocalEndpoint.For(fixture.Root), async (ch, op, data) =>
        {
            var result = await route(ch, op, data);
            if (credentialed && op == Ops.SessionsSubscribe) { credentials.Accept(ch, credentials.Of(channel)!); }
            return result;
        });
        await using var app = await LocalClient.ConnectAsync(LocalEndpoint.For(fixture.Root), TimeSpan.FromSeconds(2));
        await app.CallAsync(Ops.SessionsSubscribe, new { ownerPages = optIn });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.Equal(Events.SessionsSynced, (await app.Events.ReadAsync(timeout.Token)).Name);
        var result = JsonSerializer.SerializeToElement(await route(channel, "notify-owner", Body()), ProtocolJson.Options);
        var ack = result.GetProperty("clients")[0];
        Assert.Equal((outcome, reason), (ack.GetProperty("outcome").GetString(), ack.GetProperty("reason").GetString()));
        if (outcome == "delivered")
        {
            var page = await app.Events.ReadAsync(timeout.Token);
            Assert.Equal(("notify-owner", "private page"), (page.Name, page.Data.GetProperty("body").GetString()));
            var audit = string.Join("\n", Directory.GetFiles(Path.Combine(fixture.Root, "audit"), "*.jsonl").SelectMany(File.ReadAllLines));
            Assert.Contains("attempted", audit);
            Assert.DoesNotContain("private page", audit);
        }
        else { Assert.False(app.Events.TryRead(out _)); }
    }

    [Theory]
    [InlineData("send")]
    [InlineData("script")]
    [InlineData("person")]
    [InlineData("activeBefore")]
    [InlineData("activeAfter")]
    [InlineData("pinningOff")]
    [InlineData("noSubscriber")]
    public async Task Owner_push_is_encrypted_and_rechecks_authority_pins_and_active_device_policy(string change)
    {
        Grant();
        using var host = P256KeyPair.Generate();
        using var person = P256KeyPair.Generate();
        using var browser = P256KeyPair.Generate();
        var pins = new TrustedPeerStore(fixture.Root, host.Public, () => "1", fixture.Time);
        pins.Reset();
        pins.Put(person.Public, TrustedPeerStore.Person, fixture.Time.GetUtcNow().AddHours(1));
        using var trust = new RemoteAuthorization(change != "pinningOff", pins, fixture.Time);
        await using var source = new SessionSource(fixture.Options(), fixture.Processes, fixture.Time);
        var store = PushStore.Load(null, "1");
        await using var push = new PushNotifier(source, new MachineInfo("machine1", "Test", "windows", "0.1.0"), store, time: fixture.Time, trust: trust);
        var sender = new Sender();
        push.Attach(sender);
        var auth = new byte[16];
        push.Handle(new PushPeer(Wire.DeviceId(person.Public), "Person", "1", true, person.Public), Ops.PushSubscribe,
            JsonSerializer.SerializeToElement(new { endpoint = "https://web.push.apple.com/fake", keys = new { p256dh = B64Url.Encode(browser.Public), auth = B64Url.Encode(auth) }, app = "https://example.invalid/" }));
        var sessions = new SessionService(source, new MachineInfo("machine1", "Test", "windows", "0.1.0"), push: push) { Time = fixture.Time };
        var route = Handler(new AgentAuditLog(fixture.Root, fixture.Time), sessions);
        await using var server = LocalServer.Start(LocalEndpoint.For(fixture.Root), route);
        await using var app = await LocalClient.ConnectAsync(LocalEndpoint.For(fixture.Root), TimeSpan.FromSeconds(2));
        await app.CallAsync(Ops.SessionsSubscribe, new { });
        if (change == "activeBefore") { await app.CallAsync(Ops.SessionsFocus, new { visible = true, idleMs = 0 }); }
        if (change == "noSubscriber") { store.Remove(Wire.DeviceId(person.Public)); }
        var result = JsonSerializer.SerializeToElement(await route(channel, "notify-owner", Body()), ProtocolJson.Options);
        if (change is "activeBefore" or "pinningOff" or "noSubscriber")
        {
            var refused = result.GetProperty("clients")[1];
            var reason = change == "activeBefore" ? "policySuppressed" : change == "pinningOff" ? "deliveryUnavailable" : "noSubscribedClient";
            Assert.Equal(("refused", reason), (refused.GetProperty("outcome").GetString(), refused.GetProperty("reason").GetString()));
            await push.PumpAsync();
            Assert.Empty(sender.Bodies);
            return;
        }
        Assert.Equal("queued", result.GetProperty("clients")[1].GetProperty("outcome").GetString());
        if (change == "script") { credentials.RevokeScript("desk"); }
        if (change == "person") { pins.Revoke(person.Public); }
        if (change == "activeAfter") { await app.CallAsync(Ops.SessionsFocus, new { visible = true, idleMs = 0 }); }
        await push.PumpAsync();
        if (change != "send") { Assert.Empty(sender.Bodies); return; }
        var encrypted = Assert.Single(sender.Bodies);
        Assert.DoesNotContain("private page", Encoding.UTF8.GetString(encrypted));
        var notice = JsonDocument.Parse(WebPushCrypto.Decrypt(encrypted, browser, auth)).RootElement.GetProperty("notification");
        Assert.Equal(("Attention", "private page", "needsYou"), (notice.GetProperty("title").GetString(), notice.GetProperty("body").GetString(), notice.GetProperty("data").GetProperty("class").GetString()));
        Assert.False(notice.GetProperty("data").TryGetProperty("question", out _));
        Assert.False(notice.GetProperty("silent").GetBoolean());
        await route(channel, "notify-owner", Body());
        await push.PumpAsync();
        Assert.Equal(2, sender.Bodies.Count);
        var replacement = JsonDocument.Parse(WebPushCrypto.Decrypt(sender.Bodies[1], browser, auth)).RootElement.GetProperty("notification");
        Assert.True(replacement.GetProperty("silent").GetBoolean());
        Assert.Equal(notice.GetProperty("tag").GetString(), replacement.GetProperty("tag").GetString());
    }

    sealed class Sender : IPushSender
    {
        public string AccountId => "1";
        public bool Connected => true;
        public List<byte[]> Bodies { get; } = [];
        public Task<PushResult> PushAsync(string endpoint, byte[] body, int ttl, string urgency, string? topic)
        { Bodies.Add(body); return Task.FromResult(new PushResult(201, null)); }
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
