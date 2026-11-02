using System.Text;
using System.Text.Json;
using AgentBridge.Host.Core.Parsing;
using AgentBridge.Protocol;
using Xunit;

public class QuestionMappingTests
{
    static Question Ask(string argumentsJson)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        return QuestionMapper.FromCopilotAskUser("q", doc.RootElement, Fixture.T0);
    }

    [Fact]
    public void Every_schema_field_type_maps_onto_the_contract_field_in_order()
    {
        var q = Ask("""
        {
          "message": "Tell me",
          "requestedSchema": {
            "properties": {
              "plain": { "type": "string", "enum": ["x", "y"] },
              "named": { "type": "string", "enum": ["s6", "s7"], "enumNames": ["S6: keys", "S7: relay"] },
              "titled": { "type": "string", "title": "Next step", "description": "Pick one", "default": "a",
                          "oneOf": [ { "const": "a", "title": "Do A (Recommended)" }, { "const": "b", "title": "Do B" } ] },
              "untyped": { "oneOf": [ { "const": "u", "title": "U" } ] },
              "ok": { "type": "boolean", "default": true },
              "many": { "type": "array", "items": { "type": "string", "enum": ["p", "q"] } },
              "manyTitled": { "type": "array", "items": { "anyOf": [ { "const": "m1", "title": "First" }, { "const": "m2", "title": "Second" } ] } },
              "count": { "type": "integer", "default": 3 },
              "ratio": { "type": "number" },
              "notes": { "type": "string", "title": "Notes" }
            },
            "required": ["titled", "notes"]
          }
        }
        """);

        Assert.Equal(QuestionKind.Form, q.Kind);
        Assert.Equal("Tell me", q.Message);
        Assert.Equal(["plain", "named", "titled", "untyped", "ok", "many", "manyTitled", "count", "ratio", "notes"], q.Fields.Select(f => f.Name));
        Assert.Equal(
            [FieldType.Choice, FieldType.Choice, FieldType.Choice, FieldType.Choice, FieldType.Boolean, FieldType.Multi, FieldType.Multi, FieldType.Number, FieldType.Number, FieldType.Text],
            q.Fields.Select(f => f.Type));
        Assert.Equal([new Option("x"), new Option("y")], q.Fields[0].Options!);
        Assert.Equal([new Option("s6", "S6: keys"), new Option("s7", "S7: relay")], q.Fields[1].Options!);
        var titled = q.Fields[2];
        Assert.Equal(("Next step", "Pick one", "a", true), (titled.Title, titled.Description, titled.Default!.Value.GetString(), titled.Required));
        Assert.Equal([new Option("a", "Do A (Recommended)"), new Option("b", "Do B")], titled.Options!);
        Assert.Equal([new Option("u", "U")], q.Fields[3].Options!);
        Assert.True(q.Fields[4].Default!.Value.GetBoolean());
        Assert.Equal([new Option("p"), new Option("q")], q.Fields[5].Options!);
        Assert.Equal([new Option("m1", "First"), new Option("m2", "Second")], q.Fields[6].Options!);
        Assert.Equal(3, q.Fields[7].Default!.Value.GetInt32());
        Assert.Null(q.Fields[9].Options);
        Assert.True(q.Fields[9].Required);
        Assert.Null(q.Fields[0].Required);
    }

    [Fact]
    public void The_older_question_and_choices_shape_becomes_one_choice_field_that_also_takes_free_text()
    {
        var q = Ask("""{ "question": "Proceed?", "choices": ["Yes", "No"] }""");

        Assert.Equal("Proceed?", q.Message);
        var field = Assert.Single(q.Fields);
        Assert.Equal((FieldType.Choice, true), (field.Type, field.AllowOther));
        Assert.Equal(["Yes", "No"], field.Options!.Select(o => o.Value));
        Assert.Equal(["A", "B"], Assert.Single(Ask("""{ "question": "x", "choices": "[\"A\",\"B\"]", "allow_freeform": false }""").Fields).Options!.Select(o => o.Value));
    }

    [Fact]
    public void A_question_that_is_one_option_list_also_takes_words_through_Other_and_a_form_does_not()
    {
        var lone = Assert.Single(Ask("""{ "message": "m", "requestedSchema": { "properties": { "pick": { "type": "string", "enum": ["a", "b"] } } } }""").Fields);
        var form = Ask("""{ "message": "m", "requestedSchema": { "properties": { "pick": { "type": "string", "enum": ["a", "b"] }, "go": { "type": "boolean" } } } }""");
        var list = Assert.Single(Ask("""{ "message": "m", "requestedSchema": { "properties": { "many": { "type": "array", "items": { "enum": ["a", "b"] } } } } }""").Fields);

        Assert.True(lone.AllowOther);
        Assert.Null(form.Fields[0].AllowOther);
        Assert.Null(list.AllowOther);
    }

    [Fact]
    public void A_schema_written_as_a_string_is_parsed_and_a_plain_message_has_no_fields()
    {
        var q = Ask("""{ "message": "m", "requestedSchema": "{\"properties\":{\"go\":{\"type\":\"boolean\"}}}" }""");
        Assert.Equal(FieldType.Boolean, Assert.Single(q.Fields).Type);

        Assert.Empty(Ask("""{ "message": "Just tell me what you think" }""").Fields);
    }

    [Fact]
    public void A_question_message_cut_to_its_limit_is_marked_truncated_and_one_that_fits_is_not()
    {
        var cut = Ask($$"""{ "message": "{{new string('m', Limits.QuestionMessage + 10)}}" }""");
        var whole = Ask("""{ "message": "short" }""");
        var approval = QuestionMapper.Approval("a", new string('p', Limits.QuestionMessage + 1), Fixture.T0);
        using var claude = JsonDocument.Parse($$"""{ "questions": [ { "question": "{{new string('c', Limits.QuestionMessage + 1)}}", "options": [] } ] }""");

        Assert.True(cut.Truncated);
        Assert.Equal(Limits.QuestionMessage, Encoding.UTF8.GetByteCount(cut.Message));
        Assert.Null(whole.Truncated);
        Assert.True(approval.Truncated);
        Assert.True(QuestionMapper.FromClaudeAskUserQuestion("c", claude.RootElement, Fixture.T0).Truncated);
    }

    [Fact]
    public void A_question_round_trips_through_the_wire_format()
    {
        var q = Ask("""{ "message": "m", "requestedSchema": { "properties": { "n": { "type": "number", "default": 1.5 } } } }""");

        var json = JsonSerializer.Serialize(q, ProtocolJson.Options);
        var back = JsonSerializer.Deserialize<Question>(json, ProtocolJson.Options)!;

        Assert.Contains("\"type\":\"number\"", json);
        Assert.Equal(1.5, back.Fields[0].Default!.Value.GetDouble());
    }
}

