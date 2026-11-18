using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Relay;
using Xunit;

namespace AgentBridge.Transport.Tests;

public sealed class RegistryContractTests
{
    static JsonObject Record(int count = 1) => new()
    {
        ["v"] = 1, ["machine"] = "machine", ["epoch"] = "0123456789abcdef", ["seq"] = 1,
        ["at"] = "2026-11-17T20:00:00Z", ["truncated"] = false,
        ["sessions"] = new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject
        {
            ["id"] = i.ToString("x16"), ["node"] = "agent_bridge_" + i.ToString("x16"),
            ["state"] = "working", ["since"] = "2026-11-17T19:00:00Z",
        }).ToArray()),
    };

    static string? Check(JsonObject record) =>
        RosterFields.Check(JsonSerializer.SerializeToElement(record), "machine", out _, out _, out _);

    [Fact]
    public void An_unpaired_surrogate_role_is_a_bad_record_and_preserves_the_existing_registry()
    {
        var record = JsonSerializer.SerializeToElement(Record());
        var registry = new SessionRegistry(TimeProvider.System);
        Assert.Null(registry.Put("account", "machine", record));
        using var malformed = JsonDocument.Parse(record.GetRawText().Replace("\"state\":\"working\"", "\"state\":\"working\",\"role\":\"\\ud800\"", StringComparison.Ordinal));
        Assert.Equal("bad-record", registry.Put("account", "machine", malformed.RootElement));
        Assert.Equal(record.GetRawText(), Assert.Single(registry.Read("account")).Json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2026-11-17T18:00:00Z")]
    [InlineData("2024-02-29T23:59:59Z")]
    public void Legacy_sessions_and_optional_UTC_whole_second_process_starts_are_accepted(string? startedAt)
    {
        var record = Record();
        if (startedAt is not null) { record["sessions"]![0]!["startedAt"] = startedAt; }
        Assert.Null(Check(record));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"2026-11-17T18:00:00+00:00\"")]
    [InlineData("\"2026-11-17T18:00:00.001Z\"")]
    [InlineData("\"2026-11-17T18:00:00z\"")]
    [InlineData("\"2026-02-29T18:00:00Z\"")]
    [InlineData("\"2026-11-17\"")]
    public void Process_starts_refuse_null_wrong_types_offsets_fractions_and_noncalendar_values(string raw)
    {
        var record = Record();
        record["sessions"]![0]!["startedAt"] = JsonNode.Parse(raw);
        Assert.Equal("bad-record", Check(record));
    }

    [Theory]
    [InlineData(59, 64)]
    [InlineData(64, 65)]
    [InlineData(0, 1)]
    public void Truncated_totals_count_all_usable_sessions_even_below_the_count_cap(int published, int total)
    {
        var record = Record(published);
        record["truncated"] = true;
        record["total"] = total;
        Assert.Null(Check(record));
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(2, 1, true)]
    [InlineData(1, 2, false)]
    public void Totals_must_exceed_the_published_count_and_only_describe_truncated_records(int published, int total, bool truncated)
    {
        var record = Record(published);
        record["truncated"] = truncated;
        record["total"] = total;
        Assert.Equal("bad-record", Check(record));
    }
}
