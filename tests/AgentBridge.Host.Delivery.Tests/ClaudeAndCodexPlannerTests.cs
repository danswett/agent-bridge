using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Core.Parsing;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Claude;
using AgentBridge.Host.Delivery.Codex;
using AgentBridge.Protocol;
using Xunit;

// The keys for Claude Code's AskUserQuestion and a Codex approval, worked out without a
// console, and how what each agent records is read back. Each walk is agent-ha-bridge's
// (hooks/decision-inject.ps1, Invoke-PendingCodexApprovals).
public class ClaudeAndCodexPlannerTests
{
    const string D = KeyPlan.Down;

    static (ClaudeForm Form, Question Question) Ask(JsonObject input)
    {
        using var doc = JsonDocument.Parse(input.ToJsonString());
        var question = QuestionMapper.FromClaudeAskUserQuestion("toolu_1", doc.RootElement, Fixture.T0);
        var (form, problem) = ClaudeForm.Read(doc.RootElement.Clone());
        Assert.Null(problem);
        // The two readers of one input agree, or nothing would be typed at all.
        Assert.True(form!.Matches(question));
        return (form, question);
    }

    static ClaudePlan Plan(JsonObject input, object fields)
    {
        var (form, question) = Ask(input);
        var answer = CopilotPlannerTests.Answer(fields);
        Assert.Null(Answers.Check(question, answer));
        var (plan, problem) = ClaudePlanner.Plan(form, answer);
        Assert.Null(problem);
        return plan!;
    }

    static string Refusal(JsonObject input, object fields)
    {
        var (form, _) = Ask(input);
        var (plan, problem) = ClaudePlanner.Plan(form, CopilotPlannerTests.Answer(fields));
        Assert.Null(plan);
        return problem!;
    }

    static List<(string Text, bool Enter, int Pause)> Keys(ClaudePlan plan) => [.. plan.Keys.Sends.Select(s => (s.Text, s.Enter, s.PauseAfterMs))];

    static readonly JsonObject Database = Claude.Ask(("Which database?", false, ["PostgreSQL", "SQLite", "DuckDB"]));

    [Fact]
    public void A_single_choice_is_Down_from_the_first_row_to_the_chosen_one_then_Enter()
    {
        Assert.Equal([(D + D, true, ClaudePlanner.AfterFieldMs)], Keys(Plan(Database, new Dictionary<string, object> { ["Which database?"] = "DuckDB" })));
        // The first option is Enter alone.
        var first = Plan(Database, new Dictionary<string, object> { ["Which database?"] = "PostgreSQL" });
        Assert.Equal([("", true, ClaudePlanner.AfterFieldMs)], Keys(first));
        Assert.Equal([("Which database?", "PostgreSQL")], first.Fields);
    }

    [Fact]
    public void A_multi_select_ticks_each_chosen_row_by_its_digit_on_its_own_then_walks_past_every_row_to_Submit()
    {
        var input = Claude.Ask(("Which features?", true, ["Auth", "Billing", "Search"]));

        // Chosen out of order; ticked, and recorded, in list order.
        var plan = Plan(input, new Dictionary<string, object> { ["Which features?"] = new[] { "Search", "Billing" } });

        Assert.Equal([("2", false, ClaudePlanner.ToggleGapMs), ("3", false, ClaudePlanner.ToggleGapMs), (D + D + D + D, true, ClaudePlanner.AfterFieldMs)], Keys(plan));
        Assert.Equal([("Which features?", "Billing, Search")], plan.Fields);
    }

    [Fact]
    public void Several_questions_are_answered_in_order_each_committed_with_its_own_Enter()
    {
        var input = Claude.Ask(("Which one?", false, ["x", "y"]), ("Which ones?", true, ["a", "b"]));

        var plan = Plan(input, new Dictionary<string, object> { ["Which ones?"] = new[] { "a" }, ["Which one?"] = "y" });

        Assert.Equal([(D, true, ClaudePlanner.AfterFieldMs), ("1", false, ClaudePlanner.ToggleGapMs), (D + D + D, true, ClaudePlanner.AfterFieldMs)], Keys(plan));
        Assert.Equal([("Which one?", "y"), ("Which ones?", "a")], plan.Fields);
    }

