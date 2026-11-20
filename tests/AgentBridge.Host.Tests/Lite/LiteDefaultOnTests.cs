using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.AgentEnvelope;
using AgentBridge.Host.Lite;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

namespace AgentBridge.Host.Tests;

// The owner's Nov 19 decision: own-agents messaging is on for a signed-in host with no agentMessaging.remote key,
// through the real kill switch, sender and receiver, and nothing else about them changes.
public sealed class LiteDefaultOnTests : IDisposable
{
    const string Account = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Session = "11111111-1111-1111-1111-111111111111";
    static readonly byte[] AStatic = [.. Enumerable.Range(1, 32).Select(i => (byte)i)], BStatic = [.. Enumerable.Range(2, 32).Select(i => (byte)i)];
    static readonly string A = Wire.DeviceId(AStatic), B = Wire.DeviceId(BStatic);
    static readonly AgentCredential Caller = new("cred-1", "agent-a1", "launch", 1, DateTimeOffset.UnixEpoch, "s", 0, [], DateTimeOffset.UnixEpoch, DateTimeOffset.MaxValue);
    readonly string root = Path.Combine(Path.GetTempPath(), "abdefault-" + Guid.NewGuid().ToString("N")[..8]);
    readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public LiteDefaultOnTests() { Directory.CreateDirectory(Path.Combine(root, "a")); Directory.CreateDirectory(Path.Combine(root, "b")); }
    public void Dispose() { key.Dispose(); try { Directory.Delete(root, true); } catch (IOException) { } }

    string Folder(string name) => Path.Combine(root, name);

    sealed class Deliver : ILiteDeliver
    {
        public int Calls;
        public Task<LiteDelivery> DeliverAsync(VerifiedEnvelope envelope, string text, CancellationToken ct) { Calls++; return Task.FromResult(new LiteDelivery(true, null)); }
    }

    sealed class Link(Func<object, CancellationToken, Task<LiteAck>> reply) : ILiteLink
    {
        public int Opened;
        public LiteAck? Last;
        public string Self => A;
        public string Account { get; set; } = LiteDefaultOnTests.Account;
        public IReadOnlyList<PresenceEntry> Presence => [new PresenceEntry(B, "node-b", "host", DateTimeOffset.UnixEpoch)];
        public async Task<LiteAck> SendAsync(string peerId, string account, object request, CancellationToken ct) { Opened++; return Last = await reply(request, ct); }
    }

    // A refusal made before the envelope is read names no request, so the sender cannot match it; what b said is the ack.
    static async Task<(string, string?)> RefusedEarly(AgentLiteSender sender, Link link)
    {
        Assert.Equal((LiteKind.Unconfirmed, AgentLiteSender.BadAck), Kinded(await Send(sender)));
        return (link.Last!.Status, link.Last.Code);
    }

    // Host a sends to host b over a fake relay, each with its own real kill switch read from its own profile folder.
    (AgentLiteSender Sender, Link Link, Deliver Deliver) Pair(string receiverAccount = Account)
    {
        var deliver = new Deliver();
        var receiver = new LiteReceiver(new LiteKill(Folder("b")), deliver);
        var channel = new LiteChannel(A, "host", AStatic, B, receiverAccount, true);
        var link = new Link((req, ct) => receiver.ReceiveAsync(channel, JsonSerializer.SerializeToElement(req), ct));
        var q = key.ExportParameters(false).Q;
        var sender = new AgentLiteSender(new LiteKill(Folder("a")), link, _ => true, () => (new EcdsaEnvelopeSigner(key), [0x04, .. q.X!, .. q.Y!]), AStatic);
        return (sender, link, deliver);
    }

    static Task<LiteOutcome> Send(AgentLiteSender sender, string text = "hello") => sender.SendAsync(Caller, new LiteSendInput("node-b", "agent-b2", text), default);

    static (LiteKind, string?) Kinded(LiteOutcome o) => (o.Kind, o.Code);

    static int Config(string folder, params string[] args) => ConfigCommands.Run(["config", .. args], folder, TextWriter.Null, TextWriter.Null);

    static LiteChannel Trusted => new(B, "host", BStatic, A, Account, true);

    [Fact]
    public async Task Two_signed_in_hosts_with_no_setting_send_and_receive_and_the_cap_still_holds()
    {
        Assert.False(File.Exists(Path.Combine(Folder("a"), HostSettings.FileName)));
        var (sender, _, deliver) = Pair();
        Assert.Equal(LiteKind.Delivered, (await Send(sender)).Kind);
        Assert.Equal(1, deliver.Calls);
        Assert.Equal((LiteKind.Refused, LiteReceiver.Malformed), Kinded(await Send(sender, new string('x', 60 * 1024))));
        Assert.Equal(1, deliver.Calls);
    }

    [Fact]
    public async Task An_explicit_off_stays_off_on_either_side()
    {
        Assert.Equal(0, Config(Folder("b"), "set", "agentMessaging.remote", "off"));
        var (sender, link, deliver) = Pair();
        Assert.Equal(("refused", LiteReceiver.Off), await RefusedEarly(sender, link));
        Assert.Equal(0, Config(Folder("a"), "set", "agentMessaging.remote", "off"));
        var opened = link.Opened;
        Assert.Equal(LiteKind.Off, (await Send(sender)).Kind);
        Assert.Equal(opened, link.Opened);
        Assert.Equal(0, deliver.Calls);
    }

