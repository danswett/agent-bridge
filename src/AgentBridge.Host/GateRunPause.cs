using System.Text.Json;
using AgentBridge.Host.Delivery;

namespace AgentBridge.Host;

internal sealed class GateRunPause : IDisposable
{
    readonly AgentAuditLog audit;
    readonly Lock sync = new();
    DeliveryGate.AdmissionPause? lease;
    AgentAuditEntry? marker;
    ITimer? timer;
    DateTimeOffset lastControl;

    internal GateRunPause(AgentAuditLog audit)
    {
        this.audit = audit;
        var (rows, _) = GateAuditProjection.Load(audit.Profile);
        foreach (var group in rows.Where(r => r.Gate is { Phase: "control" }).GroupBy(r => r.Gate!.Run))
        {
            var last = group.MaxBy(r => r.Gate!.Sequence)!;
            if (last.Outcome != "paused") { continue; }
            var entry = new AgentAuditEntry("in", last.Request!, last.Principal, [], "gate.pause", "profile",
                "invalidated", Gate: last.Gate! with { Operation = Guid.NewGuid(), PauseDeadline = null, ReasonCode = "restart" });
            if (!audit.Append(entry)) { audit.ControlFault(); }
        }
    }

    internal object Pause(string principal, JsonElement data, Task controlLost)
    {
        if (data.ValueKind != JsonValueKind.Object || data.EnumerateObject().Count() != 2
            || !data.TryGetProperty("request", out var requestValue) || requestValue.ValueKind != JsonValueKind.String
            || !requestValue.TryGetGuid(out var request) || request == Guid.Empty
            || !data.TryGetProperty("run", out var runValue) || runValue.ValueKind != JsonValueKind.String
            || !runValue.TryGetGuid(out var run) || run == Guid.Empty)
        { throw new ArgumentException("gate.pause accepts only request and run UUIDs"); }
        lock (sync)
        {
            CheckClock();
            long retained = 0;
            var (rows, complete) = GateAuditProjection.Load(audit.Profile, boundary: (floor, _) => retained = floor);
            if (!complete || retained > 0) { throw new InvalidOperationException("gate pause refused: audit unavailable"); }
            var candidate = rows.SingleOrDefault(r => r.Action == "gate.start" && r.Outcome == "pending" && r.Gate?.Run == run);
            if (candidate?.Principal != principal || rows.Any(r => r.Gate?.Run == run && r.Outcome == "invalidated"))
            { throw new InvalidOperationException("gate pause refused: unknown, foreign or invalidated preparation"); }
            if (lease is not null)
            {
                if (marker!.Gate!.Run != run) { throw new InvalidOperationException("another preparation is paused"); }
                return Reply();
            }
            var now = audit.Clock.GetUtcNow();
            if (now < rows.Where(r => r.Gate?.Phase == "control").Max(r => r.At))
            { throw new InvalidOperationException("gate pause refused: uncertain control clock"); }
            var started = audit.Clock.GetTimestamp();
            lease = audit.Admission.PauseAdmission();
            marker = new AgentAuditEntry("in", request.ToString(), principal, [], "gate.pause", "profile", "paused",
                Gate: new(1, Guid.NewGuid(), run, 0, "control", "relay", "person-client", Guid.Empty, "none",
                    PauseDeadline: now.AddMinutes(5)));
            lastControl = now;
            var mine = lease;
            try
            {
                var left = TimeSpan.FromMinutes(5) - audit.Clock.GetElapsedTime(started);
                timer = audit.Clock.CreateTimer(_ => End(mine, "pause-timeout"), null, left > TimeSpan.Zero ? left : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
                if (!audit.Append(marker)) { throw new InvalidOperationException("gate pause refused: audit unavailable"); }
            }
            catch { End(); throw; }
            _ = LostAsync(controlLost, mine);
            return Reply();
        }
    }

    object Reply() => new { outcome = "paused", run = marker!.Gate!.Run, deadline = marker.Gate.PauseDeadline };

    internal void ValidateClock() { lock (sync) { CheckClock(); } }

    internal Task WaitAsync()
    {
        lock (sync) { CheckClock(); return audit.Admission.WaitForAdmissionAsync(); }
    }

    void CheckClock()
    {
        if (lease is null) { return; }
        if (lease.Resumed.IsCompleted) { End(); return; }
        try
        {
            var now = audit.Clock.GetUtcNow();
            if (now >= marker!.Gate!.PauseDeadline) { End(reason: "pause-timeout"); return; }
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

    void End(DeliveryGate.AdmissionPause? expected = null, string reason = "control-lost")
    {
        // Release admission even if the audit writer is blocked or the clock is unreadable.
        expected?.Dispose();
        lock (sync)
        {
            if (lease is null || expected is not null && !ReferenceEquals(lease, expected)) { return; }
            audit.ControlWriterRun = null;
            lease.Dispose();
            lease = null;
            timer?.Dispose();
            timer = null;
            if (!audit.Append(marker! with { Outcome = "invalidated",
                Gate = marker!.Gate! with { PauseDeadline = null, ReasonCode = reason } })) { audit.ControlFault(); }
        }
    }

    public void Dispose() => End();
}
