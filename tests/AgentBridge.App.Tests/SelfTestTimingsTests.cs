using System.Text.Json;
using AgentBridge.App.Core;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class SelfTestTimingsTests
{
    [Fact]
    public void Every_named_check_and_boundary_reports_monotonic_ms_without_changing_existing_results()
    {
        var clock = new MonotonicClock();
        var timing = new SelfTestTimings(clock);
        var result = new Dictionary<string, object?>
        {
            ["passed"] = new[] { "draws rows", "draws rows" },
            ["failed"] = new[] { "answers" }, ["unfinished"] = "contrast", ["drawnCards"] = 4
        };
        var original = JsonSerializer.Serialize(result);
        timing.Boundary("sessions");
        clock.Now = 1250;
        timing.Check("draws rows");
        clock.Now = 1500;
        timing.Check("draws rows");
        timing.Boundary("contrast");
        clock.Now = 2000;
        timing.Check("answers");
        timing.Finish();
        clock.Now = 9000;
        timing.AddTo(result);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        var measured = json.RootElement.GetProperty("timings");
        var checks = measured.GetProperty("checks").EnumerateArray().ToArray();
        Assert.Equal(new[] { "draws rows", "draws rows", "answers" },
            checks.Select(c => c.GetProperty("name").GetString()));
        Assert.Equal(new[] { 1250d, 250d, 500d }, checks.Select(c => c.GetProperty("ms").GetDouble()));
        var stages = measured.GetProperty("stages").EnumerateArray().ToArray();
        Assert.Equal(new[] { "sessions", "contrast" }, stages.Select(s => s.GetProperty("name").GetString()));
        Assert.Equal(new[] { 1500d, 500d }, stages.Select(s => s.GetProperty("ms").GetDouble()));
        Assert.Equal(2000, measured.GetProperty("totalMs").GetDouble());
        result.Remove("timings");
        Assert.Equal(original, JsonSerializer.Serialize(result));
    }

    [Fact]
    public void A_partial_result_includes_the_active_stage_without_resetting_its_duration()
    {
        var clock = new MonotonicClock();
        var timing = new SelfTestTimings(clock);
        var result = new Dictionary<string, object?>();
        timing.Boundary("unfinished stage");
        clock.Now = 500;
        timing.AddTo(result);
        Assert.Contains("\"ms\":500", JsonSerializer.Serialize(result));
        clock.Now = 1200;
        timing.Finish();
        timing.AddTo(result);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        var stages = json.RootElement.GetProperty("timings").GetProperty("stages");
        Assert.Equal(1, stages.GetArrayLength());
        Assert.Equal(1200, stages[0].GetProperty("ms").GetDouble());
    }

    sealed class MonotonicClock : TimeProvider
    {
        public long Now { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("Wall clock is not a duration clock.");
    }
}
