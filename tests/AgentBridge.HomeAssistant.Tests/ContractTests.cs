using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;
using Xunit;

// Every name, topic and payload the connector publishes, against what agent-ha-bridge's own
// functions published for the same inputs (Fixtures/ha-contract.json). A difference here is a
// card that stops drawing on the dashboard DSWETT-HOME generates.
public class ContractTests
{
    const string Machine = "DSWETT-DEV-VM1";
    static string Slug => Contract.Text("machine.slug");
    static string SessionId => Contract.Text("session.sessionId");
    static string Node => Contract.Text("session.node");

    [Fact]
    public void Machine_slugs_are_derived_as_agent_ha_bridge_derives_them()
    {
        foreach (var c in Contract.At("slugs").AsArray())
        {
            Assert.Equal(c!["slug"]!.GetValue<string>(), HaContract.MachineSlug(c["machineName"]!.GetValue<string>()));
        }
    }

    [Fact]
    public void Session_nodes_are_derived_as_agent_ha_bridge_derives_them_including_the_hashed_fallback()
    {
        foreach (var c in Contract.At("nodes").AsArray())
        {
            Assert.Equal(c!["node"]!.GetValue<string>(), HaContract.NodeId(c["sessionId"]!.GetValue<string>()));
        }
    }

    [Fact]
    public void Machine_and_session_topics_and_entity_ids_match_the_contract()
    {
        Assert.Equal(Contract.Text("machine.node"), HaContract.MachineNode(Slug));
        Assert.Equal(Contract.Text("machine.topicRoot"), HaContract.MachineTopicRoot(Slug));
        Contract.AssertJson(Contract.At("machine.device"), HaContract.MachineDevice(Slug, Machine));
        Assert.Equal(Contract.Text("machine.entityIds.online"), HaContract.MachineEntityId("binary_sensor", "online", Slug));
        Assert.Equal(Contract.Text("machine.entityIds.sessions"), HaContract.MachineEntityId("sensor", "sessions", Slug));
        Assert.Equal(Contract.Text("machine.entityIds.update"), HaContract.MachineEntityId("update", "update", Slug));
        Assert.Equal(Contract.Text("machine.entityIds.detailedActivity"), HaContract.MachineEntityId("input_boolean", "detailed_activity", Slug));
        Assert.Equal(Contract.Text("machine.entityIds.usageCopilot"), HaContract.MachineEntityId("sensor", "usage_copilot", Slug));

        var t = HaContract.Topics(Node);
        Assert.Equal(Contract.Text("session.topics.Availability"), t.Availability);
        Assert.Equal(Contract.Text("session.topics.StatusState"), t.StatusState);
        Assert.Equal(Contract.Text("session.topics.StatusAttributes"), t.StatusAttributes);
        Assert.Equal(Contract.Text("session.topics.ActivityState"), t.ActivityState);
        Assert.Equal(Contract.Text("session.topics.ActivityAttributes"), t.ActivityAttributes);
        Assert.Equal(Contract.Text("session.entityIds.Status"), HaContract.SessionSensorIds(Node)[$"{Node}_status"]);
        Assert.Equal(Contract.Text("session.entityIds.Activity"), HaContract.SessionSensorIds(Node)[$"{Node}_activity"]);
    }

    [Fact]
    public void Cleanup_topic_lists_match_the_contract_and_its_tripwire_counts()
    {
        // agent-ha-bridge's test-install-clients.ps1 holds these counts as tripwires: 25 machine
        // topics, and 17 plus one per field slot for a session.
        Assert.Equal(Contract.Strings("machine.topics"), HaContract.MachineTopics(Slug));
        Assert.Equal(25, HaContract.MachineTopics(Slug).Count);
        Assert.Equal(Contract.Strings("session.discoveryTopics"), HaContract.SessionDiscoveryTopics(Node));
        Assert.Equal(Contract.Strings("session.stateTopics"), HaContract.SessionStateTopics(Node));
        Assert.Equal(17 + HaContract.MaxFields, HaContract.SessionRemoval(Node).Count);
        Assert.Equal(Contract.Strings("machine.usageTopics"), HaContract.UsageTopics(Slug));
        Assert.Equal(Contract.Strings("machine.forgetTopics"), HaContract.ForgetTopics(Slug, [Node]));
        // The constants are the fixture's: compared that way round only to satisfy xUnit's analyzer.
        Assert.Equal(HaContract.MaxFields, Contract.At("constants.maxFields").GetValue<int>());
        Assert.Equal(HaContract.DiscoveryPrefix, Contract.Text("constants.discoveryPrefix"));
        Assert.Equal(HaContract.TopicRoot, Contract.Text("constants.topicRoot"));
    }