    [Fact]
    public void An_empty_multi_select_and_other_words_are_refused_since_no_walk_is_proven_for_them()
    {
        var input = Claude.Ask(("Which features?", true, ["Auth", "Billing"]));
        Assert.Contains("at least one", Refusal(input, new Dictionary<string, object> { ["Which features?"] = Array.Empty<string>() }));
        Assert.Contains("type anything else at the machine", Refusal(Database, new Dictionary<string, object> { ["Which database?"] = new { other = "MySQL" } }));
        // The client is never offered Other for a Claude question in the first place.
        Assert.Contains("no other answer", Answers.Check(Ask(Database).Question, CopilotPlannerTests.Answer(new Dictionary<string, object> { ["Which database?"] = new { other = "MySQL" } })));
    }

    [Fact]
    public void A_question_read_differently_from_what_the_client_was_shown_does_not_match()
    {
        var (form, question) = Ask(Database);
        var reordered = question with { Fields = [question.Fields[0] with { Options = [.. question.Fields[0].Options!.Reverse()] }] };
        var renamed = question with { Fields = [question.Fields[0] with { Name = "Which DB?" }] };
        var multi = question with { Fields = [question.Fields[0] with { Type = FieldType.Multi }] };

        Assert.False(form.Matches(reordered));
        Assert.False(form.Matches(renamed));
        Assert.False(form.Matches(multi));
    }

    [Fact]
    public void A_prompt_with_more_questions_than_Claude_takes_or_the_same_question_twice_is_not_driven()
    {
        using var five = JsonDocument.Parse(Claude.Ask([.. Enumerable.Range(1, 5).Select(i => ($"Q{i}?", false, new[] { "a" }))]).ToJsonString());
        using var twice = JsonDocument.Parse(Claude.Ask(("Q?", false, ["a"]), ("Q?", false, ["b"])).ToJsonString());

        Assert.Contains("more questions", ClaudeForm.Read(five.RootElement).Problem);
        Assert.Contains("read the same", ClaudeForm.Read(twice.RootElement).Problem);
    }

    [Fact]
    public void Claude_is_confirmed_by_the_answers_it_recorded_for_the_call_and_by_its_text_without_them()
    {
        var plan = Plan(Claude.Ask(("Which one?", false, ["x", "y"]), ("Which ones?", true, ["a", "b"])),
            new Dictionary<string, object> { ["Which one?"] = "y", ["Which ones?"] = new[] { "a", "b" } });
        var answers = new Dictionary<string, string> { ["Which one?"] = "y", ["Which ones?"] = "a, b" };
        var text = "User has answered your questions: \"Which one?\"=\"y\", \"Which ones?\"=\"a, b\". You can now continue.";

        Assert.True(ClaudeVerifier.Check(plan, new ClaudeAnswerRecord(false, text, answers)).Matched);
        Assert.True(ClaudeVerifier.Check(plan, new ClaudeAnswerRecord(false, text, null)).Matched);

        var other = new Dictionary<string, string>(answers) { ["Which one?"] = "x" };
        Assert.Equal((false, "Claude recorded a different answer"), ClaudeVerifier.Check(plan, new ClaudeAnswerRecord(false, text, other)));
        Assert.Equal((false, "Claude recorded a different answer"), ClaudeVerifier.Check(plan, new ClaudeAnswerRecord(false, text.Replace("\"y\"", "\"x\""), null)));
        Assert.False(ClaudeVerifier.Check(plan, new ClaudeAnswerRecord(false, text, new Dictionary<string, string> { ["Which one?"] = "y" })).Matched);
        Assert.Contains("declined", ClaudeVerifier.Check(plan, new ClaudeAnswerRecord(true, "The user doesn't want to proceed", null)).Reason);
    }

    [Fact]
    public void A_multi_select_whose_options_join_into_one_another_is_never_confirmed_by_its_record()
    {
        // "A" and "B" ticked and "A, B" ticked are both recorded as "A, B".
        var input = Claude.Ask(("Which?", true, ["A", "B", "A, B"]));
        var both = Plan(input, new Dictionary<string, object> { ["Which?"] = new[] { "A", "B" } });
        var third = Plan(input, new Dictionary<string, object> { ["Which?"] = new[] { "A, B" } });
        var record = new ClaudeAnswerRecord(false, "\"Which?\"=\"A, B\"", new Dictionary<string, string> { ["Which?"] = "A, B" });

        Assert.False(ClaudeVerifier.Check(both, record).Matched);
        Assert.Contains("cannot show which", ClaudeVerifier.Check(third, record).Reason);
        Assert.True(ClaudeVerifier.Check(Plan(Claude.Ask(("Which?", true, ["A", "B"])), new Dictionary<string, object> { ["Which?"] = new[] { "A", "B" } }), record).Matched);
    }

