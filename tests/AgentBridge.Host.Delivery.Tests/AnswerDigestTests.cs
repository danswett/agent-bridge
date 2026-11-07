using System.Text.Json;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using Xunit;

// What an answer says, as the digest that tells the same answer sent again from a different one
// (#1093): numbers compared exactly, as the Copilot planner compares them, never through decimal.
public sealed class AnswerDigestTests
{
    static AnswerRequest Request(params (string Name, string Json)[] fields) => new()
    {
        Id = "s",
        Question = "q",
        Request = Guid.NewGuid().ToString(),
        Fields = fields.ToDictionary(f => f.Name, f => JsonDocument.Parse(f.Json).RootElement.Clone()),
    };

    static byte[] Digest(string field) => AnswerDigest.Of(Request(("n", field)));

    [Theory]
    [InlineData("1e-29", "2e-29")]
    [InlineData("-1e-29", "-2e-29")]
    [InlineData("1e-29", "0")]
    [InlineData("3", "3.0000000000000000000000000000001")]
    [InlineData("1", "-1")]
    [InlineData("12345678901234567890123456789012345678901234567890", "12345678901234567890123456789012345678901234567891")]
    [InlineData("-79228162514264337593543950336", "-79228162514264337593543950337")]
    [InlineData("1e400", "2e400")]
    [InlineData("1e2000000000", "2e2000000000")]
    [InlineData("1", "\"1\"")]
    public void Different_numbers_are_different_answers_however_small_or_large(string a, string b) =>
        Assert.NotEqual(Digest(a), Digest(b));

    [Theory]
    [InlineData("1", "1.0")]
    [InlineData("1", "1e0")]
    [InlineData("1.0", "10e-1")]
    [InlineData("-2.50", "-25e-1")]
    [InlineData("0", "-0.0")]
    [InlineData("1e400", "10e399")]
    public void The_same_number_in_other_spellings_is_one_answer(string a, string b) =>
        Assert.Equal(Digest(a), Digest(b));

    [Fact]
    public void Text_and_the_order_of_fields_are_part_of_the_answer()
    {
        Assert.NotEqual(AnswerDigest.Of(Request(("n", "1")) with { Text = "a" }), AnswerDigest.Of(Request(("n", "1")) with { Text = "b" }));
        Assert.Equal(AnswerDigest.Of(Request(("a", "1"), ("b", "2"))), AnswerDigest.Of(Request(("b", "2"), ("a", "1"))));
    }
}
