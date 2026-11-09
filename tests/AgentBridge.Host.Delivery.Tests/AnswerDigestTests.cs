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
    [InlineData("0", "0e2000000000")]
    [InlineData("0", "-0")]
    [InlineData("-0e5", "0e-99999999999999999999")]
    [InlineData("1e1000000001", "10e1000000000")]
    [InlineData("1e-1000000001", "0.1e-1000000000")]
    [InlineData("1e99999999999999999999999", "10e99999999999999999999998")]
    [InlineData("100e-2", "1")]
    [InlineData("0.5e1", "5")]
    [InlineData("1E+2", "100")]
    public void The_same_number_in_other_spellings_is_one_answer(string a, string b) =>
        Assert.Equal(Digest(a), Digest(b));

    [Theory]
    [InlineData("1e1000000001", "1e1000000002")]
    [InlineData("1e1000000001", "-1e1000000001")]
    [InlineData("1e-1000000001", "1e1000000001")]
    [InlineData("0", "1e-2000000000")]
    public void Numbers_beyond_any_exponent_bound_are_still_told_apart(string a, string b) =>
        Assert.NotEqual(Digest(a), Digest(b));

    // The number is never expanded: a very long significand or exponent costs only its own length.
    // The test counts the memory the work allocates, which doesn't change with how busy the runner
    // is; the wall-clock bounds it replaced took 30.9 s on a loaded windows-11-arm runner and
    // failed a 30 s limit (#1374), then needed a 120 s one.
    [Theory]
    [InlineData("1{0}")]
    [InlineData("0.{0}1e200001")]
    [InlineData("1e{1}")]
    [InlineData("10e{1}")]
    public void An_absurdly_long_number_is_canonicalised_in_memory_proportional_to_its_length(string pattern)
    {
        var zeros = new string('0', 200_000);
        var nines = new string('9', 200_000);
        var number = string.Format(pattern, zeros, nines);
        AnswerDigest.Canonical("1e5");

        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.NotNull(AnswerDigest.Canonical(number));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated <= 32L * number.Length, $"allocated {allocated} bytes for {number.Length} characters");
    }

    [Fact]
    public void Equal_spellings_of_an_absurdly_long_number_still_match()
    {
        var zeros = new string('0', 200_000);
        var nines = new string('9', 200_000);
        var minus = new string('9', 199_999) + "8";

        Assert.Equal(Digest($"1{zeros}"), Digest("1e200000"));
        Assert.Equal(Digest("1"), Digest($"0.{zeros}1e200001"));
        Assert.Equal(Digest($"1e{nines}"), Digest($"10e{minus}"));
        Assert.NotEqual(Digest($"1{zeros}1"), Digest($"1{zeros}2"));
    }

    // Text that is not a JSON number has no canonical form; the digest keeps it tagged, as written.
    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1e")]
    [InlineData("1.")]
    [InlineData(".5")]
    [InlineData("--1")]
    [InlineData("1e+")]
    public void Text_that_is_not_a_number_has_no_canonical_form(string text) => Assert.Null(AnswerDigest.Canonical(text));

    // Wherever the planner's exact form exists, the digest agrees with it.
    [Theory]
    [InlineData("0")]
    [InlineData("-0.0")]
    [InlineData("1")]
    [InlineData("-2.50")]
    [InlineData("25e-1")]
    [InlineData("1e-29")]
    [InlineData("12345678901234567890123456789012345678901234567890")]
    [InlineData("0.000100")]
    public void The_canonical_form_is_the_planners_exact_form_where_it_has_one(string number) =>
        Assert.Equal(AgentBridge.Host.Core.Parsing.CopilotFieldSchema.ExactNumber(number), AnswerDigest.Canonical(number));

    [Fact]
    public void Text_and_the_order_of_fields_are_part_of_the_answer()
    {
        Assert.NotEqual(AnswerDigest.Of(Request(("n", "1")) with { Text = "a" }), AnswerDigest.Of(Request(("n", "1")) with { Text = "b" }));
        Assert.Equal(AnswerDigest.Of(Request(("a", "1"), ("b", "2"))), AnswerDigest.Of(Request(("b", "2"), ("a", "1"))));
    }
}
