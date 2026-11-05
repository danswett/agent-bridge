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

    // #766: Copilot CLI 1.0.105 checks none of a form field's constraints on ask_user, and records
    // whatever is typed, so the host checks them, on what it would type, before any key.
    static string Schema(string name, string property) =>
        $$"""{ "message": "m", "requestedSchema": { "properties": { "{{name}}": {{property}} } } }""";

    // Refused by the shared check a client also runs, and again by the host's own plan.
    static string Outside(string arguments, AnswerRequest answer)
    {
        var (form, question) = Ask(arguments);
        var problem = Answers.Check(question, answer);
        Assert.NotNull(problem);
        var (plan, planned) = CopilotPlanner.Plan(form, answer);
        Assert.Null(plan);
        Assert.EndsWith(planned!, problem);
        return problem!;
    }

    const string Count = """{ "type": "integer", "minimum": 2, "maximum": 10, "multipleOf": 2 }""";

    [Theory]
    [InlineData("""{ "n": 1 }""", "\"n\": must be at least 2")]
    [InlineData("""{ "n": 12 }""", "\"n\": must be at most 10")]
    [InlineData("""{ "n": 4.5 }""", "\"n\": must be a whole number")]
    [InlineData("""{ "n": 5 }""", "\"n\": must be a multiple of 2")]
    public void A_number_outside_its_bounds_not_whole_or_off_its_step_is_refused_before_any_key(string answer, string why)
    {
        Assert.Equal(why, Outside(Schema("n", Count), Answer(Raw(answer))));
    }

    [Fact]
    public void A_number_inside_its_constraints_is_typed_as_before()
    {
        Assert.Equal([("10", true)], Keys(Plan(Schema("n", Count), Answer(Raw("""{ "n": 10 }""")))));
        Assert.Equal([("0.3", true)], Keys(Plan(Schema("x", """{ "type": "number", "exclusiveMinimum": 0, "multipleOf": 0.1 }"""), Answer(Raw("""{ "x": 0.3 }""")))));
    }

    [Fact]
    public void Number_constraints_are_compared_exactly_never_as_doubles()
    {
        // As doubles 0.3 % 0.1 is not 0, and 0.30000000000000004 > 0.3 is easy to miss.
        Assert.Equal("\"x\": must be at most 0.3", Outside(Schema("x", """{ "type": "number", "maximum": 0.3 }"""), Answer(Raw("""{ "x": 0.30000000000000004 }"""))));
        Assert.Equal("\"x\": must be more than 0", Outside(Schema("x", """{ "type": "number", "exclusiveMinimum": 0 }"""), Answer(Raw("""{ "x": 0 }"""))));
        Assert.Equal("\"x\": must be less than 1e2", Outside(Schema("x", """{ "type": "number", "exclusiveMaximum": 1e2 }"""), Answer(Raw("""{ "x": 100.0 }"""))));
    }

    [Theory]
    [InlineData("""{ "type": "string", "minLength": 3 }""", "ab", "\"t\": must be at least 3 characters")]
    [InlineData("""{ "type": "string", "maxLength": 3 }""", "abcd", "\"t\": must be at most 3 characters")]
    [InlineData("""{ "type": "string", "pattern": "^[A-Z]{3}-[0-9]+$" }""", "ABC-x", "\"t\": does not match the form the question asks for")]
    [InlineData("""{ "type": "string", "format": "email" }""", "someone@example", "\"t\": must be an email address")]
    [InlineData("""{ "type": "string", "format": "uri" }""", "ftp://example.com", "\"t\": must be a URL starting http:// or https://")]
    [InlineData("""{ "type": "string", "format": "date" }""", "2026-02-30", "\"t\": must be a date, as YYYY-MM-DD")]
    [InlineData("""{ "type": "string", "format": "date-time" }""", "2026-11-04 19:30", "\"t\": must be a date and time, as YYYY-MM-DDTHH:MM")]
    // What the CLI's check refuses too (cli-main.js PIo and Date.parse): an hour-only offset, a
    // lower-case t, a three-digit or out-of-range offset, and an offset after a z.
    [InlineData("""{ "type": "string", "format": "date-time" }""", "2026-11-04T19:58:34+05", "\"t\": must be a date and time, as YYYY-MM-DDTHH:MM")]
    [InlineData("""{ "type": "string", "format": "date-time" }""", "2026-11-04t19:58", "\"t\": must be a date and time, as YYYY-MM-DDTHH:MM")]
    [InlineData("""{ "type": "string", "format": "date-time" }""", "2026-11-04T19:58:34+053", "\"t\": must be a date and time, as YYYY-MM-DDTHH:MM")]
    [InlineData("""{ "type": "string", "format": "date-time" }""", "2026-11-04T19:58:34+0560", "\"t\": must be a date and time, as YYYY-MM-DDTHH:MM")]
    [InlineData("""{ "type": "string", "format": "date-time" }""", "2026-11-04T19:58:34+2400", "\"t\": must be a date and time, as YYYY-MM-DDTHH:MM")]
    [InlineData("""{ "type": "string", "format": "date-time" }""", "2026-11-04T19:58:34z+0530", "\"t\": must be a date and time, as YYYY-MM-DDTHH:MM")]
    public void Text_outside_its_length_pattern_or_format_is_refused_before_any_key(string property, string text, string why)
    {
        Assert.Equal(why, Outside(Schema("t", property), Answer(new { t = text })));
    }

    [Theory]
    [InlineData("""{ "type": "string", "minLength": 3, "maxLength": 3 }""", "abc")]
    [InlineData("""{ "type": "string", "pattern": "^[A-Z]{3}-[0-9]+$" }""", "ABC-42")]
    [InlineData("""{ "type": "string", "format": "email" }""", "someone@example.com")]
    [InlineData("""{ "type": "string", "format": "uri" }""", "https://example.com/a")]
    [InlineData("""{ "type": "string", "format": "date" }""", "2028-02-29")]
    [InlineData("""{ "type": "string", "format": "date-time" }""", "2026-11-04T19:30:05.123-08:00")]
    // Date.parse reads any number of fraction digits.
    [InlineData("""{ "type": "string", "format": "date-time" }""", "2026-11-04T19:58:34.746123Z")]
    // And an offset without its colon, or a lower-case z, which it takes too (review of #844).
    [InlineData("""{ "type": "string", "format": "date-time" }""", "2026-11-04T19:58:34+0530")]
    [InlineData("""{ "type": "string", "format": "date-time" }""", "2026-11-04T19:58-0800")]
    [InlineData("""{ "type": "string", "format": "date-time" }""", "2026-11-04T19:58:34.746123+2359")]
    [InlineData("""{ "type": "string", "format": "date-time" }""", "2026-11-04T19:58:34.5z")]
    // The CLI knows four formats and ignores the rest; so does the host.
    [InlineData("""{ "type": "string", "format": "hostname" }""", "not a host name")]
    public void Text_inside_its_constraints_is_typed_as_before(string property, string text)
    {
        Assert.Equal([(text, true)], Keys(Plan(Schema("t", property), Answer(new { t = text }))));
    }

    [Fact]
    public void Lengths_count_UTF16_code_units_as_JavaScript_does()
    {
        Assert.Equal("\"t\": must be at most 1 characters", Outside(Schema("t", """{ "type": "string", "maxLength": 1 }"""), Answer(new { t = "\U0001F600" })));
    }

    [Fact]
    public void A_pattern_is_read_as_JavaScript_reads_it_not_as_dotnet_does()
    {
        // JavaScript's \s takes a no-break space, where .NET's ECMAScript mode does not.
        Outside(Schema("t", """{ "type": "string", "pattern": "^\\S+$" }"""), Answer(new { t = "a\u00A0b" }));
        // And its $ is the end, never the place before a final line break.
        var (_, question) = Ask(Schema("t", """{ "type": "string", "pattern": "^abc$" }"""));
        Assert.NotNull(Answers.Check(question, Answer(new { t = "abc\n" })));
    }

    // Found in review of #844: .NET's \w takes U+0130 even in ECMAScript mode, and JavaScript's
    // does not; its \d is ASCII too.
    [Fact]
    public void Word_and_digit_classes_are_JavaScripts_ASCII_ones()
    {
        Assert.Equal("\"t\": does not match the form the question asks for", Outside(Schema("t", """{ "type": "string", "pattern": "^\\w+$" }"""), Answer(new { t = "\u0130" })));
        Assert.Equal("\"t\": does not match the form the question asks for", Outside(Schema("t", """{ "type": "string", "pattern": "^[\\w.]+$" }"""), Answer(new { t = "a\u0130" })));
        Assert.Equal("\"t\": does not match the form the question asks for", Outside(Schema("t", """{ "type": "string", "pattern": "^\\d+$" }"""), Answer(new { t = "\u0661" })));
        Assert.Equal([("a_1", true)], Keys(Plan(Schema("t", """{ "type": "string", "pattern": "^\\w+$" }"""), Answer(new { t = "a_1" }))));
        // A hyphen first or last in a class is itself in both engines, so the class stays checkable.
        Assert.Equal([("a-1", true)], Keys(Plan(Schema("t", """{ "type": "string", "pattern": "^[\\w-]+$" }"""), Answer(new { t = "a-1" }))));
        Assert.Equal([("-b", true)], Keys(Plan(Schema("t", """{ "type": "string", "pattern": "^[-\\w]+$" }"""), Answer(new { t = "-b" }))));
        // A { that starts no quantifier is a literal after $, and a group repeated is still a group.
        Assert.Equal([("ab", true)], Keys(Plan(Schema("t", """{ "type": "string", "pattern": "^(?:a|b)+$|${x}" }"""), Answer(new { t = "ab" }))));
        Assert.Equal([("aa", true)], Keys(Plan(Schema("t", """{ "type": "string", "pattern": "^(a$|a)+$" }"""), Answer(new { t = "aa" }))));
        Assert.Equal("\"t\": does not match the form the question asks for", Outside(Schema("t", """{ "type": "string", "pattern": "^[\\w-]+$" }"""), Answer(new { t = "a\u0130" })));
    }
    [Fact]
    public void Text_is_checked_as_it_would_be_typed_with_its_line_breaks_flattened()
    {
        // The line break passes a pattern that refuses spaces; the space it is typed as does not.
        var arguments = Schema("t", """{ "type": "string", "pattern": "^[^ ]+$" }""");
        var (form, question) = Ask(arguments);
        Assert.Null(Answers.Check(question, Answer(new { t = "a\nb" })));
        Assert.Equal("\"t\": does not match the form the question asks for", CopilotPlanner.Plan(form, Answer(new { t = "a\nb" })).Problem);
    }

    [Fact]
    public void A_checkbox_list_ticked_outside_its_item_counts_is_refused_before_any_key()
    {
        var arguments = Schema("l", """{ "type": "array", "minItems": 2, "maxItems": 2, "items": { "enum": ["a", "b", "c"] } }""");
        Assert.Equal("\"l\": choose at least 2", Outside(arguments, Answer(new { l = new[] { "a" } })));
        Assert.Equal("\"l\": choose at most 2", Outside(arguments, Answer(new { l = new[] { "a", "b", "c" } })));
        Assert.Equal(2, Keys(Plan(arguments, Answer(new { l = new[] { "a", "c" } }))).Count);
    }

    [Theory]
    // A bound JavaScript would hold as another number, or none at all.
    [InlineData("""{ "type": "integer", "maximum": 9007199254740993 }""")]
    [InlineData("""{ "type": "number", "minimum": "1" }""")]
    [InlineData("""{ "type": "number", "multipleOf": 0 }""")]
    [InlineData("""{ "type": "string", "maxLength": 1.5 }""")]
    [InlineData("""{ "type": "string", "minLength": -1 }""")]
    [InlineData("""{ "type": "array", "maxItems": "2", "items": { "enum": ["a"] } }""")]
    // Patterns .NET would read otherwise: a lookbehind, an escape that is a letter in JavaScript,
    // class subtraction, JavaScript's empty class, and one that does not compile at all.
    [InlineData("""{ "type": "string", "pattern": "(?<=a)b" }""")]
    [InlineData("""{ "type": "string", "pattern": "^\\a$" }""")]
    [InlineData("""{ "type": "string", "pattern": "[a-z-[aeiou]]" }""")]
    [InlineData("""{ "type": "string", "pattern": "[]" }""")]
    [InlineData("""{ "type": "string", "pattern": "(" }""")]
    [InlineData("""{ "type": "string", "format": 5 }""")]
    // Patterns JavaScript and .NET read differently (review of #844): a back-reference into a
    // capture a later pass clears, \s beside a class hyphen, a word boundary.
    [InlineData("""{ "type": "string", "pattern": "^(a|(b))+\\2$" }""")]
    [InlineData("""{ "type": "string", "pattern": "^[\\s-\\uFFFF]+$" }""")]
    [InlineData("""{ "type": "string", "pattern": "\\bword\\b" }""")]
    // Patterns JavaScript refuses ("Nothing to repeat"): an end or start anchor quantified, which
    // .NET took, the end even as a lookahead that matched (review of #844).
    [InlineData("""{ "type": "string", "pattern": "^a$?" }""")]
    [InlineData("""{ "type": "string", "pattern": "^a${2}" }""")]
    [InlineData("""{ "type": "string", "pattern": "a$*" }""")]
    [InlineData("""{ "type": "string", "pattern": "a$+?" }""")]
    [InlineData("""{ "type": "string", "pattern": "a${1,}" }""")]
    [InlineData("""{ "type": "string", "pattern": "^?a" }""")]
    [InlineData("""{ "type": "string", "pattern": "^{1,2}a" }""")]
    [InlineData("""{ "type": "string", "pattern": "a\\b?" }""")]
    // A quantified lookahead: JavaScript, but not shown to repeat the same in .NET.
    [InlineData("""{ "type": "string", "pattern": "(?=a)?a" }""")]
    [InlineData("""{ "type": "string", "pattern": "(?!b){2}a" }""")]
    // A list of choices with text limits, which its options and Other are not checked against.
    [InlineData("""{ "type": "string", "enum": ["ok", "toolong"], "maxLength": 2 }""")]
    [InlineData("""{ "type": "array", "items": { "enum": ["ok", "toolong"], "maxLength": 2 } }""")]
    // Numbers limited by what the host does not check (review of #844): a number box's listed
    // values, and options under number limits. The CLI draws a number with an enum as a box that
    // takes any number and records it (walked as a list, its arrows added one, #700).
    [InlineData("""{ "type": "integer", "enum": [1, 2, 3] }""")]
    [InlineData("""{ "type": "integer", "enum": [1, 2, 3], "default": 2 }""")]
    [InlineData("""{ "type": "number", "oneOf": [{ "const": 1 }, { "const": 2 }] }""")]
    [InlineData("""{ "type": "number", "const": 4 }""")]
    [InlineData("""{ "type": "string", "const": "only" }""")]
    [InlineData("""{ "type": "array", "items": { "enum": [1, 2, 3], "minimum": 2 } }""")]
    [InlineData("""{ "type": "array", "items": { "type": "number", "enum": [1, 1.5], "multipleOf": 1 } }""")]
    [InlineData("""{ "type": "array", "items": { "enum": [1, 2], "exclusiveMaximum": 2 } }""")]
    [InlineData("""{ "type": "string", "enum": [1, 2], "maximum": 1 }""")]
    // A default outside the field's own constraints, which is the only answer it can be given.
    [InlineData("""{ "type": "integer", "minimum": 5, "default": 3 }""")]
    [InlineData("""{ "type": "string", "maxLength": 2, "default": "none" }""")]
    public void A_field_whose_constraints_cannot_be_checked_exactly_or_whose_default_breaks_them_is_answered_at_the_machine(string property)
    {
        using var doc = JsonDocument.Parse(Schema("f", property));
        Assert.True(QuestionMapper.FromCopilotAskUser("call-1", doc.RootElement, Fixture.T0).AnswerAtMachine);
        var (form, problem) = CopilotForm.Read(doc.RootElement);
        Assert.Null(form);
        Assert.EndsWith("; answer it at the machine", problem);
    }

    static string Fields(int count, string property) =>
        $$"""{ "message": "m", "requestedSchema": { "properties": { {{string.Join(", ", Enumerable.Range(0, count).Select(i => $"\"f{i}\": {property}"))}} } } }""";

    // Found in review of #844: each field's pattern ran on its default with 250 ms a match, and the
    // question was read twice, so an agent's ask_user of many slow patterns held up the refresh of
    // every session. All of a question's patterns now share one 250 ms.
    [Fact]
    public void Many_slow_patterns_in_one_question_cost_one_budget_and_answer_it_at_the_machine()
    {
        var slow = $$"""{ "type": "string", "pattern": "^(a+)+$", "default": "{{new string('a', 40)}}!" }""";
        using var doc = JsonDocument.Parse(Fields(40, slow));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var question = QuestionMapper.FromCopilotAskUser("call-1", doc.RootElement, Fixture.T0);
        var (form, problem) = CopilotForm.Read(doc.RootElement);
        clock.Stop();

        Assert.True(question.AnswerAtMachine);
        Assert.Null(form);
        Assert.EndsWith("has limits the host cannot check exactly; answer it at the machine", problem);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"took {clock.Elapsed}");
    }

    // Found in review of #844: the email check's classes overlap on dots, so a backtracking match of
    // a default made of `a@`, a run of dots and a space was quadratic, and it ran while the question
    // was read, outside the pattern budget. Measured at 5,000, 10,000 and 20,000 dots it took 29, 117
    // and 452 ms; the generated matcher's vectorised scans only shrink the constant, so at n and 2n of
    // a million it ran for minutes, far past a bound the linear match keeps with a wide margin. The
    // default is still not an email.
    [Fact]
    public async Task An_email_default_of_a_long_run_of_dots_is_read_in_linear_time()
    {
        foreach (var n in new[] { 1_000_000, 2_000_000 })
        {
            using var doc = JsonDocument.Parse(Schema("e", $$"""{ "type": "string", "format": "email", "default": "a@{{new string('.', n)}} " }"""));
            var root = doc.RootElement.Clone();
            // Past the bound, WaitAsync throws: the old match was still running.
            var question = await Task.Run(() => QuestionMapper.FromCopilotAskUser("call-1", root, Fixture.T0)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(question.AnswerAtMachine);
        }
        Assert.Equal([("a@b.c", true)], Keys(Plan(Schema("t", """{ "type": "string", "format": "email" }"""), Answer(new { t = "a@b.c" }))));
        Assert.Equal("\"t\": must be an email address", Outside(Schema("t", """{ "type": "string", "format": "email" }"""), Answer(new { t = "a@b." })));
    }
    [Fact]
    public void Many_quick_patterns_in_one_question_fit_its_budget()
    {
        var (_, question) = Ask(Fields(50, """{ "type": "string", "pattern": "^[a-z]+$" }"""));
        Assert.Null(question.AnswerAtMachine);
        Assert.Equal(50, question.Fields.Count(f => f.Constraints?.Pattern is not null));
    }

    [Fact]
    public void A_question_carries_its_fields_constraints_so_a_client_can_check_them_first()
    {
        var (_, question) = Ask(Schema("n", Count));
        var limits = Assert.Single(question.Fields).Constraints!;
        Assert.Equal("""{"minimum":2,"maximum":10,"multipleOf":2,"integer":true}""", JsonSerializer.Serialize(limits, ProtocolJson.Options));
        Assert.Null(Ask(Schema("t", """{ "type": "string", "format": "hostname" }""")).Question.Fields[0].Constraints);
    }
    // Fields as JSON text, so a number keeps the spelling it was given.
    static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void A_number_that_only_rounds_to_the_default_is_not_the_default()
    {
        // A number field, not Days's integer one, which the shared check refuses first for not being whole.
        const string Amount = """{ "message": "m", "requestedSchema": { "properties": { "days": { "type": "number", "default": 3 } } } }""";
        Assert.Contains("pre-filled default", Refusal(Amount, Answer(Raw("""{ "days": 3.0000000000000000000000000000001 }"""))));
        Assert.Contains("pre-filled default", Refusal(Amount, Answer(Raw("""{ "days": 3e-29 }"""))));
        Assert.Equal("\"days\": must be a whole number", Answers.Check(Ask(Days).Question, Answer(Raw("""{ "days": 3e-29 }"""))));
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
        // minItems is now a constraint a client checks too (#766); the plan still refuses on its own.
        var (form, question) = Ask(AtLeastOne);
        var none = Answer(new { many = Array.Empty<string>() });
        Assert.Equal("\"many\": choose at least 1", Answers.Check(question, none));
        Assert.Contains("at least one", CopilotPlanner.Plan(form, none).Problem);
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
