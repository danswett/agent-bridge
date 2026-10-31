using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// Stage 7, the daemon's upkeep, against an in-process Home Assistant and the recording of
// agent-ha-bridge's own functions (Fixtures/ha-upkeep.json): the Detailed activity switch, the
// liveness sensor's repair, a live session's entities put back, and what the dashboard's forget
// leaves of this machine.
public sealed class UpkeepTests : IDisposable
{
    const string Machine = "DSWETT-DEV-VM1";
    const string A = "6fcbab0c-1d2e-4f50-9a61-7b8c9d0e1f20";
    const string B = "c1a2b3c4-d5e6-4f70-8a91-b2c3d4e5f607";
    static readonly string Slug = HaContract.MachineSlug(Machine);
    static readonly string NodeA = HaContract.NodeId(A);
    static readonly string Online = HaContract.MachineEntityId("binary_sensor", "online", Slug);
    static readonly string OnlineUnique = $"agent_bridge_{Slug}_online";

    static readonly Lazy<JsonObject> Loaded = new(() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ha-upkeep.json")))!);

    readonly string folder = Path.Combine(Path.GetTempPath(), "abhk-" + Guid.NewGuid().ToString("N"));
    readonly FakeHomeAssistant ha = new();
    readonly FakeSessions sessions = new();
    readonly FakeTimeProvider time = new(FakeSessions.T0);
    readonly List<string> log = [];
    readonly List<AnswerRequest> answered = [];
    // agent-ha-bridge running for this user, as G5 sees it.
    string? daemon;
    // What the host's answerer says of each answer.
    AnswerOutcome answerOutcome = AnswerOutcome.Delivered;

    public void Dispose()
    {
        if (Directory.Exists(folder)) { Directory.Delete(folder, true); }
    }

    HaConnector Connector(bool answers = false, IHostUpdates? updates = null, HaLauncher? launch = null,
        Func<(UsageReport, long)>? usage = null, IReadOnlyList<string>? accounts = null) => new(new HaConnectorOptions
    {
        MachineName = Machine,
        Version = "0.1.0",
        StateFile = Path.Combine(folder, "ha-connector.json"),
        FieldPinDelay = TimeSpan.Zero,
        SelectDelay = TimeSpan.Zero,
        DiscoveryReset = TimeSpan.Zero,
        RegisteredDelay = TimeSpan.Zero,
        HelperStateDelay = TimeSpan.Zero,
        UsageAccountSensors = accounts ?? [],
    }, ha, sessions, () => daemon, usage, m => { lock (log) { log.Add(m); } }, time,
        answer: answers ? (r, _) =>
        {
            lock (answered) { answered.Add(r); }
            return Task.FromResult(new AnswerResult(answerOutcome));
        } : null,
        stop: answers ? (_, _) => Task.FromResult(new StopResult(StopOutcome.Ended)) : null,
        launch: launch, updates: updates);

    async Task Run(HaConnector c, double seconds)
    {
        for (var t = 0; t < seconds; t++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await c.TickAsync();
        }
    }

    int Logged(string text) { lock (log) { return log.Count(l => l.Contains(text, StringComparison.Ordinal)); } }

    int Retractions(string topic) => ha.To(topic).Count(p => p.Payload.Length == 0);

    // ---- The contract -------------------------------------------------------------------------

    [Fact]
    public void The_switch_the_daemon_makes_and_the_repairs_timings_are_the_connectors()
    {
        var helper = Loaded.Value["helper"]!;
        Assert.Equal(helper["id"]!.GetValue<string>(), HaContract.DetailedHelperId(Slug));
        Assert.Equal(helper["name"]!.GetValue<string>(), HaContract.DetailedHelperName(Slug));
        Assert.Equal(helper["displayName"]!.GetValue<string>(), HaContract.DetailedHelperDisplayName(Machine));
        Assert.Equal(HaContract.DetailedHelperIcon, helper["icon"]!.GetValue<string>());
        Assert.Equal(helper["entityId"]!.GetValue<string>(), HaContract.MachineEntityId("input_boolean", "detailed_activity", Slug));
        // Home Assistant makes the id from the name, so the two must give the same.
        Assert.Equal(HaContract.DetailedHelperId(Slug), HaContract.HelperIdOf(HaContract.DetailedHelperName(Slug)));

        var l = Loaded.Value["liveness"]!;
        Assert.Equal(l["DetachedEntitySeconds"]!.GetValue<int>(), (int)HaContract.OnlineStall.TotalSeconds);
        Assert.Equal(HaContract.OnlineStallBeats, l["DetachedEntityBeats"]!.GetValue<int>());
        Assert.Equal(HaContract.OnlineRepairLimit, l["DetachedEntityRepairLimit"]!.GetValue<int>());
        Assert.Equal(HaContract.OnlineExpireSeconds, l["OnlineExpireSeconds"]!.GetValue<int>());
        Assert.Equal(l["OnlineHeartbeatSeconds"]!.GetValue<int>(), (int)HaContract.HeartbeatInterval.TotalSeconds);
        Assert.Equal(l["EntityRestoreSeconds"]!.GetValue<int>(), (int)HaContract.EntityRestore.TotalSeconds);
        Assert.Equal(HaContract.RegisteredAttempts, l["WaitAttempts"]!.GetValue<int>());
        Assert.Equal(l["WaitDelayMs"]!.GetValue<int>(), (int)HaContract.RegisteredDelay.TotalMilliseconds);
        Assert.Equal(l["DiscoveryResetMs"]!.GetValue<int>(), (int)HaContract.DiscoveryReset.TotalMilliseconds);
    }

    public static TheoryData<string> Scenarios() => [.. ((JsonObject)Loaded.Value["scenarios"]!).Select(p => p.Key)];

    // Confirm-DaemonOnlineHeartbeat run beat by beat over scripted reads, and the connector's
    // judgement over the same reads: a rebuild, and the one notice of giving up, at exactly the
    // beats the daemon's came at.
    [Theory]
    [MemberData(nameof(Scenarios))]
    public void The_liveness_sensor_is_judged_beat_by_beat_as_the_daemon_judges_it(string scenario)
    {
        var l = new HaLiveness(HaContract.OnlineStall, HaContract.OnlineStallBeats, HaContract.OnlineRepairLimit);
        var start = DateTimeOffset.Parse("2026-10-29T20:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var step in Loaded.Value["scenarios"]![scenario]!.AsArray())
        {
            var at = step!["at"]!.GetValue<int>();
            var read = step["read"]!.GetValue<string>() switch
            {
                "absent" => HaLiveness.Read.Gone,
                "error" => HaLiveness.Read.Unreadable,
                "nofield" => HaLiveness.Read.Present(null),
                var reported => HaLiveness.Read.Present(reported),
            };
            var verdict = l.Observe(start.AddSeconds(at), read);
            var gaveUp = step["logs"]!.AsArray().Any(m => m!.GetValue<string>().Contains("leaving it alone", StringComparison.Ordinal));
            Assert.True(step["rebuilt"]!.GetValue<bool>() == (verdict == HaLiveness.Verdict.Rebuild), $"{scenario} at {at}: the daemon {(step["rebuilt"]!.GetValue<bool>() ? "rebuilt" : "did not rebuild")}, the connector said {verdict}");
            Assert.True(gaveUp == (verdict == HaLiveness.Verdict.GiveUp), $"{scenario} at {at}: the daemon {(gaveUp ? "gave up" : "did not give up")}, the connector said {verdict}");
        }
    }

    public static TheoryData<string> HelperCases() => [.. ((JsonObject)Loaded.Value["helperCases"]!).Select(p => p.Key)];

    // Initialize-CopilotVerboseToggle, case by case: the same calls that change anything, in the
    // same order, for the same Home Assistant.
    [Theory]
    [MemberData(nameof(HelperCases))]
    public async Task The_detailed_activity_switch_is_made_and_named_as_the_daemon_makes_and_names_it(string name)
    {
        var id = HaContract.DetailedHelperId(Slug);
        var entityId = "input_boolean." + id;
        var display = HaContract.DetailedHelperDisplayName(Machine);
        switch (name)
        {
            case "absent": break;
            case "present": ha.Helper(id, HaContract.DetailedHelperName(Slug)); ha.HelperNames[entityId] = display; break;
            case "renamed": ha.Helper(id, HaContract.DetailedHelperName(Slug)); ha.HelperNames[entityId] = "Detailed activity"; break;
            case "starting": ha.Helpers[id] = HaContract.DetailedHelperName(Slug); ha.HelperNames[entityId] = display; break;
            case "deduplicated": ha.Helper(id, HaContract.DetailedHelperName(Slug), "on"); ha.HelperListEmpty = true; break;
            case "noState": ha.HelperStateless = true; break;
            case "listFails": ha.HelperListFails = new HaCommandException("input_boolean/list", "unknown_error", "Home Assistant refused the command"); break;
            default: Assert.Fail($"no setup for {name}"); break;
        }
        await using var c = Connector();
        await c.TickAsync();

        var expected = Loaded.Value["helperCases"]![name]!["commands"]!.AsArray().Select(command => command!["type"]!.GetValue<string>() switch
        {
            "input_boolean/list" => "list",
            "input_boolean/create" => $"create {command["name"]!.GetValue<string>()} {command["icon"]!.GetValue<string>()}",
            "input_boolean/delete" => $"delete {command["input_boolean_id"]!.GetValue<string>()}",
            "config/entity_registry/update" => $"name {command["entity_id"]!.GetValue<string>()} {command["name"]!.GetValue<string>()}",
            _ => null,
        }).OfType<string>();
        Assert.Equal(expected, ha.HelperCalls);
        if (name == "deduplicated")
        {
            // The original is kept with its value; only the numbered copy went.
            Assert.Equal("on", (await ha.GetStateAsync(entityId))!.State);
            Assert.Equal([id], ha.Helpers.Keys);
        }
    }

    [Fact]
    public async Task A_switch_Home_Assistant_makes_under_another_name_is_left_as_it_is_not_deleted()
    {
        // Something the connector did not ask for, under a name that is not a numbered copy of the
        // switch: never removed.
        ha.CreateAs = _ => "living_room_lamp";
        await using var c = Connector();
        await c.TickAsync();
        Assert.DoesNotContain(ha.HelperCalls, call => call.StartsWith("delete", StringComparison.Ordinal));
        Assert.Equal(1, Logged("left as it is"));
    }

    [Fact]
    public async Task The_switch_is_never_made_again_over_one_Home_Assistant_holds_and_its_value_is_kept()
    {
        ha.Helper(HaContract.DetailedHelperId(Slug), HaContract.DetailedHelperName(Slug), "on");
        sessions.Set(FakeSessions.Copilot(A) with { Activity = new Activity { Reasoning = "**Weighing it**" } });
        await using var c = Connector();
        await Run(c, 3);
        Assert.DoesNotContain(ha.HelperCalls, call => call.StartsWith("create", StringComparison.Ordinal) || call.StartsWith("delete", StringComparison.Ordinal));
        var detail = JsonDocument.Parse(ha.Retained[HaContract.Topics(NodeA).ActivityAttributes]).RootElement;
        Assert.True(detail.GetProperty("verbose").GetBoolean());
        var capabilities = JsonDocument.Parse(ha.Retained[HaContract.GlobalAttributesTopic(Slug)]).RootElement.GetProperty("capabilities");
        Assert.True(capabilities.GetProperty("detailed").GetBoolean());
    }

    // ---- Liveness -----------------------------------------------------------------------------

    [Fact]
    public async Task A_liveness_sensor_that_stops_taking_beats_is_rebuilt_once_and_then_takes_them_again()
    {
        await using var c = Connector();
        await Run(c, 5);
        var (topic, _) = HaContract.OnlineConfig(Slug, Machine);
        Assert.Equal(0, Retractions(topic));

        // Home Assistant stops applying its messages (agent-ha-bridge #177): every beat is answered
        // 200, and nothing moves.
        // The beat at 63 s is the first the sensor answers by its pinned id (the one at 3 s came
        // before the pin), so the silence it is measured from begins there.
        ha.Deaf.Add(OnlineUnique);
        await Run(c, 230);
        Assert.Equal(0, Retractions(topic));
        await Run(c, 20);
        Assert.Equal(1, Retractions(topic));
        Assert.Equal(1, Logged("has taken no beat for"));

        // Exactly the daemon's rebuild: retracted, then declared again, then the entity pinned and only
        // then a beat.
        var rebuild = Loaded.Value["rebuild"]!.AsArray();
        var sent = ha.To(topic).TakeLast(2).ToList();
        Assert.Equal(rebuild[0]!["payload"]!.GetValue<string>(), sent[0].Payload);
        Assert.True(sent[0].Retain);
        Contract.AssertJson(rebuild[1]!["payload"], JsonNode.Parse(sent[1].Payload), "rebuilt config");
        Assert.True(ha.Exists(Online));
        var afterConfig = ha.Log.IndexOf(sent[1]);
        Assert.Contains(ha.Log.Skip(afterConfig), p => p.Topic == HaContract.OnlineStateTopic(Slug));

        // Built again, it listens: no second rebuild.
        await Run(c, 600);
        Assert.Equal(1, Retractions(topic));
    }

    [Fact]
    public async Task A_rebuilt_sensor_Home_Assistant_does_not_take_back_is_waited_for_a_bounded_while_and_sent_no_beat_until_it_returns()
    {
        await using var c = Connector();
        await Run(c, 5);
        var (topic, _) = HaContract.OnlineConfig(Slug, Machine);
        ha.Deaf.Add(OnlineUnique);
        // Retracted for the rebuild, and not taken back when it is declared again.
        ha.BeforePublish = t => { if (t == topic) { ha.Loading.Add(Online); } };

        var readsBefore = 0;
        var logBefore = 0;
        for (var i = 0; i < 400 && Retractions(topic) == 0; i++)
        {
            readsBefore = ha.ReadsOf(Online);
            logBefore = ha.Log.Count;
            await Run(c, 1);
        }
        Assert.Equal(1, Retractions(topic));
        ha.BeforePublish = null;
        // The beat's own read, then one per registration attempt and no more.
        Assert.Equal(1 + HaContract.RegisteredAttempts, ha.ReadsOf(Online) - readsBefore);
        Assert.Equal(1, Logged("did not come back after the rebuild"));
        // Declared again, and no beat after it: one that outruns discovery is dropped.
        var pass = ha.Log.Skip(logBefore).ToList();
        var redeclared = pass.FindLastIndex(p => p.Topic == topic && p.Payload.Length > 0);
        Assert.True(redeclared >= 0, "the sensor was declared again");
        Assert.DoesNotContain(pass.Skip(redeclared), p => p.Topic == HaContract.OnlineStateTopic(Slug));

        // Home Assistant takes it back before the next beat: that beat goes, and it holds.
        ha.Loading.Remove(Online);
        var beatsBefore = ha.To(HaContract.OnlineStateTopic(Slug)).Count();
        await Run(c, 60);
        Assert.True(ha.To(HaContract.OnlineStateTopic(Slug)).Count() > beatsBefore, "the next beat was sent");
        await Run(c, 600);
        Assert.Equal(1, Retractions(topic));
        Assert.Equal(0, Logged("publishing the machine again"));
    }

    [Fact]
    public async Task A_rebuild_that_does_not_hold_is_tried_three_times_then_left_alone_and_said_once()
    {
        await using var c = Connector();
        await Run(c, 5);
        var (topic, _) = HaContract.OnlineConfig(Slug, Machine);
        ha.StaysDeaf = true;
        ha.Deaf.Add(OnlineUnique);
        await Run(c, 1500);
        Assert.Equal(HaContract.OnlineRepairLimit, Retractions(topic));
        Assert.Equal(1, Logged("leaving it alone rather than flapping it"));

        // agent-ha-bridge had the machine for a while and handed it back with the sensor still deaf:
        // the count is kept, so the hand-back is not three more rebuilds.
        daemon = "agent-ha-bridge is running for this user (AgentBridgeDaemon_0123)";
        await Run(c, 5);
        daemon = null;
        await Run(c, 900);
        Assert.Equal(HaContract.OnlineRepairLimit, Retractions(topic));
        Assert.Equal(1, Logged("leaving it alone rather than flapping it"));
    }

    [Fact]
    public async Task A_sensor_Home_Assistant_cannot_be_asked_about_is_never_rebuilt()
    {
        await using var c = Connector();
        await Run(c, 5);
        ha.GetFailsFor = id => id == Online;
        await Run(c, 900);
        Assert.Equal(0, Retractions(HaContract.OnlineConfig(Slug, Machine).Topic));
        Assert.Equal(0, Logged("has taken no beat"));
        Assert.Equal(0, Logged("publishing the machine again"));
    }

    // ---- A live session's entities ------------------------------------------------------------

    [Fact]
    public async Task A_running_sessions_card_removed_from_Home_Assistant_is_published_again_with_what_it_showed()
    {
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Working, response: "Halfway there"));
        await using var c = Connector();
        await Run(c, 5);
        var status = $"sensor.{NodeA}_status";
        Assert.True(ha.Exists(status));

        // Everything of it cleared, as a forget or a hand in Home Assistant would: entities and the
        // retained values they showed.
        foreach (var topic in HaContract.ForgetTopics(Slug, [NodeA]).Where(t => t.Contains(NodeA, StringComparison.Ordinal)))
        {
            await ha.PublishAsync(topic, "", true);
        }
        Assert.False(ha.Exists(status));
        await Run(c, 140);
        Assert.True(ha.Exists(status));
        Assert.True(ha.Exists($"sensor.{NodeA}_activity"));
        Assert.Equal("working", (await ha.GetStateAsync(status))!.State);
        Assert.Equal("Halfway there", JsonDocument.Parse(ha.Retained[HaContract.Topics(NodeA).ActivityAttributes]).RootElement.GetProperty("response").GetString());
        Assert.Equal(1, Logged("are gone from Home Assistant while it runs here"));
    }

