using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using AgentBridge.Host.Core.Parsing;
using AgentBridge.Protocol;
using Xunit;

// Arming and clearing a session's answer controls, against what agent-ha-bridge's own
// Set-CopilotMqttDecision and Clear-CopilotMqttDecision published for the same questions
// (Fixtures/ha-decisions.json, recorded by tools/ha-contract/Export-HaContract.ps1).
public sealed class DecisionContractTests
{
    static readonly Lazy<JsonObject> Loaded = new(() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ha-decisions.json")))!);

    static JsonObject Root => Loaded.Value;
    static string Machine => Root["machine"]!.GetValue<string>();
    static string SessionName => Root["sessionName"]!.GetValue<string>();
    static string Node => HaContract.NodeId(Root["sessionId"]!.GetValue<string>());

    static JsonObject Question(string name) => Root["questions"]!.AsArray().Select(q => q!.AsObject()).Single(q => q["name"]!.GetValue<string>() == name);

    public static TheoryData<string> Questions => [.. Loaded.Value["questions"]!.AsArray().Select(q => q!["name"]!.GetValue<string>())];

    static DecisionField FieldOf(JsonNode f) => new(
        f["label"]!.GetValue<string>(),
        [.. f["options"]!.AsArray().Select(o => o!.GetValue<string>())],
        f["isText"]!.GetValue<bool>(),
        f["multiSelect"]!.GetValue<bool>(),
        [.. f["defaultIndexes"]!.AsArray().Select(i => i!.GetValue<int>())],
        f["allowsEmpty"]!.GetValue<bool>());

    // What a step would put on the wire, in the fixture's shape.
    static JsonObject Wire(DecisionStep step) => step switch
    {
        PublishStep p => new JsonObject { ["topic"] = p.Topic, ["payload"] = p.Payload, ["retain"] = p.Retain },
        SelectStep s => new JsonObject { ["service"] = "select.select_option", ["entity_id"] = s.EntityId, ["option"] = s.Option },
        _ => throw new InvalidOperationException(step.ToString()),
    };

    static void Same(JsonArray recorded, IReadOnlyList<DecisionStep> steps)
    {
        var actual = steps.Where(s => s is not PinFieldsStep).Select(Wire).ToList();
        Assert.Equal(recorded.Count, actual.Count);
        for (var i = 0; i < recorded.Count; i++)
        {
            var e = recorded[i]!.AsObject();
            var a = actual[i];
            if (e.ContainsKey("service"))
            {
                Assert.Equal(e["entity_id"]!.GetValue<string>(), a["entity_id"]?.GetValue<string>());
                Assert.Equal(e["option"]!.GetValue<string>(), a["option"]?.GetValue<string>());
                continue;
            }
            var p = Published.FromFixture(e);
            Contract.Equal(p, new Published(a["topic"]!.GetValue<string>(), a["payload"]!.GetValue<string>(), a["retain"]!.GetValue<bool>()));
        }
    }

    [Theory]
    [MemberData(nameof(Questions))]
    public void Arming_a_question_publishes_what_the_daemon_publishes(string name)
    {
        var q = Question(name);
        var plan = new DecisionPlan(
            "decision-1",
            q["question"]!.GetValue<string>(),
            [.. q["choices"]!.AsArray().Select(c => c!.GetValue<string>())],
            [.. q["fields"]!.AsArray().Select(f => FieldOf(f!))],
            q["answerOnTap"]!.GetValue<bool>(),
            TerminalOnly: false,
            q["takesTypedAnswer"]!.GetValue<bool>());
        Assert.Equal(q["takesTypedAnswer"]!.GetValue<bool>(), HaDecision.TakesTypedAnswer(plan.Fields, plan.Choices, plan.AnswerOnTap));
        var steps = HaDecision.Arm(plan, Node, SessionName, Machine, q["armedSlots"]!.GetValue<int>(), DateTimeOffset.UnixEpoch);
        Same(q["published"]!.AsArray(), steps);
        // The slots it republished go back onto their pinned ids before they are driven.
        var pin = steps.OfType<PinFieldsStep>().Single();
        Assert.True(steps.ToList().IndexOf(pin) < steps.ToList().FindIndex(s => s is SelectStep { EntityId: var id } && id.EndsWith("_f1", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Clearing_a_question_publishes_what_the_daemon_publishes(int armed)
    {
        Same(Root["clears"]![$"armed{armed}"]!.AsArray(), HaDecision.Clear(Node, SessionName, Machine, armed));
    }

    [Fact]
    public void Multi_select_slots_offer_and_read_back_exactly_the_daemons_values()
    {
        foreach (var m in Root["multiSelect"]!.AsArray())
        {
            var schema = m!["schema"]!.GetValue<string>();
            var fieldName = m["field"]!.GetValue<string>();
            var question = Question(schema);
            var fieldIndex = Schema(question).EnumerateObject().Select(p => p.Name).ToList().IndexOf(fieldName);
            var field = FieldOf(question["fields"]!.AsArray()[fieldIndex]!);
            Assert.Equal(m["choices"]!.AsArray().Select(c => c!.GetValue<string>()), MultiSelect.Choices(field));
            Assert.Equal(m["codes"]!.AsArray().Select(c => c!.GetValue<string>()), MultiSelect.Codes(field));
            Assert.Equal(m["codeSafe"]!.GetValue<bool>(), MultiSelect.CodeSafe(field));
            Assert.Equal(m["emptyChoice"]!.GetValue<string>(), MultiSelect.EmptyChoice(field));
            foreach (var r in m["resolve"]!.AsArray())
            {
                var value = r!["value"]!.GetValue<string>();
                Assert.True(r["indexes"]!.AsArray().Select(i => i!.GetValue<int>()).SequenceEqual(MultiSelect.Resolve(field, value)), $"{fieldName}: {value}");
                Assert.Equal(r["empty"]!.GetValue<bool>(), MultiSelect.IsEmptyChoice(field, value));
            }
        }
    }

    static JsonElement Schema(JsonObject question) =>
        JsonDocument.Parse(question["schema"]!.GetValue<string>()).RootElement.GetProperty("properties");

    // The host parses ask_user itself; captured from its fields, the card gets the daemon's.
    [Theory]
    [InlineData("single")]
    [InlineData("boolean")]
    [InlineData("text")]
    [InlineData("form")]
    [InlineData("manyMulti")]
    [InlineData("unsafeMulti")]
    [InlineData("emptyMulti")]
    public void A_copilot_form_is_planned_as_the_copilot_hook_publishes_it(string name)
    {
        var q = Question(name);
        var message = name == "form" ? "Set up the repository" : q["question"]!.GetValue<string>();
        using var arguments = JsonDocument.Parse(new JsonObject { ["message"] = message, ["requestedSchema"] = JsonNode.Parse(q["schema"]!.GetValue<string>()) }.ToJsonString());
        var hostQuestion = QuestionMapper.FromCopilotAskUser("decision-1", arguments.RootElement, DateTimeOffset.UnixEpoch);
        var plan = HaDecision.Plan(hostQuestion, AgentKind.Copilot);

        Assert.False(plan.TerminalOnly);
        Assert.Equal(q["question"]!.GetValue<string>(), plan.Question);
        Assert.Equal(q["choices"]!.AsArray().Select(c => c!.GetValue<string>()), plan.Choices);
        var expected = q["fields"]!.AsArray().Select(f => FieldOf(f!)).ToList();
        Assert.Equal(expected.Count, plan.Fields.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var a = plan.Fields[i];
            Assert.Equal(e.Label, a.Label);
            Assert.Equal(e.Options, a.Options);
            Assert.Equal(e.IsText, a.IsText);
            Assert.Equal(e.MultiSelect, a.MultiSelect);
            Assert.Equal(e.DefaultIndexes, a.DefaultIndexes);
            // The one thing the host's field cannot say: whether the schema's minItems allows an
            // empty answer. manyMulti's says it does not; the host offers "none" anyway, and an
            // answer the prompt refuses comes back as not sent.
            Assert.Equal(name == "manyMulti" || e.AllowsEmpty, a.AllowsEmpty);
        }
        // "Other" is offered only where the host can deliver words: a lone choice. The daemon
        // offered it beside a lone Yes/No too, where nothing types the words in.
        Assert.Equal(name == "single", plan.TakesTypedAnswer);
        Assert.Equal(q["takesTypedAnswer"]!.GetValue<bool>(), name is "single" or "boolean");
    }

    [Fact]
    public void A_question_the_card_cannot_drive_is_shown_for_the_terminal_and_never_read()
    {
        var tooMany = new Question
        {
            Id = "q", Kind = QuestionKind.Form, Message = "Fill this in", AskedAt = DateTimeOffset.UnixEpoch,
            Fields = [new Field { Name = "a", Type = FieldType.Text }, new Field { Name = "b", Type = FieldType.Text }],
        };
        var plan = HaDecision.Plan(tooMany, AgentKind.Copilot);
        Assert.True(plan.TerminalOnly);
        Assert.Empty(plan.Fields);
        Assert.Empty(plan.Choices);
        Assert.StartsWith("Fill this in\n\n**This one has to be answered in the terminal** - ", plan.Question, StringComparison.Ordinal);

        var atMachine = tooMany with { Fields = [], AnswerAtMachine = true };
        Assert.True(HaDecision.Plan(atMachine, AgentKind.Claude).TerminalOnly);

        var reserved = tooMany with { Fields = [new Field { Name = "a", Type = FieldType.Choice, Options = [new Option("Idle"), new Option("Go")] }] };
        Assert.True(HaDecision.Plan(reserved, AgentKind.Copilot).TerminalOnly);
    }

    [Fact]
    public void An_approval_is_answered_by_its_own_tap()
    {
        var approval = new Question
        {
            Id = "q", Kind = QuestionKind.Approval, Message = "Codex wants to run:\n\ngit push", AskedAt = DateTimeOffset.UnixEpoch,
            Fields = [new Field { Name = "decision", Type = FieldType.Choice, Options = QuestionMapper.ApprovalOptions, Required = true }],
        };
        var plan = HaDecision.Plan(approval, AgentKind.Codex);
        var q = Question("approval");
        Assert.Equal(q["choices"]!.AsArray().Select(c => c!.GetValue<string>()), plan.Choices);
        Assert.True(plan.AnswerOnTap);
        Assert.Empty(plan.Fields);
    }

    [Fact]
    public void A_question_too_long_to_show_in_full_is_answered_in_the_terminal_never_on_the_card()
    {
        var command = "git push --force " + new string('x', HaDecision.QuestionMaxChars);
        var approval = new Question
        {
            Id = "q", Kind = QuestionKind.Approval, Message = "Codex wants to run:\n\n" + command, AskedAt = DateTimeOffset.UnixEpoch,
            Fields = [new Field { Name = "decision", Type = FieldType.Choice, Options = QuestionMapper.ApprovalOptions, Required = true }],
        };
        var plan = HaDecision.Plan(approval, AgentKind.Codex);
        Assert.True(plan.TerminalOnly);
        Assert.False(plan.AnswerOnTap);
        Assert.Empty(plan.Choices);
        // The card cuts the question, never the note saying where to answer it.
        Assert.True(plan.Question.Length <= HaDecision.QuestionMaxChars, $"{plan.Question.Length}");
        Assert.EndsWith(HaDecision.TerminalNote("it was too long to show here in full, so it is answered where it can be read."), plan.Question, StringComparison.Ordinal);
        Assert.Contains(HaDecision.TruncatedNote, plan.Question, StringComparison.Ordinal);

        // A form made too long by its hint, too.
        var form = approval with
        {
            Kind = QuestionKind.Form, Message = new string('y', HaDecision.QuestionMaxChars - 10),
            Fields = [new Field { Name = "a", Type = FieldType.Choice, Options = [new Option("go"), new Option("stop")] }, new Field { Name = "b", Type = FieldType.Text }],
        };
        Assert.True(HaDecision.Plan(form, AgentKind.Copilot).TerminalOnly);
    }

    [Fact]
    public void The_mixed_form_hint_is_the_hooks()
    {
        Assert.Equal(Root["mixedHint"]!.GetValue<string>(), HaDecision.MixedFormHint("Anything else?"));
        Assert.Equal(HaContract.ReplyMaxChars, Root["constants"]!["replyMaxChars"]!.GetValue<int>());
        Assert.Equal(HaDecision.QuestionMaxChars, Root["constants"]!["questionMaxChars"]!.GetValue<int>());
    }
}
