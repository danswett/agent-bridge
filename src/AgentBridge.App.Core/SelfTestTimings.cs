using System.Text.Json.Serialization;

namespace AgentBridge.App.Core;

public sealed class SelfTestTimings
{
    readonly TimeProvider clock;
    readonly long started;
    readonly List<SelfTestTiming> checks = [];
    readonly List<SelfTestTiming> stages = [];
    long lastCheck;
    long stageStart;
    string? stage;
    long? finished;

    public SelfTestTimings(TimeProvider? time = null)
    {
        clock = time ?? TimeProvider.System;
        started = lastCheck = stageStart = clock.GetTimestamp();
    }

    // Checks are synchronous assertions: attribute the work/waits since the preceding check.
    public void Check(string name)
    {
        var now = clock.GetTimestamp();
        checks.Add(new(name, Milliseconds(lastCheck, now)));
        lastCheck = now;
    }

    public void Boundary(string name)
    {
        var now = clock.GetTimestamp();
        CloseStage(now);
        stage = name;
        stageStart = now;
    }

    public void Finish()
    {
        finished = clock.GetTimestamp();
        CloseStage(finished.Value);
        stage = null;
    }

    public void AddTo(Dictionary<string, object?> result)
    {
        var now = finished ?? clock.GetTimestamp();
        result["timings"] = new
        {
            checks = checks.ToArray(),
            stages = stage is null ? stages.ToArray() : [.. stages, new SelfTestTiming(stage, Milliseconds(stageStart, now))],
            totalMs = Milliseconds(started, now)
        };
    }

    void CloseStage(long now)
    {
        if (stage is not null) { stages.Add(new(stage, Milliseconds(stageStart, now))); }
    }

    double Milliseconds(long from, long to) => clock.GetElapsedTime(from, to).TotalMilliseconds;
}

public sealed record SelfTestTiming(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("ms")] double Ms);