    [Fact]
    public void Retiring_a_session_publishes_exactly_what_agent_ha_bridge_publishes_offline_first()
    {
        Contract.Equal(Contract.Publications("sessionRemove"), [.. HaContract.SessionRemoval(Node).Select(Published.Of)]);
    }

    [Fact]
    public void A_sessions_sensor_configs_match_its_discovery_and_a_rename_republishes_only_them()
    {
        var recorded = Contract.Publications("sessionPublish");
        var t = HaContract.Topics(Node);
        var name = Contract.Text("session.sessionName");
        var (statusTopic, status) = HaContract.StatusConfig(t, name, Machine);
        var (activityTopic, activity) = HaContract.ActivityConfig(t, name, Machine);
        Contract.Equal(recorded.Single(p => p.Topic == t.Availability), new Published(t.Availability, "online", true));
        Contract.Equal(recorded.Single(p => p.Topic == statusTopic), new Published(statusTopic, HaContract.Serialize(status), true));
        Contract.Equal(recorded.Single(p => p.Topic == activityTopic), new Published(activityTopic, HaContract.Serialize(activity), true));

        var (renamedStatusTopic, renamedStatus) = HaContract.StatusConfig(t, "Copilot: Renamed", Machine);
        var (renamedActivityTopic, renamedActivity) = HaContract.ActivityConfig(t, "Copilot: Renamed", Machine);
        Contract.Equal(Contract.Publications("sessionRename"),
            [new Published(renamedStatusTopic, HaContract.Serialize(renamedStatus), true), new Published(renamedActivityTopic, HaContract.Serialize(renamedActivity), true)]);
    }

    [Fact]
    public void Machine_entity_configs_match_the_contract()
    {
        var (onlineTopic, online) = HaContract.OnlineConfig(Slug, Machine);
        Contract.Equal(Contract.Publications("onlineConfig"), [new Published(onlineTopic, HaContract.Serialize(online), true)]);
        Contract.Equal(Contract.Publications("heartbeat"), [new Published(HaContract.OnlineStateTopic(Slug), "online", false)]);

        var update = Contract.Publications("update");
        var (updateTopic, updateConfig) = HaContract.UpdateConfig(Slug, Machine);
        Contract.Equal(update.Take(3).ToList(),
        [
            new Published(updateTopic, HaContract.Serialize(updateConfig), true),
            new Published(HaContract.UpdateStateTopic(Slug), HaContract.Serialize(HaPayloads.UpdateState("0.1.0")), true),
            new Published(HaContract.UpdateAttributesTopic(Slug), HaContract.Serialize(HaPayloads.UpdateAttributes()), true),
        ]);
    }

    [Fact]
    public void The_machine_session_list_matches_the_contract()
    {
        var descriptors = new[]
        {
            new HaPayloads.Descriptor("Copilot: Fixture session", Machine, Node, "copilot"),
            new HaPayloads.Descriptor("Claude: agent-bridge", Machine, HaContract.NodeId("c1a2b3c4-d5e6-4f70-8a91-b2c3d4e5f607"), "claude"),
        };
        var capabilities = HaPayloads.Capabilities(detailed: false, dev: true);
        Contract.AssertJson(Contract.At("capabilities"), capabilities);
        var (topic, config) = HaContract.SessionsConfig(Slug, Machine);
        Contract.Equal(Contract.Publications("globalStatus"),
        [
            new Published(topic, HaContract.Serialize(config), true),
            new Published(HaContract.GlobalStateTopic(Slug), "2", true),
            new Published(HaContract.GlobalAttributesTopic(Slug), HaContract.Serialize(HaPayloads.GlobalAttributes(descriptors, Machine, Slug, capabilities, DateTimeOffset.Now)), true),
        ]);
    }