    [Fact]
    public async Task A_session_is_not_published_again_on_a_read_that_failed_nor_before_its_entities_could_register()
    {
        sessions.Set(FakeSessions.Copilot(A));
        await using var c = Connector();
        await Run(c, 2);
        foreach (var topic in HaContract.ForgetTopics(Slug, [NodeA]).Where(t => t.Contains(NodeA, StringComparison.Ordinal) && t.EndsWith("/config", StringComparison.Ordinal)))
        {
            await ha.PublishAsync(topic, "", true);
        }
        // Gone at once, but too new to judge: published under a minute ago.
        await Run(c, 50);
        Assert.False(ha.Exists($"sensor.{NodeA}_status"));
        // And a Home Assistant that cannot be read says nothing about whether it is there.
        ha.GetFailsFor = id => id == $"sensor.{NodeA}_status";
        await Run(c, 120);
        Assert.False(ha.Exists($"sensor.{NodeA}_status"));
        ha.GetFailsFor = null;
        // Read gone, but not yet for a minute: a Home Assistant still starting answers so too.
        await Run(c, 50);
        Assert.False(ha.Exists($"sensor.{NodeA}_status"));
        await Run(c, 30);
        Assert.True(ha.Exists($"sensor.{NodeA}_status"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_card_Home_Assistant_answers_for_again_within_the_minute_is_left_as_it_is(bool answers)
    {
        sessions.Set(FakeSessions.Copilot(A));
        await using var c = Connector(answers);
        await Run(c, 70);
        // Home Assistant starting up, twice: the entities answer 404 until MQTT has loaded them
        // again, each time for under a minute.
        ha.ClearLog();
        var sentinel = answers ? SessionEntities.Reply(NodeA) : $"sensor.{NodeA}_status";
        for (var i = 0; i < 2; i++)
        {
            ha.Loading.Add(sentinel);
            await Run(c, 40);
            ha.Loading.Clear();
            await Run(c, 40);
        }
        await Run(c, 60);
        Assert.Equal(0, Logged("are gone from Home Assistant"));
        Assert.Empty(ha.To(HaContract.Topics(NodeA).Availability));
    }

    static readonly Field Release = new()
    {
        Name = "release", Title = "Release", Type = FieldType.Choice, Required = true,
        Options = [new Option("cut_now", "Cut the release now"), new Option("wait", "Wait for the fix")],
    };

    [Fact]
    public async Task After_a_Home_Assistant_restart_the_reply_box_is_blank_again_and_a_waiting_question_is_armed_again()
    {
        var question = new Question { Id = "q1", Kind = QuestionKind.Form, Message = "Cut the release?", AskedAt = FakeSessions.T0, Fields = [Release] };
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Waiting) with { Question = question });
        sessions.Set(FakeSessions.Copilot(B));
        await using var c = Connector(answers: true);
        await Run(c, 90);
        var decisionA = SessionEntities.Decision(NodeA);
        var nodeB = HaContract.NodeId(B);
        Assert.Equal(HaDecision.Awaiting, (await ha.GetStateAsync(decisionA))!.State);
        Assert.Equal(HaDecision.Idle, (await ha.GetStateAsync(SessionEntities.Decision(nodeB)))!.State);

        // The optimistic entities come back from a restart with no value at all.
        ha.ForgetOptimistic();
        Assert.Equal("unknown", (await ha.GetStateAsync(decisionA))!.State);
        await Run(c, 20);
        var armed = (await ha.GetStateAsync(decisionA))!;
        Assert.Equal(HaDecision.Awaiting, armed.State);
        Assert.Equal("q1", armed.Attributes.GetProperty("decision_id").GetString());
        Assert.Equal(HaDecision.ReplyBlank, (await ha.GetStateAsync(SessionEntities.Reply(NodeA)))!.State);
        Assert.Equal(HaDecision.Idle, (await ha.GetStateAsync(SessionEntities.Decision(nodeB)))!.State);
        Assert.Equal(HaDecision.ReplyBlank, (await ha.GetStateAsync(SessionEntities.Reply(nodeB)))!.State);

        // And the question re-armed is still answered once, from the card.
        c.Observe(ha.Choose(SessionEntities.Field(NodeA, 1), "Cut the release now"));
        await Run(c, 1);
        c.Observe(ha.Press(SessionEntities.Submit(NodeA)));
        await Run(c, 5);
        Assert.Single(answered);
    }

    [Fact]
    public async Task A_new_cards_reply_box_is_blank_from_the_start_and_its_question_is_not_armed_again_a_minute_later()
    {
        var question = new Question { Id = "q1", Kind = QuestionKind.Form, Message = "Cut the release?", AskedAt = FakeSessions.T0, Fields = [Release] };
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Waiting) with { Question = question });
        await using var c = Connector(answers: true);
        await Run(c, 1);
        Assert.Equal(HaDecision.ReplyBlank, (await ha.GetStateAsync(SessionEntities.Reply(NodeA)))!.State);

