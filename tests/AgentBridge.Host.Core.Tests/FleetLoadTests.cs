using System.Text.Json;
using AgentBridge.Host.Core.Load;
using Xunit;

// #1587: the fleet load record (v1) and the limits file the fleet judges it against.
public class FleetLoadTests
{
    static readonly DateTimeOffset At = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    static string LimitsFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgentBridge.slnx"))) { return Path.Combine(dir.FullName, "docs", "fleet-load-limits.json"); }
        }
        throw new FileNotFoundException("repository root not found");
    }

    [Fact]
    public void An_unknown_machine_reading_is_null_in_the_record_never_zero()
    {
        var report = new ProcessLoadReport(At, new MachineLoad(8, null, null), new Dictionary<string, SessionLoad>());

        var r = FleetLoadRecord.From(report, "M", sessions: null);

        Assert.Null(r.CpuPercent);
        Assert.Null(r.CpuPeakPercent);
        Assert.Null(r.AvailableMemoryMiB);
        Assert.Null(r.Sessions);
        Assert.Null(r.RunQueue);
        Assert.Null(r.RunQueueAvg);
        Assert.Null(r.RunQueueMax);
        Assert.Equal(At, r.ObservedAt);
    }

    [Fact]
    public void The_record_has_exactly_the_v1_properties_and_nothing_per_session()
    {
        var names = typeof(FleetLoadRecord).GetProperties().Select(p => p.Name).Where(n => n != "EqualityContract").Order().ToArray();

        Assert.Equal(new[]
        {
            "AvailableMemoryMiB", "Cores", "CpuPeakPercent", "CpuPercent", "Machine", "ObservedAt", "RunQueue", "RunQueueAvg", "RunQueueMax", "Sessions", "Version",
        }, names);
        Assert.Equal(11, FleetLoadRecord.Keys.Count);
    }

    [Fact]
    public void The_limits_file_is_valid_and_holds_the_agreed_values()
    {
        var limits = FleetLoadLimits.Parse(File.ReadAllText(LimitsFile()));

        Assert.Equal(new FleetLoadLimits(75, 9830, 2, 30, true), limits);
    }

    [Theory]
    [InlineData("maxCpuPercent", "101")]
    [InlineData("maxCpuPercent", "0")]
    [InlineData("minAvailableMemoryMiB", "0")]
    [InlineData("maxAgeSeconds", "0")]
    [InlineData("maxRunQueuePerCore", "-1")]
    [InlineData("maxAgeSeconds", "\"30\"")]
    [InlineData("nullFails", "1")]
    public void The_limits_schema_rejects_out_of_range_and_mistyped_values(string field, string value)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(LimitsFile()))!.AsObject();
        node[field] = System.Text.Json.Nodes.JsonNode.Parse(value);

        Assert.Throws<FormatException>(() => FleetLoadLimits.Parse(node.ToJsonString()));
    }

    [Fact]
    public void A_limits_file_missing_a_field_is_rejected()
    {
        Assert.Throws<FormatException>(() => FleetLoadLimits.Parse("""{"maxCpuPercent":75}"""));
    }
}
