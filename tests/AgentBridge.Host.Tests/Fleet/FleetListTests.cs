using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Fleet;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed class FleetListTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "abfleet-" + Guid.NewGuid().ToString("N")[..8]);
    static readonly DateTimeOffset Started = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    readonly AgentCredentials store;
    readonly Roster roster = new();
    readonly List<string> audited = [];

    public FleetListTests()
    {
        Directory.CreateDirectory(root);
        store = new AgentCredentials(root, "machine1");
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    Chan Hello(string scope)
    {
        var chan = new Chan();
        var token = store.Mint("sess-1", "launch-1", 100, Started, scope);
        store.Accept(chan, store.Check(token, new ProcessAncestry([new PeerProcess(100, Started, "agent.exe")], true)).Accepted!);
        return chan;
    }

    FleetList Op() => new(store, roster, new FixedTime(Started.AddSeconds(90)), new AgentAuditLog(root)) { AuditFleetList = audited.Add };

    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Listing_before_agent_hello_is_refused_and_not_audited()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Op().HandleAsync(new Chan(), Body("{}")));
        Assert.Empty(audited);
    }

    [Fact]
    public async Task A_credential_without_the_read_scope_is_refused_with_a_readable_reason()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Op().HandleAsync(Hello(AgentProtocol.ScopeNone), Body("{}")));
        Assert.Contains("needs at least 'read'", e.Message);
        Assert.Empty(audited);
    }

    [Fact]
    public async Task The_reply_carries_only_the_allow_listed_fields_and_no_name_unless_asked()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        roster.Machines = Frames("build-box");
        var chan = Hello(AgentProtocol.ScopeRead);
        var op = Op();
        var plain = JsonSerializer.Serialize(await op.HandleAsync(chan, Body("""{"prompt":"secret prompt","account":"other"}""")), Web);
        Assert.Equal(["machine", "machineName", "machineOnline", "node", "secondsInState", "since", "state"], Names(plain));
        Assert.DoesNotContain("my project", plain);
        Assert.DoesNotContain("secret", plain);
        Assert.Contains("\"secondsInState\":90", plain);
        var named = JsonSerializer.Serialize(await op.HandleAsync(chan, Body("""{"include":"content"}""")), Web);
        Assert.Equal(["machine", "machineName", "machineOnline", "name", "node", "secondsInState", "since", "state"], Names(named));
        Assert.Contains("my project", named);
    }

    static IReadOnlyList<FleetMachine> Frames(object? name, bool includeName = true)
    {
        var frame = new Dictionary<string, object?>
        {
            ["machine"] = "m2", ["online"] = true,
            ["record"] = new { sessions = new[] { new { id = "s1", node = "n1", name = "my project", state = "working", since = Started } } }
        };
        if (includeName) { frame["name"] = name; }
        return RelayFleetRoster.Parse([JsonSerializer.SerializeToElement(frame)]);
    }

    [Theory]
    [InlineData("build-box", "build-box")]
    [InlineData("  build box  ", "build box")]
    [InlineData("build-\u00e9", "build-\u00e9")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("line\nbreak", null)]
    [InlineData(42, null)]
    public void Parsing_preserves_only_valid_machine_names(object? input, string? expected)
    {
        var machine = JsonSerializer.SerializeToElement(Assert.Single(Frames(input)), Web);
        Assert.Equal(expected, machine.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("build\\u202Ebox", null)]
    [InlineData("build\\u200Bbox", null)]
    [InlineData("build\\uD800box", null)]
    [InlineData("build\\uDC00box", null)]
    [InlineData("\\u2028build-box", null)]
    [InlineData("build-box\\u2029", null)]
    [InlineData("build\\uD83D\\uDE80box", "build\U0001F680box")]
    public async Task Parsing_rejects_hidden_or_invalid_Unicode_but_keeps_valid_emoji_pairs(string escaped, string? expected)
    {
        var frame = Body($$$"""
            {"machine":"m2","name":"{{{escaped}}}","online":true,
             "record":{"sessions":[{"id":"s1","node":"n1","name":"my project","state":"working","since":"2026-11-07T12:00:00Z"}]}}
            """);
        roster.Machines = RelayFleetRoster.Parse([frame]);
        var machine = JsonSerializer.SerializeToElement(Assert.Single(roster.Machines), Web);
        Assert.Equal(expected, machine.GetProperty("name").GetString());
        if (!OperatingSystem.IsWindows()) { return; }
        var chan = Hello(AgentProtocol.ScopeRead);
        var listed = JsonSerializer.SerializeToElement(await Op().HandleAsync(chan, Body("{}")), Web).GetProperty("sessions")[0];
        var found = JsonSerializer.SerializeToElement(await Op().FindAsync(chan, Body("""{"name":"my project","machine":"m2"}""")), Web).GetProperty("machines")[0];
        foreach (var projection in new[] { listed, found })
        {
            if (expected is null) { Assert.False(projection.TryGetProperty("machineName", out _)); }
            else { Assert.Equal(expected, projection.GetProperty("machineName").GetString()); }
        }
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void Parsing_accepts_the_machine_name_cap_but_never_truncates_an_overlong_name(int length, bool valid)
    {
        var name = new string('x', length);
        var machine = JsonSerializer.SerializeToElement(Assert.Single(Frames(name)), Web);
        Assert.Equal(valid ? name : null, machine.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("build-box", true, true)]
    [InlineData("build-box", false, false)]
    [InlineData(null, true, false)]
    [InlineData("", true, false)]
    [InlineData("line\nbreak", true, false)]
    [InlineData(42, true, false)]
    public async Task Listing_adds_only_machineName_from_the_registry_and_never_guesses_it(object? input, bool includeName, bool hasName)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        roster.Machines = Frames(input, includeName);
        var reply = JsonSerializer.Serialize(await Op().HandleAsync(Hello(AgentProtocol.ScopeRead), Body("{}")), Web);
        Assert.Equal(hasName
            ? ["machine", "machineName", "machineOnline", "node", "secondsInState", "since", "state"]
            : ["machine", "machineOnline", "node", "secondsInState", "since", "state"], Names(reply));
        if (hasName) { Assert.Contains("\"machineName\":\"build-box\"", reply); }
    }

    [Fact]
    public async Task Finding_carries_the_machine_display_name_without_changing_verification_or_optional_machine_filtering()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        roster.Machines = Frames("build-box");
        var chan = Hello(AgentProtocol.ScopeRead);
        async Task<JsonElement> Find(string? machine) => JsonSerializer.SerializeToElement(
            await Op().FindAsync(chan, Body(JsonSerializer.Serialize(new { name = "my project", machine }))), Web);
        var unsigned = await Find("m2");
        Assert.Equal("unverified", unsigned.GetProperty("result").GetString());
        Assert.Equal("build-box", unsigned.GetProperty("machines")[0].GetProperty("machineName").GetString());
        Assert.DoesNotContain("node", unsigned.GetRawText());
        roster.Machines = roster.Machines.Select(m => m with { Verified = true }).ToList();
        var verified = await Find("m2");
        Assert.Equal("found", verified.GetProperty("result").GetString());
        Assert.Equal("build-box", verified.GetProperty("session").GetProperty("machineName").GetString());
        Assert.Equal("notFound", (await Find("build-box")).GetProperty("result").GetString());
        var named = await Find(null);
        Assert.Equal("found", named.GetProperty("result").GetString());
        Assert.Equal("build-box", named.GetProperty("session").GetProperty("machineName").GetString());
        var machine = roster.Machines[0];
        roster.Machines = [machine, machine with { Id = "m3" }];
        var ambiguous = await Find(null);
        Assert.Equal("ambiguous", ambiguous.GetProperty("result").GetString());
        Assert.All(ambiguous.GetProperty("candidates").EnumerateArray(), c => Assert.Equal("build-box", c.GetProperty("machineName").GetString()));
        roster.Machines = [machine with { Online = false }];
        var offline = await Find("m2");
        Assert.Equal("offline", offline.GetProperty("result").GetString());
        Assert.Equal("build-box", offline.GetProperty("machines")[0].GetProperty("machineName").GetString());
        roster.Machines = Frames(null);
        Assert.False((await Find("m2")).GetProperty("machines")[0].TryGetProperty("machineName", out _));
    }

    [Fact]
    public async Task Duplicate_machine_display_names_do_not_merge_machines_or_change_find_pins_or_match_session_names()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var first = Frames("build-box")[0] with { Verified = true };
        roster.Machines = [first, first with { Id = "m3", Sessions = [new FleetSession("n2", "my project", "working", Started)] }];
        var chan = Hello(AgentProtocol.ScopeRead);
        var listed = JsonSerializer.SerializeToElement(await Op().HandleAsync(chan, Body("{}")), Web).GetProperty("sessions").EnumerateArray().ToList();
        Assert.Equal(["m2", "m3"], listed.Select(s => s.GetProperty("machine").GetString()));
        Assert.All(listed, s => Assert.Equal("build-box", s.GetProperty("machineName").GetString()));
        foreach (var (pin, node) in new[] { ("m2", "n1"), ("m3", "n2") })
        {
            var found = JsonSerializer.SerializeToElement(await Op().FindAsync(chan, Body(JsonSerializer.Serialize(new { name = "my project", machine = pin }))), Web);
            Assert.Equal("found", found.GetProperty("result").GetString());
            Assert.Equal(pin, found.GetProperty("session").GetProperty("machine").GetString());
            Assert.Equal(node, found.GetProperty("session").GetProperty("node").GetString());
        }
        var noMatch = JsonSerializer.SerializeToElement(await Op().FindAsync(chan, Body("""{"name":"build-box","machine":"m2"}""")), Web);
        Assert.Equal("notFound", noMatch.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Each_accepted_call_is_audited_with_the_agents_principal()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await Op().HandleAsync(Hello(AgentProtocol.ScopeRead), Body("{}"));
        Assert.Equal([AgentProtocol.Principal("machine1", "sess-1")], audited);
    }

    [Fact]
    public async Task A_read_reply_credential_is_accepted_and_the_local_api_routes_fleet_list()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var handler = LocalApi.Handler(null!, null!, null!, null, null, store, null, Op());
        var json = JsonSerializer.Serialize(await handler(Hello(AgentProtocol.ScopeReadReply), FleetList.Op, Body("{}")), Web);
        Assert.Contains("\"node\":\"n1\"", json);
        var bare = LocalApi.Handler(null!, null!, null!, null, null, store, null);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => bare(Hello(AgentProtocol.ScopeRead), FleetList.Op, Body("{}")));
        Assert.StartsWith("refused:", e.Message);
    }

    [Fact]
    public void Parsing_skips_a_frame_without_a_machine_and_lists_no_sessions_for_an_offline_machine()
    {
        var frames = new[]
        {
            """{"t":"registry","id":2,"name":"x","online":true,"record":null}""",
            """{"t":"registry","id":2,"machine":"off","online":false,"record":null}""",
            """{"t":"registry","id":2,"machine":"on","online":true,"record":{"sessions":[{"id":"a","node":"n","name":"p","state":"idle","since":"2026-11-07T12:00:00Z"}]}}""",
        }.Select(Body).ToList();
        var parsed = RelayFleetRoster.Parse(frames);
        Assert.Equal(["off", "on"], parsed.Select(m => m.Id));
        Assert.Empty(parsed[0].Sessions);
        Assert.Equal("idle", Assert.Single(parsed[1].Sessions).State);
    }

    [Fact]
    public async Task A_roster_with_no_relay_refuses_at_once()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => new RelayFleetRoster().ReadAsync(default));
        Assert.Contains("isn't connected to the relay", e.Message);
    }

    static IReadOnlyList<FleetMachine> ExpiredFrames(bool knownFirst = false)
    {
        var frames = new[]
        {
            """
            {"machine":"live","name":"build-box","online":true,"lastSeen":"2026-11-07T12:01:00Z",
             "record":{"sessions":[{"id":"s1","node":"n1","name":"my project","state":"working","since":"2026-11-07T12:00:00Z"}]}}
            """,
            """{"machine":"expired","online":false,"lastSeen":"2026-11-07T11:59:00Z","record":null}""",
            """
            {"known":[
              {"machine":"live","name":"old-name","lastSeen":"2026-11-07T11:00:00Z"},
              {"machine":"expired","name":"quiet-box","lastSeen":"2026-11-07T11:58:00Z"},
              {"machine":"gone","name":"offline-box","lastSeen":"2026-11-06T12:00:00Z"}]}
            """,
        }.Select(Body);
        return RelayFleetRoster.Parse(knownFirst ? frames.Reverse() : frames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parsing_keeps_known_machines_without_duplicating_or_overriding_registry_liveness(bool knownFirst)
    {
        var parsed = ExpiredFrames(knownFirst).ToDictionary(m => m.Id);
        Assert.Equal(3, parsed.Count);
        Assert.True(parsed["live"].Online);
        Assert.Equal("build-box", parsed["live"].Name);
        Assert.Single(parsed["live"].Sessions);
        Assert.False(parsed["expired"].Online);
        Assert.Equal("quiet-box", parsed["expired"].Name);
        Assert.Empty(parsed["expired"].Sessions);
        Assert.False(parsed["gone"].Online);
        Assert.Empty(parsed["gone"].Sessions);
        Assert.All(parsed.Values, m => Assert.False(m.Verified));
        var expired = JsonSerializer.SerializeToElement(parsed["expired"], Web);
        Assert.Equal(Started.AddMinutes(-1), expired.GetProperty("lastSeen").GetDateTimeOffset());
        var gone = JsonSerializer.SerializeToElement(parsed["gone"], Web);
        Assert.Equal(Started.AddDays(-1), gone.GetProperty("lastSeen").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("\"not-a-time\"", typeof(FormatException))]
    [InlineData("42", typeof(InvalidOperationException))]
    public void Invalid_last_seen_is_refused_instead_of_inventing_machine_freshness(string value, Type error)
    {
        var frame = Body($$"""{"machine":"expired","online":false,"record":null,"lastSeen":{{value}}}""");
        Assert.Throws(error, () => RelayFleetRoster.Parse([frame]));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"include":"content","account":"other"}""")]
    public async Task Listing_retains_expired_machines_as_offline_with_last_seen_and_leaves_live_sessions_unchanged(string request)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        roster.Machines = ExpiredFrames();
        var reply = JsonSerializer.SerializeToElement(await Op().HandleAsync(Hello(AgentProtocol.ScopeRead), Body(request)), Web);
        var machines = reply.GetProperty("machines").EnumerateArray().ToDictionary(m => m.GetProperty("machine").GetString()!);
        Assert.Equal(3, machines.Count);
        foreach (var id in new[] { "expired", "gone" })
        {
            var machine = machines[id];
            Assert.Equal("offline", machine.GetProperty("state").GetString());
            Assert.Equal(["lastSeen", "machine", "machineName", "state"], machine.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.False(machine.TryGetProperty("node", out _));
        }
        Assert.Equal(Started.AddMinutes(-1), machines["expired"].GetProperty("lastSeen").GetDateTimeOffset());
        Assert.Equal("quiet-box", machines["expired"].GetProperty("machineName").GetString());
        Assert.Equal("online", machines["live"].GetProperty("state").GetString());
        Assert.Equal(Started.AddMinutes(1), machines["live"].GetProperty("lastSeen").GetDateTimeOffset());
        var session = Assert.Single(reply.GetProperty("sessions").EnumerateArray());
        Assert.Equal("live", session.GetProperty("machine").GetString());
        Assert.Equal("working", session.GetProperty("state").GetString());
        Assert.True(session.GetProperty("machineOnline").GetBoolean());
        Assert.Equal(90, session.GetProperty("secondsInState").GetInt64());
        Assert.Equal(request.Contains("content"), session.TryGetProperty("name", out _));
        Assert.DoesNotContain("other", reply.GetRawText());
    }

    [Fact]
    public async Task Listing_a_machine_without_last_seen_never_invents_a_time_or_changes_find_results()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        roster.Machines = Frames("build-box");
        var chan = Hello(AgentProtocol.ScopeRead);
        var op = Op();
        var before = JsonSerializer.Serialize(await op.FindAsync(chan, Body("""{"name":"my project"}""")), Web);
        var reply = JsonSerializer.SerializeToElement(await op.HandleAsync(chan, Body("{}")), Web);
        Assert.False(Assert.Single(reply.GetProperty("machines").EnumerateArray()).TryGetProperty("lastSeen", out _));
        Assert.Equal(before, JsonSerializer.Serialize(await op.FindAsync(chan, Body("""{"name":"my project"}""")), Web));
        roster.Machines = ExpiredFrames();
        var absent = JsonSerializer.SerializeToElement(await op.FindAsync(chan, Body("""{"name":"missing"}""")), Web);
        Assert.Equal("notFound", absent.GetProperty("result").GetString());
        Assert.False(absent.TryGetProperty("machines", out _));
    }

    static string[] Names(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("sessions")[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

    sealed class Roster : IScopedFleetRoster
    {
        public IReadOnlyList<FleetMachine>? Machines { get; set; }
        public Task<IReadOnlyList<FleetMachine>> ReadAsync(CancellationToken ct) =>
            Task.FromResult(Machines ?? [new FleetMachine("m2", true, [new FleetSession("n1", "my project", "working", Started)])]);
        public async Task<FleetRosterSnapshot> ReadScopedAsync(CancellationToken ct) => new("account-a", await ReadAsync(ct));
    }

    sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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
