using System.Text;
using System.Text.Json;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Copilot;
using Xunit;

// Reading one tool call out of a transcript by its id, and judging what it recorded.
public sealed class CopilotTranscriptTests : IDisposable
{
    readonly Fixture f = new();
    readonly string events;

    public CopilotTranscriptTests() => events = f.CopilotSession("11111111-1111-4111-8111-111111111111", 100);

    public void Dispose() => f.Dispose();

    static readonly object Schema = new { properties = new { pick = new { type = "string", @enum = new[] { "a", "b" } } } };

    static string Recorded(string id, object content, int s) =>
        Copilot.Event("human_response.recorded", new { toolCallId = id, requestId = "r", actor = "human_response", response = new { responseKind = "ask_user", content } }, s);

    [Fact]
    public void A_pending_question_is_found_with_its_arguments_and_nothing_answering_it()
    {
        Fixture.Append(events, Copilot.User("go", 1), Copilot.ToolStart("q1", "ask_user", new { message = "m", requestedSchema = Schema }, 2));

        var call = CopilotTranscript.Find(events, "q1");

        Assert.Equal("m", call.Arguments!.Value.GetProperty("message").GetString());
        Assert.True(call.IsMainAgent);
        Assert.Null(call.Answer);
        Assert.False(call.SessionEnded);
        Assert.Equal(new FileInfo(events).Length, call.ScannedTo);
    }

    [Fact]
    public void An_answer_to_that_call_is_found_by_id_and_answers_to_other_calls_are_not()
    {
        Fixture.Append(events, Copilot.ToolStart("q1", "ask_user", new { message = "m" }, 1), Recorded("q2", new { pick = "a" }, 2), Copilot.ToolResult("q9", "User responded: no", 3));
        Assert.Null(CopilotTranscript.Find(events, "q1").Answer);

        Fixture.Append(events, Recorded("q1", new { pick = "b" }, 4), Copilot.ToolResult("q1", "User responded: b", 5));
        var answer = CopilotTranscript.Find(events, "q1").Answer!;

        Assert.Equal("b", answer.HumanContent!.Value.GetProperty("pick").GetString());
        Assert.Equal("User responded: b", answer.ResultText);
    }

    [Fact]
    public void The_sessions_own_abort_ends_its_question_and_a_subagents_does_not()
    {
        Fixture.Append(events, Copilot.ToolStart("q1", "ask_user", new { message = "m" }, 1), Copilot.Event("abort", new { reason = "x" }, 2, agentId: "helper"));
        Assert.False(CopilotTranscript.Find(events, "q1").SessionEnded);

        Fixture.Append(events, Copilot.Event("abort", new { reason = "user" }, 3));
        Assert.True(CopilotTranscript.Find(events, "q1").SessionEnded);
    }

    [Fact]
    public void A_question_far_behind_a_large_tail_is_still_found()
    {
        Fixture.Append(events, Copilot.ToolStart("q1", "ask_user", new { message = "deep" }, 1));
        var padding = Copilot.ToolResult("c", new string('x', 1 << 20), 2);
        Fixture.Append(events, Enumerable.Repeat(padding, 6).ToArray());

        Assert.Equal("deep", CopilotTranscript.Find(events, "q1").Arguments!.Value.GetProperty("message").GetString());
    }

    [Fact]
    public void A_line_still_being_written_is_left_for_the_next_read()
    {
        Fixture.Append(events, Copilot.ToolStart("q1", "ask_user", new { message = "m" }, 1));
        var before = new FileInfo(events).Length;
        var answer = Recorded("q1", new { pick = "a" }, 2);
        File.AppendAllText(events, answer[..20]);

        var partial = CopilotTranscript.Since(events, "q1", before);
        Assert.Null(partial.Answer);
        Assert.Equal(before, partial.ScannedTo);

        File.AppendAllText(events, answer[20..] + "\n");
        Assert.NotNull(CopilotTranscript.Since(events, "q1", before).Answer);
    }

