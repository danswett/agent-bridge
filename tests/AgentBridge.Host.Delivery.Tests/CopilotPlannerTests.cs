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
    public void A_choice_moves_from_the_default_row_to_the_chosen_one_then_commits_with_Enter()
    {
        Assert.Equal([(D, true)], Keys(Plan(Pick, Answer(new { pick = "d" }))));
        Assert.Equal([(U + U, true)], Keys(Plan(Pick, Answer(new { pick = "a" }))));
        Assert.Equal([("", true)], Keys(Plan(Pick, Answer(new { pick = "c" }))));
    }

    [Fact]
    public void A_choice_without_a_default_starts_on_its_first_row_and_is_recorded_by_its_value_not_its_label()
    {
        var plan = Plan("""
            { "message": "m", "requestedSchema": { "properties": { "next": { "type": "string",
              "oneOf": [ { "const": "s4", "title": "S4 relay (Recommended)" }, { "const": "s6", "title": "S6 keys" } ] } } } }
            """, Answer(new { next = "s6" }));

        Assert.Equal([(D, true)], Keys(plan));
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

        Assert.Equal([(D + D, true), (U, true), ("note here", true), ("42", true)], Keys(plan));
        Assert.All(plan.Keys.Sends, s => Assert.Equal((CopilotPlanner.FieldEnterDelayMs, CopilotPlanner.AfterFieldMs), (s.EnterDelayMs, s.PauseAfterMs)));
        Assert.Equal(["colour", "go", "notes", "count"], plan.Fields.Select(f => f.Name));
    }

    [Fact]
    public void A_boolean_is_a_Yes_No_list_whose_caret_starts_on_its_default()
    {
        const string NoByDefault = """{ "message": "m", "requestedSchema": { "properties": { "go": { "type": "boolean", "default": false } } } }""";
        const string Plain = """{ "message": "m", "requestedSchema": { "properties": { "go": { "type": "boolean" } } } }""";

        Assert.Equal([(U, true)], Keys(Plan(NoByDefault, Answer(new { go = true }))));
        Assert.Equal([("", true)], Keys(Plan(NoByDefault, Answer(new { go = false }))));
        Assert.Equal([(D, true)], Keys(Plan(Plain, Answer(new { go = false }))));
        Assert.False(Assert.IsType<Expected.Flag>(Plan(Plain, Answer(new { go = false })).Fields[0].Value).Value);
    }

    [Fact]
    public void A_multi_select_list_is_walked_once_top_to_bottom_toggling_only_rows_that_must_change()
    {
        const string Many = """{ "message": "m", "requestedSchema": { "properties": { "many": { "type": "array", "items": { "enum": ["a", "b", "c", "d"] }, "default": ["b", "c"] } } } }""";

        var plan = Plan(Many, Answer(new { many = new[] { "a", "c", "d" } }));

        // a on, b off (it opens ticked), c left alone, d on; only the last carries Enter.
        Assert.Equal([(" ", false), (D + " ", false), (D + D + " ", true)], Keys(plan));
        Assert.Equal([CopilotPlanner.ToggleGapMs, CopilotPlanner.ToggleGapMs, CopilotPlanner.AfterFieldMs], plan.Keys.Sends.Select(s => s.PauseAfterMs));
        Assert.Equal(["a", "c", "d"], Assert.IsType<Expected.Set>(plan.Fields[0].Value).Raws.Select(r => r.GetString()));
    }

    [Fact]
    public void A_multi_select_answer_that_is_its_defaults_is_accepted_with_Enter_alone()
    {
        const string Many = """{ "message": "m", "requestedSchema": { "properties": { "many": { "type": "array", "items": { "enum": ["a", "b"] }, "default": ["b"] } } } }""";

        Assert.Equal([("", true)], Keys(Plan(Many, Answer(new { many = new[] { "b" } }))));
    }

    [Fact]
    public void An_empty_multi_select_answer_is_typed_only_where_the_schema_allows_none()
    {
        const string AnyOrNone = """{ "message": "m", "requestedSchema": { "properties": { "many": { "type": "array", "items": { "enum": ["a", "b"] } } } } }""";
        const string AtLeastOne = """{ "message": "m", "requestedSchema": { "properties": { "many": { "type": "array", "minItems": 1, "items": { "enum": ["a", "b"] } } } } }""";

        Assert.Equal([("", true)], Keys(Plan(AnyOrNone, Answer(new { many = Array.Empty<string>() }))));
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
        var (form, _) = Ask("""{ "message": "m", "requestedSchema": { "properties": { "pick": { "enum": ["a"] }, "go": { "type": "boolean" } } } }""");
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
        const string Form = """{ "message": "m", "requestedSchema": { "properties": { "pick": { "enum": ["a", "b"] }, "notes": { "type": "string" } } } }""";

        var plan = Plan(Form, Answer(new { pick = "b" }));
        Assert.Equal([(D, true), ("", true)], Keys(plan));
        Assert.IsType<Expected.Omitted>(plan.Fields[1].Value);

        Assert.Contains("cannot leave \"pick\" unanswered", Refusal(Form, Answer(new { notes = "hi" })));
    }

    [Fact]
    public void The_older_choices_format_and_a_default_that_names_no_option_are_refused_before_planning()
    {
        using var legacy = JsonDocument.Parse("""{ "question": "Go?", "choices": ["Yes", "No"] }""");
        Assert.Contains("older Copilot format", CopilotForm.Read(legacy.RootElement).Problem);

        using var lost = JsonDocument.Parse("""{ "message": "m", "requestedSchema": { "properties": { "pick": { "enum": ["a", "b"], "default": "z" } } } }""");
        Assert.Contains("caret starts is unknown", CopilotForm.Read(lost.RootElement).Problem);
    }

    [Fact]
    public void A_null_default_leaves_the_caret_on_the_first_row_and_a_schema_written_as_text_is_read()
    {
        var plan = Plan("""{ "message": "m", "requestedSchema": "{\"properties\":{\"pick\":{\"enum\":[\"a\",\"b\"],\"default\":null}}}" }""", Answer(new { pick = "b" }));

        Assert.Equal([(D, true)], Keys(plan));
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
