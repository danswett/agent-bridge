using System.Text;
using System.Text.Json;
using AgentBridge.Protocol;
using Xunit;

public sealed class ReleaseHintContractTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-12T06:00:00Z");
    const string Valid = """{"t":"release.hint","tag":"v0.1.123","channel":"win","seenAt":"2026-11-12T06:00:00Z"}""";

    // Locate the new reader without making the tests-first head fail to compile.
    static Type Reader => Assert.IsAssignableFrom<Type>(typeof(UpdateState).Assembly.GetType("AgentBridge.Protocol.ReleaseHints"));

    [Theory]
    [InlineData("win")]
    [InlineData("win-arm64")]
    public void A_valid_hint_reads_without_trusting_or_adding_a_download_url(string channel)
    {
        var reader = Activator.CreateInstance(Reader)!;
        var log = new List<string>();
        var result = Reader.GetMethod("Read")!.Invoke(reader, [Encoding.UTF8.GetBytes(Valid.Replace("win", channel)), Now, (Action<string>)log.Add]);
        var json = JsonSerializer.SerializeToElement(result, ProtocolJson.Options);
        Assert.Equal("v0.1.123", json.GetProperty("tag").GetString());
        Assert.Equal(channel, json.GetProperty("channel").GetString());
        Assert.False(json.TryGetProperty("url", out _));
        Assert.Empty(log);
        Assert.Equal(0L, Reader.GetProperty("Dropped")!.GetValue(reader));
    }

    [Theory]
    [InlineData("v0.1.123", "v0.1.1x", "tag")]
    [InlineData("v0.1.123", "v0.1.01", "tag")]
    [InlineData("v0.1.123", "v0.1.9223372036854775808", "tag")]
    [InlineData("win", "secret-channel", "channel")]
    [InlineData("06:00:00Z", "06:05:01Z", "seenAt")]
    [InlineData("06:00:00Z", "06:00:00+01:00", "seenAt")]
    [InlineData("06:00:00Z", "06:00:00", "seenAt")]
    [InlineData("\"tag\":\"v0.1.123\"", "\"tag\":42", "tag")]
    [InlineData("\"t\":\"release.hint\"", "\"t\":\"secret-frame\"", "t")]
    [InlineData("\"channel\":\"win\"", "\"channel\":\"win\",\"channel\":\"win\"", "channel")]
    [InlineData("{", "{\"secret-field\":\"secret-value\",", "frame")]
    [InlineData("}", "", "frame")]
    public void Bad_hints_are_dropped_counted_and_log_only_the_field(string before, string after, string field)
    {
        var reader = Activator.CreateInstance(Reader)!;
        var log = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            Assert.Null(Reader.GetMethod("Read")!.Invoke(reader, [Encoding.UTF8.GetBytes(Valid.Replace(before, after)), Now, (Action<string>)log.Add]));
        }
        Assert.Equal(2L, Reader.GetProperty("Dropped")!.GetValue(reader));
        Assert.Equal([field, field], log);
    }

    [Theory]
    [InlineData(255, true)]
    [InlineData(256, false)]
    [InlineData(300, false)]
    public void The_limit_counts_the_whole_frame_in_bytes_before_parsing(int size, bool accepted)
    {
        var reader = Activator.CreateInstance(Reader)!;
        var log = new List<string>();
        var result = Reader.GetMethod("Read")!.Invoke(reader, [Encoding.UTF8.GetBytes(Valid.PadRight(size)), Now, (Action<string>)log.Add]);
        Assert.Equal(accepted, result is not null);
        Assert.Equal(accepted ? 0L : 1L, Reader.GetProperty("Dropped")!.GetValue(reader));
        Assert.Equal(accepted ? Array.Empty<string>() : ["frame"], log);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("""{"releaseHint":true}""", true)]
    [InlineData("""{"releaseHint":false}""", false)]
    [InlineData("""{"releaseHint":"true"}""", false)]
    public void The_hello_capability_is_optional_and_only_literal_true_opts_in(string hello, bool capable) =>
        Assert.Equal(capable, Reader.GetMethod("IsCapable")!.Invoke(null, [JsonSerializer.Deserialize<JsonElement>(hello)]));

    [Theory]
    [InlineData("""{"now":true,"release":"0.1.123"}""")]
    [InlineData("""{"now":true,"release":"0.1.123","batch":true,"unchecked":false}""")]
    [InlineData("""{"now":true,"release":"0.1.123","batch":false,"unchecked":true}""")]
    public void Optional_signed_flags_round_trip_without_changing_old_args(string json)
    {
        var args = JsonSerializer.Deserialize<UpdateNowArgs>(json, ProtocolJson.Options)!;
        Assert.Equal(json, JsonSerializer.Serialize(args, ProtocolJson.Options));
    }
}
