using System.Diagnostics;
using System.Text.Json.Nodes;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class AuditLifecycleTests : LaunchFlowFixture
{
    private protected override bool UsesRelay => false;
    readonly FakeTimeProvider auditClock = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    readonly Guid run = Guid.NewGuid();
    string Profile => Path.Combine(f.Root, "audit-lifecycle", "default");
    string Audit => Path.Combine(Profile, "audit");
    string Head(bool launch) => Path.Combine(Audit, launch ? "launches.head" : "agents.head");
    AgentAuditLog Writer(AuditLockTiming? lockTiming = null) => new(Profile, auditClock, logged.Enqueue) { LockTiming = lockTiming };
    AgentAuditEntry Ordinary() => new("local", "fixture", "person:local", [], "lookup", "fixture", "ok");
    AgentAuditEntry Intent(bool launch)
    {
        var id = Guid.NewGuid();
        return new("local", id.ToString(), "person:local", [], launch ? "session.launch" : "session.stop",
            id.ToString(), "reserved", Gate: new GateAuditMetadata(1, id, run, 0, "intent", "local", "person-client", id, "none"));
    }
    void Pair(bool launch = false)
    {
        var intent = Intent(launch);
        Assert.True(Writer().Append(intent));
        Assert.True(Writer().Append(intent with { Outcome = launch ? "started" : "ended", Gate = intent.Gate! with { Phase = "terminal" } }));
    }
    Task<LaunchResult> Launch() => GateLaunchAudit.LaunchAsync(Launcher(Config()),
        new LaunchRequest { Request = Guid.NewGuid().ToString(), Agent = "copilot", Workspace = "App", Prompt = "fixture" },
        "local", Writer(), new GateLaunchIngress("local", "person-client", "person:local", null));
    Dictionary<string, byte[]> Evidence() => Directory.GetFiles(Audit, "*.jsonl").ToDictionary(p => p, File.ReadAllBytes);

    [Theory]
    [InlineData("person-client", "person:local")]
    [InlineData("agent", "agent:fixture")]
    public async Task A_launch_records_its_verified_ingress_with_the_bound_process(string actor, string principal)
    {
        var request = Guid.NewGuid();
        var result = await GateLaunchAudit.LaunchAsync(Launcher(Config()),
            new LaunchRequest { Request = request.ToString(), Agent = "copilot", Workspace = "App", Prompt = "fixture" },
            "local", Writer(), new GateLaunchIngress("local", actor, principal, null));
        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        var path = Path.Combine(f.Root, "profile", "launch-provenance.json");
        void Check()
        {
            var origin = JsonNode.Parse(File.ReadAllText(path))![request.ToString()]!;
            Assert.Equal("local", origin["attribution"]?["path"]?.GetValue<string>());
            Assert.Equal(actor, origin["attribution"]?["actorKind"]?.GetValue<string>());
            Assert.Equal(principal, origin["attribution"]?["principal"]?.GetValue<string>());
            Assert.Equal(result.Session, origin["session"]?.GetValue<string>());
            Assert.Equal(source.ProcessOf(result.Session!)!.Id, origin["process"]!["id"]!.GetValue<int>());
        }
        Check();
        var reopened = new LaunchProvenance(path, logged.Enqueue);
        Assert.Equal(new LaunchAttribution("local", actor, principal), reopened.AttributionOf(request));
        reopened.Bound(request, result.Session!, source.ProcessOf(result.Session!)!);
        Check();
    }

    [Theory]
    [InlineData("agent_bridge_1111111111111111")]
    [InlineData("SG Builder 1")]
    public async Task An_unaudited_peer_name_never_establishes_launch_attribution(string peer)
    {
        var request = Guid.NewGuid();
        var result = await Launcher(Config()).LaunchAsync(
            new LaunchRequest { Request = request.ToString(), Agent = "copilot", Workspace = "App", Prompt = "fixture" }, peer);
        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        var path = Path.Combine(f.Root, "profile", "launch-provenance.json");
        var origin = JsonNode.Parse(File.ReadAllText(path))![request.ToString()]!;
        Assert.Null(origin["attribution"]);
        Assert.True(new LaunchProvenance(path, logged.Enqueue).Contains(result.Session!, source.ProcessOf(result.Session!)));
    }

    [Theory]
    [InlineData(null), InlineData("--dangerously-skip-permissions"), InlineData("--verbose")]
    public async Task Only_a_registered_controller_ingress_without_extra_arguments_records_durable_fleet_origin(string? extra)
    {
        var store = new AgentCredentials(Path.Combine(f.Root, "controller"), "fixture");
        var credential = store.MintScript("sre").Credential;
        var channel = new FleetChannel();
        store.Accept(channel, credential);
        Git(f.Root, "init", "-q", "-b", "main", app);
        File.WriteAllText(Path.Combine(app, "README.md"), "fixture");
        Git(app, "add", "README.md");
        Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-qm", "fixture");
        locator.Installed["claude"] = @"C:\fake\claude.exe";
        var launcher = Launcher(Config(c => c with { Workspaces = [new("App", app, true)],
            Agents = new Dictionary<string, AgentConfig> { ["claude"] = new() { ExtraArgs = extra is null ? [] : [extra] } } }),
            agentWorktreeCheck: AgentLaunchWorktreeCheck.Check);
        var controller = new FleetController(store, source, launcher, null, Writer());
        var handler = LocalApi.Handler(null!, null!, null!, credentials: store, fleetController: controller);
        var body = System.Text.Json.JsonSerializer.SerializeToElement(new LaunchRequest
            { Request = Guid.NewGuid().ToString(), Agent = "claude", Workspace = "App", Permissions = LaunchProtocol.Ask, Prompt = "fixture" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler(channel, FleetController.LaunchOp, body));
        controller.RegisterController(credential);
        var launched = Assert.IsType<LaunchResult>(await handler(channel, FleetController.LaunchOp, body));
        if (extra is not null)
        {
            Assert.Equal(LaunchOutcome.Refused, launched.Outcome);
            Assert.Contains("extra", launched.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(starter.Specs);
            return;
        }
        Assert.True(launched.Outcome == LaunchOutcome.Started, launched.Reason);
        var saved = new LaunchProvenance(Path.Combine(f.Root, "profile", "launch-provenance.json"), logged.Enqueue);
        Assert.True(saved.FleetContains(launched.Session!, source.ProcessOf(launched.Session!)!));
        Assert.False(saved.FleetContains(launched.Session!, new SessionProcess(123, DateTimeOffset.UtcNow)));
    }

    [Theory]
    [InlineData("""{"path":"local","actorKind":"agent"}""")]
    [InlineData("""{"path":"local","actorKind":"agent","principal":""}""")]
    [InlineData("""{"path":"guessed","actorKind":"agent","principal":"agent:fixture"}""")]
    [InlineData("""{"path":"local","actorKind":"guessed","principal":"agent:fixture"}""")]
    public void Invalid_saved_attribution_refuses_provenance_instead_of_granting_agent_origin(string attribution)
    {
        var path = Path.Combine(f.Root, "provenance.json");
        File.WriteAllText(path, new JsonObject
        {
            [Guid.NewGuid().ToString()] = new JsonObject
                { ["session"] = "fixture", ["process"] = null, ["attribution"] = JsonNode.Parse(attribution) },
        }.ToJsonString());
        var provenance = new LaunchProvenance(path, logged.Enqueue);
        var refusal = Assert.Throws<InvalidOperationException>(() => provenance.Contains("another", new SessionProcess(1234, DateTimeOffset.UtcNow)));
        Assert.Contains("provenance is unavailable", refusal.Message);
    }

    [Fact]
    public async Task Clean_retention_advances_the_root_without_disabling_subsequent_production_launches()
    {
        Assert.Equal(LaunchOutcome.Started, (await Launch()).Outcome);
        Pair();
        var oldest = JsonNode.Parse(File.ReadAllText(Head(false)))!["root"]!.GetValue<string>();
        for (var day = 0; day <= AgentAuditLog.KeepDays; day++)
        {
            auditClock.Advance(TimeSpan.FromDays(1));
            Assert.True(Writer().Append(Ordinary()));
        }
        Assert.False(File.Exists(Path.Combine(Audit, oldest)));
        Assert.True(Writer().Verify().Intact);
        Assert.Equal("none", JsonNode.Parse(File.ReadAllText(Head(false)))!["fault"]!.GetValue<string>());
        Assert.True(GateAuditProjection.Read(Profile, run).Invalidated);
        Assert.Equal(LaunchOutcome.Started, (await Launch()).Outcome);
        Assert.Equal(LaunchOutcome.Started, (await Launch()).Outcome);
    }

    [Fact]
    public async Task External_root_loss_is_not_reanchored_by_a_later_append_or_launch()
    {
        Pair();
        var original = JsonNode.Parse(File.ReadAllText(Head(false)))!["root"]!.GetValue<string>();
        auditClock.Advance(TimeSpan.FromDays(1));
        Assert.True(Writer().Append(Ordinary()));
        File.Delete(Path.Combine(Audit, original));
        Assert.True(Writer().Append(Ordinary()));
        Assert.False(Writer().Verify().Intact);
        Assert.True(GateAuditProjection.Read(Profile, run).Invalidated);
        Assert.Equal(original, JsonNode.Parse(File.ReadAllText(Head(false)))!["root"]!.GetValue<string>());
        Assert.Equal(LaunchOutcome.Refused, (await Launch()).Outcome);
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public async Task Upgrade_adopts_intact_existing_chains_but_never_a_damaged_chain(bool damaged)
    {
        Assert.Equal(LaunchOutcome.Started, (await Launch()).Outcome);
        Pair();
        File.Delete(Head(false));
        File.Delete(Head(true));
        if (damaged)
        {
            var file = Directory.GetFiles(Audit, "agents-*.jsonl").Single();
            File.WriteAllText(file, File.ReadAllText(file).Replace("\"reserved\"", "\"changed\"", StringComparison.Ordinal));
        }
        var before = Evidence();
        Assert.False(GateAuditProjection.Load(Profile).Complete);
        Assert.False(File.Exists(Head(false)));
        Assert.False(File.Exists(Head(true)));
        var result = await Launch();
        Assert.Equal(damaged ? LaunchOutcome.Refused : LaunchOutcome.Started, result.Outcome);
        foreach (var (file, bytes) in before) { Assert.Equal(bytes, File.ReadAllBytes(file).Take(bytes.Length).ToArray()); }
        if (damaged) { return; }
        Assert.True(File.Exists(Head(false)));
        Assert.True(File.Exists(Head(true)));
        Assert.True(Writer().Verify().Intact);
        Assert.True(Writer().Verify(launch: true).Intact);
        var sequences = GateAuditProjection.Load(Profile).Rows.Select(r => r.Gate!.Sequence).Order().ToArray();
        Assert.Equal(Enumerable.Range(1, 6).Select(i => (long)i), sequences);
    }

    [Theory]
    [InlineData(false, false), InlineData(false, true)]
    [InlineData(true, false), InlineData(true, true)]
    public void Reader_and_writer_wait_for_contention_but_refuse_after_the_bound(bool reader, bool release)
    {
        Pair();
        var before = Evidence();
        using var held = new FileStream(Audit + ".lock", FileMode.Open,
            reader ? FileAccess.ReadWrite : FileAccess.Read, reader ? FileShare.None : FileShare.Read);
        long elapsed = 0;
        var waits = 0;
        var timing = new AuditLockTiming(() => 10_000 + elapsed, () =>
        {
            // Advance only after a real sharing violation, on the lock's own thread.
            // A stalled runner cannot exhaust the budget before this releases the file.
            waits++;
            if (waits > 100) { throw new InvalidOperationException("The audit lock exceeded its retry budget."); }
            elapsed += 20;
            if (release && elapsed == 100) { held.Dispose(); }
        });
        var result = reader ? GateAuditProjection.Load(Profile, lockTiming: timing).Complete
            : Writer(timing).Append(Ordinary());
        Assert.Equal(release, result);
        Assert.Equal(release ? 5 : 100, waits);
        Assert.Equal(release ? 100L : 2000L, elapsed);
        if (!release)
        {
            Assert.InRange(TimeSpan.FromMilliseconds(elapsed).TotalSeconds, 1.5, 5);
            foreach (var (file, bytes) in before) { Assert.Equal(bytes, File.ReadAllBytes(file)); }
            held.Dispose();
            Assert.True(reader ? GateAuditProjection.Load(Profile).Complete : Writer().Append(Ordinary()));
        }
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public void Default_reader_and_writer_wait_before_refusing_and_recover_after_release(bool reader)
    {
        Pair();
        var before = Evidence();
        using (var held = new FileStream(Audit + ".lock", FileMode.Open,
            reader ? FileAccess.ReadWrite : FileAccess.Read, reader ? FileShare.None : FileShare.Read))
        {
            var elapsed = Stopwatch.StartNew();
            var result = reader ? GateAuditProjection.Load(Profile).Complete : Writer().Append(Ordinary());
            elapsed.Stop();
            Assert.False(result);
            Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(1500),
                $"The default audit lock refused after {elapsed.Elapsed}; expected at least 1.5 seconds.");
            foreach (var (file, bytes) in before) { Assert.Equal(bytes, File.ReadAllBytes(file)); }
        }
        Assert.True(reader ? GateAuditProjection.Load(Profile).Complete : Writer().Append(Ordinary()));
    }

    [Theory]
    [InlineData(false, false), InlineData(false, true)]
    [InlineData(true, false), InlineData(true, true)]
    public void Flushed_evidence_ahead_of_its_checkpoint_is_incomplete_not_a_coverage_break(bool launch, bool rotate)
    {
        Pair(false);
        Pair(true);
        var checkpoint = File.ReadAllBytes(Head(launch));
        if (rotate) { auditClock.Advance(TimeSpan.FromDays(1)); }
        var intent = Intent(launch);
        Assert.True(Writer().Append(intent));
        File.WriteAllBytes(Head(launch), checkpoint);
        var before = Evidence();
        var report = GateAuditProjection.Read(Profile, run);
        Assert.False(report.Invalidated);
        Assert.Contains("audit-incomplete", report.IncompleteReasons);
        Assert.DoesNotContain("coverage-break", report.IncompleteReasons);
        Assert.False(Writer().Verify(launch).Intact);
        Assert.Equal(checkpoint, File.ReadAllBytes(Head(launch)));
        foreach (var (file, bytes) in before) { Assert.Equal(bytes, File.ReadAllBytes(file)); }
        Assert.True(Writer().Append(intent with { Outcome = launch ? "started" : "ended", Gate = intent.Gate! with { Phase = "terminal" } }));
        Assert.Contains("audit-incomplete", GateAuditProjection.Read(Profile, run).IncompleteReasons);
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public void An_interrupted_head_replacement_does_not_permanently_refuse_later_appends(bool launch)
    {
        Pair(false);
        Pair(true);
        File.WriteAllText(Head(launch) + ".new", "interrupted checkpoint");
        Pair(launch);
        Assert.False(File.Exists(Head(launch) + ".new"));
        Assert.True(Writer().Verify(launch).Intact);
        Assert.True(GateAuditProjection.Load(Profile).Complete);
    }
}