    [Fact]
    public void Session_names_follow_each_agents_rule_and_neutralise_template_markup()
    {
        foreach (var c in Contract.At("display.copilot").AsArray())
        {
            var s = FakeSessions.Copilot(c!["sessionId"]!.GetValue<string>(), title: c["title"]?.GetValue<string>()!);
            Assert.Equal(c["name"]!.GetValue<string>(), HaPayloads.SessionName(s));
        }
        foreach (var (agent, key) in new[] { (AgentKind.Claude, "claude"), (AgentKind.Codex, "codex") })
        {
            foreach (var c in Contract.At($"display.{key}").AsArray())
            {
                var s = FakeSessions.Copilot(c!["sessionId"]!.GetValue<string>(), title: "a title the folder rule ignores") with
                {
                    Agent = agent,
                    Cwd = c["workingDirectory"]!.GetValue<string>(),
                };
                Assert.Equal(c["name"]!.GetValue<string>(), HaPayloads.SessionName(s));
            }
        }
    }

    [Fact]
    public void Status_attributes_carry_settings_and_counts_only_when_there_are_some()
    {
        var name = Contract.Text("session.sessionName");
        var tuned = FakeSessions.Copilot(SessionId) with
        {
            Model = "gpt-6.1-sol",
            Activity = new Activity { Compactions = 2, Background = Background.Of(2, 0) },
        };
        Assert.Equal("agents", HaPayloads.Status(tuned));
        Contract.AssertJson(Contract.At("statusAttributes.tuned"), HaPayloads.StatusAttributes(tuned, name, Machine, DateTimeOffset.Now));
        Contract.Equal(Contract.Publications("statusTuned"),
        [
            new Published(HaContract.Topics(Node).StatusState, HaPayloads.Status(tuned), true),
            new Published(HaContract.Topics(Node).StatusAttributes, HaContract.Serialize(HaPayloads.StatusAttributes(tuned, name, Machine, DateTimeOffset.Now)), true),
        ]);

        var bare = FakeSessions.Copilot(SessionId) with { Activity = new Activity { Compactions = 0 } };
        Contract.AssertJson(Contract.At("statusAttributes.bare"), HaPayloads.StatusAttributes(bare, name, Machine, DateTimeOffset.Now));

        var shell = FakeSessions.Copilot(SessionId) with { Model = "opus", Activity = new Activity { Compactions = 1, Background = Background.Of(0, 1) } };
        Assert.Equal("shell", HaPayloads.Status(shell));
        Contract.AssertJson(Contract.At("statusAttributes.shell"), HaPayloads.StatusAttributes(shell, name, Machine, DateTimeOffset.Now));
    }

    [Fact]
    public void The_cards_text_is_laid_out_as_agent_ha_bridge_lays_it_out()
    {
        foreach (var (key, card) in Contract.At("cards").AsObject())
        {
            var kind = card!["kind"]!.GetValue<string>();
            var expected = card["detail"]!.AsObject();
            var actual = new JsonObject();
            foreach (var k in new[] { "session", "machine", "verbose", "updated", "driver", "history" }) { actual[k] = expected[k]!.DeepClone(); }
            HaPayloads.AddCardText(actual, inline: kind is "copilot" or "claude" or "codex", verbose: card["verbose"]!.GetValue<bool>(),
                lastResponse: card["response"]!.GetValue<string>(), lastMessage: card["message"]!.GetValue<string>(),
                lastIsThinking: card["thinking"]!.GetValue<bool>(), lastReasoning: card["reasoning"]!.GetValue<string>());
            Contract.AssertJson(expected, actual, key);
        }
    }