    [Fact]
    public async Task A_killed_machine_stays_off_across_a_restart_and_the_default_until_a_person_sets_own_account()
    {
        Assert.Equal(0, await AgentCommands.RunAsync(["agents", "lite", "kill"], Folder("a"), () => false, TextWriter.Null, TextWriter.Null));
        Assert.Contains("\"off\"", File.ReadAllText(Path.Combine(Folder("a"), HostSettings.FileName)));
        var (sender, link, _) = Pair();
        Assert.Equal(LiteKind.Off, (await Send(sender)).Kind);
        // Removing the key after a kill does not bring the default back: only an explicit own-account does.
        Assert.Equal(0, Config(Folder("a"), "unset", "agentMessaging.remote"));
        Assert.False(new LiteKill(Folder("a")).On);
        Assert.Equal(LiteKind.Off, (await Send(sender)).Kind);
        Assert.Equal(0, link.Opened);
        Assert.Equal(0, Config(Folder("a"), "set", "agentMessaging.remote", "own-account"));
        Assert.True(new LiteKill(Folder("a")).On);
    }

    [Fact]
    public void A_newer_kill_from_a_present_peer_turns_a_default_on_machine_off_for_good()
    {
        var b = new LiteKill(Folder("b"));
        Assert.True(b.On);
        Assert.Equal(true, ((dynamic)b.HandleKill(Trusted, JsonDocument.Parse("{\"epoch\":5}").RootElement)).applied);
        Assert.False(new LiteKill(Folder("b")).On);
        Assert.False(HostSettings.CurrentAgentMessaging(Folder("b")).RemoteEnabled);
        b.HandleKill(Trusted, JsonDocument.Parse("{\"epoch\":0}").RootElement);
        Assert.False(new LiteKill(Folder("b")).On);
    }

    [Theory]
    [InlineData("""{ "agentMessaging": "own-account" }""", "agentMessaging is not an object")]
    [InlineData("""{ "agentMessaging": { "remote": "on" } }""", "agentMessaging.remote")]
    [InlineData("""{ "agentMessaging": { "allowAllTargets": "yes" } }""", "agentMessaging.allowAllTargets")]
    [InlineData("""{ "agentMessaging": { "allowAllTargetSessions": "*" } }""", "agentMessaging.allowAllTargetSessions")]
    [InlineData("{ not json", "is not valid")]
    [InlineData("""{ "version": 99 }""", "newer")]
    public async Task A_malformed_section_an_unknown_value_or_an_unreadable_file_keeps_the_mode_off(string text, string said)
    {
        File.WriteAllText(Path.Combine(Folder("b"), HostSettings.FileName), text);
        var (settings, problem) = HostSettings.Load(Folder("b"));
        Assert.Contains(said, string.Join("; ", settings.SectionProblems.Append(problem ?? "")));
        Assert.False(new LiteKill(Folder("b")).On);
        var (sender, link, deliver) = Pair();
        Assert.Equal(("refused", LiteReceiver.Off), await RefusedEarly(sender, link));
        Assert.Equal(0, deliver.Calls);
    }

    [Fact]
    public async Task A_config_json_that_cannot_be_opened_keeps_the_mode_off()
    {
        Directory.CreateDirectory(Path.Combine(Folder("a"), HostSettings.FileName));
        var (sender, link, _) = Pair();
        Assert.Equal(LiteKind.Off, (await Send(sender)).Kind);
        Assert.Equal(0, link.Opened);
    }

    [Fact]
    public async Task A_host_with_no_signed_in_account_neither_sends_nor_receives_nor_takes_a_kill()
    {
        var (sender, link, _) = Pair();
        link.Account = "";
        Assert.Equal(LiteKind.Undeliverable, (await Send(sender)).Kind);
        Assert.Equal(0, link.Opened);

        var (unsigned, unsignedLink, deliver) = Pair(receiverAccount: "");
        Assert.Equal(("refused", LiteReceiver.NotSameAccount), await RefusedEarly(unsigned, unsignedLink));
        Assert.Equal(0, deliver.Calls);

        var b = new LiteKill(Folder("b"));
        Assert.Equal(false, ((dynamic)b.HandleKill(Trusted with { Account = "" }, JsonDocument.Parse("{\"epoch\":5}").RootElement)).applied);
        Assert.False(b.Observe(Trusted with { Account = "" }, 5));
        Assert.True(b.On);
    }

    [Fact]
    public async Task The_default_never_lets_own_agents_into_an_allow_all_session()
    {
        File.WriteAllText(Path.Combine(Folder("b"), HostSettings.FileName), $$"""{ "agentMessaging": { "allowAllTargets": true, "allowAllTargetSessions": ["{{Session}}"] } }""");
        var launches = new LiteLaunchLog();
        launches.Launched(Session, new LaunchedWith { Agent = "copilot", Permissions = LaunchProtocol.AllowAll });
        var gate = new AllowlistedAllTargetsGate(launches, () => HostSettings.CurrentAgentMessaging(Folder("b")));
        Assert.True(new LiteKill(Folder("b")).On);
        Assert.False((await gate.CheckAsync(null!, Session)).Allowed);
        Assert.Equal(0, Config(Folder("b"), "set", "agentMessaging.remote", "own-account"));
        Assert.True((await gate.CheckAsync(null!, Session)).Allowed);
    }
}