        // Somebody choosing an answer over the first minutes keeps the choice.
        ha.Choose(SessionEntities.Field(NodeA, 1), "Wait for the fix");
        await Run(c, 150);
        Assert.Equal("Wait for the fix", (await ha.GetStateAsync(SessionEntities.Field(NodeA, 1)))!.State);
        Assert.Equal(0, Logged("goes on screen again"));
        Assert.Equal(0, Logged("armed " + NodeA + " again"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Send_pressed_on_the_card_Home_Assistant_brought_back_by_itself_is_read_before_the_question_is_armed_again(bool firstReadFails)
    {
        var question = new Question { Id = "q1", Kind = QuestionKind.Form, Message = "Cut the release?", AskedAt = FakeSessions.T0, Fields = [Release] };
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Waiting) with { Question = question });
        await using var c = Connector(answers: true);
        await Run(c, 90);

        // A restart that keeps the selector and the choice but not the reply box. Once the box is
        // put back, the card looks as it did, and an answer is given on it before the next pass,
        // with the watch still down after the restart.
        ha.ForgetOptimistic(SessionEntities.Reply(NodeA));
        for (var i = 0; i < 30 && (await ha.GetStateAsync(SessionEntities.Reply(NodeA)))!.State != HaDecision.ReplyBlank; i++) { await Run(c, 1); }
        Assert.Equal(HaDecision.ReplyBlank, (await ha.GetStateAsync(SessionEntities.Reply(NodeA)))!.State);
        var rearmed = Logged("armed " + NodeA + " again");
        ha.Choose(SessionEntities.Field(NodeA, 1), "Wait for the fix");
        ha.Press(SessionEntities.Submit(NodeA));
        if (firstReadFails)
        {
            // The first read of Send after the restart fails: no press can be told from none.
            var failures = 1;
            ha.GetFailsFor = id => id == SessionEntities.Submit(NodeA) && failures-- > 0;
        }
        await Run(c, 10);
        var given = Assert.Single(answered);
        Assert.Equal("wait", given.Fields!["release"].GetString());

        // Answered there, it is not put on screen again over the answer, however the session moves
        // on before the question goes.
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Working) with { Question = question });
        await Run(c, 60);
        Assert.Single(answered);
        Assert.Equal(rearmed, Logged("armed " + NodeA + " again"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Words_in_the_reply_box_survive_the_card_being_put_back_around_them(bool machineAgain)
    {
        sessions.Set(FakeSessions.Copilot(A));
        await using var c = Connector(answers: true);
        await Run(c, 70);
        await ha.CallServiceAsync("text", "set_value", new JsonObject { ["entity_id"] = SessionEntities.Reply(NodeA), ["value"] = "half a reply" });
        if (machineAgain)
        {
            // Only the liveness sensor's config goes: the machine is published again around the card.
            await ha.PublishAsync(HaContract.OnlineConfig(Slug, Machine).Topic, "", true);
            await Run(c, 400);
            Assert.Equal(1, Logged("publishing the machine again"));
        }
        else
        {
            // A restart that loses the selector's value but not the box's.
            ha.ForgetOptimistic(SessionEntities.Decision(NodeA));
            await Run(c, 30);
            Assert.Equal(HaDecision.Idle, (await ha.GetStateAsync(SessionEntities.Decision(NodeA)))!.State);
        }
        Assert.Equal("half a reply", (await ha.GetStateAsync(SessionEntities.Reply(NodeA)))!.State);
    }

    [Fact]
    public async Task A_forms_words_kept_in_the_box_through_a_selector_only_loss_are_still_its_answer()
    {
        var note = new Field { Name = "note", Title = "Note", Type = FieldType.Text };
        var question = new Question { Id = "q1", Kind = QuestionKind.Form, Message = "Cut the release?", AskedAt = FakeSessions.T0, Fields = [Release, note] };
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Waiting) with { Question = question });
        await using var c = Connector(answers: true);
        await Run(c, 70);
        await ha.CallServiceAsync("text", "set_value", new JsonObject { ["entity_id"] = SessionEntities.Reply(NodeA), ["value"] = "after the fix lands" });

        // A restart that loses the selector's value but not the box's: the question goes on
        // screen again around the words.
        var rearmed = Logged("armed " + NodeA + " again");
        ha.ForgetOptimistic(SessionEntities.Decision(NodeA));
        for (var i = 0; i < 30 && Logged("armed " + NodeA + " again") == rearmed; i++) { await Run(c, 1); }
        Assert.Equal(rearmed + 1, Logged("armed " + NodeA + " again"));
        Assert.Equal("after the fix lands", (await ha.GetStateAsync(SessionEntities.Reply(NodeA)))!.State);

        c.Observe(ha.Choose(SessionEntities.Field(NodeA, 1), "Wait for the fix"));
        await Run(c, 1);
        c.Observe(ha.Press(SessionEntities.Submit(NodeA)));
        await Run(c, 5);
        var given = Assert.Single(answered);
        Assert.Equal("wait", given.Fields!["release"].GetString());
        Assert.Equal("after the fix lands", given.Fields!["note"].GetString());
    }

    [Fact]
    public async Task An_answer_given_just_as_the_machine_is_published_again_around_its_card_is_delivered()
    {
        var question = new Question { Id = "q1", Kind = QuestionKind.Form, Message = "Cut the release?", AskedAt = FakeSessions.T0, Fields = [Release] };
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Waiting) with { Question = question });
        await using var c = Connector(answers: true);
        await Run(c, 70);

        // Only the liveness sensor's config goes; the card stays, values and all. The answer is
        // given just as the machine is published again, with the watch down.
        var (topic, _) = HaContract.OnlineConfig(Slug, Machine);
        await ha.PublishAsync(topic, "", true);
        var given = false;
        ha.BeforePublish = t =>
        {
            if (t != topic || given) { return; }
            given = true;
            ha.Choose(SessionEntities.Field(NodeA, 1), "Wait for the fix");
            ha.Press(SessionEntities.Submit(NodeA));
        };
        await Run(c, 400);
        Assert.True(given);
        Assert.Equal(1, Logged("publishing the machine again"));
        var request = Assert.Single(answered);
        Assert.Equal("wait", request.Fields!["release"].GetString());
    }

    // An answer that may have reached the terminal is never typed again: not after a Home Assistant
    // restart lost the card's values, nor after its entities were lost and published again.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_answer_that_may_have_landed_is_not_given_again_once_the_card_is_put_back(bool entitiesLost)
    {
        answerOutcome = AnswerOutcome.Unconfirmed;
        var question = new Question { Id = "q1", Kind = QuestionKind.Form, Message = "Cut the release?", AskedAt = FakeSessions.T0, Fields = [Release] };
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Waiting) with { Question = question });
        await using var c = Connector(answers: true);
        await Run(c, 5);
        c.Observe(ha.Choose(SessionEntities.Field(NodeA, 1), "Cut the release now"));
        await Run(c, 1);
        c.Observe(ha.Press(SessionEntities.Submit(NodeA)));
        await Run(c, 2);
        Assert.Single(answered);

        if (entitiesLost)
        {
            foreach (var topic in HaContract.ForgetTopics(Slug, [NodeA]).Where(t => t.Contains(NodeA, StringComparison.Ordinal))) { await ha.PublishAsync(topic, "", true); }
        }
        else { ha.ForgetOptimistic(); }
        await Run(c, 150);
        Assert.Equal(HaDecision.Awaiting, (await ha.GetStateAsync(SessionEntities.Decision(NodeA)))!.State);

        // The question is still the one asked, and the card shows it; a second Send is not a
        // second answer.
        c.Observe(ha.Choose(SessionEntities.Field(NodeA, 1), "Wait for the fix"));
        await Run(c, 1);
        c.Observe(ha.Press(SessionEntities.Submit(NodeA)));
        await Run(c, 5);
        Assert.Single(answered);
    }

    [Fact]
    public async Task Reply_card_words_held_after_a_refusal_are_still_sent_on_a_press_once_the_card_is_put_back()
    {
        answerOutcome = AnswerOutcome.Busy;
        var note = new Field { Name = "note", Title = "Note", Type = FieldType.Text };
        var question = new Question { Id = "q1", Kind = QuestionKind.Form, Message = "Cut the release?", AskedAt = FakeSessions.T0, Fields = [Release, note] };
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Waiting) with { Question = question });
        await using var c = Connector(answers: true);
        await Run(c, 5);
        c.Observe(ha.Choose(SessionEntities.Field(NodeA, 1), "Wait for the fix"));
        await Run(c, 1);
        c.Observe(ha.SendFromCard(NodeA, "after the hotfix", "q1"));
        await Run(c, 1);
        Assert.Single(answered);

        // Home Assistant restarts: the choices are gone, the card's retained words are not.
        ha.ForgetOptimistic();
        await Run(c, 80);
        answerOutcome = AnswerOutcome.Delivered;
        c.Observe(ha.Choose(SessionEntities.Field(NodeA, 1), "Wait for the fix"));
        await Run(c, 1);
        c.Observe(ha.Press(SessionEntities.Submit(NodeA)));
        await Run(c, 2);
        Assert.Equal(2, answered.Count);
        Assert.Equal("after the hotfix", answered[1].Fields!["note"].GetString());
    }

    // A tap that reached nothing can be made again on the card put back; one that may have reached
    // the terminal cannot.
    [Theory]
    [InlineData(AnswerOutcome.Busy, 2)]
    [InlineData(AnswerOutcome.Unconfirmed, 1)]
    public async Task An_approval_tapped_before_the_card_was_put_back_is_answerable_again_only_if_nothing_landed(AnswerOutcome first, int delivered)
    {
        answerOutcome = first;
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Waiting) with
        {
            Agent = AgentKind.Codex,
            Question = new Question
            {
                Id = "a1", Kind = QuestionKind.Approval, Message = "Codex wants to run git push", AskedAt = FakeSessions.T0,
                Fields = [new Field { Name = "decision", Type = FieldType.Choice, Options = [new Option("approve", "Approve once"), new Option("deny", "Deny")], Required = true }],
            },
        });
        await using var c = Connector(answers: true);
        await Run(c, 5);
        c.Observe(ha.Choose(SessionEntities.Decision(NodeA), "Approve"));
        await Run(c, 1);
        Assert.Single(answered);

        // Home Assistant restarts before the card is read waiting again: put back, it waits.
        ha.ForgetOptimistic();
        await Run(c, 80);
        Assert.Equal(HaDecision.Awaiting, (await ha.GetStateAsync(SessionEntities.Decision(NodeA)))!.State);
        answerOutcome = AnswerOutcome.Delivered;
        c.Observe(ha.Choose(SessionEntities.Decision(NodeA), "Approve"));
        await Run(c, 1);
        Assert.Equal(delivered, answered.Count);
    }

    [Fact]
    public async Task A_selector_removed_on_its_own_is_published_again_with_its_question()
    {
        var question = new Question { Id = "q1", Kind = QuestionKind.Form, Message = "Cut the release?", AskedAt = FakeSessions.T0, Fields = [Release] };
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Waiting) with { Question = question });
        await using var c = Connector(answers: true);
        await Run(c, 5);
        var decision = SessionEntities.Decision(NodeA);
        await ha.PublishAsync(HaContract.DecisionConfig(HaContract.Topics(NodeA), "x", Machine, [HaDecision.Idle], true).Topic, "", true);
        Assert.Null(await ha.GetStateAsync(decision));
        Assert.NotNull(await ha.GetStateAsync(SessionEntities.Reply(NodeA)));
        await Run(c, 150);
        var armed = (await ha.GetStateAsync(decision))!;
        Assert.Equal(HaDecision.Awaiting, armed.State);
        Assert.Equal("q1", armed.Attributes.GetProperty("decision_id").GetString());
    }

    [Fact]
    public async Task A_machine_published_again_while_its_host_runs_keeps_what_its_sessions_were_showing()
    {
        ha.Helper(HaContract.DetailedHelperId(Slug), HaContract.DetailedHelperName(Slug), "on");
        sessions.Set(FakeSessions.Copilot(A, response: "First answer"));
        await using var c = Connector();
        await Run(c, 5);
        // A new turn thinking over the last turn's reply: the card shows the thought.
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Working) with { Activity = new Activity { Response = "First answer", Reasoning = "Weighing the second" } });
        await Run(c, 2);
        JsonElement Detail() => JsonDocument.Parse(ha.Retained[HaContract.Topics(NodeA).ActivityAttributes]).RootElement;
        Assert.Equal("reasoning", Detail().GetProperty("response_kind").GetString());

        foreach (var topic in HaContract.ForgetTopics(Slug, [NodeA])) { await ha.PublishAsync(topic, "", true); }
        await Run(c, 260);
        Assert.Equal(1, Logged("publishing the machine again"));
        // Not a restart: the session is still the one thinking, not one first seen with a reply.
        Assert.Equal("reasoning", Detail().GetProperty("response_kind").GetString());
        Assert.Equal("Weighing the second", Detail().GetProperty("response").GetString());
    }

    // ---- Forget -------------------------------------------------------------------------------

    [Fact]
    public async Task Everything_this_connector_keeps_in_Home_Assistant_is_on_the_list_the_dashboards_forget_clears()
    {
        var updates = new FakeUpdates();
        var launch = new HaLauncher(() => true, _ => Task.FromResult(new LaunchOptions
        {
            Agents = [new LaunchAgent { Id = "copilot", Label = "Copilot", Kind = AgentKind.Copilot }],
            Workspaces = [new LaunchWorkspace { Label = "agent-bridge", Path = @"C:\repos\agent-bridge" }],
            Defaults = new LaunchDefaults { Agent = "copilot", Workspace = "agent-bridge", Permissions = LaunchProtocol.Ask },
        }), (_, _) => Task.FromResult(new LaunchResult(LaunchOutcome.Refused, "not in this test")));
        var report = new UsageReport(
        [
            new AgentUsage { Agent = AgentKind.Copilot, Label = "GitHub Copilot", Account = "octocat", Source = UsageSource.Api, MeasuredAt = FakeSessions.T0,
                Windows = [new UsageWindow { Key = "plan", Label = "Plan", Percent = 40 }] },
            new AgentUsage { Agent = AgentKind.Copilot, Label = "GitHub Copilot", Account = "hubot", Source = UsageSource.Api, MeasuredAt = FakeSessions.T0, Slot = 2,
                Windows = [new UsageWindow { Key = "plan", Label = "Plan", Percent = 7.5 }] },
        ]);
        var question = new Question { Id = "q1", Kind = QuestionKind.Form, Message = "Cut the release?", AskedAt = FakeSessions.T0, Fields = [Release] };
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Waiting) with { Question = question });
        sessions.Set(FakeSessions.Copilot(B, response: "Done"));
        await using (var c = Connector(answers: true, updates: updates, launch: launch, usage: () => (report, 1), accounts: ["copilot_2"]))
        {
            await Run(c, 30);
            // One that ends and is retired, leaving only empties behind.
            sessions.End(B);
            ha.Draw(NodeA);
            await Run(c, 60);
        }
        Assert.NotEmpty(ha.Retained);

        // What the writer hands the card's X for this machine (Get-BridgeMachineForgetTopic): its own
        // topics and the sessions its retained list names.
        var listed = JsonNode.Parse(ha.Retained[HaContract.GlobalAttributesTopic(Slug)])!["sessions"]!.AsArray().Select(s => s!["node"]!.GetValue<string>()).ToList();
        var forget = HaContract.ForgetTopics(Slug, listed).ToHashSet(StringComparer.Ordinal);
        Assert.All(ha.Retained.Keys, topic => Assert.Contains(topic, forget));

        // So the X, pressed, leaves nothing of the machine but the switch, which is Home
        // Assistant's own helper and not MQTT's, as with the daemon.
        foreach (var topic in forget) { await ha.PublishAsync(topic, "", true); }
        Assert.Empty(ha.Retained);
        Assert.DoesNotContain(await ha.ListEntityRegistryAsync(), e => !e.EntityId.StartsWith("input_boolean.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_machine_forgotten_while_its_host_runs_is_published_again_whole_and_one_forgotten_while_it_was_off_comes_back_when_it_starts()
    {
        sessions.Set(FakeSessions.Copilot(A));
        var updates = new FakeUpdates();
        var wanted = new[]
        {
            Online, HaContract.MachineEntityId("sensor", "sessions", Slug), HaContract.MachineEntityId("update", "update", Slug),
            $"sensor.{NodeA}_status", $"sensor.{NodeA}_activity",
        };
        await using (var c = Connector(updates: updates))
        {
            await Run(c, 5);
            Assert.All(wanted, id => Assert.True(ha.Exists(id), id));

            // The row's X, pressed while the machine's liveness had lapsed.
            foreach (var topic in HaContract.ForgetTopics(Slug, [NodeA])) { await ha.PublishAsync(topic, "", true); }
            Assert.All(wanted, id => Assert.False(ha.Exists(id), id));
            await Run(c, 260);
            Assert.All(wanted, id => Assert.True(ha.Exists(id), id));
            Assert.Equal(1, Logged("publishing the machine again"));
        }

        // Forgotten again while the host is stopped: the host starting is the machine coming back.
        foreach (var topic in HaContract.ForgetTopics(Slug, [NodeA])) { await ha.PublishAsync(topic, "", true); }
        await using (var again = Connector(updates: updates))
        {
            await Run(again, 5);
            Assert.All(wanted, id => Assert.True(ha.Exists(id), id));
        }
    }
}
