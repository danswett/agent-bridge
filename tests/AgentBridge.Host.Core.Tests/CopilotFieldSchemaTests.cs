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

    // A default holding half a surrogate pair is not text .NET reads: a field opening with one is
    // answered at the machine, never a question that fails to map (#921 review).
    [Theory]
    [InlineData("""{ "type": "string", "default": "a\uD800" }""")]
    [InlineData("""{ "type": "number", "default": "a\uD800" }""")]
    [InlineData("""{ "enum": [1, 2], "default": "\uD800" }""")]
    [InlineData("""{ "type": "array", "items": { "enum": [1] }, "default": ["\uD800"] }""")]
    // With constraints, whose default is checked against them first (#921 review): these threw.
    [InlineData("""{ "type": "string", "maxLength": 10, "default": "a\uD800" }""")]
    [InlineData("""{ "type": "string", "pattern": "^a", "default": "a\uD800" }""")]
    [InlineData("""{ "type": "number", "minimum": 0, "default": "a\uD800" }""")]
    public void A_default_holding_half_a_surrogate_pair_is_answered_at_the_machine(string field)
    {
        using var doc = JsonDocument.Parse($$"""
            { "message": "m", "requestedSchema": { "properties": { "f": {{field}} } } }
            """);

        Assert.True(QuestionMapper.FromCopilotAskUser("q", doc.RootElement, DateTimeOffset.UnixEpoch).AnswerAtMachine);
    }

    // Anywhere else in the form, half a surrogate pair threw too, and the transcript line holding
    // it failed on every pass, freezing the session (#921 review). Each is answered at the machine.
    [Theory]
    [InlineData("""{ "requestedSchema": { "properties": { "f": { "type": "string", "pattern": "a\uD800" } } } }""")]
    [InlineData("""{ "requestedSchema": { "properties": { "f": { "type": "string", "format": "\uD800" } } } }""")]
    [InlineData("""{ "requestedSchema": { "properties": { "f": { "type": "\uD800" } } } }""")]
    [InlineData("""{ "requestedSchema": { "properties": { "f": { "type": "string", "title": "a\uD800" } } } }""")]
    [InlineData("""{ "requestedSchema": { "properties": { "f": { "type": "string", "enum": ["a\uD800", "b"] } } } }""")]
    [InlineData("""{ "requestedSchema": { "properties": { "f": { "type": "string", "oneOf": [{ "const": "a", "title": "\uD800" }] } } } }""")]
    [InlineData("""{ "requestedSchema": { "properties": { "f\uD800": { "type": "string" } } } }""")]
    [InlineData("""{ "requestedSchema": { "properties": { "f": { "type": "string" } }, "required": ["f\uD800"] } }""")]
    [InlineData("""{ "choices": ["a\uD800", "b"] }""")]
    public void A_form_holding_half_a_surrogate_pair_anywhere_is_answered_at_the_machine(string arguments)
    {
        using var doc = JsonDocument.Parse(arguments);

        Assert.True(QuestionMapper.FromCopilotAskUser("q", doc.RootElement, DateTimeOffset.UnixEpoch).AnswerAtMachine);
    }

    // A form's smallest answer is added up, not built: written out and serialised whole, about 45
    // bytes of {"type":"string","minLength":5000} cost 16 KiB, so 6,000 such fields took 640 MB
    // on every read of the line (#921 review). Over the bound, the form is answered at the machine
    // for little more than reading it costs.
    [Theory]
    [InlineData(5_000)]
    [InlineData(4_000)]
    public void A_form_of_many_long_required_texts_is_answered_at_the_machine_without_writing_them_out(int minLength)
    {
        var properties = string.Join(",", Enumerable.Range(0, 6_000).Select(i => $$"""
            "f{{i}}": { "type": "string", "minLength": {{minLength}} }
            """));
        var required = string.Join(",", Enumerable.Range(0, 6_000).Select(i => $"\"f{i}\""));
        using var doc = JsonDocument.Parse($$"""
            { "message": "m", "requestedSchema": { "properties": { {{properties}} }, "required": [{{required}}] } }
            """);
        QuestionMapper.FromCopilotAskUser("q", doc.RootElement, DateTimeOffset.UnixEpoch);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var q = QuestionMapper.FromCopilotAskUser("q", doc.RootElement, DateTimeOffset.UnixEpoch);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(q.AnswerAtMachine);
        Assert.Equal(6_000, q.Fields.Count);
        Assert.InRange(allocated, 0, 64L * 1024 * 1024);
    }

    // The message was read outside that guard, and froze the session the same way (#921 review).
    // It still shows, half a pair as U+FFFD, as a client shows one; a whole pair is kept.
    [Theory]
    [InlineData("""{ "message": "a\uD800 b\uD83D\uDE00\n", "requestedSchema": { "properties": { "f": { "type": "string" } } } }""")]
    [InlineData("""{ "question": "a\uD800 b\uD83D\uDE00\n", "choices": ["x", "y"] }""")]
    public void A_message_holding_half_a_surrogate_pair_is_shown_with_a_replacement_character(string arguments)
    {
        using var doc = JsonDocument.Parse(arguments);

        Assert.Equal("a\uFFFD b\U0001F600\n", QuestionMapper.FromCopilotAskUser("q", doc.RootElement, DateTimeOffset.UnixEpoch).Message);
    }

    // As JavaScript's Number#toString writes each, which is what the PWA sends for a number box's
    // default; checked against Node.
    [Theory]
    [InlineData(1e20, "100000000000000000000")]
    [InlineData(1e21, "1e+21")]
    [InlineData(2e21, "2e+21")]
    [InlineData(123456789012345680000d, "123456789012345680000")]
    [InlineData(1.2345678901234568e22, "1.2345678901234568e+22")]
    [InlineData(1.5e-7, "1.5e-7")]
    [InlineData(1e-7, "1e-7")]
    [InlineData(0.000001, "0.000001")]
    [InlineData(-0.000001, "-0.000001")]
    [InlineData(0.0000015, "0.0000015")]
    [InlineData(123.456, "123.456")]
    [InlineData(-0.5, "-0.5")]
    [InlineData(0.1, "0.1")]
    [InlineData(100d, "100")]
    [InlineData(4.35, "4.35")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(1.7976931348623157e308, "1.7976931348623157e+308")]
    [InlineData(1.2345678901234568e-300, "1.2345678901234568e-300")]
    [InlineData(0d, "0")]
    public void A_number_is_written_as_JavaScript_writes_it(double value, string written)
    {
        Assert.Equal(written, CopilotFieldSchema.JavaScriptNumber(value));
    }

    // A text box that opens with only spaces, or only what shows as nothing (Invisible.Blank), can
    // be given nothing but that default from another device: refused as blank when required
    // (review of #844), and left out of the answer by the PWA when not (#921 review). Either way
    // it is answered at the machine.
    [Theory]
    [InlineData("\"   \"", true, true)]
    [InlineData("\" \\t\\u00A0\"", true, true)]
    [InlineData("\"\\u0085\\u001b\"", true, true)]
    [InlineData("\"\"", true, false)]
    [InlineData("\" a \"", true, false)]
    [InlineData("\"   \"", false, true)]
    [InlineData("\" a \"", false, false)]
    [InlineData("\"\\u200B\"", true, true)]
    [InlineData("\"\\u2800\"", false, true)]
    [InlineData("\"\\u0301\"", false, true)]
    [InlineData("\"a\\u200Bb\"", true, false)]
    public void A_text_box_that_opens_with_only_spaces_is_answered_at_the_machine(string def, bool required, bool atMachine)
    {
        using var doc = JsonDocument.Parse($$"""
            { "message": "m", "requestedSchema": { "properties": { "t": { "type": "string", "default": {{def}} } }{{(required ? ", \"required\": [\"t\"]" : "")}} } }
            """);

        Assert.Equal(atMachine ? true : null, QuestionMapper.FromCopilotAskUser("q", doc.RootElement, DateTimeOffset.UnixEpoch).AnswerAtMachine);
    }
}
