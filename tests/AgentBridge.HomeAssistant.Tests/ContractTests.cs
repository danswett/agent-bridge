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
        // agent-ha-bridge's test-install-clients.ps1 holds these counts as tripwires: 28 machine
        // topics plus three per usage sensor, and 17 plus one per field slot for a session.
        Assert.Equal(Contract.Strings("machine.topics"), HaContract.MachineTopics(Slug));
        Assert.Equal(28 + 3 * HaContract.UsageSensorKeys.Count, HaContract.MachineTopics(Slug).Count);
        Assert.Equal(46, HaContract.MachineTopics(Slug).Count);
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
        // The beat: the state exactly, then its time, which only has to be a time.
        var beat = Contract.Publications("heartbeat");
        var ours = HaContract.Heartbeat(Slug, DateTimeOffset.Now).Select(Published.Of).ToList();
        Assert.Equal(beat.Select(p => (p.Topic, p.Retain)), ours.Select(p => (p.Topic, p.Retain)));
        Contract.Equal(beat[0], ours[0]);
        Assert.Equal("<now>", Assert.Single(JsonNode.Parse(beat[1].Payload)!.AsObject(), p => p.Key == "at").Value!.GetValue<string>());
        var at = Assert.Single(JsonNode.Parse(ours[1].Payload)!.AsObject());
        Assert.Equal("at", at.Key);
        Assert.True(DateTimeOffset.TryParse(at.Value!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _));

        var update = Contract.Publications("update");
        var (updateTopic, updateConfig) = HaContract.UpdateConfig(Slug, Machine);
        Contract.Equal(update,
        [
            new Published(updateTopic, HaContract.Serialize(updateConfig), true),
            new Published(HaContract.UpdateStateTopic(Slug), HaContract.Serialize(HaPayloads.UpdateState("0.1.0")), true),
            new Published(HaContract.UpdateAttributesTopic(Slug), HaContract.Serialize(HaPayloads.UpdateAttributes()), true),
            .. HaContract.UpdateButtonConfigs(Slug, Machine).Select(b => new Published(b.Topic, HaContract.Serialize(b.Config), true)),
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

    // subagents.md §7: the card is the main agent's. A delegate's words, the latest on the session,
    // never stand in for its reply or its reasoning, in the state, the card text or the trail.
    [Fact]
    public void The_cards_text_is_the_main_agents_own_while_a_delegate_wrote_last()
    {
        var name = Contract.Text("session.sessionName");
        var s = FakeSessions.Copilot(SessionId, status: SessionStatus.Working) with
        {
            Activity = new Activity
            {
                Response = "Delegate: the suite passed",
                Reasoning = "Delegate: reading the log",
                Source = new ActivitySource { Response = "agent-1", Reasoning = "agent-1" },
                Own = new OwnActivity { Response = "Main: cutting the release", Reasoning = "Main: which tag" },
            },
            Subagents = [new Subagent { Id = "agent-1", Kind = SubagentKind.Background, Name = "Run the suite", Status = SubagentStatus.Working }],
        };
        Assert.Equal("Main: cutting the release", HaPayloads.ActivitySummary(s));
        var detail = HaPayloads.ActivityAttributes(s, name, Machine, verbose: true, DateTimeOffset.Now);
        Assert.Equal("Main: cutting the release", detail["response"]!.GetValue<string>());
        Assert.Equal(["Thinking: Main: which tag", "Main: cutting the release"], detail["history"]!.AsArray().Select(h => h!.GetValue<string>()));
        // Only its reasoning is a delegate's: the reply shown is the main agent's, which is also the
        // top-level one, and the thought is the main agent's own.
        var mixed = s with { Activity = s.Activity with { Response = "Main: cutting the release", Source = new ActivitySource { Reasoning = "agent-1" }, Own = new OwnActivity { Reasoning = "Main: which tag" } } };
        detail = HaPayloads.ActivityAttributes(mixed, name, Machine, verbose: true, DateTimeOffset.Now, thinking: true);
        Assert.Equal(("Main: which tag", "reasoning"), (detail["response"]!.GetValue<string>(), detail["response_kind"]!.GetValue<string>()));
        // A main agent with no words of its own shows none, not the delegate's.
        var silent = s with { Activity = s.Activity with { Own = null } };
        Assert.Equal("Thinking", HaPayloads.ActivitySummary(silent));
        Assert.False(HaPayloads.ActivityAttributes(silent, name, Machine, verbose: true, DateTimeOffset.Now).ContainsKey("response"));
        // One whose reply a budget dropped, keeping only that it had one, says so: neither blank
        // nor the delegate's.
        var dropped = s with { Activity = s.Activity with { Own = new OwnActivity { ResponseTruncated = true } } };
        Assert.Equal(HaPayloads.OwnResponseDropped, HaPayloads.ActivitySummary(dropped));
        Assert.Equal(HaPayloads.OwnResponseDropped, HaPayloads.ActivityAttributes(dropped, name, Machine, verbose: false, DateTimeOffset.Now)["response"]!.GetValue<string>());
    }

    // Home Assistant keeps at most 16 KiB of a state's attributes. The subagents attribute lists
    // five of however many there are, each bounded, so it stays far inside that even with twenty
    // whose names and lines are the longest and most expensive text there is.
    [Fact]
    public void The_subagents_attribute_stays_well_inside_home_assistants_limit_with_twenty_delegates()
    {
        var emoji = string.Concat(Enumerable.Repeat("\U0001F680", 400));
        var templates = string.Concat(Enumerable.Repeat("{{", 2000));
        var s = FakeSessions.Copilot(SessionId, status: SessionStatus.Working) with
        {
            Subagents = [.. Enumerable.Range(0, 20).Select(i => new Subagent
            {
                Id = $"agent-{i}", Kind = SubagentKind.Background, Status = SubagentStatus.Working,
                Name = i % 2 == 0 ? emoji : templates, Line = i % 2 == 0 ? templates : emoji, Task = emoji,
            })],
            SubagentsMore = 7,
        };
        var detail = HaPayloads.ActivityAttributes(s, Contract.Text("session.sessionName"), Machine, verbose: true, DateTimeOffset.Now);
        var listed = detail["subagents"]!.AsArray();
        Assert.Equal(HaPayloads.SubagentsShown, listed.Count);
        Assert.Equal(15 + 7, detail["subagents_more"]!.GetValue<int>());
        Assert.All(listed, e => Assert.Equal(["name", "status", "line"], e!.AsObject().Select(p => p.Key)));
        var bytes = System.Text.Encoding.UTF8.GetByteCount(HaContract.Serialize(listed));
        Assert.True(bytes < 8 * 1024, $"{bytes} bytes");
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(HaContract.Serialize(detail)) < 16 * 1024);
    }

    // Found in review: read before the protocol budget, a reply's first line goes whole into the
    // trail beside up to 6000 characters of it in response, and JSON escaping doubles quotes. The
    // whole payload stays inside Home Assistant's limit, the trail giving way first and the reply
    // keeping its start; an ordinary card is untouched.
    [Fact]
    public void The_whole_activity_payload_stays_inside_home_assistants_limit_for_the_longest_replies()
    {
        int Bytes(JsonObject o) => System.Text.Encoding.UTF8.GetByteCount(HaContract.Serialize(o));
        JsonObject Detail(string reply, string? reasoning = null, AgentKind agent = AgentKind.Copilot) => HaPayloads.ActivityAttributes(
            FakeSessions.Copilot(SessionId, response: reply) with { Agent = agent, Activity = new Activity { Response = reply, Reasoning = reasoning } },
            Contract.Text("session.sessionName"), Machine, verbose: true, DateTimeOffset.Now);

        var line = "Start of the answer " + new string('a', 16 * 1024);
        var detail = Detail(line);
        Assert.True(Bytes(detail) <= HaPayloads.ActivityAttributesBudget, $"{Bytes(detail)} bytes");
        Assert.Equal(line[..(HaPayloads.ResponseMaxChars)] + HaPayloads.TruncatedNote, detail["response"]!.GetValue<string>());
        Assert.Equal(HaPayloads.CapState(line), detail["history"]!.AsArray()[^1]!.GetValue<string>());

        var quotes = new string('"', 6000);
        detail = Detail(quotes);
        Assert.True(Bytes(detail) <= HaPayloads.ActivityAttributesBudget, $"{Bytes(detail)} bytes");
        Assert.Equal(quotes, detail["response"]!.GetValue<string>());

        // Even when the shown text alone is too much for it (control characters escape to six
        // bytes each): its start, and the note.
        var escaped = new string('\u0001', 6000);
        detail = Detail(escaped);
        Assert.True(Bytes(detail) <= HaPayloads.ActivityAttributesBudget, $"{Bytes(detail)} bytes");
        var shown = detail["response"]!.GetValue<string>();
        Assert.EndsWith(HaPayloads.TruncatedNote, shown, StringComparison.Ordinal);
        Assert.InRange(shown.Length - HaPayloads.TruncatedNote.Length, 1000, 2600);

        Assert.Equal("Done.\nMore", Detail("Done.\nMore")["response"]!.GetValue<string>());
    }

    // Found in review: a 6000-character reply is in both response and history, and five delegates at
    // their limits on top of it came to over 17 KiB. They make way for the card's text, never the
    // other way round, and are still counted when none fit.
    [Fact]
    public void Delegates_make_way_for_the_cards_own_text_within_home_assistants_limit_and_are_still_counted()
    {
        var templates = string.Concat(Enumerable.Repeat("{{", 2000));
        var delegates = Enumerable.Range(0, 6).Select(i => new Subagent
        {
            Id = $"agent-{i}", Kind = SubagentKind.Background, Status = SubagentStatus.Working, Name = templates, Line = templates,
        }).ToList();
        int Bytes(JsonObject o) => System.Text.Encoding.UTF8.GetByteCount(HaContract.Serialize(o));
        JsonObject Detail(string reply, IReadOnlyList<Subagent>? list, int? more = null) => HaPayloads.ActivityAttributes(
            FakeSessions.Copilot(SessionId, response: reply) with { Subagents = list, SubagentsMore = more },
            Contract.Text("session.sessionName"), Machine, verbose: false, DateTimeOffset.Now);

        var reply = new string('r', 6000);
        var detail = Detail(reply, delegates);
        Assert.True(Bytes(detail) <= HaPayloads.ActivityAttributesBudget, $"{Bytes(detail)} bytes");
        Assert.Equal(reply, detail["response"]!.GetValue<string>());
        var listed = detail["subagents"]!.AsArray();
        Assert.NotEmpty(listed);
        Assert.Equal(6 - listed.Count, detail["subagents_more"]!.GetValue<int>());

        // A reply that fills the budget on its own: none listed, all counted.
        var full = Detail(string.Concat(Enumerable.Repeat("\U0001F680", 3000)), delegates, more: 2);
        Assert.Empty(full["subagents"]!.AsArray());
        Assert.Equal(8, full["subagents_more"]!.GetValue<int>());

        // The host's budget dropped every entry: still a session with delegates.
        var dropped = Detail("Done.", null, more: 3);
        Assert.Empty(dropped["subagents"]!.AsArray());
        Assert.Equal(3, dropped["subagents_more"]!.GetValue<int>());
        Assert.False(Detail("Done.", null).ContainsKey("subagents"));
    }

    // The readings the usage contract was recorded from.
    internal static readonly AgentUsage[] UsageReadings =
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
        // Further Copilot logins (usage.copilotAccounts) on the numbered sensors of their places.
        new AgentUsage
        {
            Agent = AgentKind.Copilot, Label = "GitHub Copilot", Account = "hubot", Plan = "individual", Source = UsageSource.Api, Slot = 2,
            MeasuredAt = DateTimeOffset.Parse("2026-10-29T19:00:03Z", System.Globalization.CultureInfo.InvariantCulture),
            Windows = [new UsageWindow { Key = "premium", Label = "Premium requests", Percent = 42.3, Used = 127, Limit = 300, Unit = "requests", ResetsAt = DateTimeOffset.Parse("2026-11-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture) }],
        },
        new AgentUsage
        {
            Agent = AgentKind.Copilot, Label = "GitHub Copilot", Account = "monalisa", Source = UsageSource.None, Slot = 4,
            Error = "No Copilot credential was available for monalisa (gh has no token for monalisa - run gh auth login).",
            MeasuredAt = DateTimeOffset.Parse("2026-10-29T19:00:04Z", System.Globalization.CultureInfo.InvariantCulture),
        },
    };

    [Fact]
    public void Usage_sensors_match_the_contract_for_a_reading_and_a_failure_and_for_further_copilot_logins()
    {
        var recorded = Contract.Publications("usage");
        var now = DateTimeOffset.Parse("2026-10-29T19:00:05Z", System.Globalization.CultureInfo.InvariantCulture);
        var readings = UsageReadings;

        var actual = new List<Published>();
        foreach (var r in readings)
        {
            var u = HaPayloads.Usage(r, Machine, Slug, now)!.Value;
            var (topic, config) = HaContract.UsageConfig(Slug, Machine, u.Key, u.Label, u.Key == u.Client ? null : r.Account);
            actual.Add(new Published(topic, HaContract.Serialize(config), true));
            actual.Add(new Published(HaContract.UsageStateTopic(Slug, u.Key), u.State, true));
            actual.Add(new Published(HaContract.UsageAttributesTopic(Slug, u.Key), HaContract.Serialize(u.Attributes), true));
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
