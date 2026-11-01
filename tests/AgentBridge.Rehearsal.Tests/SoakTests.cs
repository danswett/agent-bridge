using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using Xunit;

namespace AgentBridge.Rehearsal.Tests;

public class SoakTests
{
    static HaState State(string id, string state, object? attributes = null) =>
        new(id, state, JsonSerializer.SerializeToElement(attributes ?? new { }));

    const string Node = "agent_bridge_14867bc704dd45aa";

    [Fact]
    public void A_lone_Copilot_choice_is_answered_on_its_slot_then_Send_as_the_card_does()
    {
        var q = CardQuestion.Read(State($"select.{Node}_decision", "Awaiting answer...",
            new { decision_id = "d1", mode = "multiple_choice", choices = new[] { "alpha", "beta", "gamma" }, field_count = 1, multi_field = true, takes_typed_answer = true }))!;
        var plan = CardAnswer.Plan(Node, q, AnswerStyle.Card, _ => ["Choose...", "alpha", "beta", "gamma"], 0, DateTimeOffset.UnixEpoch);
        Assert.Equal(["select.select_option", "button.press"], plan.Select(a => $"{a.Domain}.{a.Service}"));
        Assert.Equal($"select.{Node}_f1", (string?)plan[0].Data!["entity_id"]);
        Assert.Contains((string?)plan[0].Data!["option"], new[] { "alpha", "beta", "gamma" });
        Assert.Equal($"button.{Node}_submit", (string?)plan[1].Data!["entity_id"]);
    }

    [Fact]
    public void A_form_with_a_text_field_takes_the_reply_box_words_before_its_choices_and_Send()
    {
        var q = CardQuestion.Read(State($"select.{Node}_decision", "Awaiting answer...", new { decision_id = "d2", field_count = 3, multi_field = true }))!;
        var slots = new Dictionary<int, string[]> { [1] = ["Choose...", "red", "green", "blue"], [2] = ["Choose...", "true", "false"], [3] = ["Idle"] };
        var plan = CardAnswer.Plan(Node, q, AnswerStyle.Card, i => slots[i], 1, DateTimeOffset.UnixEpoch);
        Assert.Equal([$"text.{Node}_reply", $"select.{Node}_f1", $"select.{Node}_f2", $"button.{Node}_submit"], plan.Select(a => (string?)a.Data!["entity_id"]));
        Assert.Equal(CardAnswer.Words, (string?)plan[0].Data!["value"]);
    }

    [Fact]
    public void An_approval_is_answered_by_its_tap_alone()
    {
        var q = CardQuestion.Read(State($"select.{Node}_decision", "Awaiting answer...", new { decision_id = "d3", choices = new[] { "Approve", "Deny" }, answer_on_tap = true }))!;
        var plan = CardAnswer.Plan(Node, q, AnswerStyle.Box, _ => [], 5, DateTimeOffset.UnixEpoch);
        var tap = Assert.Single(plan);
        Assert.Equal("Approve", (string?)tap.Data!["option"]);
    }

    [Fact]
    public void Words_go_by_the_reply_card_only_where_words_answer_and_by_the_controls_otherwise()
    {
        var text = CardQuestion.Read(State($"select.{Node}_decision", "Awaiting answer...", new { decision_id = "d4", mode = "freeform" }))!;
        var publish = Assert.Single(CardAnswer.Plan(Node, text, AnswerStyle.Payload, _ => [], 0, DateTimeOffset.UnixEpoch));
        Assert.Equal($"copilot/cli/{Node}/replypayload/set", publish.Topic);
        Assert.Equal("d4", (string?)JsonNode.Parse(publish.Payload!)!["decision_id"]);

        var strict = CardQuestion.Read(State($"select.{Node}_decision", "Awaiting answer...", new { decision_id = "d5", choices = new[] { "a", "b" } }))!;
        Assert.Equal(["select.select_option", "button.press"], CardAnswer.Plan(Node, strict, AnswerStyle.Payload, _ => [], 0, DateTimeOffset.UnixEpoch).Select(a => $"{a.Domain}.{a.Service}"));
    }

    [Fact]
    public void A_selector_back_at_Idle_is_no_question_even_with_its_old_attributes()
    {
        Assert.Null(CardQuestion.Read(State($"select.{Node}_decision", "Idle", new { decision_id = "d6", choices = new[] { "a" } })));
        Assert.Null(CardQuestion.Read(State($"select.{Node}_decision", "Idle", new { })));
    }

    [Theory]
    [InlineData("14867bc7-04dd-45aa-9a3e-0123456789ab", "agent_bridge_14867bc704dd45aa")]
    [InlineData("ABC-def", "agent_bridge_abcdef")]
    public void A_session_node_is_the_first_16_letters_and_digits_of_its_id_lowercased(string id, string node) =>
        Assert.Equal(node, SoakDriver.NodeOf(id));

