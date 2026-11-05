using System.Text.Json;
using AgentBridge.Host.Core.Parsing;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Copilot;
using AgentBridge.Protocol;
using Xunit;

// The keys for every shape of Copilot question, worked out without a console. Each walk is
// agent-ha-bridge's (hooks/decision-inject.ps1), which was settled against live prompts.
public class CopilotPlannerTests
{
    const string U = KeyPlan.Up;
    const string D = KeyPlan.Down;

    internal static (CopilotForm Form, Question Question) Ask(string arguments)
    {
        using var doc = JsonDocument.Parse(arguments);
        var question = QuestionMapper.FromCopilotAskUser("call-1", doc.RootElement, Fixture.T0);
        var (form, problem) = CopilotForm.Read(doc.RootElement.Clone());
        Assert.Null(problem);
        // The two readers of one schema agree, or nothing would be typed at all.
        Assert.True(form!.Matches(question));
        return (form, question);
    }

    internal static AnswerRequest Answer(object? fields = null, string? text = null) => new()
    {
        Id = "s",
        Question = "call-1",
        Request = Guid.NewGuid().ToString(),
        Text = text,
        Fields = fields is null ? null : JsonSerializer.SerializeToElement(fields).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()),
    };

    static CopilotPlan Plan(string arguments, AnswerRequest answer)
    {
        var (form, question) = Ask(arguments);
        Assert.Null(Answers.Check(question, answer));
        var (plan, problem) = CopilotPlanner.Plan(form, answer);
        Assert.Null(problem);
        return plan!;
    }

    static string Refusal(string arguments, AnswerRequest answer)
    {
        var (form, question) = Ask(arguments);
        Assert.Null(Answers.Check(question, answer));
        var (plan, problem) = CopilotPlanner.Plan(form, answer);
        Assert.Null(plan);
        return problem!;
    }

    static List<(string Text, bool Enter)> Keys(CopilotPlan plan) => [.. plan.Keys.Sends.Select(s => (s.Text, s.Enter))];

    const string Pick = """{ "message": "m", "requestedSchema": { "properties": { "pick": { "type": "string", "enum": ["a", "b", "c", "d"], "default": "c" } } } }""";

    [Fact]
    public void A_free_text_question_types_the_words_then_presses_Enter_as_its_own_keystroke()
    {
        var plan = Plan("""{ "message": "What next?" }""", Answer(text: "ship it"));

        var send = Assert.Single(plan.Keys.Sends);
        Assert.Equal(new KeySend("ship it", true, CopilotPlanner.ReplyEnterDelayMs, 0), send);
        Assert.Equal("ship it", plan.Text);
    }

    [Fact]
    public void Line_breaks_and_control_characters_in_typed_words_become_spaces_so_they_cannot_submit_early()
    {
        var plan = Plan("""{ "message": "m" }""", Answer(text: "one\r\ntwo\tthree\u001b[Afour\u2028five\u0085six"));

        Assert.Equal("one  two three [Afour five six", Assert.Single(plan.Keys.Sends).Text);
    }

    [Fact]
    public void Emoji_and_CJK_are_typed_as_they_are_and_long_words_go_in_one_burst()
    {
        var words = "café – 世界 ✓ 🚀 " + new string('x', 4000);
        var plan = Plan("""{ "message": "m" }""", Answer(text: words));

        Assert.Equal(words, Assert.Single(plan.Keys.Sends).Text);
    }

    [Fact]
    public void Words_that_are_only_control_characters_are_refused_rather_than_typed_as_an_empty_answer()
    {
        var (form, _) = Ask("""{ "message": "m" }""");
        var (plan, problem) = CopilotPlanner.Plan(form, Answer(text: "\u0001\u0002"));

        Assert.Null(plan);
        Assert.Equal("the answer is empty", problem);
    }

    [Fact]
    public void A_choice_walks_up_to_the_top_wherever_the_caret_was_left_then_down_to_the_chosen_row_and_commits_with_Enter()
    {
        Assert.Equal([(U + U + U + D + D + D, true)], Keys(Plan(Pick, Answer(new { pick = "d" }))));
        Assert.Equal([(U + U + U, true)], Keys(Plan(Pick, Answer(new { pick = "a" }))));
        Assert.Equal([(U + U + U + D + D, true)], Keys(Plan(Pick, Answer(new { pick = "c" }))));
    }

    [Fact]
    public void A_choice_without_a_default_starts_on_its_first_row_and_is_recorded_by_its_value_not_its_label()
    {
        var plan = Plan("""
            { "message": "m", "requestedSchema": { "properties": { "next": { "type": "string",
              "oneOf": [ { "const": "s4", "title": "S4 relay (Recommended)" }, { "const": "s6", "title": "S6 keys" } ] } } } }
            """, Answer(new { next = "s6" }));

        Assert.Equal([(U + D, true)], Keys(plan));
        var (name, expected) = Assert.Single(plan.Fields);
        Assert.Equal("next", name);
        Assert.Equal("s6", Assert.IsType<Expected.Option>(expected).Raw.GetString());
    }

    [Fact]
    public void Each_field_of_a_form_is_answered_in_order_with_its_own_Enter_and_a_pause_after_it()
    {
        var plan = Plan("""
            { "message": "m", "requestedSchema": { "properties": {
              "colour": { "type": "string", "enum": ["red", "green", "blue"] },
              "go": { "type": "boolean", "default": false },
              "notes": { "type": "string" },
              "count": { "type": "integer" } } } }
            """, Answer(new { colour = "blue", go = true, notes = "note\nhere", count = 42 }));

        Assert.Equal([(U + U + D + D, true), (U, true), ("note here", true), ("42", true)], Keys(plan));
        Assert.All(plan.Keys.Sends, s => Assert.Equal((CopilotPlanner.FieldEnterDelayMs, CopilotPlanner.AfterFieldMs), (s.EnterDelayMs, s.PauseAfterMs)));
        Assert.Equal(["colour", "go", "notes", "count"], plan.Fields.Select(f => f.Name));
    }

    // A number field the prompt opens with 3 already typed into it (#662).
    const string Days = """{ "message": "m", "requestedSchema": { "properties": { "days": { "type": "integer", "default": 3 } } } }""";
    const string Note = """{ "message": "m", "requestedSchema": { "properties": { "note": { "type": "string", "default": "none" } } } }""";

    [Fact]
    public void A_field_answered_with_the_default_it_opens_with_is_left_as_it_is_and_only_its_Enter_goes()
    {
        var plan = Plan(Days, Answer(new { days = 3 }));

        Assert.Equal([("", true)], Keys(plan));
        Assert.Equal(3, Assert.IsType<Expected.Number>(Assert.Single(plan.Fields).Value).Raw.GetInt32());
        // As the prompt shows it: 3.0 is the 3 it opened with.
        Assert.Equal([("", true)], Keys(Plan(Days, Answer(Raw("""{ "days": 3.0 }""")))));
        Assert.Equal([("", true)], Keys(Plan(Note, Answer(new { note = "none" }))));
        Assert.Equal("none", Assert.IsType<Expected.Words>(Plan(Note, Answer(new { note = "none" })).Fields[0].Value).Text);
    }

    [Fact]
    public void Anything_but_the_default_a_field_opens_with_is_refused_whole_since_its_keys_would_be_added_to_the_default()
    {
        // 33 typed into a field that opened with 3 would be 333, and 3 into one that opened with 3, 33.
        Assert.EndsWith("answer at the machine (this field has a pre-filled default)", Refusal(Days, Answer(new { days = 33 })));
        Assert.Contains("\"days\"", Refusal(Days, Answer(new { days = 4 })));
        Assert.Contains("pre-filled default", Refusal(Note, Answer(new { note = "nonenone" })));
        Assert.Contains("pre-filled default", Refusal(Note, Answer(new { note = "" })));
        // Left out, the field would still be recorded with its default, which nobody gave.
        const string PickAndDays = """{ "message": "m", "requestedSchema": { "properties": { "pick": { "type": "string", "enum": ["a", "b"] }, "days": { "type": "integer", "default": 3 } } } }""";
        Assert.Contains("pre-filled default", Refusal(PickAndDays, Answer(new { pick = "a" })));
    }

    // Fields as JSON text, so a number keeps the spelling it was given.
    static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void A_number_that_only_rounds_to_the_default_is_not_the_default()
    {
        Assert.Contains("pre-filled default", Refusal(Days, Answer(Raw("""{ "days": 3.0000000000000000000000000000001 }"""))));
        Assert.Contains("pre-filled default", Refusal(Days, Answer(Raw("""{ "days": 3e-29 }"""))));
    }

    [Theory]
    // Neither can be compared exactly (an exponent too large to hold, or no finite double for
    // the CLI to record), so the same spelling in both is no proof: refused before any key (#700).
    [InlineData("1e99999999999")]
    [InlineData("1e400")]
    public void A_default_and_answer_that_cannot_be_compared_as_numbers_are_refused_however_alike_they_read(string number)
    {
        var huge = $$"""{ "message": "m", "requestedSchema": { "properties": { "n": { "type": "number", "default": {{number}} } } } }""";

        Assert.Contains("pre-filled default", Refusal(huge, Answer(Raw($$"""{ "n": {{number}} }"""))));
    }

    [Fact]
    public void A_number_fields_string_default_is_kept_only_when_it_is_plainly_that_number()
    {
        // The CLI records a number field's string as Number(text), so "3" is kept as 3; hex,
        // padding and words are not read as numbers here, and nothing is typed for them.
        Assert.Equal([("", true)], Keys(Plan(Number("\"3\""), Answer(new { n = 3 }))));
        Assert.Contains("pre-filled default", Refusal(Number("\"0x10\""), Answer(new { n = 16 })));
        Assert.Contains("pre-filled default", Refusal(Number("\" 3\""), Answer(new { n = 3 })));
        Assert.Contains("pre-filled default", Refusal(Number("\"abc\""), Answer(new { n = 3 })));
    }

    internal static string Number(string @default) =>
        $$"""{ "message": "m", "requestedSchema": { "properties": { "n": { "type": "integer", "default": {{@default}} } } } }""";

    [Theory]
    // The CLI holds each as another double, so what it records is not the default or the answer:
    // precision lost, underflow to 0, and past the largest double (#700).
    [InlineData("\"9007199254740993\"", "9007199254740993")]
    [InlineData("9007199254740993", "9007199254740993")]
    [InlineData("\"1e-400\"", "1e-400")]
    [InlineData("1e-400", "1e-400")]
    [InlineData("\"1e400\"", "1e400")]
    public void A_default_the_CLI_would_hold_as_another_number_is_refused_even_when_the_answer_reads_the_same(string @default, string answer)
    {
        var schema = $$"""{ "message": "m", "requestedSchema": { "properties": { "n": { "type": "number", "default": {{@default}} } } } }""";

        Assert.Contains("pre-filled default", Refusal(schema, Answer(Raw($$"""{ "n": {{answer}} }"""))));
    }

    [Theory]
    [InlineData("9007199254740993")]
    [InlineData("1e-400")]
    [InlineData("1e300")]
    public void A_number_the_CLI_would_record_differently_or_that_is_too_long_to_type_is_refused_before_any_key(string answer)
    {
        const string Free = """{ "message": "m", "requestedSchema": { "properties": { "n": { "type": "number" } } } }""";

        Assert.Contains("answer at the machine", Refusal(Free, Answer(Raw($$"""{ "n": {{answer}} }"""))));
    }

    [Fact]
    public void A_number_is_typed_as_plain_decimal_since_the_box_drops_a_key_that_leaves_it_not_a_number()
    {
        // "1e" is not a number, so the "e" of 1e5 typed key by key would be dropped: 15.
        const string Free = """{ "message": "m", "requestedSchema": { "properties": { "n": { "type": "number" } } } }""";

        var plan = Plan(Free, Answer(Raw("""{ "n": 1e5 }""")));

        Assert.Equal([("100000", true)], Keys(plan));
        // Expected as typed, the number the CLI records.
        Assert.Equal("100000", Assert.IsType<Expected.Number>(Assert.Single(plan.Fields).Value).Raw.GetRawText());
    }

    [Theory]
    [InlineData("2.50", "25e-1", true)]
    [InlineData("100", "1E+2", true)]
    [InlineData("-0", "0.000", true)]
    [InlineData("3", "3.0000000000000000000000000000001", false)]
    [InlineData("1e-29", "0", false)]
    [InlineData("1e99999999999", "1e99999999999", false)]
    public void Numbers_are_the_same_only_when_they_are_exactly_equal(string a, string b, bool same)
    {
        Assert.Equal(same, CopilotForm.SameScalar(Raw(a), Raw(b)));
    }

    // ---- fields drawn as Copilot CLI draws them (cli-main.js `tle`), not as their keywords suggest ----

    [Fact]
    public void A_number_with_an_enum_is_a_number_box_so_its_default_is_kept_by_Enter_and_anything_else_refused()
    {
        // Arrows in a number box add or take one: walking it as a list to pick 1 recorded 4.
        const string Rated = """{ "message": "m", "requestedSchema": { "properties": { "rate": { "type": "integer", "enum": [1, 2, 3], "default": 2 } } } }""";

        Assert.Equal([("", true)], Keys(Plan(Rated, Answer(new { rate = 2 }))));
        Assert.Contains("pre-filled default", Refusal(Rated, Answer(new { rate = 1 })));
    }

    [Fact]
    public void A_number_with_an_enum_and_no_default_opens_empty_and_takes_its_digits_typed()
    {
        const string Rated = """{ "message": "m", "requestedSchema": { "properties": { "rate": { "type": "integer", "enum": [1, 2, 3] } } } }""";

        Assert.Equal([("1", true)], Keys(Plan(Rated, Answer(new { rate = 1 }))));
    }

    [Fact]
    public void A_string_with_only_anyOf_is_a_text_box_typed_into_only_when_it_opens_empty()
    {
        const string Prefilled = """{ "message": "m", "requestedSchema": { "properties": { "mode": { "type": "string", "anyOf": [ { "const": "x", "title": "X" }, { "const": "y", "title": "Y" } ], "default": "x" } } } }""";
        const string Empty = """{ "message": "m", "requestedSchema": { "properties": { "mode": { "type": "string", "anyOf": [ { "const": "x", "title": "X" }, { "const": "y", "title": "Y" } ] } } } }""";

        Assert.Equal([("", true)], Keys(Plan(Prefilled, Answer(new { mode = "x" }))));
        Assert.Contains("pre-filled default", Refusal(Prefilled, Answer(new { mode = "y" })));
        Assert.Equal([("y", true)], Keys(Plan(Empty, Answer(new { mode = "y" }))));
    }

    [Fact]
    public void A_string_with_enum_or_oneOf_is_still_a_list_walked_with_arrows_and_oneOf_is_read_first_as_the_CLI_reads_it()
    {
        Assert.Equal([(U + U + U + D + D + D, true)], Keys(Plan(Pick, Answer(new { pick = "d" }))));
        const string Both = """{ "message": "m", "requestedSchema": { "properties": { "pick": { "type": "string", "enum": ["a", "b"], "oneOf": [ { "const": "b" }, { "const": "a" } ] } } } }""";
        Assert.Equal([(U, true)], Keys(Plan(Both, Answer(new { pick = "b" }))));
    }

    [Theory]
    // Untyped options: the CLI draws a text box.
    [InlineData("""{ "pick": { "enum": ["a", "b"] } }""")]
    [InlineData("""{ "pick": { "oneOf": [ { "const": "a" } ] } }""")]
    // A type it does not know, or one that is not a name.
    [InlineData("""{ "pick": { "type": "object" } }""")]
    [InlineData("""{ "pick": { "type": ["string", "null"], "enum": ["a"] } }""")]
    // An option the CLI would record as nothing, and keywords that are not lists.
    [InlineData("""{ "pick": { "type": "string", "oneOf": [ { "title": "A" } ] } }""")]
    [InlineData("""{ "pick": { "type": "string", "enum": "a" } }""")]
    [InlineData("""{ "many": { "type": "array", "items": { "anyOf": [ { "title": "A" } ] } } }""")]
    public void A_field_whose_input_cannot_be_told_from_the_CLIs_reading_is_answered_at_the_machine(string properties)
    {
        using var doc = JsonDocument.Parse($$"""{ "message": "m", "requestedSchema": { "properties": {{properties}} } }""");

        Assert.EndsWith("answer it at the machine", CopilotForm.Read(doc.RootElement).Problem);
        Assert.True(QuestionMapper.FromCopilotAskUser("call-1", doc.RootElement, Fixture.T0).AnswerAtMachine);
    }

    [Fact]
    public void A_number_or_text_field_without_a_default_or_with_an_empty_one_is_typed_as_before()
    {
        const string NoDefault = """{ "message": "m", "requestedSchema": { "properties": { "days": { "type": "integer" } } } }""";
        const string NullText = """{ "message": "m", "requestedSchema": { "properties": { "note": { "type": "string", "default": null } } } }""";
        const string EmptyDefault = """{ "message": "m", "requestedSchema": { "properties": { "note": { "type": "string", "default": "" } } } }""";
        const string EmptyNumber = """{ "message": "m", "requestedSchema": { "properties": { "days": { "type": "integer", "default": "" } } } }""";

        Assert.Equal([("33", true)], Keys(Plan(NoDefault, Answer(new { days = 33 }))));
        // A text box shows `default ?? ""`: a null default leaves it empty.
        Assert.Equal([("hello", true)], Keys(Plan(NullText, Answer(new { note = "hello" }))));
        Assert.Equal([("hello", true)], Keys(Plan(EmptyDefault, Answer(new { note = "hello" }))));
        Assert.Equal([("3", true)], Keys(Plan(EmptyNumber, Answer(new { days = 3 }))));
    }

    [Fact]
    public void A_number_field_with_a_null_default_opens_reading_null_so_nothing_is_typed_into_it()
    {
        // The number box shows String(default), and String(null) is "null" (cli-main.js `Lxn`).
        const string NullNumber = """{ "message": "m", "requestedSchema": { "properties": { "days": { "type": "integer", "default": null } } } }""";

        Assert.Contains("pre-filled default", Refusal(NullNumber, Answer(new { days = 3 })));
    }

    [Fact]
    public void A_field_left_at_its_default_is_followed_by_the_next_fields_and_lists_with_defaults_are_still_walked()
    {
        const string Form = """
            { "message": "m", "requestedSchema": { "properties": {
              "window": { "type": "integer", "default": 90 },
              "pick": { "type": "string", "enum": ["a", "b", "c", "d"], "default": "c" },
              "go": { "type": "boolean", "default": false },
              "notes": { "type": "string" } } } }
            """;

        var plan = Plan(Form, Answer(new { window = 90, pick = "a", go = true, notes = "hi" }));

        Assert.Equal([("", true), (U + U + U, true), (U, true), ("hi", true)], Keys(plan));
        // Refused whole: not the fields before it either.
        Assert.Contains("\"window\"", Refusal(Form, Answer(new { window = 9090, pick = "a", go = true, notes = "hi" })));
    }

    [Fact]
    public void A_boolean_is_a_Yes_No_list_walked_from_its_top_row_like_any_option_list()
    {
        const string NoByDefault = """{ "message": "m", "requestedSchema": { "properties": { "go": { "type": "boolean", "default": false } } } }""";
        const string Plain = """{ "message": "m", "requestedSchema": { "properties": { "go": { "type": "boolean" } } } }""";

        Assert.Equal([(U, true)], Keys(Plan(NoByDefault, Answer(new { go = true }))));
        Assert.Equal([(U + D, true)], Keys(Plan(NoByDefault, Answer(new { go = false }))));
        Assert.Equal([(U + D, true)], Keys(Plan(Plain, Answer(new { go = false }))));
        Assert.False(Assert.IsType<Expected.Flag>(Plan(Plain, Answer(new { go = false })).Fields[0].Value).Value);
    }

    [Fact]
    public void A_multi_select_list_is_walked_once_from_its_top_row_wherever_the_caret_was_left_toggling_only_rows_that_must_change()
    {
        const string Many = """{ "message": "m", "requestedSchema": { "properties": { "many": { "type": "array", "items": { "enum": ["a", "b", "c", "d"] }, "default": ["b", "c"] } } } }""";

        var plan = Plan(Many, Answer(new { many = new[] { "a", "c", "d" } }));

        // Up to the top first, then a on, b off (it opens ticked), c left alone, d on; only the last carries Enter.
        Assert.Equal([(U + U + U + " ", false), (D + " ", false), (D + D + " ", true)], Keys(plan));
        Assert.Equal([CopilotPlanner.ToggleGapMs, CopilotPlanner.ToggleGapMs, CopilotPlanner.AfterFieldMs], plan.Keys.Sends.Select(s => s.PauseAfterMs));
        Assert.Equal(["a", "c", "d"], Assert.IsType<Expected.Set>(plan.Fields[0].Value).Raws.Select(r => r.GetString()));
    }

    [Fact]
    public void A_multi_select_answer_that_is_its_defaults_is_accepted_with_Enter_after_the_walk_to_the_top()
    {
        const string Many = """{ "message": "m", "requestedSchema": { "properties": { "many": { "type": "array", "items": { "enum": ["a", "b"] }, "default": ["b"] } } } }""";

        Assert.Equal([(U, true)], Keys(Plan(Many, Answer(new { many = new[] { "b" } }))));
    }

    [Fact]
    public void An_empty_multi_select_answer_is_typed_only_where_the_schema_allows_none()
    {
        const string AnyOrNone = """{ "message": "m", "requestedSchema": { "properties": { "many": { "type": "array", "items": { "enum": ["a", "b"] } } } } }""";
        const string AtLeastOne = """{ "message": "m", "requestedSchema": { "properties": { "many": { "type": "array", "minItems": 1, "items": { "enum": ["a", "b"] } } } } }""";

        Assert.Equal([(U, true)], Keys(Plan(AnyOrNone, Answer(new { many = Array.Empty<string>() }))));
        Assert.Contains("at least one", Refusal(AtLeastOne, Answer(new { many = Array.Empty<string>() })));
    }

    [Fact]
    public void Words_through_Other_walk_up_to_the_top_then_down_past_the_last_option_and_submit_separately()
    {
        var plan = Plan(Pick, Answer(new { pick = new { other = "none of these 🙂" } }));

        // Four options: three Ups reach the top from any row, four Downs open Other.
        Assert.Equal([(U + U + U + D + D + D + D, false), ("none of these 🙂", true)], Keys(plan));
        Assert.Equal(CopilotPlanner.OtherOpenDelayMs, plan.Keys.Sends[0].PauseAfterMs);
        Assert.Equal(CopilotPlanner.ReplyEnterDelayMs, plan.Keys.Sends[1].EnterDelayMs);
        Assert.Equal("none of these 🙂", Assert.IsType<Expected.Words>(plan.Fields[0].Value).Text);
    }

    [Fact]
    public void Other_in_a_tabbed_form_or_a_checkbox_list_is_refused_because_no_walk_reaches_it()
    {
        var (form, _) = Ask("""{ "message": "m", "requestedSchema": { "properties": { "pick": { "type": "string", "enum": ["a"] }, "go": { "type": "boolean" } } } }""");
        var (plan, problem) = CopilotPlanner.Plan(form, Answer(new { pick = new { other = "x" }, go = true }));
        Assert.Null(plan);
        Assert.Contains("one option list", problem);

        var (list, _) = Ask("""{ "message": "m", "requestedSchema": { "properties": { "many": { "type": "array", "items": { "enum": ["a"] } } } } }""");
        Assert.Contains("no other answer", CopilotPlanner.Plan(list, Answer(new { many = new object[] { new { other = "x" } } })).Problem);
    }

    [Fact]
    public void Other_words_that_collapse_to_nothing_are_refused_since_an_empty_submit_answers_nothing()
    {
        var (form, _) = Ask(Pick);

        Assert.Contains("empty", CopilotPlanner.Plan(form, Answer(new { pick = new { other = "\r\n" } })).Problem);
    }

    [Fact]
    public void An_optional_text_field_left_out_is_skipped_with_Enter_and_an_option_list_cannot_be()
    {
        const string Form = """{ "message": "m", "requestedSchema": { "properties": { "pick": { "type": "string", "enum": ["a", "b"] }, "notes": { "type": "string" } } } }""";

        var plan = Plan(Form, Answer(new { pick = "b" }));
        Assert.Equal([(U + D, true), ("", true)], Keys(plan));
        Assert.IsType<Expected.Omitted>(plan.Fields[1].Value);

        Assert.Contains("cannot leave \"pick\" unanswered", Refusal(Form, Answer(new { notes = "hi" })));
    }

    [Fact]
    public void The_older_choices_format_and_a_default_that_names_no_option_are_refused_before_planning()
    {
        using var legacy = JsonDocument.Parse("""{ "question": "Go?", "choices": ["Yes", "No"] }""");
        Assert.Contains("older Copilot format", CopilotForm.Read(legacy.RootElement).Problem);

        using var lost = JsonDocument.Parse("""{ "message": "m", "requestedSchema": { "properties": { "pick": { "type": "string", "enum": ["a", "b"], "default": "z" } } } }""");
        Assert.Contains("not one of its options", CopilotForm.Read(lost.RootElement).Problem);
    }

    [Fact]
    public void An_absent_default_leaves_the_caret_on_the_first_row_and_a_schema_written_as_text_is_read()
    {
        var plan = Plan("""{ "message": "m", "requestedSchema": "{\"properties\":{\"pick\":{\"type\":\"string\",\"enum\":[\"a\",\"b\"]}}}" }""", Answer(new { pick = "b" }));

        Assert.Equal([(U + D, true)], Keys(plan));
    }

    [Theory]
    // Copilot 1.0.105 copies a null default in and opens the list on its Other box; the walk
    // from the top row then ends a row off: "a" recorded "b", and "b" reopened Other (#700).
    [InlineData("""{ "type": "string", "enum": ["a", "b"], "default": null }""")]
    [InlineData("""{ "type": "string", "oneOf": [ { "const": "a" }, { "const": "b" } ], "default": null }""")]
    public void An_option_list_whose_default_is_null_opens_on_Other_and_is_refused_before_any_key(string field)
    {
        using var doc = JsonDocument.Parse($$"""{ "message": "m", "requestedSchema": { "properties": { "pick": {{field}} } } }""");

        var (form, problem) = CopilotForm.Read(doc.RootElement);

        Assert.Null(form);
        Assert.Contains("opens on its Other box", problem);
        Assert.Contains("answer it at the machine", problem);
    }

    [Theory]
    [InlineData("""{ "type": "array" }""")]
    [InlineData("""{ "type": "array", "items": true }""")]
    [InlineData("""{ "type": "array", "items": "x" }""")]
    [InlineData("""{ "type": "array", "items": [] }""")]
    [InlineData("""{ "type": "array", "items": {} }""")]
    [InlineData("""{ "type": "array", "items": { "enum": [] } }""")]
    public void A_checkbox_list_whose_items_yield_no_options_is_refused_before_any_key(string field)
    {
        using var doc = JsonDocument.Parse($$"""{ "message": "m", "requestedSchema": { "properties": { "many": {{field}} } } }""");

        var (form, problem) = CopilotForm.Read(doc.RootElement);

        Assert.Null(form);
        Assert.Contains("no options to choose from", problem);
    }

    [Fact]
    public void A_yes_or_no_field_whose_default_is_null_is_still_walked_from_the_top_row()
    {
        // Its Yes / No list has no Other box while schema constraints are enforced; a null
        // default only puts the caret on No, and the walk starts with an Up.
        var plan = Plan("""{ "message": "m", "requestedSchema": { "properties": { "ok": { "type": "boolean", "default": null } } } }""", Answer(new { ok = true }));

        Assert.Equal([(U, true)], Keys(plan));
    }

    [Fact]
    public void A_form_that_reads_differently_from_the_question_the_client_saw_does_not_match()
    {
        var (form, question) = Ask(Pick);
        var reordered = question with { Fields = [question.Fields[0] with { Options = [new("b"), new("a"), new("c"), new("d")] }] };

        Assert.False(form.Matches(reordered));
        Assert.False(form.Matches(question with { Fields = [] }));
    }
}