    [Fact]
    public void Claudes_transcript_is_read_for_the_call_by_its_id_and_an_answer_or_an_interrupt_after_it()
    {
        using var f = new Fixture();
        var path = f.ClaudeTranscript("c1");
        var input = Claude.Ask(("Which one?", false, ["x", "y"]));
        Fixture.Append(path,
            Claude.User("plan it", 1),
            Claude.Assistant(2, "tool_use", Claude.ToolUse("toolu_old", "AskUserQuestion", input)),
            Claude.Answered("toolu_old", 3, new Dictionary<string, string> { ["Which one?"] = "x" }),
            Claude.Assistant(4, "tool_use", Claude.ToolUse("toolu_1", "AskUserQuestion", input)));

        var asking = ClaudeTranscript.Find(path, "toolu_1");
        Assert.NotNull(asking.Input);
        Assert.True(asking.IsMainAgent);
        Assert.Null(asking.Answer);
        Assert.False(asking.Interrupted);

        Fixture.Append(path, Claude.Answered("toolu_1", 5, new Dictionary<string, string> { ["Which one?"] = "y" }));
        var answered = ClaudeTranscript.Since(path, "toolu_1", asking.ScannedTo);
        Assert.Equal("y", answered.Answer!.Answers!["Which one?"]);

        Fixture.Append(path, Claude.Assistant(6, "tool_use", Claude.ToolUse("toolu_2", "AskUserQuestion", input)), Claude.User("[Request interrupted by user]", 7));
        Assert.True(ClaudeTranscript.Find(path, "toolu_2").Interrupted);
        // Another question's answer, and a sidechain's question, are not this call's.
        Assert.Null(ClaudeTranscript.Find(path, "toolu_2").Answer);
        Assert.Null(ClaudeTranscript.Find(path, "toolu_none").Input);
    }

    [Fact]
    public void A_Codex_approval_is_y_or_n_then_Enter_as_agent_ha_bridge_types_it()
    {
        static List<(string, bool, int)> Of(string decision) =>
            [.. CodexApproval.Plan(CopilotPlannerTests.Answer(new { decision }).Fields).Keys!.Sends.Select(s => (s.Text, s.Enter, s.EnterDelayMs))];

        Assert.Equal([("y", true, 300)], Of("approve"));
        Assert.Equal([("n", true, 300)], Of("deny"));
        Assert.NotNull(CodexApproval.Plan(CopilotPlannerTests.Answer(new { decision = "always" }).Fields).Problem);
        Assert.NotNull(CodexApproval.Plan(null).Problem);
    }
}

public class AttemptLedgerTests
{
    [Fact]
    public void A_prompt_is_claimed_once_across_restarts_and_only_a_release_frees_it()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "profile", "answer-attempts.json");
        var key = AttemptLedger.Key("codex", "s1", "node-1");

        Assert.Null(new AttemptLedger(path).Claim(key));
        // A new process reads what the last one wrote.
        var restarted = new AttemptLedger(path);
        Assert.Contains("already typed", restarted.Claim(key));
        Assert.Null(restarted.Claim(AttemptLedger.Key("codex", "s1", "node-2")));

        restarted.Release(key);
        Assert.Null(new AttemptLedger(path).Claim(key));
    }

    [Fact]
    public void A_ledger_that_cannot_be_read_or_written_refuses_every_claim()
    {
        using var f = new Fixture();
        var corrupt = Path.Combine(f.Root, "corrupt.json");
        File.WriteAllText(corrupt, "{\"codex");
        Assert.Contains("could not be read", new AttemptLedger(corrupt).Claim("k"));

        // A folder where the file should be: nothing can be written there.
        var blocked = Path.Combine(f.Root, "blocked");
        Directory.CreateDirectory(blocked + ".tmp");
        Assert.Contains("could not record", new AttemptLedger(blocked).Claim("k"));
    }
}
