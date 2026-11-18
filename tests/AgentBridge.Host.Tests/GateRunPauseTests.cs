using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed partial class RemoteAuthorizationTests
{
    [Theory]
    [InlineData(false), InlineData(true)]
    public async Task Gate_pause_queues_actions_without_extending_deadline_and_rechecks_authority(bool revoked)
    {
        var clock = new FakeTimeProvider(time.GetUtcNow());
        var (channel, audit, _) = await GateAsync(clock);
        var replies = new ReplyFlowTests.IntentRecorder();
        var handler = authorization.Handler(new SessionService(source,
            new MachineInfo(host.Id, "Fixture", "windows", "0.1.0"), replies: replies), audit);
        var arrived = new TaskCompletionSource<Task<object?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Hub.Handler = (peer, op, data) =>
        {
            var result = handler(peer, op, data);
            if (op == Ops.SessionReply) { arrived.TrySetResult(result); }
            return result;
        };
        var run = Result(await channel.RequestAsync("gate.start", new { request = Guid.NewGuid() }, Patience)).GetProperty("run").GetGuid();
        var paused = Result(await channel.RequestAsync("gate.pause", new { request = Guid.NewGuid(), run }, Patience));
        var deadline = paused.GetProperty("deadline").GetDateTimeOffset();
        Assert.Equal(clock.GetUtcNow().AddMinutes(5), deadline);
        var queued = channel.RequestAsync(Ops.SessionReply, new { id = SessionId, request = Guid.NewGuid(), text = "continue" }, Patience);
        var admitted = await arrived.Task.WaitAsync(Patience);
        Assert.False(admitted.IsCompleted);
        Assert.Null(replies.Last);
        Result(await channel.RequestAsync(Ops.SessionsList, new { }, Patience));
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(deadline, Result(await channel.RequestAsync("gate.pause", new { request = Guid.NewGuid(), run }, Patience)).GetProperty("deadline").GetDateTimeOffset());
        if (revoked) { store.Revoke(devices[^1].Key.Public); clock.Advance(TimeSpan.FromMinutes(1)); }
        else
        {
            Assert.Equal(run, Result(await channel.RequestAsync("gate.resume", new { request = Guid.NewGuid(), run }, Patience)).GetProperty("run").GetGuid());
        }
        if (revoked)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await admitted.WaitAsync(Patience));
            await Assert.ThrowsAsync<IOException>(() => queued);
        }
        else { await admitted.WaitAsync(Patience); Result(await queued); }
        Assert.Equal(revoked, replies.Last is null);
        Assert.Equal(revoked, GateAuditProjection.Read(audit.Profile, run).Invalidated);
    }

    [Theory]
    [InlineData("monotonic"), InlineData("earlier"), InlineData("unreadable"), InlineData("later"), InlineData("restart")]
    public async Task Gate_pause_voids_on_timeout_or_uncertainty_and_preserves_its_original_deadline(string cause)
    {
        var clock = new GateClock(time.GetUtcNow());
        var (channel, audit, _) = await GateAsync(clock);
        var run = Result(await channel.RequestAsync("gate.start", new { request = Guid.NewGuid() }, Patience)).GetProperty("run").GetGuid();
        var deadline = Result(await channel.RequestAsync("gate.pause", new { request = Guid.NewGuid(), run }, Patience)).GetProperty("deadline").GetDateTimeOffset();
        if (cause == "restart") { host.Hub.Handler = authorization.Handler(service, new AgentAuditLog(audit.Profile, clock)); }
        else if (cause == "earlier") { clock.AdjustTime(clock.GetUtcNow().AddSeconds(-1)); }
        else if (cause == "later") { clock.AdjustTime(clock.GetUtcNow().AddDays(1)); }
        else if (cause == "unreadable") { clock.Unreadable = true; }
        else { clock.Advance(TimeSpan.FromMinutes(5)); }
        Assert.False((await channel.RequestAsync("gate.pause", new { request = Guid.NewGuid(), run }, Patience)).GetProperty("ok").GetBoolean());
        clock.Unreadable = false;
        Assert.True(GateAuditProjection.Read(audit.Profile, run).Invalidated);
        var rows = GateAuditProjection.Load(audit.Profile).Rows;
        using var persisted = JsonDocument.Parse(JsonSerializer.Serialize(rows.Single(r => r.Outcome == "paused").Gate, ProtocolJson.Options));
        Assert.Equal(deadline, persisted.RootElement.GetProperty("pauseDeadline").GetDateTimeOffset());
        Assert.Null(audit.ControlWriterRun);
        clock.Advance(TimeSpan.FromMinutes(5));
    }

    sealed class GateClock(DateTimeOffset now) : FakeTimeProvider(now)
    {
        internal bool Unreadable;
        public override DateTimeOffset GetUtcNow() => Unreadable ? throw new IOException("clock unavailable") : base.GetUtcNow();
    }

    [Fact]
    public async Task Gate_pause_voids_and_releases_admission_when_its_control_channel_is_revoked()
    {
        var (channel, audit, _) = await GateAsync();
        var run = Result(await channel.RequestAsync("gate.start", new { request = Guid.NewGuid() }, Patience)).GetProperty("run").GetGuid();
        Result(await channel.RequestAsync("gate.pause", new { request = Guid.NewGuid(), run }, Patience));
        var resumed = audit.Admission.WaitForAdmissionAsync();
        Assert.False(resumed.IsCompleted);
        store.Revoke(devices[^1].Key.Public);
        authorization.Revalidate();
        await resumed.WaitAsync(Patience);
        audit.RunPause.ValidateClock();
        Assert.True(GateAuditProjection.Read(audit.Profile, run).Invalidated);
    }
}