    [Fact]
    public void The_default_plan_starts_with_the_transcript_scenarios_and_clears_the_gate_targets()
    {
        var plan = SoakScript.Plan(66);
        Assert.Equal(["copilot-nested", "claude-nested", "codex-subagent"], plan.Take(3).Select(e => e.Scenario.Name));
        var launched = plan.Where(e => !e.Scenario.Resume).ToList();
        Assert.True(launched.Count >= 45, $"{launched.Count} sessions");
        Assert.True(launched.Count(e => e.Scenario.Agent == SoakScript.Claude) >= 10);
        Assert.True(launched.Count(e => e.Scenario.Agent == SoakScript.Codex) >= 10);
        Assert.True(plan.Sum(e => e.Scenario.Turns.Count(t => t.Question is not null)) >= 150);
        Assert.True(plan.Sum(e => e.Scenario.Turns.Count(t => t.Compact)) >= 10);
        // A launch prompt rides the launch payload; every later one fits the reply box, so it is sent as a person types it.
        Assert.All(plan, e => Assert.All(e.Scenario.Turns.Skip(1), t => Assert.True(SoakScript.Prompt(t, e).Length <= 255, "a prompt fits the reply box")));
    }

    // A disposable Home Assistant played by the test: agent-ha-bridge launches, asks one
    // question, takes the answer from Send, and ends the session on End session.
    sealed class FakeHa : ISoakHa
    {
        public readonly Dictionary<string, HaState> States = [];
        public readonly List<string> Calls = [];
        public string? Request;
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        int polls;

        public Task<HaState?> StateAsync(string entityId, CancellationToken ct)
        {
            if (entityId == $"sensor.{Node}_status" && States.TryGetValue(entityId, out var s) && s.State == "working" && ++polls > 2)
            {
                // The agent gets to its question.
                States[entityId] = State(entityId, "waiting");
                States[$"select.{Node}_decision"] = State($"select.{Node}_decision", "Awaiting answer...", new { decision_id = "q1", choices = new[] { "alpha", "beta" }, field_count = 0 });
            }
            return Task.FromResult(States.GetValueOrDefault(entityId));
        }

        public Task CallAsync(string domain, string service, JsonObject data, CancellationToken ct)
        {
            var id = (string)data["entity_id"]!;
            Calls.Add($"{domain}.{service} {id}{(data["option"] is { } o ? " " + o : "")}");
            if (id == "button.agent_bridge_rehearsal_m_new_session")
            {
                States["sensor.agent_bridge_rehearsal_m_new_session_result"] = State("sensor.agent_bridge_rehearsal_m_new_session_result", "started",
                    new { recent = new[] { new { request = Request, outcome = "started", session = "14867bc704dd45aa" } } });
                States[$"sensor.{Node}_status"] = State($"sensor.{Node}_status", "working");
                States[$"sensor.{Node}_activity"] = State($"sensor.{Node}_activity", "Thinking", new { updated = "1" });
            }
            if (id == $"button.{Node}_submit" && States.ContainsKey($"select.{Node}_decision"))
            {
                States[$"select.{Node}_decision"] = State($"select.{Node}_decision", "Idle");
                States[$"sensor.{Node}_status"] = State($"sensor.{Node}_status", "idle");
                States[$"sensor.{Node}_activity"] = State($"sensor.{Node}_activity", "beta", new { updated = "2", response = "beta" });
            }
            if (id == $"button.{Node}_stop") { States.Remove($"sensor.{Node}_status"); }
            return Task.CompletedTask;
        }

        public Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct)
        {
            Calls.Add($"publish {topic}");
            Request = (string?)JsonNode.Parse(payload)!["request"];
            States["sensor.agent_bridge_rehearsal_m_new_prompt_payload"] = State("sensor.agent_bridge_rehearsal_m_new_prompt_payload", "x", new { request = Request });
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task An_episode_launches_holds_its_question_for_the_samplers_answers_it_and_ends_the_session()
    {
        var dir = Directory.CreateTempSubdirectory("soak-test").FullName;
        try
        {
            var fake = new FakeHa();
            var options = new SoakOptions("rehearsal_m", "Rehearsal", new Dictionary<string, string> { ["Copilot"] = "claude-haiku-4.5" }, new Dictionary<string, string>(),
                TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(35), TimeSpan.FromMinutes(6), 1, null, null, "copilot-nested");
            var homes = Homes.For(dir);
            var driver = new SoakDriver(fake, options, Path.Combine(dir, "state"), homes, () => fake.Now, (d, _) => { fake.Now += d; return Task.CompletedTask; });
            await driver.RunAsync(CancellationToken.None);
            var state = SoakDriver.Load(Path.Combine(dir, "state"));
            var record = Assert.Single(state.Episodes);
            Assert.Equal(1, record.Answered);
            Assert.Equal(1, record.Questions);
            // Launched with the request, held for the samplers, answered on the selector with Send, ended.
            Assert.StartsWith("publish copilot/cli/machine/rehearsal_m/newsession/promptpayload", fake.Calls[0]);
            Assert.Contains(fake.Calls, c => c.StartsWith($"select.select_option select.{Node}_decision ", StringComparison.Ordinal));
            var answeredAt = fake.Calls.FindIndex(c => c.StartsWith($"select.select_option select.{Node}_decision", StringComparison.Ordinal));
            Assert.Equal($"button.press button.{Node}_submit", fake.Calls[answeredAt + 1]);
            Assert.Contains($"button.press button.{Node}_stop", fake.Calls);
        }
        finally { Directory.Delete(dir, true); }
    }
}