    [Fact]
    public void A_question_asked_by_a_subagent_is_marked_as_not_the_sessions_own()
    {
        Fixture.Append(events, Copilot.ToolStart("q1", "ask_user", new { message = "m" }, 1, agentId: "helper"));

        Assert.False(CopilotTranscript.Find(events, "q1").IsMainAgent);
    }
}

public class CopilotVerifierTests
{
    static CopilotPlan Plan(string arguments, object? fields = null, string? text = null)
    {
        var (form, _) = CopilotPlannerTests.Ask(arguments);
        return CopilotPlanner.Plan(form, CopilotPlannerTests.Answer(fields, text)).Plan!;
    }

    static CopilotAnswerRecord Human(string json) => new(JsonDocument.Parse(json).RootElement.Clone(), false, true, null);
    static CopilotAnswerRecord Rendered(string text, bool ok = true) => new(null, true, ok, text);

    const string Form = """
        { "message": "m", "requestedSchema": { "properties": {
          "pick": { "oneOf": [ { "const": "s4", "title": "S4 (Recommended)" }, { "const": "s6", "title": "S6" } ] },
          "go": { "type": "boolean" },
          "many": { "type": "array", "items": { "enum": ["a", "b", "c"] } },
          "notes": { "type": "string" },
          "count": { "type": "number" } } } }
        """;

    [Fact]
    public void A_structured_answer_matching_every_field_is_delivered_in_any_list_order_and_whitespace()
    {
        var plan = Plan(Form, new { pick = "s6", go = false, many = new[] { "a", "c" }, notes = "two  words ", count = 2.5 });

        var (matched, _) = CopilotVerifier.Check(plan, Human("""{ "pick": "s6", "go": false, "many": ["c", "a"], "notes": "two words", "count": 2.50 }"""));

        Assert.True(matched);
    }

    [Theory]
    [InlineData("""{ "pick": "s4", "go": false, "many": ["a"], "count": 1 }""", "pick")]
    [InlineData("""{ "pick": "S6", "go": false, "many": ["a"], "count": 1 }""", "pick")]
    [InlineData("""{ "pick": "s6", "go": "false", "many": ["a"], "count": 1 }""", "go")]
    [InlineData("""{ "pick": "s6", "go": false, "many": ["a", "b"], "count": 1 }""", "many")]
    [InlineData("""{ "pick": "s6", "go": false, "many": ["a"], "count": 2 }""", "count")]
    [InlineData("""{ "pick": "s6", "go": false, "many": ["a"], "count": 1, "notes": "x" }""", "notes")]
    [InlineData("""{ "pick": "s6", "go": false, "many": ["a"], "count": 1, "extra": 1 }""", "extra")]
    public void Any_field_recorded_differently_is_not_a_match(string recorded, string named)
    {
        var plan = Plan(Form, new { pick = "s6", go = false, many = new[] { "a" }, count = 1 });

        var (matched, reason) = CopilotVerifier.Check(plan, Human(recorded));

        Assert.False(matched);
        Assert.Contains($"\"{named}\"", reason);
    }

    [Fact]
    public void A_matching_structured_answer_beside_a_failed_tool_call_is_not_a_match()
    {
        var plan = Plan("""{ "message": "m", "requestedSchema": { "properties": { "pick": { "enum": ["a", "b"] } } } }""", new { pick = "a" });
        var failed = new CopilotAnswerRecord(JsonDocument.Parse("""{ "pick": "a" }""").RootElement.Clone(), true, false, null);

        var (matched, reason) = CopilotVerifier.Check(plan, failed);

        Assert.False(matched);
        Assert.Contains("error", reason);
    }

    [Fact]
    public void Words_through_Other_match_only_when_the_words_were_recorded_and_not_an_option()
    {
        const string Pick = """{ "message": "m", "requestedSchema": { "properties": { "pick": { "enum": ["a", "b"] } } } }""";
        var plan = Plan(Pick, new { pick = new { other = "neither, thanks" } });

        Assert.True(CopilotVerifier.Check(plan, Human("""{ "pick": "neither, thanks" }""")).Matched);
        // agent-ha-bridge #240: the walk went wrong and the session recorded the last option.
        Assert.False(CopilotVerifier.Check(plan, Human("""{ "pick": "b" }""")).Matched);
    }

