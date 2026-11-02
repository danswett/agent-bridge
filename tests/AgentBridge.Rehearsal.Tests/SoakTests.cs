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

        // A lone schema text field, as agent-ha-bridge arms it (ha-decisions.json "text"): one slot offering only Idle.
        var field = CardQuestion.Read(State($"select.{Node}_decision", "Awaiting answer...", new { decision_id = "d7", field_count = 1, multi_field = true }))!;
        Assert.NotNull(Assert.Single(CardAnswer.Plan(Node, field, AnswerStyle.Payload, _ => ["Idle"], 0, DateTimeOffset.UnixEpoch)).Topic);

        var strict = CardQuestion.Read(State($"select.{Node}_decision", "Awaiting answer...", new { decision_id = "d5", choices = new[] { "a", "b" } }))!;
        Assert.Equal(["select.select_option", "button.press"], CardAnswer.Plan(Node, strict, AnswerStyle.Payload, _ => [], 0, DateTimeOffset.UnixEpoch).Select(a => $"{a.Domain}.{a.Service}"));
    }

    [Fact]
    public void A_selector_back_at_Idle_is_no_question_even_with_its_old_attributes()
    {
        Assert.Null(CardQuestion.Read(State($"select.{Node}_decision", "Idle", new { decision_id = "d6", choices = new[] { "a" } })));
        Assert.Null(CardQuestion.Read(State($"select.{Node}_decision", "Idle", new { })));
    }

    [Fact]
    public void Two_Codex_turns_answering_alike_still_mark_different_ends()
    {
        // Codex's activity has no updated stamp: the response alone would not move.
        var first = State($"sensor.{Node}_activity", "ok", new { response = "ok", history = new[] { "Reading your message", "ok" } });
        var second = State($"sensor.{Node}_activity", "ok", new { response = "ok", history = new[] { "Reading your message", "ok", "Reading your message", "ok" } });
        Assert.NotEqual(SoakDriver.Marker(first), SoakDriver.Marker(second));
        Assert.Equal("7", SoakDriver.Marker(State($"sensor.{Node}_activity", "ok", new { updated = "7", response = "ok" })));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("..\\..\\x")]
    [InlineData("abc*")]
    public void Collect_takes_only_a_session_id_never_a_path(string id) =>
        Assert.Throws<UsageException>(() => SoakDriver.Collect(Homes.For(Path.Combine(AppContext.BaseDirectory, "no-home")), id, "x.zip"));

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
    }

    // A disposable Home Assistant played by the test: agent-ha-bridge launches (the card a few
    // reads after the outcome, as on VM-M), asks one question, takes the answer from Send, runs
    // each later prompt from the reply card to idle, and ends the session on End session.
    sealed class FakeHa : ISoakHa
    {
        public readonly Dictionary<string, HaState> States = [];
        public readonly List<string> Calls = [];
        public readonly List<JsonNode> Launches = [];
        public string? Request;
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        int polls;
        int unseen;
        bool launched;
        bool asked;
        int updated = 1;

        void Status(string state) => States[$"sensor.{Node}_status"] = State($"sensor.{Node}_status", state);
        void Reply(string text) => States[$"sensor.{Node}_activity"] = State($"sensor.{Node}_activity", text, new { updated = $"{++updated}", response = text });

        public Task<HaState?> StateAsync(string entityId, CancellationToken ct)
        {
            if (entityId == $"sensor.{Node}_status" && launched && !States.ContainsKey(entityId) && ++unseen > 3)
            {
                Status("working");
                launched = false;
            }
            if (entityId == $"sensor.{Node}_status" && States.TryGetValue(entityId, out var s) && s.State == "working" && ++polls > 2)
            {
                polls = 0;
                if (!asked)
                {
                    // The agent gets to its question.
                    asked = true;
                    Status("waiting");
                    States[$"select.{Node}_decision"] = State($"select.{Node}_decision", "Awaiting answer...", new { decision_id = "q1", choices = new[] { "alpha", "beta" }, field_count = 0 });
                }
                else
                {
                    Status("idle");
                    Reply("ok");
                }
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
                launched = true;
                States[$"sensor.{Node}_activity"] = State($"sensor.{Node}_activity", "Thinking", new { updated = "1" });
            }
            if (id == $"button.{Node}_submit")
            {
                if (States.TryGetValue($"select.{Node}_decision", out var d) && d.State != "Idle")
                {
                    States[$"select.{Node}_decision"] = State($"select.{Node}_decision", "Idle");
                    Status("idle");
                    Reply("beta");
                }
                else { Status("working"); }
            }
            if (id == $"button.{Node}_stop") { States.Remove($"sensor.{Node}_status"); }
            return Task.CompletedTask;
        }

        public Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct)
        {
            Calls.Add($"publish {topic}{(retain ? " retained" : "")}");
            var node = JsonNode.Parse(payload)!;
            if (topic == $"copilot/cli/{Node}/replypayload/set")
            {
                // The reply card's words: the session takes them as its next prompt.
                Status("working");
                return Task.CompletedTask;
            }
            Launches.Add(node);
            Request = (string?)node["request"];
            States["sensor.agent_bridge_rehearsal_m_new_prompt_payload"] = State("sensor.agent_bridge_rehearsal_m_new_prompt_payload", "x", new { request = Request });
            return Task.CompletedTask;
        }
    }

    static SoakOptions Options(string only, int count = 66) => new("rehearsal_m", "Rehearsal", new Dictionary<string, string> { ["Copilot"] = "claude-haiku-4.5" }, new Dictionary<string, string>(),
        TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(35), TimeSpan.FromMinutes(6), count, null, null, only);

    static SoakDriver Driver(FakeHa fake, SoakOptions options, string dir) =>
        new(fake, options, Path.Combine(dir, "state"), Homes.For(dir), () => fake.Now, (d, _) => { fake.Now += d; return Task.CompletedTask; });

    [Fact]
    public async Task An_episode_waits_for_its_card_holds_its_question_for_the_samplers_answers_it_runs_its_next_prompt_and_ends_the_session()
    {
        var dir = Directory.CreateTempSubdirectory("soak-test").FullName;
        try
        {
            var fake = new FakeHa();
            await Driver(fake, Options("copilot-nested"), dir).RunAsync(CancellationToken.None);
            var record = Assert.Single(SoakDriver.Load(Path.Combine(dir, "state")).Episodes);
            Assert.Empty(record.Problems);
            Assert.Equal(2, record.Turns);
            Assert.Equal(1, record.Answered);
            Assert.Equal(1, record.Questions);
            // Launched with the request, held for the samplers, answered on the selector with Send, ended.
            Assert.StartsWith("publish copilot/cli/machine/rehearsal_m/newsession/promptpayload", fake.Calls[0]);
            Assert.Equal("Rehearsal", (string?)fake.Launches[0]["settings"]!["workspace"]);
            var answeredAt = fake.Calls.FindIndex(c => c.StartsWith($"select.select_option select.{Node}_decision", StringComparison.Ordinal));
            Assert.True(answeredAt > 0);
            Assert.Equal($"button.press button.{Node}_submit", fake.Calls[answeredAt + 1]);
            // The next prompt goes as the reply card publishes it, not retained, never through Send.
            Assert.Contains($"publish copilot/cli/{Node}/replypayload/set", fake.Calls);
            Assert.Single(fake.Calls, c => c == $"button.press button.{Node}_submit");
            Assert.Equal($"button.press button.{Node}_stop", fake.Calls[^1]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_resume_names_the_session_and_no_workspace_and_a_restarted_driver_carries_on_after_the_last_episode()
    {
        var dir = Directory.CreateTempSubdirectory("soak-test").FullName;
        try
        {
            // The block's copilot-resume is plan index 12; a Copilot episode before it ended cleanly.
            Directory.CreateDirectory(Path.Combine(dir, "state"));
            var earlier = new SoakState { Began = DateTimeOffset.UnixEpoch, Episodes = [new SoakRecord { Index = 3, Scenario = "copilot-questions", Agent = "Copilot", Session = "0894c91f-f3f5-4546-8684-a8f9e6ad58e7", Ended = DateTimeOffset.UnixEpoch }] };
            File.WriteAllText(Path.Combine(dir, "state", "soak.json"), JsonSerializer.Serialize(earlier, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var fake = new FakeHa { Now = DateTimeOffset.UnixEpoch.AddHours(10) };
            await Driver(fake, Options("copilot-resume", count: 13), dir).RunAsync(CancellationToken.None);
            var settings = fake.Launches[0]["settings"]!.AsObject();
            Assert.Equal("0894c91f-f3f5-4546-8684-a8f9e6ad58e7", (string?)settings["resume"]);
            Assert.False(settings.ContainsKey("workspace"));
            var episodes = SoakDriver.Load(Path.Combine(dir, "state")).Episodes;
            Assert.Equal([3, 12], episodes.Select(e => e.Index));
            Assert.Equal("0894c91f-f3f5-4546-8684-a8f9e6ad58e7", episodes[1].ResumedFrom);
        }
        finally { Directory.Delete(dir, true); }
    }
}
