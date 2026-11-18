using System.Text.Json;
using AgentBridge.Host;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed partial class RemoteAuthorizationTests
{
    [Theory]
    [InlineData("prepared"), InlineData("paused"), InlineData("voided")]
    public async Task Gate_reset_preserves_old_evidence_and_creates_one_fresh_identity_across_retries(string state)
    {
        var clock = new FakeTimeProvider(time.GetUtcNow());
        var (channel, audit, enrollment) = await GateAsync(clock);
        var old = Result(await channel.RequestAsync("gate.start", new { request = Guid.NewGuid() }, Patience)).GetProperty("run").GetGuid();
        var operation = Guid.NewGuid();
        var gate = new GateAuditMetadata(1, operation, old, 0, "intent", "relay", "person-client", Guid.NewGuid(), "none");
        var entry = new AgentAuditEntry("in", operation.ToString(), "person:fixture", [], "session.stop", Guid.NewGuid().ToString(), "attempted", Gate: gate);
        Assert.True(audit.Append(entry));
        Assert.True(audit.Append(entry with { Outcome = "ended", Gate = gate with { Phase = "terminal" } }));
        if (state != "prepared")
        {
            Result(await channel.RequestAsync("gate.pause", new { request = Guid.NewGuid(), run = old }, Patience));
            if (state == "voided") { clock.Advance(TimeSpan.FromMinutes(5)); }
        }
        var file = Assert.Single(Directory.GetFiles(Path.Combine(audit.Profile, "audit"), "agents-*.jsonl"));
        var before = File.ReadAllBytes(file);
        enrollment.Value = new GateEnrollmentFixture().Value;
        var request = Guid.NewGuid();
        var reset = Result(await channel.RequestAsync("gate.reset", new { request }, Patience)).GetProperty("run").GetGuid();
        Assert.NotEqual(old, reset);
        Assert.NotEqual(Guid.Empty, reset);
        Assert.True(audit.Admission.WaitForAdmissionAsync().IsCompleted);
        Assert.Equal(before, File.ReadAllBytes(file).Take(before.Length));
        var oldReport = GateAuditProjection.Read(audit.Profile, old);
        Assert.True(oldReport.Invalidated);
        Assert.Equal(1, Assert.Single(oldReport.Totals).Successes);
        host.Hub.Handler = authorization.Handler(service, new AgentAuditLog(audit.Profile, clock));
        Assert.Equal(reset, Result(await channel.RequestAsync("gate.reset", new { request }, Patience)).GetProperty("run").GetGuid());
        var begin = Assert.Single(GateAuditProjection.Load(audit.Profile).Rows, r => r.Action == "gate.reset" && r.Outcome == "reset");
        Assert.Equal(enrollment.Value!.Participants, begin.Gate!.Enrollment!.Participants);
        Assert.Empty(GateAuditProjection.Read(audit.Profile, reset).Totals);
        Assert.False(GateAuditProjection.Read(audit.Profile, reset).Invalidated);
        Assert.Null(audit.ControlWriterRun);
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public async Task Gate_roster_identity_change_voids_but_an_advancing_recorder_cursor_does_not(bool recorder)
    {
        var (channel, audit, enrollment) = await GateAsync();
        var request = Guid.NewGuid();
        var run = Result(await channel.RequestAsync("gate.start", new { request }, Patience)).GetProperty("run").GetGuid();
        enrollment.Change(enrollment.Value! with { Cursor = enrollment.Value.Cursor + 1 });
        Assert.False(GateAuditProjection.Read(audit.Profile, run).Invalidated);
        var participants = enrollment.Value!.Participants.ToArray();
        participants[0] = participants[0] with { Profile = Guid.NewGuid().ToString() };
        enrollment.Change(recorder ? enrollment.Value with { Recorder = Guid.NewGuid() } : enrollment.Value with { Participants = participants });
        Assert.True(GateAuditProjection.Read(audit.Profile, run).Invalidated);
        Assert.Equal(run, Result(await channel.RequestAsync("gate.start", new { request }, Patience)).GetProperty("run").GetGuid());
        Assert.False((await channel.RequestAsync("gate.pause", new { request = Guid.NewGuid(), run }, Patience)).GetProperty("ok").GetBoolean());
        var reset = Result(await channel.RequestAsync("gate.reset", new { request = Guid.NewGuid() }, Patience)).GetProperty("run").GetGuid();
        Assert.NotEqual(run, reset);
    }

    [Fact]
    public async Task Gate_reset_refuses_missing_enrollment_without_erasing_or_replacing_the_old_run()
    {
        var (channel, audit, enrollment) = await GateAsync();
        var run = Result(await channel.RequestAsync("gate.start", new { request = Guid.NewGuid() }, Patience)).GetProperty("run").GetGuid();
        enrollment.Value = null;
        var refused = await channel.RequestAsync("gate.reset", new { request = Guid.NewGuid() }, Patience);
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Contains("enrollment unavailable", refused.GetProperty("error").GetString());
        Assert.Single(GateAuditProjection.Load(audit.Profile).Rows);
        Assert.False(GateAuditProjection.Read(audit.Profile, run).Invalidated);
    }

    [Fact]
    public async Task Gate_roster_observation_does_not_turn_unrelated_incomplete_history_into_coverage_loss()
    {
        var (_, audit, enrollment) = await GateAsync();
        var entry = new AgentAuditEntry("in", Guid.NewGuid().ToString(), "person:fixture", [], "agent.send", "fleet", "attempted");
        Assert.True(audit.Append(entry));
        Assert.True(audit.Bootstrap());
        var file = Assert.Single(Directory.GetFiles(Path.Combine(audit.Profile, "audit"), "agents-*.jsonl"));
        File.WriteAllText(file, File.ReadAllText(file).TrimEnd('\n'));
        Assert.True(audit.Append(entry with { Request = Guid.NewGuid().ToString() }));
        var before = new List<string>();
        GateAuditProjection.Load(audit.Profile, before.Add);
        Assert.Contains("audit-incomplete", before);
        Assert.DoesNotContain("coverage-break", before);
        enrollment.Change(enrollment.Value);
        var after = new List<string>();
        GateAuditProjection.Load(audit.Profile, after.Add);
        Assert.DoesNotContain("coverage-break", after);
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public async Task Gate_review_reuses_a_durable_void_when_releasing_and_recovering_a_pause(bool reset)
    {
        var clock = new GateClock(time.GetUtcNow());
        var (channel, audit, enrollment) = await GateAsync(clock);
        var run = Result(await channel.RequestAsync("gate.start", new { request = Guid.NewGuid() }, Patience)).GetProperty("run").GetGuid();
        Result(await channel.RequestAsync("gate.pause", new { request = Guid.NewGuid(), run }, Patience));
        var pause = Assert.Single(GateAuditProjection.Load(audit.Profile).Rows, r => r.Outcome == "paused");
        var gate = pause.Gate! with { Operation = Guid.NewGuid(), PauseDeadline = null, ReasonCode = "roster-change" };
        if (reset) { gate = gate with { Run = Guid.NewGuid(), PreviousRun = run, Enrollment = enrollment.Value, ReasonCode = "none" }; }
        Assert.True(audit.Append(new("in", Guid.NewGuid().ToString(), pause.Principal, [], reset ? "gate.reset" : "gate.pause",
            "profile", reset ? "reset" : "invalidated", Gate: gate)));
        clock.Unreadable = true;
        audit.RunPause.Dispose();
        clock.Unreadable = false;
        _ = new AgentAuditLog(audit.Profile, clock, maxBytes: 0).RunPause;
        Assert.True(audit.Admission.WaitForAdmissionAsync().IsCompleted);
        Assert.DoesNotContain("coverage-break", GateAuditProjection.Read(audit.Profile, gate.Run).IncompleteReasons);
    }

    [Fact]
    public async Task Gate_review_preserves_an_A_B_A_transition_while_control_is_locked()
    {
        var (channel, audit, enrollment) = await GateAsync();
        var run = Result(await channel.RequestAsync("gate.start", new { request = Guid.NewGuid() }, Patience)).GetProperty("run").GetGuid();
        var original = enrollment.Value!;
        var changed = original with { Participants = original.Participants.Select((p, i) => i == 0 ? p with { Profile = Guid.NewGuid().ToString() } : p).ToArray() };
        using var published = new AutoResetEvent(false);
        enrollment.BeforeNotification = () => published.Set();
        Task first, second;
        lock (GateRunPreparation.Sync(audit))
        {
            first = Task.Run(() => enrollment.Change(changed));
            Assert.True(published.WaitOne(Patience));
            second = Task.Run(() => enrollment.Change(original));
            Assert.True(published.WaitOne(Patience));
        }
        await Task.WhenAll(first, second).WaitAsync(Patience);
        Assert.True(GateAuditProjection.Read(audit.Profile, run).Invalidated);
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public async Task Gate_review_reconciles_a_roster_void_after_storage_recovers(bool readFailure)
    {
        var (channel, audit, enrollment) = await GateAsync();
        var run = Result(await channel.RequestAsync("gate.start", new { request = Guid.NewGuid() }, Patience)).GetProperty("run").GetGuid();
        using (new FileStream(Path.Combine(audit.Profile, "audit") + ".lock", FileMode.Open,
            readFailure ? FileAccess.ReadWrite : FileAccess.Read, readFailure ? FileShare.None : FileShare.Read))
        { enrollment.Change(enrollment.Value! with { Recorder = Guid.NewGuid(), Participants = enrollment.Value.Participants.Reverse().Select(p => p with { Profile = Guid.NewGuid().ToString() }).ToArray() }); }
        Assert.False((await channel.RequestAsync("gate.pause", new { request = Guid.NewGuid(), run }, Patience)).GetProperty("ok").GetBoolean());
        Assert.True(GateAuditProjection.Read(audit.Profile, run).Invalidated);
        Assert.DoesNotContain("coverage-break", GateAuditProjection.Read(audit.Profile, run).IncompleteReasons);
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public async Task Gate_review_holds_unavailable_observation_without_voiding_intact_evidence(bool io)
    {
        var (channel, audit, enrollment) = await GateAsync();
        var run = Result(await channel.RequestAsync("gate.start", new { request = Guid.NewGuid() }, Patience)).GetProperty("run").GetGuid();
        var original = enrollment.Value!;
        if (io) { enrollment.BeforeRead = () => throw new IOException("observation unavailable"); source.NotifyGateRosterChanged(); }
        else { enrollment.Change(null); }
        Assert.False(GateAuditProjection.Read(audit.Profile, run).Invalidated);
        Assert.False((await channel.RequestAsync("gate.pause", new { request = Guid.NewGuid(), run }, Patience)).GetProperty("ok").GetBoolean());
        enrollment.BeforeRead = null;
        enrollment.Change(original);
        Result(await channel.RequestAsync("gate.pause", new { request = Guid.NewGuid(), run }, Patience));
    }

    [Fact]
    public async Task Gate_review_replays_a_reset_binding_won_by_another_audit_instance()
    {
        var (channel, audit, enrollment) = await GateAsync();
        Result(await channel.RequestAsync("gate.start", new { request = Guid.NewGuid() }, Patience));
        var principal = Assert.Single(GateAuditProjection.Load(audit.Profile).Rows).Principal;
        var request = Guid.NewGuid();
        object? winner = null;
        enrollment.BeforeRead = () =>
        {
            enrollment.BeforeRead = null;
            winner = GateRunPreparation.Reset(new AgentAuditLog(audit.Profile, time), principal,
                JsonSerializer.SerializeToElement(new { request }), new GateEnrollmentFixture());
        };
        var reset = Result(await channel.RequestAsync("gate.reset", new { request }, Patience)).GetProperty("run").GetGuid();
        Assert.Equal(JsonSerializer.SerializeToElement(winner).GetProperty("run").GetGuid(), reset);
        Assert.Single(GateAuditProjection.Load(audit.Profile).Rows, r => r.Outcome == "reset");
    }

    [Fact]
    public async Task Gate_review_observation_cannot_leave_a_racing_reset_with_stale_pins()
    {
        var (channel, audit, enrollment) = await GateAsync();
        Result(await channel.RequestAsync("gate.start", new { request = Guid.NewGuid() }, Patience));
        var principal = Assert.Single(GateAuditProjection.Load(audit.Profile).Rows).Principal;
        var original = enrollment.Value!;
        Exception? refused = null;
        enrollment.BeforeRead = () =>
        {
            enrollment.BeforeRead = null;
            refused = Record.Exception(() => GateRunPreparation.Reset(new AgentAuditLog(audit.Profile, time), principal,
                JsonSerializer.SerializeToElement(new { request = Guid.NewGuid() }), new GateEnrollmentFixture { Value = original }));
        };
        enrollment.Change(original with { Participants = original.Participants.Select(p => p with { Profile = Guid.NewGuid().ToString() }).ToArray() });
        Assert.True(refused is null or InvalidOperationException);
        var rows = GateAuditProjection.Load(audit.Profile).Rows;
        Assert.DoesNotContain(rows, r => GateRunPreparation.IsBegin(r) && !GateRunPreparation.Voided(rows, r.Gate!.Run));
    }
}