    [Fact]
    public void A_free_text_answer_is_checked_against_the_rendered_result_when_that_is_all_there_is()
    {
        var plan = Plan("""{ "message": "m" }""", text: "ship it");

        Assert.True(CopilotVerifier.Check(plan, Rendered("User responded: ship it")).Matched);
        Assert.False(CopilotVerifier.Check(plan, Rendered("User responded: ship")).Matched);
        Assert.Contains("skipped", CopilotVerifier.Check(plan, Rendered("User skipped question")).Reason);
        Assert.Contains("error", CopilotVerifier.Check(plan, Rendered("", ok: false)).Reason);
    }

    [Fact]
    public void A_form_of_several_fields_is_never_confirmed_from_its_rendering_alone()
    {
        const string Two = """{ "message": "m", "requestedSchema": { "properties": { "a": { "type": "string" }, "b": { "type": "string" } } } }""";

        // The same text is a="x", b="y", or a="x, b=y" with b left out: only the structured
        // record can tell them apart.
        var (matched, reason) = CopilotVerifier.Check(Plan(Two, new { a = "x", b = "y" }), Rendered("User responded: a=x, b=y"));

        Assert.False(matched);
        Assert.Contains("structured", reason);
        Assert.True(CopilotVerifier.Check(Plan(Two, new { a = "x", b = "y" }), Human("""{ "a": "x", "b": "y" }""")).Matched);
    }

    [Fact]
    public void A_rendered_option_is_compared_exactly_and_only_typed_words_have_their_whitespace_flattened()
    {
        var plan = Plan("""{ "message": "m", "requestedSchema": { "properties": { "pick": { "enum": ["a b", "a  b"] } } } }""", new { pick = "a b" });

        Assert.True(CopilotVerifier.Check(plan, Rendered("User selected: a b")).Matched);
        Assert.False(CopilotVerifier.Check(plan, Rendered("User selected: a  b")).Matched);
    }

    [Fact]
    public void A_lone_fields_rendering_is_read_bare_so_an_option_spelled_like_name_equals_value_cannot_pass_for_another()
    {
        var plan = Plan("""{ "message": "m", "requestedSchema": { "properties": { "choice": { "enum": ["both", "hold"] } } } }""", new { choice = "both" });
        var collide = Plan("""{ "message": "m", "requestedSchema": { "properties": { "pick": { "enum": ["x", "pick=x"] } } } }""", new { pick = "x" });

        Assert.True(CopilotVerifier.Check(plan, Rendered("User responded: both")).Matched);
        Assert.False(CopilotVerifier.Check(plan, Rendered("User responded: hold")).Matched);
        Assert.False(CopilotVerifier.Check(collide, Rendered("User selected: pick=x")).Matched);
    }
}

public class InjectableTextTests
{
    [Fact]
    public void Lone_surrogates_become_replacement_characters_and_pairs_are_kept_whole()
    {
        Assert.Equal("a\uFFFDb🚀\uFFFD", InjectableText.Collapse("a\uD800b🚀\uDC00"));
    }

    [Fact]
    public void Flattening_collapses_whitespace_runs_and_trims_but_keeps_everything_else()
    {
        Assert.Equal("a b c", InjectableText.Flatten("  a \t b\n\nc  "));
    }

    [Fact]
    public void The_worker_script_carries_each_send_with_its_words_as_base64_so_any_text_survives()
    {
        var plan = new KeyPlan([new("\u001b[B ", false, 0, 400), new("世界 🚀", true, 300, 0)]);

        var lines = plan.ToWorkerScript().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(KeyPlan.Header, lines[0]);
        Assert.Equal($"send {Convert.ToBase64String(Encoding.UTF8.GetBytes("世界 🚀"))} 1 300 0", lines[2]);
        Assert.Equal(TimeSpan.FromMilliseconds(700), plan.Duration);
        Assert.Equal("1down space | 5 chars enter", plan.Describe());
    }
}
