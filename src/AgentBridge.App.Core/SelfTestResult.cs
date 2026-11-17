using System.Text.Json;

namespace AgentBridge.App.Core;

public sealed class SelfTestResult(TimeProvider? time = null)
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    readonly List<Stage> stages = [];
    sealed class Stage(string name, DateTimeOffset now, long stamp)
    {
        public string Name = name;
        public DateTimeOffset StartedAt = now, LastProgressAt = now;
        public DateTimeOffset? EndedAt;
        public long Stamp = stamp;
        public double? Elapsed;
        public List<(string Step, DateTimeOffset At, double Seconds)> Steps = [];
    }

    public async Task WriteAsync(string file, Dictionary<string, object?> result, List<string> checks,
        List<string> failures, string? unfinished, string? step = null)
    {
        var now = clock.GetUtcNow();
        double Elapsed(Stage s) => s.Elapsed ?? clock.GetElapsedTime(s.Stamp).TotalSeconds;
        var active = stages.LastOrDefault(s => s.EndedAt is null);
        if (active is not null && active.Name != unfinished)
        {
            active.Elapsed = Elapsed(active);
            active.EndedAt = active.LastProgressAt = now;
            active = null;
        }
        if (unfinished is not null && active is null)
        {
            active = new Stage(unfinished, now, clock.GetTimestamp());
            stages.Add(active);
        }
        if (active is not null && step is not null)
        {
            active.LastProgressAt = now;
            active.Steps.Add((step, now, Elapsed(active)));
        }
        result["passed"] = checks;
        result["failed"] = failures;
        result["unfinished"] = unfinished;
        result["stages"] = stages.Select(s => new
        {
            name = s.Name, startedAt = s.StartedAt, endedAt = s.EndedAt,
            lastProgressAt = s.LastProgressAt, elapsedSeconds = Elapsed(s),
            step = s.Steps.LastOrDefault().Step,
            steps = s.Steps.Select((m, i) => new
            {
                step = m.Step, startedAt = m.At,
                endedAt = i + 1 < s.Steps.Count ? s.Steps[i + 1].At : s.EndedAt,
                elapsedSeconds = (i + 1 < s.Steps.Count ? s.Steps[i + 1].Seconds : Elapsed(s)) - m.Seconds,
            }),
        });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        // A guard kill during a checkpoint must leave the previous complete snapshot readable.
        await File.WriteAllTextAsync(file + ".tmp", JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(file + ".tmp", file, overwrite: true);
    }
}
