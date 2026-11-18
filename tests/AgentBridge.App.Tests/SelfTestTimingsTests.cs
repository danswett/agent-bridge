using System.Text.Json;
using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.App.Tests;

public class SelfTestTimingsTests
{
    [Fact]
    public void Every_check_and_stage_has_monotonic_time_without_changing_existing_results()
    {
        var clock = new FakeTimeProvider();
        var timings = new SelfTestTimings(clock);
        var result = new Dictionary<string, object?>
        {
            ["passed"] = new[] { "same name", "same name" },
            ["failed"] = new[] { "failed check" }, ["unfinished"] = "next stage"
        };
        var original = result.ToDictionary(p => p.Key, p => JsonSerializer.Serialize(p.Value));
        timings.Boundary("first stage");
        clock.Advance(TimeSpan.FromMilliseconds(25));
        timings.Check("same name");
        clock.Advance(TimeSpan.FromMilliseconds(5));
        timings.Check("same name");
        timings.Boundary("next stage");
        clock.Advance(TimeSpan.FromMilliseconds(10));
        timings.Check("failed check");
        timings.WriteTo(result);
        using var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(result));
        var root = snapshot.RootElement;
        Assert.Equal(new[] { "same name", "same name", "failed check" },
            root.GetProperty("checkDurations").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.Equal(new[] { 25d, 5d, 10d },
            root.GetProperty("checkDurations").EnumerateArray().Select(c => c.GetProperty("durationMs").GetDouble()));
        Assert.Equal(new[] { 30d, 10d },
            root.GetProperty("stageDurations").EnumerateArray().Select(c => c.GetProperty("durationMs").GetDouble()));
        Assert.Equal(new[] { "first stage", "next stage" },
            root.GetProperty("stageDurations").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        timings.Boundary(null);
        timings.WriteTo(result);
        Assert.Equal(40d, result["durationMs"]);
        foreach (var (key, value) in original)
            Assert.Equal(value, JsonSerializer.Serialize(result[key]));
        Assert.Equal(10, root.GetProperty("stageDurations")[1].GetProperty("durationMs").GetDouble());
    }
}