public class LimitsTests
{
    const string Id = "77777777-7777-4777-8777-777777777777";

    [Fact]
    public async Task Long_text_is_cut_to_its_byte_limits_keeping_the_end_of_responses_without_splitting_characters()
    {
        using var f = new Fixture();
        var events = f.CopilotSession(Id, 100, name: null);
        var emoji = string.Concat(Enumerable.Repeat("😀", 10_000));
        var cjk = string.Concat(Enumerable.Repeat("漢", 1_000));
        // The response is CJK (three bytes each, sent raw); emoji go where the budget has room,
        // since System.Text.Json writes characters outside the BMP as \u escapes (12 bytes each).
        Fixture.Append(events, Copilot.User(emoji, 1), Copilot.Assistant("start" + string.Concat(Enumerable.Repeat(cjk, 20)) + "END", 2, reasoning: "r" + emoji),
            Copilot.ToolStart("c1", "powershell", new { command = cjk }, 3));
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source();

        await source.RefreshAsync();

        var s = source.Get(Id)!;
        Assert.True(s.Activity.ResponseTruncated);
        Assert.EndsWith("END", s.Activity.Response);
        Assert.InRange(Encoding.UTF8.GetByteCount(s.Activity.Response!), Limits.Response - 3, Limits.Response);
        Assert.InRange(Encoding.UTF8.GetByteCount(s.Activity.Reasoning!), Limits.Reasoning - 3, Limits.Reasoning);
        Assert.InRange(Encoding.UTF8.GetByteCount(s.Title!), Limits.Title - 3, Limits.Title);
        Assert.InRange(Encoding.UTF8.GetByteCount(s.Activity.UserMessage!), Limits.UserMessage - 3, Limits.UserMessage);
        Assert.InRange(Encoding.UTF8.GetByteCount(s.Activity.CurrentTool!.Summary!), Limits.ToolSummary - 2, Limits.ToolSummary);
        foreach (var text in new[] { s.Activity.Response!, s.Activity.Reasoning!, s.Title!, s.Activity.UserMessage! })
        {
            Assert.False(char.IsLowSurrogate(text[0]));
            Assert.False(char.IsHighSurrogate(text[^1]));
            Assert.DoesNotContain('\uFFFD', text);
        }
    }

