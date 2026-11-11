using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.Protocol;
using Xunit;

public class MachineLoadTests
{
    [Theory]
    [InlineData(MachineLoadState.Fresh)]
    [InlineData(MachineLoadState.Partial)]
    [InlineData(MachineLoadState.Stale)]
    [InlineData(MachineLoadState.Unavailable)]
    public void Load_round_trips_without_inventing_missing_figures(MachineLoadState state)
    {
        var load = new MachineLoad { State = state };
        var json = JsonSerializer.SerializeToElement(load, ProtocolJson.Options);
        var read = Received.Read<MachineLoad>(json);
        Assert.Equal(state, read.State);
        Assert.Null(read.Cpu);
        Assert.Null(read.Memory);
        Assert.Null(read.AgeMs);
        Assert.Null(read.StaleAfterMs);
        Assert.Null(read.SampledAt);
        Assert.Empty(read.Sessions);
        Assert.False(json.TryGetProperty("cpu", out _));
        Assert.False(json.TryGetProperty("memory", out _));
    }

    [Fact]
    public void Unknown_load_enums_read_as_unavailable_or_other()
    {
        using var doc = JsonDocument.Parse("""{"state":"future","sessions":[{"session":"s-1","agent":"future","state":"future","handleKind":"future"}]}""");
        var read = Received.Read<MachineLoad>(doc.RootElement);
        Assert.Equal(MachineLoadState.Unavailable, read.State);
        var session = Assert.Single(read.Sessions);
        Assert.Equal(SessionLoadState.Unavailable, session.State);
        Assert.Equal(LoadAgentKind.Other, session.Agent);
        Assert.Equal(LoadHandleKind.Other, session.HandleKind);
        Assert.Null(session.CpuPercent);
        Assert.Null(session.WorkingSetBytes);
    }

    [Fact]
    public void Empty_counter_records_do_not_turn_missing_figures_into_zero()
    {
        using var doc = JsonDocument.Parse("""{"cpu":{},"memory":{},"unattributed":{},"sessions":[{"session":"s-1"}]}""");
        var read = Received.Read<MachineLoad>(doc.RootElement);
        Assert.Null(read.Cpu!.Percent);
        Assert.Null(read.Cpu.Cores);
        Assert.Null(read.Memory!.TotalBytes);
        Assert.Null(read.Memory.AvailableBytes);
        Assert.Null(read.Unattributed!.Processes);
        Assert.Null(Assert.Single(read.Sessions).Processes);
    }

    [Fact]
    public void Over_200_sessions_read_as_partial_keeping_the_largest_working_sets()
    {
        var sessions = Enumerable.Range(0, 201).Select(i => new SessionLoad { Session = $"s-{i}", WorkingSetBytes = i }).ToArray();
        var json = JsonSerializer.SerializeToElement(new { state = "fresh", sessions }, ProtocolJson.Options);
        var read = Received.Read<MachineLoad>(json);
        Assert.Equal(MachineLoadState.Partial, read.State);
        Assert.Equal(200, read.Sessions.Count);
        Assert.Equal("s-200", read.Sessions[0].Session);
        Assert.DoesNotContain(read.Sessions, s => s.Session == "s-0");
    }

    [Fact]
    public void Serialized_load_vectors_have_only_approved_strings_and_utc_timestamps()
    {
        var vectors = ProtocolVectors.Files()["machine-load.json"]["records"]!["MachineLoad"]!["valid"]!.AsArray();
        foreach (var vector in vectors)
        {
            var read = Received.Read<MachineLoad>(JsonSerializer.SerializeToElement(vector!["json"]));
            Check(JsonSerializer.SerializeToElement(read, ProtocolJson.Options));
        }

        static void Check(JsonElement value, string? field = null)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject()) { Check(property.Value, property.Name); }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in value.EnumerateArray()) { Check(entry); }
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                Assert.Contains(field, new[] { "session", "agent", "state", "handleKind", "sampledAt" });
                if (field == "sampledAt") { Assert.Matches(new Regex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$"), value.GetString()!); }
            }
        }
    }
}
