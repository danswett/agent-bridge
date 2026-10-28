using System.Text.Json;
using AgentBridge.Protocol;
using Xunit;

public class AnswerTests
{
    static readonly DateTimeOffset At = new(2026, 10, 28, 14, 0, 0, TimeSpan.Zero);

    static Question Form(params Field[] fields) => new() { Id = "q1", Kind = QuestionKind.Form, Message = "Pick", Fields = fields, AskedAt = At };

    static Field Choice(string name, bool other = false, bool required = true) => new()
    {
        Name = name,
        Type = FieldType.Choice,
        Options = [new("a", "A (Recommended)"), new("b")],
        AllowOther = other ? true : null,
        Required = required ? true : null,
    };

    static AnswerRequest Answer(string fieldsJson) => new()
    {
        Id = "s1",
        Question = "q1",
        Request = "r1",
        Fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(fieldsJson),
    };

    [Fact]
    public void A_choice_takes_one_of_its_option_values_and_nothing_else()
    {
        var q = Form(Choice("answer"));
        Assert.Null(Answers.Check(q, Answer("""{"answer":"a"}""")));
        Assert.Contains("not one of the choices", Answers.Check(q, Answer("""{"answer":"A (Recommended)"}""")));
        Assert.Contains("no other answer", Answers.Check(q, Answer("""{"answer":{"other":"c"}}""")));
    }

    [Fact]
    public void Other_text_is_accepted_only_where_the_field_allows_it_and_must_say_something()
    {
        var q = Form(Choice("answer", other: true));
        Assert.Null(Answers.Check(q, Answer("""{"answer":{"other":"neither, use c"}}""")));
        Assert.Contains("empty", Answers.Check(q, Answer("""{"answer":{"other":"  "}}""")));
        var tooLong = JsonSerializer.Serialize(new { answer = new { other = new string('x', Limits.AnswerText + 1) } });
        Assert.Contains("longer than 4 KiB", Answers.Check(q, Answer(tooLong)));
    }

    [Fact]
    public void A_multi_choice_takes_a_list_with_at_most_one_other()
    {
        var q = Form(new Field { Name = "pick", Type = FieldType.Multi, Options = [new("a"), new("b")], AllowOther = true });
        Assert.Null(Answers.Check(q, Answer("""{"pick":["a","b",{"other":"c"}]}""")));
        Assert.Contains("at most one other", Answers.Check(q, Answer("""{"pick":[{"other":"c"},{"other":"d"}]}""")));
        Assert.Contains("expected a list", Answers.Check(q, Answer("""{"pick":"a"}""")));
    }

    [Fact]
    public void Booleans_numbers_and_text_must_have_their_own_json_type()
    {
        var q = Form(
            new Field { Name = "ok", Type = FieldType.Boolean },
            new Field { Name = "n", Type = FieldType.Number },
            new Field { Name = "t", Type = FieldType.Text });
        Assert.Null(Answers.Check(q, Answer("""{"ok":true,"n":3,"t":"hi"}""")));
        Assert.Contains("true or false", Answers.Check(q, Answer("""{"ok":"true"}""")));
        Assert.Contains("a number", Answers.Check(q, Answer("""{"n":"3"}""")));
        Assert.Contains("expected text", Answers.Check(q, Answer("""{"t":3}""")));
    }

    [Fact]
    public void Required_fields_must_be_answered_and_unknown_fields_are_refused()
    {
        var q = Form(Choice("answer"), Choice("extra", required: false));
        Assert.Null(Answers.Check(q, Answer("""{"answer":"b"}""")));
        Assert.Contains("needs an answer", Answers.Check(q, Answer("""{"extra":"a"}""")));
        Assert.Contains("no field \"nope\"", Answers.Check(q, Answer("""{"answer":"a","nope":1}""")));
    }

    [Fact]
    public void A_question_without_fields_takes_non_empty_text_and_one_with_fields_takes_no_text()
    {
        var plain = Form();
        Assert.Null(Answers.Check(plain, new AnswerRequest { Id = "s1", Question = "q1", Request = "r1", Text = "ship it" }));
        Assert.Contains("empty", Answers.Check(plain, new AnswerRequest { Id = "s1", Question = "q1", Request = "r1", Text = " " }));
        Assert.Contains("not fields", Answers.Check(plain, Answer("""{"answer":"a"}""")));
        Assert.Contains("not a text answer", Answers.Check(Form(Choice("answer")), new AnswerRequest { Id = "s1", Question = "q1", Request = "r1", Text = "a" }));
    }

    [Fact]
    public void A_truncated_question_is_never_answered_remotely()
    {
        var q = Form(Choice("answer")) with { Truncated = true };
        Assert.Contains("answer it at the machine", Answers.Check(q, Answer("""{"answer":"a"}""")));
    }

    [Fact]
    public void An_outcome_from_a_newer_host_reads_as_unconfirmed_never_as_delivered()
    {
        var result = JsonSerializer.Deserialize<AnswerResult>("""{"outcome":"queued","reason":"later"}""", ProtocolJson.Options)!;
        Assert.Equal(AnswerOutcome.Unconfirmed, result.Outcome);
        Assert.Equal("""{"outcome":"delivered"}""", JsonSerializer.Serialize(new AnswerResult(AnswerOutcome.Delivered), ProtocolJson.Options));
    }
}
