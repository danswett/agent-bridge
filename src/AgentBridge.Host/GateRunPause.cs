using System.Text.Json;
using AgentBridge.Host.Delivery;

namespace AgentBridge.Host;

internal sealed class GateRunPause : IDisposable
{
    readonly AgentAuditLog audit;
    readonly Lock sync;
    DeliveryGate.AdmissionPause? lease;
    AgentAuditEntry? marker;
    ITimer? timer;
    DateTimeOffset lastControl;

    internal GateRunPause(AgentAuditLog audit)
    {
        this.audit = audit;
        sync = GateRunPreparation.Sync(audit);
        foreach (var group in GateAuditProjection.Load(audit.Profile).Rows.Where(r => r.Gate?.Phase == "control").GroupBy(r => r.Gate!.Run))
        {
            var last = group.MaxBy(r => r.Gate!.Sequence)!;
            if (last.Outcome == "paused")
            {
                if (!audit.Append(new("in", last.Request!, last.Principal, [], "gate.pause", "profile", "invalidated",
                    Gate: last.Gate! with { Operation = Guid.NewGuid(), PauseDeadline = null, ReasonCode = "restart" }))) { audit.ControlFault(); }
            }
        }
    }

    internal object Control(string principal, JsonElement data, Task controlLost, bool resume)
    {
        if (data.ValueKind != JsonValueKind.Object || data.EnumerateObject().Count() != 2
            || !data.TryGetProperty("request", out var q) || q.ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(q.GetString(), "D", out var request) || request == Guid.Empty
            || !data.TryGetProperty("run", out var r) || r.ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(r.GetString(), "D", out var run) || run == Guid.Empty)
        { throw new ArgumentException("gate pause/resume accepts only request and run UUIDs"); }
        lock (sync)
        {
            CheckClock();
            var rows = GateRunPreparation.Read(audit);
            var begin = rows.SingleOrDefault(r => r.Action == "gate.start" && r.Outcome == "prepared" && r.Gate?.Run == run);
            if (begin?.Principal != principal || rows.Any(r => r.Gate?.Run == run && r.Outcome == "invalidated"))
            { throw new InvalidOperationException("gate pause refused: unknown, foreign or invalidated run"); }
            if (resume)
            {
                if (lease is null || marker!.Gate!.Run != run || !End(resumed: true))
                { throw new InvalidOperationException("gate resume refused: pause or audit unavailable"); }
                return new { outcome = "resumed", run, held = true };
            }
            if (lease is not null)
            {
                if (marker!.Gate!.Run != run) { throw new InvalidOperationException("another run is paused"); }
                return Reply();
            }
            marker = new("in", request.ToString(), principal, [], "gate.pause", "profile", "paused",
                Gate: new(1, Guid.NewGuid(), run, 0, "control", "relay", "person-client", Guid.Empty, "none"));
            try
            {
                var now = audit.Clock.GetUtcNow();
                if (now < rows.Where(r => r.Gate?.Phase == "control").Max(r => r.At)) { throw new IOException("uncertain control clock"); }
                lastControl = now;
                marker = marker with { Gate = marker.Gate! with { PauseDeadline = now.AddMinutes(5) } };
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentOutOfRangeException)
            { if (!audit.Append(marker with { Outcome = "invalidated", Gate = marker.Gate! with { ReasonCode = "clock-uncertain" } })) { audit.ControlFault(); } throw; }
            var started = audit.Clock.GetTimestamp();
            lease = audit.Admission.PauseAdmission();
            var mine = lease;
            try
            {
                var left = TimeSpan.FromMinutes(5) - audit.Clock.GetElapsedTime(started);
                timer = audit.Clock.CreateTimer(_ => End(mine, "pause-timeout"), null, left > TimeSpan.Zero ? left : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
                if (lease is null || !audit.Append(marker)) { throw new InvalidOperationException("gate pause refused: audit unavailable"); }
            }
            catch { End(); throw; }
            _ = LostAsync(controlLost, mine);
            CheckClock();
            if (lease is null) { throw new InvalidOperationException("gate pause ended before acknowledgment"); }
            return Reply();
        }
    }

    object Reply() => new { outcome = "paused", run = marker!.Gate!.Run, deadline = marker.Gate.PauseDeadline };
    internal void ValidateClock() { lock (sync) { CheckClock(); } }
    internal Task WaitAsync() { lock (sync) { CheckClock(); return audit.Admission.WaitForAdmissionAsync(); } }
    void CheckClock()
    {
        if (lease is null) { return; }
        try
        {
            var now = audit.Clock.GetUtcNow();
            if (now >= marker!.Gate!.PauseDeadline) { End(reason: "pause-timeout"); return; }
            if (lease.Resumed.IsCompleted) { End(); return; }
            if (now >= lastControl) { return; }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentOutOfRangeException) { }
        End(reason: "clock-uncertain");
    }
    async Task LostAsync(Task lost, DeliveryGate.AdmissionPause mine)
    {
        await Task.WhenAny(lost, mine.Resumed);
        if (lost.IsCompleted) { End(mine, "control-lost"); }
    }
    bool End(DeliveryGate.AdmissionPause? expected = null, string reason = "control-lost", bool resumed = false)
    {
        expected?.Dispose();
        lock (sync)
        {
            if (lease is null || expected is not null && !ReferenceEquals(lease, expected)) { return false; }
            audit.ControlWriterRun = null;
            lease.Dispose();
            lease = null;
            timer?.Dispose();
            timer = null;
            var written = audit.Append(marker! with { Outcome = resumed ? "resumed" : "invalidated",
                Gate = marker!.Gate! with { PauseDeadline = null, ReasonCode = resumed ? "none" : reason } });
            if (!written) { audit.ControlFault(); }
            return written;
        }
    }
    public void Dispose() => End();
}
