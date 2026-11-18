using System.Collections;
using System.Text.Json;
using AgentBridge.Protocol;
using Xunit;
using Xunit.Abstractions;

public class MultiAnswerTests(ITestOutputHelper output)
{
    static Question Form(IReadOnlyList<Option>? options, bool allowOther = true) => new()
    {
        Id = "q1",
        Kind = QuestionKind.Form,
        Message = "Pick",
        AskedAt = DateTimeOffset.UnixEpoch,
        Fields = [new Field { Name = "pick", Type = FieldType.Multi, Options = options, AllowOther = allowOther }],
    };

    static AnswerRequest Answer(string json) => new()
    {
        Id = "s1",
        Question = "q1",
        Request = "r1",
        Fields = new Dictionary<string, JsonElement> { ["pick"] = JsonSerializer.Deserialize<JsonElement>(json) },
    };

    // Count option visits as #921 does: shared-runner pauses cannot hide a nested scan.
    [Fact]
    public void Four_times_the_options_and_selections_need_linear_option_work()
    {
        int Visits(int count)
        {
            var options = new CountedOptions(Enumerable.Range(0, count).Select(i => new Option($"v{i}")).ToArray());
            var answer = Answer(JsonSerializer.Serialize(options.Values.Select(o => o.Value)));
            Assert.Null(Answers.Check(Form(options), answer));
            return options.Visits;
        }

        var quarter = Visits(128);
        var full = Visits(512);
        var ratio = (double)full / Math.Max(quarter, 1);
        output.WriteLine($"Option visits: {quarter} at quarter size, {full} at full size; {ratio:F2}x.");
        Assert.True(ratio <= 8, $"4x the options and selections took {ratio:F2}x the option visits");
    }

    [Theory]
    [InlineData("""["a","b","a"]""", true, null)]
    [InlineData("""["A"]""", true, "not one of the choices")]
    [InlineData("""["a",7]""", true, "expected a choice")]
    [InlineData("""[true]""", true, "expected a choice")]
    [InlineData("""[null]""", true, "expected a choice")]
    [InlineData("""[[]]""", true, "expected a choice")]
    [InlineData("""[{}]""", true, "expected a choice")]
    [InlineData("""[{"other":"c"}]""", true, null)]
    [InlineData("""[{"other":"c"}]""", false, "this choice takes no other answer")]
    [InlineData("""[{"other":7}]""", true, "the other answer is empty")]
    [InlineData("""[{"other":" "}]""", true, "the other answer is empty")]
    [InlineData("""[{"other":"c","extra":1}]""", true, "unexpected properties beside other")]
    [InlineData("""[{"other":"c"},{"other":"d"}]""", true, "at most one other answer")]
    [InlineData("""[{"other":"c"},{"other":"d"},"missing"]""", true, "not one of the choices")]
    public void Multi_answers_keep_their_choice_verdicts(string json, bool allowOther, string? problem)
    {
        var reason = Answers.Check(Form([new("a", "A"), new("b"), new("a")], allowOther), Answer(json));
        Assert.Equal(problem is null ? null : $"\"pick\": {problem}", reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_options_still_allow_an_empty_list_or_other_but_no_named_choice(bool absent)
    {
        var question = Form(absent ? null : []);
        Assert.Null(Answers.Check(question, Answer("[]")));
        Assert.Null(Answers.Check(question, Answer("""[{"other":"c"}]""")));
        Assert.Equal("\"pick\": not one of the choices", Answers.Check(question, Answer("""["a"]""")));
    }

    sealed class CountedOptions(Option[] values) : IReadOnlyList<Option>
    {
        public Option[] Values => values;
        public int Visits { get; private set; }
        public int Count => values.Length;
        public Option this[int index] { get { Visits++; return values[index]; } }
        public IEnumerator<Option> GetEnumerator()
        {
            for (var i = 0; i < Count; i++) { yield return this[i]; }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
