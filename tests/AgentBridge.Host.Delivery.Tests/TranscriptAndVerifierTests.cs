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
    public void A_call_kept_up_to_date_is_read_from_what_the_transcript_gained_and_a_shorter_transcript_is_unreadable()
    {
        // Checked before every write of an answer, with the typing slot every session shares
        // held: only the lines added since the call was validated (Copilot 4479319394).
        Fixture.Append(events, [.. Enumerable.Range(0, 2000).Select(i => Copilot.ToolStart($"t{i}", "powershell", new { command = $"echo {i}" }, i))]);
        Fixture.Append(events, Copilot.ToolStart("q1", "ask_user", new { message = "m", requestedSchema = Schema }, 2001));
        var call = CopilotTranscript.Find(events, "q1");
        var watch = new CopilotCallWatch(events, "q1", call.ScannedTo);

        var none = watch.Read();
        Assert.Equal((true, (CopilotAnswerRecord?)null, 0L), (none.Readable, none.Answer, watch.LastRead));

        var before = new FileInfo(events).Length;
        Fixture.Append(events, Copilot.ToolStart("c1", "powershell", new { command = "ls" }, 2002));
        Assert.Null(watch.Read().Answer);
        Assert.Equal(new FileInfo(events).Length - before, watch.LastRead);

        before = new FileInfo(events).Length;
        Fixture.Append(events, Recorded("q1", new { pick = "b" }, 2003));
        Assert.Equal("b", watch.Read().Answer!.HumanContent!.Value.GetProperty("pick").GetString());
        Assert.Equal(new FileInfo(events).Length - before, watch.LastRead);

        // Replaced by something shorter, the question's start still in it: not the transcript read.
        File.WriteAllText(events, "");
        Fixture.Append(events, Copilot.ToolStart("q1", "ask_user", new { message = "m", requestedSchema = Schema }, 1));
        Assert.False(watch.Read().Readable);
        Assert.False(new CopilotCallWatch(events + ".missing", "q1", 0).Read().Readable);
    }

    // An equal-length file in the transcript's place (Copilot 4484702591): another file moved
    // there, byte for byte the same, or the same file rewritten with other bytes at its start or
    // just before where it was read to, another call's record of the same question, say. Read no
    // further, then or after anything is added to it; what is added to the file read is read.
    [Theory]
    [InlineData("moved into place")]
    [InlineData("rewritten at its start")]
    [InlineData("rewritten where it was read to")]
    public void A_transcript_replaced_by_one_of_the_same_length_is_never_read_on_from_where_the_call_was_read(string how)
    {
        Fixture.Append(events, [.. Enumerable.Range(0, 200).Select(i => Copilot.ToolStart($"t{i}", "powershell", new { command = $"echo {i}" }, i))]);
        Fixture.Append(events, Copilot.ToolStart("q1", "ask_user", new { message = "m", requestedSchema = Schema }, 201));
        var watch = new CopilotCallWatch(events, "q1", CopilotTranscript.Find(events, "q1").ScannedTo);
        Fixture.Append(events, Copilot.ToolStart("c1", "powershell", new { command = "ls" }, 202));
        Assert.True(watch.Read().Readable);

        var bytes = File.ReadAllBytes(events);
        var length = bytes.Length;
        static bool Digit(byte b) => b is >= (byte)'0' and <= (byte)'9';
        if (how == "moved into place")
        {
            File.WriteAllBytes(events + ".new", bytes);
            File.Move(events + ".new", events, overwrite: true);
        }
        else
        {
            var at = how == "rewritten at its start" ? Array.FindIndex(bytes, Digit) : Array.FindLastIndex(bytes, Digit);
            bytes[at] = bytes[at] == (byte)'1' ? (byte)'2' : (byte)'1';
            File.WriteAllBytes(events, bytes);
        }
        Assert.Equal(length, new FileInfo(events).Length);

        Assert.False(watch.Read().Readable);
        Fixture.Append(events, Recorded("q1", new { pick = "b" }, 203));
        Assert.False(watch.Read().Readable);
    }

    [Fact]
    public void The_sessions_own_abort_ends_its_question_and_a_subagents_does_not()
    {
        Fixture.Append(events, Copilot.ToolStart("q1", "ask_user", new { message = "m" }, 1), Copilot.Event("abort", new { reason = "x" }, 2, agentId: "helper"));
        Assert.False(CopilotTranscript.Find(events, "q1").SessionEnded);

        Fixture.Append(events, Copilot.Event("abort", new { reason = "user" }, 3));
        Assert.True(CopilotTranscript.Find(events, "q1").SessionEnded);
    }

    // A new CLI resumed the session: the killed one's question may or may not still be shown, so it
    // is ended (never answered by keys), until the new CLI asks it again.
    [Fact]
    public void The_sessions_own_resume_ends_its_question_until_the_new_cli_asks_it_again()
    {
        Fixture.Append(events, Copilot.ToolStart("q1", "ask_user", new { message = "m" }, 1), Copilot.Event("session.resume", new { }, 2, agentId: "helper"));
        Assert.False(CopilotTranscript.Find(events, "q1").SessionEnded);

        Fixture.Append(events, Copilot.Event("session.resume", new { }, 3));
        var call = CopilotTranscript.Find(events, "q1");
        Assert.Equal((true, true), (call.SessionEnded, call.Resumed));

        Fixture.Append(events, Copilot.ToolStart("q1", "ask_user", new { message = "m" }, 4));
        call = CopilotTranscript.Find(events, "q1");
        Assert.Equal((false, false), (call.SessionEnded, call.Resumed));
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
          "pick": { "type": "string", "oneOf": [ { "const": "s4", "title": "S4 (Recommended)" }, { "const": "s6", "title": "S6" } ] },
          "go": { "type": "boolean" },
          "many": { "type": "array", "items": { "enum": ["a", "b", "c"] } },
          "notes": { "type": "string" },
          "count": { "type": "number" } } } }
        """;

    [Fact]
    public void A_default_kept_by_its_Enter_alone_is_confirmed_by_the_default_however_the_answer_spelled_it()
    {
        // 3.0 given for a field that opened with 3: only its Enter goes, so the session records
        // the 3 it opened with, as a number or as the text the box showed (#662).
        const string Days = """{ "message": "m", "requestedSchema": { "properties": { "days": { "type": "integer", "default": 3 } } } }""";
        var plan = Plan(Days, JsonDocument.Parse("""{ "days": 3.0 }""").RootElement.Clone());

        Assert.True(CopilotVerifier.Check(plan, Human("""{ "days": "3" }""")).Item1);
        Assert.True(CopilotVerifier.Check(plan, Human("""{ "days": 3 }""")).Item1);
        Assert.False(CopilotVerifier.Check(plan, Human("""{ "days": 33 }""")).Item1);
    }

    [Fact]
    public void A_number_fields_string_default_kept_by_its_Enter_is_confirmed_by_the_number_the_CLI_records()
    {
        // Copilot's form turns the "3" the box opened with into the number 3 when it is accepted.
        var plan = Plan(CopilotPlannerTests.Number("\"3\""), new { n = 3 });

        Assert.True(CopilotVerifier.Check(plan, Human("""{ "n": 3 }""")).Item1);
        Assert.False(CopilotVerifier.Check(plan, Human("""{ "n": 33 }""")).Item1);
    }

    [Theory]
    // Typed as plain decimal, so recorded as that: the verifier takes the number, or its text,
    // for exactly that value, and nothing else (#700).
    [InlineData("1e5", "100000", "100000", "100001")]
    [InlineData("-1.5e-3", "-0.0015", "-0.0015", "-0.00150001")]
    [InlineData("1E2", "100", "100", "1000")]
    public void A_number_typed_as_plain_decimal_is_confirmed_by_the_value_typed_in_either_form(string given, string typed, string recorded, string other)
    {
        const string Free = """{ "message": "m", "requestedSchema": { "properties": { "n": { "type": "number" } } } }""";
        var plan = Plan(Free, JsonDocument.Parse($$"""{ "n": {{given}} }""").RootElement.Clone());

        Assert.Equal(typed, plan.Keys.Sends[0].Text);
        Assert.True(CopilotVerifier.Check(plan, Human($$"""{ "n": {{recorded}} }""")).Item1);
        Assert.True(CopilotVerifier.Check(plan, Human($$"""{ "n": "{{recorded}}" }""")).Item1);
        Assert.True(CopilotVerifier.Check(plan, Human($$"""{ "n": "{{given}}" }""")).Item1);
        Assert.False(CopilotVerifier.Check(plan, Human($$"""{ "n": {{other}} }""")).Item1);
        Assert.False(CopilotVerifier.Check(plan, Human($$"""{ "n": "{{other}}" }""")).Item1);
        Assert.False(CopilotVerifier.Check(plan, Human($$"""{ "n": " {{recorded}}" }""")).Item1);
        Assert.False(CopilotVerifier.Check(plan, Human("""{ "n": "Infinity" }""")).Item1);
    }

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
        var plan = Plan("""{ "message": "m", "requestedSchema": { "properties": { "pick": { "type": "string", "enum": ["a", "b"] } } } }""", new { pick = "a" });
        var failed = new CopilotAnswerRecord(JsonDocument.Parse("""{ "pick": "a" }""").RootElement.Clone(), true, false, null);

        var (matched, reason) = CopilotVerifier.Check(plan, failed);

        Assert.False(matched);
        Assert.Contains("error", reason);
    }

    [Fact]
    public void Words_through_Other_match_only_when_the_words_were_recorded_and_not_an_option()
    {
        const string Pick = """{ "message": "m", "requestedSchema": { "properties": { "pick": { "type": "string", "enum": ["a", "b"] } } } }""";
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

    [Theory]
    // a="x", b="y" or a="x, b=y" with b left out.
    [InlineData("""{ "message": "m", "requestedSchema": { "properties": { "a": { "type": "string" }, "b": { "type": "string" } } } }""", """{ "a": "x", "b": "y" }""", "User responded: a=x, b=y")]
    // Options "x" and "x ": the rendering is trimmed.
    [InlineData("""{ "message": "m", "requestedSchema": { "properties": { "pick": { "type": "string", "enum": ["x", "x "] } } } }""", """{ "pick": "x" }""", "User selected: x ")]
    // Options "x" and "pick=x": bare or named.
    [InlineData("""{ "message": "m", "requestedSchema": { "properties": { "pick": { "type": "string", "enum": ["x", "pick=x"] } } } }""", """{ "pick": "x" }""", "User selected: pick=x")]
    // Options "a b" and "a  b".
    [InlineData("""{ "message": "m", "requestedSchema": { "properties": { "pick": { "type": "string", "enum": ["a b", "a  b"] } } } }""", """{ "pick": "a b" }""", "User selected: a  b")]
    public void A_question_with_fields_is_never_confirmed_from_its_rendering_only_from_its_structured_answer(string arguments, string fields, string rendering)
    {
        var plan = Plan(arguments, JsonSerializer.Deserialize<Dictionary<string, string>>(fields));

        var (matched, reason) = CopilotVerifier.Check(plan, Rendered(rendering));

        Assert.False(matched);
        Assert.Contains("structured", reason);
        Assert.True(CopilotVerifier.Check(plan, Human(fields)).Matched);
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
