using System.Diagnostics;
using System.Text.Json.Nodes;
using AgentBridge.Host;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class AuditLifecycleTests : LaunchFlowFixture
{
    private protected override bool UsesRelay => false;
    readonly FakeTimeProvider auditClock = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    readonly Guid run = Guid.NewGuid();
    readonly P256KeyPair device = P256KeyPair.Generate();
    string Profile => Path.Combine(f.Root, "audit-lifecycle", "default");
    string Audit => Path.Combine(Profile, "audit");
    string Head(bool launch) => Path.Combine(Audit, launch ? "launches.head" : "agents.head");
    AgentAuditLog Writer(AuditLockTiming? lockTiming = null) => new(Profile, auditClock, logged.Enqueue) { LockTiming = lockTiming, DeviceKey = device };
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

    [Fact]
    public async Task The_native_launch_preflight_works_before_and_after_a_person_signed_audit_rotation()
    {
        Assert.True(Writer().Append(Ordinary()));
        Assert.Equal(LaunchOutcome.Started, (await Launch()).Outcome);
        Assert.Null(AuditRotationTests.Rotate(Writer(), device, auditClock.GetUtcNow()));
        Assert.Equal(LaunchOutcome.Started, (await Launch()).Outcome);
        Pair();
        Assert.True(Writer().Verify().Intact && Writer().Verify(launch: true).Intact);
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
