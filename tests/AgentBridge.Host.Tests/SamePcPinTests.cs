using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.App.Core;
using AgentBridge.Host;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Roster;
using AgentBridge.Transport;
using Xunit;

// Same-PC trust (#3450 ruling item 3, brief 183): the signed-in app hands the host beside it the
// roster root its own verified roster holds, over the user-only pipe, and nothing else can.
public sealed class SamePcPinTests : IDisposable
{
    readonly string profile = Path.Combine(Path.GetTempPath(), "same-pc-pin-" + Guid.NewGuid().ToString("N")[..8]);
    readonly World w = new();
    string? hostAccount = RosterFixture.Account;
    int fetches;

    public SamePcPinTests()
    {
        Directory.CreateDirectory(profile);
        w.Genesis();
        w.Add("B", "G", "B", "G");
        w.Add("H", "G", "H", "G", kind: "host");
    }

    string PinPath => HostRosterPinWriter.PinPath(profile);
    RelayRoster Relay() => new("off", w.GenerationId, [.. w.Labels.Select(l => w.Entry(l).Canonical())]);
    HostRosterReader Reader() => new(PinPath, () => w.Now);
    SamePcPinHandoff Handoff(HostRosterReader? reader = null) =>
        new(profile, () => hostAccount, _ => { fetches++; return Task.FromResult<RelayRoster?>(Relay()); }, reader ?? Reader(), () => w.Now, _ => true);

    SamePcPinRequest Request(string device = "B")
    {
        var verified = w.Verifier();
        w.Now += RosterVerifier.Probation + TimeSpan.FromHours(1);
        var (request, refusal) = SamePcPin.Offer(verified, RosterFixture.Account, w.Id(device));
        return request ?? throw new InvalidOperationException(refusal);
    }

