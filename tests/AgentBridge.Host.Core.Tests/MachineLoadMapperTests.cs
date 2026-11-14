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
    [InlineData(3L, 2L, 100d / 3d)]
    [InlineData(8L, 3L, 62.5)]
    [InlineData(8L, 8L, 0d)]
    [InlineData(8L, 0L, 100d)]
    public void Physical_used_is_serialized_from_the_same_bytes_without_rounding(long total, long available, double expected)
    {
        var record = MachineLoadMapper.Map(Report(new(4, 25.5, available, TotalMemoryBytes: total)), Now, Limit);
        var json = JsonSerializer.SerializeToElement(record, ProtocolJson.Options);
        var memory = json.GetProperty("memory");
        Assert.Equal(total, memory.GetProperty("totalBytes").GetInt64());
        Assert.Equal(available, memory.GetProperty("availableBytes").GetInt64());
        Assert.False(memory.TryGetProperty("committedPercent", out _));
        Assert.True(memory.TryGetProperty("usedPercent", out var used), "Valid physical-memory bytes must emit memory.usedPercent.");
        Assert.Equal(expected, used.GetDouble(), 12);
    }

    [Fact]
    public void Physical_used_and_committed_percentages_are_independent_wire_figures()
    {
        var mapped = MachineLoadMapper.Map(Report(new(4, 25.5, 2, TotalMemoryBytes: 3)), Now, Limit);
        // The sampler has no committed figure; seed only the existing wire member.
        var record = mapped with { Memory = mapped.Memory! with { CommittedPercent = 87.5 } };
        var json = JsonSerializer.SerializeToElement(record, ProtocolJson.Options);
        var memory = json.GetProperty("memory");
        Assert.Equal(87.5, memory.GetProperty("committedPercent").GetDouble());
        Assert.True(memory.TryGetProperty("usedPercent", out var used), "Physical used must remain distinct from committedPercent.");
        Assert.Equal(100d / 3d, used.GetDouble(), 12);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, 0L)]
    [InlineData(8L, null)]
    [InlineData(0L, 0L)]
    [InlineData(0L, 1L)]
    [InlineData(8L, 9L)]
    [InlineData(-1L, 0L)]
    [InlineData(8L, -1L)]
    [InlineData(9007199254740992L, 0L)]
    [InlineData(8L, 9007199254740992L)]
    public void Missing_or_invalid_memory_bytes_omit_physical_used_instead_of_inventing_zero(long? total, long? available)
    {
        var record = MachineLoadMapper.Map(Report(new(4, 25.5, available, TotalMemoryBytes: total)), Now, Limit);
        var json = JsonSerializer.SerializeToElement(record, ProtocolJson.Options);
        if (json.TryGetProperty("memory", out var memory))
            Assert.False(memory.TryGetProperty("usedPercent", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_supported_reader_accepts_old_and_additive_memory_payloads(bool includeUsedPercent)
    {
        var record = MachineLoadMapper.Map(Report(), Now, Limit);
        var payload = JsonSerializer.SerializeToNode(record, ProtocolJson.Options)!;
        var memory = payload["memory"]!.AsObject();
        memory.Remove("usedPercent");
        memory["committedPercent"] = 87.5;
        if (includeUsedPercent) { memory["usedPercent"] = 60d; }

        var read = Received.Read<AgentBridge.Protocol.MachineLoad>(JsonSerializer.SerializeToElement(payload, ProtocolJson.Options));
        Assert.Equal(MachineLoadState.Fresh, read.State);
        Assert.Equal(Now.AddSeconds(-2), read.SampledAt);
        Assert.Equal(2000, read.AgeMs);
        Assert.Equal(15000, read.StaleAfterMs);
        Assert.Equal(new MachineLoadCpu(25.5, 4), read.Cpu);
        Assert.Equal(1000, read.Memory!.TotalBytes);
        Assert.Equal(400, read.Memory.AvailableBytes);
        Assert.Equal(87.5, read.Memory.CommittedPercent);
        Assert.Empty(read.Sessions);
        if (!includeUsedPercent)
        {
            var json = JsonSerializer.SerializeToElement(read, ProtocolJson.Options);
            Assert.False(json.GetProperty("memory").TryGetProperty("usedPercent", out _));
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
