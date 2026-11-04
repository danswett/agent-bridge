using System.Text.Json;
using AgentBridge.Host.Core.Parsing;
using Xunit;

// Numbers in a Copilot form, read as the CLI (JavaScript) holds them, and never a throw for any
// text, however malformed or extreme (#700).
public class CopilotFieldSchemaTests
{
    [Theory]
    [InlineData("1e-9223372036854775808")]
    [InlineData("1e9223372036854775807")]
    [InlineData("1e-9223372036854775809")]
    [InlineData("1e99999999999999999999")]
    [InlineData("1e-1000000001")]
    [InlineData("1e1000000001")]
    public void An_exponent_beyond_what_is_held_is_unsupported_and_never_throws(string number)
    {
        Assert.Null(CopilotFieldSchema.ExactNumber(number));
        Assert.Null(CopilotFieldSchema.HeldExactly(number));

        // Read while a question is mapped: in a default, an option, and a checkbox default.
        using var doc = JsonDocument.Parse($$"""
            { "message": "m", "requestedSchema": { "properties": {
              "n": { "type": "integer", "default": {{number}} },
              "pick": { "type": "string", "enum": ["a"], "default": {{number}} },
              "many": { "type": "array", "items": { "enum": [{{number}}] }, "default": [{{number}}] } } } }
            """);
        var q = QuestionMapper.FromCopilotAskUser("q", doc.RootElement, DateTimeOffset.UnixEpoch);
        Assert.True(q.AnswerAtMachine);
    }

    [Theory]
    // Precision lost, underflow to 0, overflow past the largest double: the CLI holds another
    // number, so none is held exactly.
    [InlineData("9007199254740993")]
    [InlineData("1e-400")]
    [InlineData("1e400")]
    [InlineData("0.1000000000000000000001")]
    public void A_number_the_CLI_would_hold_as_another_double_is_not_held_exactly(string number)
    {
        Assert.NotNull(CopilotFieldSchema.ExactNumber(number));
        Assert.Null(CopilotFieldSchema.HeldExactly(number));
    }

    [Theory]
    [InlineData("3", "3")]
    [InlineData("2.50", "2.5")]
    [InlineData("0.1", "0.1")]
    [InlineData("-0", "0")]
    [InlineData("1e5", "100000")]
    [InlineData("25e-3", "0.025")]
    [InlineData("-1.5E+2", "-150")]
    [InlineData("9007199254740992", "9007199254740992")]
    public void A_number_held_exactly_is_typed_as_plain_decimal(string number, string typed)
    {
        Assert.Equal(typed, CopilotFieldSchema.PlainDecimal(CopilotFieldSchema.HeldExactly(number)!, 40));
    }

    [Fact]
    public void A_number_too_long_to_type_as_plain_decimal_is_not_typed()
    {
        Assert.Null(CopilotFieldSchema.PlainDecimal(CopilotFieldSchema.HeldExactly("1e300")!, 40));
        Assert.Null(CopilotFieldSchema.PlainDecimal(CopilotFieldSchema.HeldExactly("1e-300")!, 40));
    }

    [Fact]
    public void Malformed_and_extreme_number_text_never_throws()
    {
        var random = new Random(700);
        const string Alphabet = "0123456789-+.eE x";
        string[] fixedCases = ["", "-", "+1", ".5", "5.", "1e", "1e+", "e5", "--1", "1.2.3", "01", "-01", "0x10", " 3", "Infinity", "NaN", "1e-", "1E-0", "-0.0e-0", new string('9', 5000), "1" + new string('0', 400), "0." + new string('0', 400) + "1"];
        var cases = fixedCases.Concat(Enumerable.Range(0, 2000).Select(_ => new string([.. Enumerable.Range(0, random.Next(0, 30)).Select(_ => Alphabet[random.Next(Alphabet.Length)])])));
        foreach (var text in cases)
        {
            var exact = CopilotFieldSchema.ExactNumber(text);
            var held = CopilotFieldSchema.HeldExactly(text);
            if (exact is not null) { _ = CopilotFieldSchema.PlainDecimal(exact, 40); }
            if (held is not null)
            {
                Assert.Equal(exact, held);
                var plain = CopilotFieldSchema.PlainDecimal(held, 400);
                // What is typed reads back as the same number.
                if (plain is not null) { Assert.Equal(held, CopilotFieldSchema.ExactNumber(plain)); }
            }
        }
    }

    [Fact]
    public void A_list_whose_number_options_the_CLI_would_hold_as_other_numbers_is_answered_at_the_machine()
    {
        // 9007199254740993 becomes the same double as 9007199254740992: picking either records it.
        using var doc = JsonDocument.Parse("""
            { "message": "m", "requestedSchema": { "properties": {
              "many": { "type": "array", "items": { "enum": [9007199254740992, 9007199254740993] } } } } }
            """);

        Assert.True(QuestionMapper.FromCopilotAskUser("q", doc.RootElement, DateTimeOffset.UnixEpoch).AnswerAtMachine);
    }
}
