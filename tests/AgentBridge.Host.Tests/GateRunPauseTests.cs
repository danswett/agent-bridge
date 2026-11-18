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
    public async Task Gate_pause_review_replayed_requests_do_not_recreate_or_release_a_later_barrier(bool resume)
    {
        var clock = new FakeTimeProvider(time.GetUtcNow());
        var (channel, audit, _) = await GateAsync(clock);
        var run = Result(await channel.RequestAsync("gate.start", new { request = Guid.NewGuid() }, Patience)).GetProperty("run").GetGuid();
        var pause = Guid.NewGuid(); var release = Guid.NewGuid();
        Result(await channel.RequestAsync("gate.pause", new { request = pause, run }, Patience));
        Result(await channel.RequestAsync("gate.resume", new { request = release, run }, Patience));
        clock.Advance(TimeSpan.FromMinutes(1));
        if (resume) { Result(await channel.RequestAsync("gate.pause", new { request = Guid.NewGuid(), run }, Patience)); }
        var count = GateAuditProjection.Load(audit.Profile).Rows.Count;
        await channel.RequestAsync(resume ? "gate.resume" : "gate.pause", new { request = resume ? release : pause, run }, Patience);
        Assert.Equal(count, GateAuditProjection.Load(audit.Profile).Rows.Count);
        Assert.Equal(!resume, audit.Admission.WaitForAdmissionAsync().IsCompleted);
    }

    [Fact]
    public async Task Gate_pause_review_malformed_checkpoint_does_not_escape_its_timeout_callback()
    {
        var clock = new FakeTimeProvider(time.GetUtcNow());
        var (channel, audit, _) = await GateAsync(clock);
        var run = Result(await channel.RequestAsync("gate.start", new { request = Guid.NewGuid() }, Patience)).GetProperty("run").GetGuid();
        Result(await channel.RequestAsync("gate.pause", new { request = Guid.NewGuid(), run }, Patience));
        File.WriteAllText(Path.Combine(audit.Profile, AgentAuditLog.FolderName, "agents.head"), "{");
        Assert.Null(Record.Exception(() => clock.Advance(TimeSpan.FromMinutes(5))));
        Assert.True(audit.Admission.WaitForAdmissionAsync().IsCompleted);
        Assert.Null(audit.ControlWriterRun);
        Assert.False(GateAuditProjection.Load(audit.Profile).Complete);
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public async Task Gate_pause_review_acknowledgment_waits_for_drain_and_refuses_an_expired_barrier(bool expires)
    {
        var clock = new FakeTimeProvider(time.GetUtcNow());
        var (channel, audit, _) = await GateAsync(clock);
        var run = Result(await channel.RequestAsync("gate.start", new { request = Guid.NewGuid() }, Patience)).GetProperty("run").GetGuid();
        using var admitted = audit.Admission.TryEnterDelivery();
        Assert.NotNull(admitted);
        var handler = authorization.Handler(service, audit);
        var request = Guid.NewGuid();
        async Task<Task<JsonElement>> Pending()
        {
            var arrived = new TaskCompletionSource<Task<object?>>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Hub.Handler = (peer, op, data) => { var task = handler(peer, op, data); arrived.TrySetResult(task); return task; };
            var response = channel.RequestAsync("gate.pause", new { request, run }, Patience);
            Assert.False((await arrived.Task.WaitAsync(Patience)).IsCompleted);
            return response;
        }
        var first = await Pending(); var duplicate = await Pending();
        if (expires) { clock.Advance(TimeSpan.FromMinutes(5)); }
        admitted.Dispose();
        Assert.Equal(!expires, (await first).GetProperty("ok").GetBoolean());
        Assert.Equal(!expires, (await duplicate).GetProperty("ok").GetBoolean());
        Assert.Equal(expires, GateAuditProjection.Read(audit.Profile, run).Invalidated);
    }

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