    [Fact]
    public async Task A_session_with_a_huge_question_still_fits_the_budget_and_says_its_question_was_cut()
    {
        using var f = new Fixture();
        var events = f.CopilotSession(Id, 100);
        var properties = Enumerable.Range(0, 300).ToDictionary(i => "field" + i, i => (object)new
        {
            type = "string",
            title = "A long title for field " + i + new string('t', 100),
            oneOf = Enumerable.Range(0, 5).Select(o => new { @const = "v" + o, title = "Option " + o + new string('o', 40) }).ToArray(),
        });
        Fixture.Append(events, Copilot.User("ask", 1), Copilot.Assistant(new string('r', 20_000), 2, reasoning: new string('k', 5000)),
            Copilot.ToolStart("q1", "ask_user", new { message = new string('m', 9000), requestedSchema = new { properties } }, 3));
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source();

        await source.RefreshAsync();

        var full = source.Get(Id)!;
        var listed = Assert.Single(source.Snapshot());
        Assert.InRange(ProtocolJson.ByteCount(full), 1, SessionBudget.MaxSessionBytes);
        Assert.InRange(ProtocolJson.ByteCount(listed), 1, SessionBudget.MaxSessionBytes);
        Assert.True(listed.Question!.Truncated);
        Assert.Equal(SessionStatus.Waiting, listed.Status);
        Assert.Null(listed.Activity.History);
    }

    // #175: the HA connector keeps Home Assistant's limits itself and reads a session before the
    // protocol budget, whose order could otherwise drop the main agent's own reply from its card.
    [Fact]
    public async Task A_session_over_the_budget_is_also_kept_whole_within_its_field_limits_for_the_home_assistant_connector()
    {
        using var f = new Fixture();
        var events = f.CopilotSession(Id, 100);
        // Quotes, which JSON escapes, put the reply alone near the budget.
        Fixture.Append(events, Copilot.User("ask", 1), Copilot.Assistant(new string('"', 20_000), 2, reasoning: new string('k', 5000)));
        Fixture.Append(events, [.. Enumerable.Range(0, 60).Select(i => Copilot.ToolStart("t" + i, "view", new { path = new string('p', 2000) }, 3 + i))]);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source();

        await source.RefreshAsync();

        var budgeted = source.Get(Id)!;
        var whole = source.GetUnbudgeted(Id)!;
        Assert.InRange(ProtocolJson.ByteCount(budgeted), 1, SessionBudget.MaxSessionBytes);
        Assert.True(ProtocolJson.ByteCount(whole) > SessionBudget.MaxSessionBytes);
        Assert.Equal(16 * 1024, whole.Activity.Response!.Length);
        Assert.Equal(JsonSerializer.Serialize(SessionBudget.Fit(whole), ProtocolJson.Options), JsonSerializer.Serialize(budgeted, ProtocolJson.Options));
    }

    [Fact]
    public void A_tool_summary_prefers_the_description_then_the_main_argument_on_one_line()
    {
        static string? Summary(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return ToolSummary.From(doc.RootElement);
        }

        Assert.Equal("Build the app", Summary("""{ "command": "dotnet build", "description": "Build the app" }"""));
        Assert.Equal("git status", Summary("""{ "command": "git status\ngit log" }"""));
        Assert.Equal("/src/a.cs", Summary("""{ "path": "/src/a.cs", "old_str": "x" }"""));
        Assert.Equal("/src/b.cs", Summary("""{ "file_path": "/src/b.cs" }"""));
        Assert.Equal("TODO", Summary("""{ "pattern": "TODO", "glob": "*.cs" }"""));
        Assert.Equal("npm test", Summary("""{ "command": ["bash", "-lc", "npm test"] }"""));
        Assert.Equal("echo hi", Summary("\"{\\\"cmd\\\":\\\"echo hi\\\"}\""));
        Assert.Equal("first string", Summary("""{ "n": 3, "other": "first string" }"""));
        Assert.Null(Summary("""{ "n": 3 }"""));
        Assert.Equal(Limits.ToolSummary, Encoding.UTF8.GetByteCount(Summary($$"""{ "command": "{{new string('x', 500)}}" }""")!));
    }
}
