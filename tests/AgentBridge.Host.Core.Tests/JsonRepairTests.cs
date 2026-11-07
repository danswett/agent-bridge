using System.Text;
using System.Text.Json;
using AgentBridge.Host.Core;
using Xunit;

// Json.Repaired: half a surrogate pair escaped in a record is read as the replacement character,
// in the same bytes, and everything else is left as written (found in review of #292).
public class JsonRepairTests
{
    static string Repaired(string line) => Encoding.UTF8.GetString(Json.Repaired(Encoding.UTF8.GetBytes(line)).Span);

    [Theory]
    [InlineData("{\"a\":\"x\\uD800\"}", "{\"a\":\"x\\uFFFD\"}")]
    [InlineData("{\"a\":\"\\ud83d end\"}", "{\"a\":\"\\uFFFD end\"}")]
    [InlineData("{\"a\":\"\\uDC00\"}", "{\"a\":\"\\uFFFD\"}")]
    [InlineData("{\"a\":\"\\uD800\\uD800\\uDC00\"}", "{\"a\":\"\\uFFFD\\uD800\\uDC00\"}")]
    [InlineData("{\"\\uDBFF\":1}", "{\"\\uFFFD\":1}")]
    [InlineData("{\"a\":\"\\uD83D\\uDE00\"}", "{\"a\":\"\\uD83D\\uDE00\"}")]
    [InlineData("{\"a\":\"\\\\uD800\"}", "{\"a\":\"\\\\uD800\"}")]
    [InlineData("{\"a\":\"\\\\\\uD800\"}", "{\"a\":\"\\\\\\uFFFD\"}")]
    [InlineData("{\"a\":\"\\u0041\\uD8\"}", "{\"a\":\"\\u0041\\uD8\"}")]
    public void Half_a_surrogate_pair_becomes_the_replacement_character_and_nothing_else_changes(string line, string expected)
    {
        Assert.Equal(expected, Repaired(line));
        Assert.Equal(line.Length, expected.Length);
    }

    [Fact]
    public void A_line_with_nothing_to_repair_is_returned_as_it_is()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"a\":\"\\uD83D\\uDE00\"}");
        Assert.True(Json.Repaired(bytes).Span == bytes.AsSpan());
    }

    [Fact]
    public void A_repaired_record_reads_and_so_does_one_holding_bytes_that_are_not_utf8()
    {
        using var doc = JsonDocument.Parse(Json.Repaired(Encoding.UTF8.GetBytes("{\"a\":\"x\\uD800\"}")));
        Assert.Equal("x\uFFFD", doc.RootElement.GetProperty("a").GetString());

        // A raw surrogate, and a character cut short: the parser takes them, and reading them throws.
        byte[] raw = [.. "{\"a\":\"x"u8, 0xED, 0xA0, 0x80, (byte)' ', 0xF0, 0x9F, .. "\"}"u8];
        using (var unrepaired = JsonDocument.Parse(raw))
        {
            Assert.Throws<InvalidOperationException>(() => unrepaired.RootElement.GetProperty("a").GetString());
        }
        using var repaired = JsonDocument.Parse(Json.Repaired(raw));
        Assert.Equal("x\uFFFD\uFFFD\uFFFD \uFFFD", repaired.RootElement.GetProperty("a").GetString());
    }
}
