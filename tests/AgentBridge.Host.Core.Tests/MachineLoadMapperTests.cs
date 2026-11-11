using System.Text.Json;
using AgentBridge.Host.Core.Load;
using AgentBridge.Protocol;
using Xunit;
using LocalMachine = AgentBridge.Host.Core.Load.MachineLoad;
using LocalSession = AgentBridge.Host.Core.Load.SessionLoad;

public class MachineLoadMapperTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);
    static ProcessLoadReport Report(LocalMachine? machine = null, Dictionary<string, LocalSession>? sessions = null) =>
        new(Now.AddSeconds(-2), machine ?? new(4, 25.5, 400, TotalMemoryBytes: 1000), sessions ?? []);

    [Fact]
    public void The_mapper_preserves_measured_numbers_and_emits_only_approved_strings()
    {
        var record = MachineLoadMapper.Map(Report(sessions: new() { ["session-1"] = new(LoadState.Ok, 3, 12.5, 200) }), Now, Limit);
        Assert.Equal(MachineLoadState.Fresh, record.State);
        Assert.Equal(Now.AddSeconds(-2), record.SampledAt);
        Assert.Equal(2000, record.AgeMs);
        Assert.Equal(15000, record.StaleAfterMs);
        Assert.Equal(new MachineLoadCpu(25.5, 4), record.Cpu);
        Assert.Equal(new MachineLoadMemory(1000, 400), record.Memory);
        var session = Assert.Single(record.Sessions);
        Assert.Equal(("session-1", LoadAgentKind.Other, 3, 12.5, 200L, SessionLoadState.Fresh),
            (session.Session, session.Agent, session.Processes, session.CpuPercent, session.WorkingSetBytes, session.State));
        Assert.Null(session.Handles);
        Assert.Null(session.HandleKind);
        Assert.Null(record.Unattributed);
        Check(JsonSerializer.SerializeToElement(record, ProtocolJson.Options));

        static void Check(JsonElement value, string? field = null)
        {
            if (value.ValueKind == JsonValueKind.Object)
                foreach (var property in value.EnumerateObject()) { Check(property.Value, property.Name); }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) { Check(item); }
            else if (value.ValueKind == JsonValueKind.String)
                Assert.Contains(field, new[] { "session", "agent", "state", "handleKind", "sampledAt" });
        }
    }

    [Theory]
    [InlineData(0, MachineLoadState.Fresh)]
    [InlineData(15, MachineLoadState.Fresh)]
    [InlineData(16, MachineLoadState.Stale)]
    [InlineData(-1, MachineLoadState.Unavailable)]
    public void Freshness_comes_from_the_host_clock_not_a_client_guess(int seconds, MachineLoadState state)
    {
        var record = MachineLoadMapper.Map(Report() with { SampledAt = Now.AddSeconds(-seconds) }, Now, Limit);
        Assert.Equal(state, record.State);
        Assert.Equal(seconds < 0 ? (long?)null : seconds * 1000L, record.AgeMs);
    }

    [Fact]
    public void An_absent_report_and_a_first_sample_do_not_invent_zeroes()
    {
        var absent = MachineLoadMapper.Map(null, Now, Limit);
        Assert.Equal(MachineLoadState.Unavailable, absent.State);
        Assert.Null(absent.SampledAt);
        Assert.Null(absent.AgeMs);
        Assert.Null(absent.Cpu);
        Assert.Null(absent.Memory);
        var first = MachineLoadMapper.Map(Report(new(4, null, 0)), Now, Limit);
        Assert.Equal(MachineLoadState.Partial, first.State);
        var json = JsonSerializer.SerializeToElement(first, ProtocolJson.Options);
        Assert.False(json.TryGetProperty("cpu", out _));
        Assert.False(json.GetProperty("memory").TryGetProperty("totalBytes", out _));
        Assert.Equal(0, first.Memory!.AvailableBytes);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_figures_are_omitted_and_do_not_claim_freshness(double invalid)
    {
        var record = MachineLoadMapper.Map(Report(new(0, invalid, -1, TotalMemoryBytes: 9007199254740992),
            new() { ["s"] = new(LoadState.Ok, -1, invalid, -1) }), Now, Limit);
        Assert.Equal(MachineLoadState.Unavailable, record.State);
        Assert.Null(record.Cpu);
        Assert.Null(record.Memory);
        var session = Assert.Single(record.Sessions);
        Assert.Equal(SessionLoadState.Unavailable, session.State);
        Assert.Null(session.Processes);
        Assert.Null(session.CpuPercent);
        Assert.Null(session.WorkingSetBytes);
    }

    [Theory]
    [InlineData(0, MachineLoadState.Partial)]
    [InlineData(16, MachineLoadState.Stale)]
    public void The_wire_cap_keeps_largest_working_sets_and_never_promotes_stale_data(int seconds, MachineLoadState state)
    {
        var sessions = Enumerable.Range(0, 201).ToDictionary(i => $"s-{i}-{new string('\u0001', 100)}", i => new LocalSession(LoadState.Ok, 1, 0, i));
        var record = MachineLoadMapper.Map(Report(sessions: sessions) with { SampledAt = Now.AddSeconds(-seconds) }, Now, Limit);
        Assert.Equal(state, record.State);
        Assert.InRange(ProtocolJson.ByteCount(record), 1, 64 * 1024);
        Assert.InRange(record.Sessions.Count, 1, 199);
        Assert.Equal(200, record.Sessions[0].WorkingSetBytes);
        Assert.True(record.Sessions.SequenceEqual(record.Sessions.OrderByDescending(s => s.WorkingSetBytes)));
    }
}
