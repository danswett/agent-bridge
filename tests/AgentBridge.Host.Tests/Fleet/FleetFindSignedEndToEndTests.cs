using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Host.Fleet;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Relay;
using AgentBridge.Roster;
using AgentBridge.Transport;
using Xunit;

public sealed class FleetFindSignedEndToEndTests : IDisposable
{
    const string Name = "SG Quality";
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    readonly string root = Path.Combine(Path.GetTempPath(), "abfind-signed-" + Guid.NewGuid().ToString("N"));
    readonly AgentCredentials credentials;
    readonly AgentAuditLog audit;
    readonly World world = new();
    readonly RelayFleetRoster fleetReader;
    readonly SnapshotRoster roster;
    readonly string machine;
    readonly DateTimeOffset now;
    readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider time;

    public FleetFindSignedEndToEndTests()
    {
        now = DateTimeOffset.UtcNow;
        time = new(now);
        PrivateFolder.Ensure(root);
        credentials = new AgentCredentials(root, "machine-b");
        audit = new AgentAuditLog(root);
        world.Genesis();
        world.AddHostV2("host-add", "G", "A", "G");
        machine = world.Id("A");

        var pin = Path.Combine(root, "roster-pin.json");
        using (var stream = PrivateFolder.CreateNew(pin))
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                account = RosterFixture.Account,
                generation = world.GenerationId,
                genesis = Codec.Base64Url(world.Entry("G").Hash),
                heads = new[] { Codec.Base64Url(world.Entry("G").Hash) },
            }));
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        var reader = new HostRosterReader(pin, () => world.Now);
        var verifier = new RosterVerifier(RosterFixture.Account, world.GenerationId, () => world.Now) { AcceptHostSign = true };
        Assert.Equal(Verdict.Accepted, Assert.Single(verifier.Offer(world.Entry("G"))).Verdict);
        typeof(HostRosterReader).GetProperty(nameof(HostRosterReader.Verified))!.SetValue(reader, verifier);
        Assert.Equal("verified", reader.Update(new RelayRoster("observe", world.GenerationId,
            world.Labels.Select(label => world.Entry(label).Canonical()).ToArray()), RosterFixture.Account).Outcome);
        world.Now = RosterVerifier.Probation + TimeSpan.FromSeconds(1);
        Assert.True(reader.Verified!.Serving.ContainsKey(machine));

        var trust = new FleetTrust(reader, root, () => RosterFixture.Account);
        Assert.Same(reader.Verified, trust.VerifiedRoster());
        Assert.Equal(World.Keys("A").Sign.Public, trust.VerifiedRoster()!.State.Device(machine)!.HostSign);
        fleetReader = new RelayFleetRoster(trust.VerifiedRoster, time);
        roster = new SnapshotRoster(fleetReader, RosterFixture.Account);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Only_a_fresh_record_signed_by_the_rostered_host_is_found()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var registry = new SessionRegistry(time);
        var current = Record(2, World.Keys("A").Sign);
        Assert.Null(registry.Put(RosterFixture.Account, machine, current));
        roster.Frames = registry.Read(RosterFixture.Account).Select(item => Frame(item.Machine, item.Fresh,
            JsonSerializer.Deserialize<JsonElement>(item.Json)!)).ToArray();

        var found = await FindAsync();
        Assert.Equal("found", found.GetProperty("result").GetString());
        Assert.Equal(machine, found.GetProperty("session").GetProperty("machine").GetString());
        Assert.Equal("agent_bridge_" + new string('a', 16), found.GetProperty("session").GetProperty("node").GetString());

        var altered = JsonNode.Parse(current.GetRawText())!.AsObject();
        altered["sessions"]![0]!["state"] = "idle";
        roster.Frames = [Frame(machine, true, JsonSerializer.SerializeToElement(altered))];
        AssertNoFound(await FindAsync());

        roster.Frames = [Frame(machine, true, Record(3, World.Keys("wrong signer").Sign))];
        AssertNoFound(await FindAsync());

        roster.Frames = [Frame(machine, true, Record(1, World.Keys("A").Sign))];
        AssertNoFound(await FindAsync());

        roster.Frames = [Frame(machine, true, Record(3, World.Keys("A").Sign, now.AddSeconds(-61)))];
        AssertNoFound(await FindAsync());
    }

    JsonElement Record(long sequence, TestKey signer, DateTimeOffset? at = null) => FleetRecordSigning.Sign(JsonSerializer.SerializeToElement(new
    {
        v = 1,
        machine,
        epoch = new string('b', 16),
        seq = sequence,
        at = at ?? now,
        truncated = false,
        sessions = new[] { new { id = new string('a', 16), node = "agent_bridge_" + new string('a', 16), name = Name, state = "working", since = now.AddSeconds(-5) } },
    }), signer.Sign);

    static JsonElement Frame(string id, bool online, JsonElement record) =>
        JsonSerializer.SerializeToElement(new { machine = id, online, record });

    async Task<JsonElement> FindAsync()
    {
        var channel = new Chan();
        var session = Guid.NewGuid().ToString("N");
        var token = credentials.Mint(session, "launch-" + session, 100, now, AgentProtocol.ScopeRead);
        credentials.Accept(channel, credentials.Check(token,
            new ProcessAncestry([new PeerProcess(100, now, "agent.exe")], true)).Accepted!);
        return JsonSerializer.SerializeToElement(
            await new FleetList(credentials, roster, audit: audit).FindAsync(channel, JsonSerializer.SerializeToElement(new { name = Name })), Web);
    }

    static void AssertNoFound(JsonElement result)
    {
        Assert.NotEqual("found", result.GetProperty("result").GetString());
        Assert.False(result.TryGetProperty("session", out _));
    }

    sealed class SnapshotRoster(RelayFleetRoster reader, string account) : IScopedFleetRoster
    {
        public IReadOnlyList<JsonElement> Frames { get; set; } = [];
        public Task<IReadOnlyList<FleetMachine>> ReadAsync(CancellationToken ct) => Task.FromResult(reader.Read(Frames));
        public Task<FleetRosterSnapshot> ReadScopedAsync(CancellationToken ct) =>
            Task.FromResult(new FleetRosterSnapshot(account, reader.Read(Frames)));
    }

    sealed class Chan : IProtocolChannel
    {
        public string PeerId => "fixture";
        public string PeerName => "fixture";
        public string PeerKind => "fixture";
        public bool IsOpen => true;
        public Task Closed => Task.Delay(Timeout.Infinite);
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }
}
