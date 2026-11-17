using System.Text.Json;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class LatestTimingTests
{
    [Fact]
    public void Failed_checks_keep_step_section_and_self_test_times_in_the_record()
    {
        var now = TimeSpan.FromSeconds(2);
        var log = new List<string>();
        var timing = new LatestTiming(log.Add, () => now);
        timing.Boundary("latest wheel");
        now = TimeSpan.FromSeconds(5);
        timing.Check(true, "reaches history");
        now = TimeSpan.FromSeconds(9);
        timing.Check(false, "returns to bottom");
        now = TimeSpan.FromSeconds(10);
        timing.Boundary("");

        var record = JsonSerializer.SerializeToElement(timing.Records);
        Assert.Equal(4, record.GetArrayLength());
        var failed = record[2];
        Assert.Equal("check", failed.GetProperty("Kind").GetString());
        Assert.Equal("latest wheel", failed.GetProperty("Section").GetString());
        Assert.Equal("returns to bottom", failed.GetProperty("Name").GetString());
        Assert.False(failed.GetProperty("Passed").GetBoolean());
        Assert.Equal(4, failed.GetProperty("Seconds").GetDouble());
        Assert.Equal(7, failed.GetProperty("SectionSeconds").GetDouble());
        Assert.Equal(9, failed.GetProperty("SelfTestSeconds").GetDouble());
        Assert.Equal("end", record[3].GetProperty("Kind").GetString());
        Assert.Equal(8, record[3].GetProperty("Seconds").GetDouble());
        Assert.Equal(4, log.Count);
        Assert.Contains("passed=False", log[2]);
    }

    [Fact]
    public void Checks_outside_latest_are_ignored_and_a_new_section_resets_its_clock()
    {
        var now = TimeSpan.Zero;
        var timing = new LatestTiming(_ => { }, () => now);
        timing.Check(false, "unrelated");
        timing.Boundary("latest setup");
        now = TimeSpan.FromSeconds(2);
        timing.Boundary("latest return");
        now = TimeSpan.FromSeconds(5);
        timing.Check(true, "bottom");
        timing.Boundary("");
        timing.Check(false, "unrelated again");

        Assert.Equal(new[] { "begin", "end", "begin", "check", "end" }, timing.Records.Select(r => r.Kind));
        Assert.Equal(3, timing.Records[3].Seconds);
        Assert.Equal(3, timing.Records[3].SectionSeconds);
        Assert.Equal(5, timing.Records[3].SelfTestSeconds);
    }
}