    [Fact]
    public void The_activity_state_is_cut_to_home_assistants_limit_as_agent_ha_bridge_cuts_it()
    {
        var recorded = Contract.Publications("activityLong").Single();
        Assert.Equal(recorded.Payload, HaPayloads.CapState(new string('S', 300)));
        Assert.Equal("Running: view", Contract.Publications("activityShort")[0].Payload);
        var running = FakeSessions.Copilot(SessionId, status: SessionStatus.Working) with
        {
            Activity = new Activity { CurrentTool = new ToolCall { Id = "t", Name = "view", Status = ToolStatus.Running, StartedAt = FakeSessions.T0 } },
        };
        Assert.Equal("Running: view", HaPayloads.ActivitySummary(running));
    }

    [Fact]
    public void Thought_lines_in_the_trail_read_as_agent_ha_bridge_writes_them()
    {
        foreach (var c in Contract.At("thoughtLines").AsArray())
        {
            Assert.Equal(c!["line"]!.GetValue<string>(), HaPayloads.ThoughtLine(c["text"]!.GetValue<string>()));
        }
    }

    [Fact]
    public void Usage_sensors_match_the_contract_for_a_reading_and_a_failure()
    {
        var recorded = Contract.Publications("usage");
        var now = DateTimeOffset.Parse("2026-10-29T19:00:05Z", System.Globalization.CultureInfo.InvariantCulture);
        var readings = new[]
        {
            new AgentUsage
            {
                Agent = AgentKind.Copilot, Label = "GitHub Copilot", Account = "octocat", Plan = "business", Source = UsageSource.Api,
                MeasuredAt = DateTimeOffset.Parse("2026-10-29T19:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                Windows = [new UsageWindow { Key = "premium", Label = "Premium requests", Percent = 42.3, Used = 127, Limit = 300, Unit = "requests", ResetsAt = DateTimeOffset.Parse("2026-11-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture) }],
            },
            new AgentUsage
            {
                Agent = AgentKind.Claude, Label = "Claude Code", AccountKey = "a1b2c3d4e5f60718", Plan = "max", Source = UsageSource.Api,
                MeasuredAt = DateTimeOffset.Parse("2026-10-29T19:00:01Z", System.Globalization.CultureInfo.InvariantCulture),
                Windows =
                [
                    new UsageWindow { Key = "five_hour", Label = "5h limit", Percent = 12, ResetsAt = DateTimeOffset.Parse("2026-10-29T23:00:00Z", System.Globalization.CultureInfo.InvariantCulture) },
                    new UsageWindow { Key = "seven_day", Label = "Weekly", Percent = 61, ResetsAt = DateTimeOffset.Parse("2020-01-08T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), StartsAt = DateTimeOffset.Parse("2020-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture) },
                ],
            },
            new AgentUsage
            {
                Agent = AgentKind.Codex, Label = "Codex", Source = UsageSource.None, Error = "The Codex usage could not be read: no rollout",
                MeasuredAt = DateTimeOffset.Parse("2026-10-29T19:00:02Z", System.Globalization.CultureInfo.InvariantCulture),
            },
        };
        var actual = new List<Published>();
        foreach (var r in readings)
        {
            var u = HaPayloads.Usage(r, Machine, Slug, now)!.Value;
            var (topic, config) = HaContract.UsageConfig(Slug, Machine, u.Client, u.Label);
            actual.Add(new Published(topic, HaContract.Serialize(config), true));
            actual.Add(new Published(HaContract.UsageStateTopic(Slug, u.Client), u.State, true));
            actual.Add(new Published(HaContract.UsageAttributesTopic(Slug, u.Client), HaContract.Serialize(u.Attributes), true));
        }
        Contract.Equal(recorded, actual);
    }

    [Fact]
    public void The_service_call_guard_allows_and_refuses_exactly_what_agent_ha_bridges_does()
    {
        foreach (var c in Contract.At("serviceTargetGuard").AsArray())
        {
            var data = (JsonObject)c!["data"]!.DeepClone();
            var allowed = c["allowed"]!.GetValue<bool>();
            Assert.True(allowed == (ServiceTarget.Check(data) is null), c["name"]!.GetValue<string>());
        }
    }
}
