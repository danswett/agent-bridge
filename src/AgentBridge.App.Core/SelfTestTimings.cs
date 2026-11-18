using System.Text.Json.Serialization;

namespace AgentBridge.App.Core;

public sealed class SelfTestTimings
{
    readonly TimeProvider clock;
    readonly long started;
    long lastCheck, lastStage;
    string? stage;
    readonly List<Duration> checks = [];
    readonly List<Duration> stages = [];

    public SelfTestTimings(TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        started = lastCheck = lastStage = this.clock.GetTimestamp();
    }

    // Checks are completion markers: attribute work/waits since the preceding check,
    // not just the time spent recording its already-evaluated boolean.
    public void Check(string name)
    {
        var now = clock.GetTimestamp();
        checks.Add(new(name, Milliseconds(lastCheck, now)));
        lastCheck = now;
    }

    public void Boundary(string? name)
    {
        var now = clock.GetTimestamp();
        if (stage is not null) stages.Add(new(stage, Milliseconds(lastStage, now)));
        stage = name;
        lastStage = now;
    }

    public void WriteTo(Dictionary<string, object?> result)
    {
        var now = clock.GetTimestamp();
        result["durationMs"] = Milliseconds(started, now);
        result["checkDurations"] = checks.ToArray();
        result["stageDurations"] = stage is null ? stages.ToArray()
            : [.. stages, new Duration(stage, Milliseconds(lastStage, now))];
    }

    double Milliseconds(long from, long to) => clock.GetElapsedTime(from, to).TotalMilliseconds;

    sealed record Duration(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("durationMs")] double DurationMs);
}
