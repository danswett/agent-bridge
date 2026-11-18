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

    [Fact]
    public async Task Gate_roster_identity_change_voids_but_an_advancing_recorder_cursor_does_not()
    {
        var (channel, audit, enrollment) = await GateAsync();
        var run = Result(await channel.RequestAsync("gate.start", new { request = Guid.NewGuid() }, Patience)).GetProperty("run").GetGuid();
        enrollment.Change(enrollment.Value! with { Cursor = enrollment.Value.Cursor + 1 });
        Assert.False(GateAuditProjection.Read(audit.Profile, run).Invalidated);
        var participants = enrollment.Value!.Participants.ToArray();
        participants[0] = participants[0] with { Profile = Guid.NewGuid().ToString() };
        enrollment.Change(enrollment.Value with { Participants = participants });
        Assert.True(GateAuditProjection.Read(audit.Profile, run).Invalidated);
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
}