    static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, ProtocolJson.Options);
    async Task<string> Hand(SamePcPinRequest request, HostRosterReader? reader = null) =>
        (await Handoff(reader).HandleAsync(Json(request), CancellationToken.None)).State;

    [Fact]
    public void The_app_offers_its_roster_root_only_as_an_active_non_probationary_person_device_of_the_signed_in_account()
    {
        var verified = w.Verifier();
        Assert.Equal("unverified", SamePcPin.Offer(null, RosterFixture.Account, w.Id("B")).Refusal);
        Assert.Equal("not-signed-in", SamePcPin.Offer(verified, null, w.Id("B")).Refusal);
        Assert.Equal("other-account", SamePcPin.Offer(verified, "someone-else@example", w.Id("B")).Refusal);
        Assert.Equal("not-person-device", SamePcPin.Offer(verified, RosterFixture.Account, w.Id("H")).Refusal);
        Assert.Equal("not-person-device", SamePcPin.Offer(verified, RosterFixture.Account, w.Id("Z")).Refusal);
        Assert.Equal("probation", SamePcPin.Offer(verified, RosterFixture.Account, w.Id("B")).Refusal);

        w.Now += RosterVerifier.Probation + TimeSpan.FromHours(1);
        var (request, refusal) = SamePcPin.Offer(verified, RosterFixture.Account, w.Id("B"));
        Assert.Null(refusal);
        Assert.Equal(Codec.Base64Url(w.Entry("G").Hash), request!.Genesis);
        Assert.Equal(verified.Heads.Select(h => Codec.Base64Url(h)), request.Heads);
        Assert.Equal("This PC is now trusted for fleet messaging", SamePcPin.Line(SamePcPinProtocol.Pinned));
        Assert.Equal("This PC could not be trusted for fleet messaging (account-mismatch)", SamePcPin.Line("account-mismatch"));
    }

    [Fact]
    public async Task A_handoff_pins_an_unpinned_host_of_the_same_account_and_the_serving_reader_uses_it_without_a_restart()
    {
        var serving = Reader();
        var unnotified = Reader();
        var other = Reader();
        Assert.Null(serving.Pinned(RosterFixture.Account));

        Assert.Equal(SamePcPinProtocol.Pinned, await Hand(Request(), serving));

        Assert.NotNull(serving.Pinned(RosterFixture.Account));
        Assert.NotNull(new AgentBridge.Host.Fleet.FleetTrust(serving, profile, () => hostAccount).Membership());
        Assert.Equal("verified", unnotified.Update(Relay(), RosterFixture.Account).Outcome);
        Assert.NotNull(unnotified.Pinned(RosterFixture.Account));
        Assert.Equal("pin-mismatch", other.Update(Relay() with { Generation = new string('0', 32) }, RosterFixture.Account).Outcome);
        Assert.Equal(w.Entry("G").Hash, HostRosterReader.ReadPinned(PinPath).Genesis);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_fetch_paused_across_an_account_switch_writes_no_pin(bool switchBack)
    {
        var request = Request();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetched = new TaskCompletionSource<RelayRoster?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handoff = new SamePcPinHandoff(profile, () => hostAccount,
            _ => { started.SetResult(); return fetched.Task; }, Reader(), () => w.Now, _ => true);
        var pending = handoff.HandleAsync(Json(request), default);
        await started.Task;
        handoff.AccountChanged(() => hostAccount = "other@example");
        if (switchBack) { handoff.AccountChanged(() => hostAccount = RosterFixture.Account); }
        fetched.SetResult(Relay());
        Assert.Equal("account-mismatch", (await pending).State);
        Assert.False(File.Exists(PinPath));
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("genesis")]
    [InlineData("head")]
    [InlineData("heads")]
    public async Task Null_request_values_are_invalid_before_any_relay_read(string field)
    {
        var good = Request();
        var data = new Dictionary<string, object?>
        {
            ["account"] = good.Account, ["generation"] = good.Generation, ["genesis"] = good.Genesis,
            ["heads"] = good.Heads, ["device"] = good.Device,
        };
        data[field == "head" ? "heads" : field] = field == "head" ? new string?[] { null } : null;
        Assert.Equal("invalid-request", (await Handoff().HandleAsync(Json(data), default)).State);
        Assert.Equal(0, fetches);
        Assert.False(File.Exists(PinPath));
    }

    [Fact]
    public async Task A_handoff_refuses_an_existing_pin_another_account_a_non_person_device_or_an_unverified_roster()
    {
        var good = Request();
        hostAccount = "someone-else@example";
        Assert.Equal("account-mismatch", await Hand(good));
        hostAccount = null;
        Assert.Equal("no-account", await Hand(good));
        hostAccount = RosterFixture.Account;
        Assert.Equal("not-person-device", await Hand(good with { Device = w.Id("H") }));
        Assert.Equal("genesis-mismatch", await Hand(good with { Genesis = Codec.Base64Url(w.Entry("B").Hash) }));
        Assert.Equal("heads-missing", await Hand(good with { Heads = [Codec.Base64Url(new byte[32])] }));
        Assert.Equal("generation-mismatch", await Hand(good with { Generation = new string('0', 32) }));
        Assert.Equal("invalid-request", (await Handoff().HandleAsync(Json(new { good.Account, typed = "7d0f 3a2e" }), default)).State);
        Assert.False(File.Exists(PinPath));

        Assert.Equal(SamePcPinProtocol.Pinned, await Hand(good));
        var before = File.ReadAllBytes(PinPath);
        fetches = 0;
        Assert.Equal("pin-exists", await Hand(good));
        Assert.Equal(0, fetches);
        Assert.Equal(before, File.ReadAllBytes(PinPath));
    }

    [Fact]
    public async Task Only_the_app_on_the_local_pipe_reaches_the_handoff_never_an_agent_another_process_the_host_cli_or_a_relay_channel()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var request = Request();
        var callers = new Switch();
        var handler = LocalApi.Handler(null!, null!, callers, samePcPin: Handoff());
        await using (var server = LocalServer.Start(LocalEndpoint.For(profile), handler))
        await using (var pipe = await LocalClient.ConnectAsync(LocalEndpoint.For(profile)))
        {
            foreach (var kind in new[] { CallerKind.Agent, CallerKind.Other, CallerKind.HostCli })
            {
                callers.Kind = kind;
                var refused = await Assert.ThrowsAsync<HostErrorException>(() => pipe.CallAsync(SamePcPinProtocol.Op, request));
                Assert.Contains("only the signed-in app on this machine", refused.Message);
            }
            callers.Kind = CallerKind.App;
            var relayed = await Assert.ThrowsAsync<InvalidOperationException>(() => handler(new RelayChannel(), SamePcPinProtocol.Op, Json(request)));
            Assert.Contains("only the signed-in app on this machine", relayed.Message);
            Assert.False(File.Exists(PinPath));
            Assert.Equal(0, fetches);

            var pinned = await pipe.CallAsync<SamePcPinResult>(SamePcPinProtocol.Op, request);
            Assert.Equal(SamePcPinProtocol.Pinned, pinned.State);
        }
    }

    [Fact]
    public void The_handoff_is_wired_only_into_the_local_pipe_handler_never_a_relay_ha_or_agent_surface()
    {
        var src = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(src.FullName, "AgentBridge.slnx"))) { src = src.Parent ?? throw new IOException("repository root not found"); }
        string[] Files(string pattern) => [.. Directory.EnumerateFiles(Path.Combine(src.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => Regex.IsMatch(File.ReadAllText(f), pattern)).Select(f => Path.GetRelativePath(src.FullName, f).Replace('\\', '/')).Order()];
        Assert.Equal(["src/AgentBridge.Host/LocalApi.cs", "src/AgentBridge.Host/Program.cs", "src/AgentBridge.Host/SamePcPinHandoff.cs"],
            Files(@"\bSamePcPinHandoff\b"));
        Assert.Equal(["src/AgentBridge.Host/LocalApi.cs"], Files(@"samePcPin\.HandleAsync\("));
        Assert.Equal(["src/AgentBridge.App.Core/SamePcPin.cs", "src/AgentBridge.Host/LocalApi.cs", "src/AgentBridge.Protocol/SamePcPinProtocol.cs"],
            Files(@"SamePcPinProtocol\.Op\b|const string Op = ""host\.roster\.pin"""));
    }

    sealed class Switch : ICallerGuard
    {
        public CallerKind Kind { get; set; } = CallerKind.App;
        public Caller Classify(IProtocolChannel channel, bool fresh = false) => new(Kind, "fixture " + Kind);
    }

    // What a relay channel looks like to the handler: authenticated, but not this machine's pipe.
    sealed class RelayChannel : IProtocolChannel
    {
        public string PeerId => "a".PadRight(32, 'a');
        public string PeerName => "phone";
        public string PeerKind => "app";
        public bool IsOpen => true;
        public Task Closed => new TaskCompletionSource().Task;
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }

    public void Dispose()
    {
        try { Directory.Delete(profile, recursive: true); } catch (IOException) { }
    }
}
