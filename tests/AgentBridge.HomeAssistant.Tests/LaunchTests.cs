using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// The launch card, against an in-process Home Assistant and a stand-in for the host's launcher
// offering what the exporter's recording offered: the controls, the labels and the notes, as the
// daemon's own functions produced them (Fixtures/ha-launch.json), and what reaches the host.
public sealed class LaunchTests : IDisposable
{
    const string Machine = "DSWETT-DEV-VM1";
    const string AgentUser = "agent-user";
    static readonly string Slug = HaContract.MachineSlug(Machine);

    static readonly Lazy<JsonObject> Loaded = new(() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ha-launch.json")))!);

    readonly string folder = Path.Combine(Path.GetTempPath(), "abhl-" + Guid.NewGuid().ToString("N"));
    readonly FakeHomeAssistant ha = new();
    readonly FakeSessions sessions = new();
    readonly FakeTimeProvider time = new(FakeSessions.T0);
    readonly List<string> log = [];
    readonly List<LaunchRequest> launched = [];
    Func<LaunchRequest, Task<LaunchResult>>? answer;
    bool available = true;
    // agent-ha-bridge running for this user, as G5 sees it: the connector pauses while it is.
    string? daemon;
    int launchedCount;

    // What the recording's machine offered: four agents, Copilot's (and Agency's) tuning, Agency's
    // two profiles, two workspaces and one resumable Copilot session.
    LaunchOptions offered = new()
    {
        Agents =
        [
            new LaunchAgent { Id = "agency", Label = "Agency", Kind = AgentKind.Copilot, Models = ["gpt-5.5", "claude-sonnet-5"], Efforts = ["low", "medium", "high"], Profiles = ["default", "work"], Defaults = new LaunchTuning { Profile = "work" } },
            new LaunchAgent { Id = "copilot", Label = "Copilot", Kind = AgentKind.Copilot, Models = ["gpt-5.5", "claude-sonnet-5"], Efforts = ["low", "medium", "high"], Defaults = new LaunchTuning { Model = "gpt-5.5" } },
            new LaunchAgent { Id = "claude", Label = "Claude", Kind = AgentKind.Claude },
            new LaunchAgent { Id = "codex", Label = "Codex", Kind = AgentKind.Codex, NeedsPrompt = true },
        ],
        Workspaces = [new LaunchWorkspace { Label = "agent-bridge", Path = @"C:\repos\agent-bridge" }, new LaunchWorkspace { Label = "scratch", Path = @"C:\scratch" }],
        Resumable = [new ResumableSession { Id = "6f000000-0000-4000-8000-000000000001", Agent = AgentKind.Copilot, Launcher = "copilot", Title = "Fix the relay - agent-bridge", Cwd = @"C:\repos\agent-bridge", UpdatedAt = FakeSessions.T0 }],
        Defaults = new LaunchDefaults { Agent = "copilot", Workspace = "agent-bridge", Permissions = LaunchProtocol.Ask },
    };

    public void Dispose()
    {
        if (Directory.Exists(folder)) { Directory.Delete(folder, true); }
    }

    HaConnector Connector(bool launches = true) => new(new HaConnectorOptions
    {
        MachineName = Machine,
        Version = "0.1.0",
        StateFile = Path.Combine(folder, "ha-connector.json"),
        FieldPinDelay = TimeSpan.Zero,
        SelectDelay = TimeSpan.Zero,
        AgentUserIds = [AgentUser],
    }, ha, sessions, () => daemon, log: m => { lock (log) { log.Add(m); } }, time: time,
        answer: (_, _) => Task.FromResult(new AnswerResult(AnswerOutcome.Delivered)),
        launch: launches ? new HaLauncher(() => available, _ => Task.FromResult(offered), (r, _) =>
        {
            lock (launched) { launched.Add(r); }
            Interlocked.Increment(ref launchedCount);
            return answer?.Invoke(r) ?? Task.FromResult(new LaunchResult(LaunchOutcome.Started)
            {
                Session = "6f000000-0000-4000-8000-0000000000aa",
                Launched = new LaunchedWith { Agent = r.Agent ?? "copilot", Workspace = r.Workspace ?? "agent-bridge", Resume = r.Resume, Profile = r.Profile, Model = r.Model, Permissions = r.Permissions ?? LaunchProtocol.Ask },
            });
        }) : null);

    async Task Pass(HaConnector c, double seconds = 1)
    {
        time.Advance(TimeSpan.FromSeconds(seconds));
        await c.TickAsync();
    }

    // Published, pinned once the pin delay has passed, and its selectors driven to their
    // defaults on a later pass, well before the card would next be refreshed.
    async Task<HaConnector> Ready()
    {
        var c = Connector();
        await Pass(c);
        await Pass(c, 2);
        await Pass(c, 6);
        return c;
    }

    static string Entity(string domain, string key) => HaContract.MachineEntityId(domain, key, Slug);
    string State(string domain, string key) => ha.GetStateAsync(Entity(domain, key)).GetAwaiter().GetResult()!.State;
    string Note => ha.Retained.GetValueOrDefault(HaContract.LaunchResultTopic(Slug), "");
    JsonObject Outcome(int i = 0) => JsonNode.Parse(ha.Retained[HaContract.LaunchOutcomeTopic(Slug)])!["recent"]![i]!.AsObject();

    void Payload(JsonObject attributes)
    {
        attributes["at"] ??= ha.Now.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        ha.PublishAsync(HaContract.PromptPayloadTopic(Slug), HaContract.Serialize(attributes), true).GetAwaiter().GetResult();
    }

    async Task Launch(HaConnector c, string? user = "person")
    {
        c.Observe(ha.Press(Entity("button", "new_session"), user));
        await Pass(c);
        await c.DeliveredAsync();
        await Pass(c, 0.01);
    }

    static IReadOnlyList<Published> Recorded(string name, bool withTransfer = false) =>
        [.. Loaded.Value[name]!.AsArray().Select(p => Published.FromFixture(p!.AsObject())).Where(p => withTransfer || !p.Topic.Contains("/transfer_request/", StringComparison.Ordinal))];

    [Fact]
    public void The_cards_controls_are_the_daemons_for_the_same_choices_and_for_none()
    {
        var card = new HaContract.LaunchCard
        {
            Workspaces = ["agent-bridge", "scratch"],
            Profiles = ["default", "work"],
            Resumable = ["Fix the relay - agent-bridge", "Claude: Write docs - scratch"],
            Agents = ["Agency", "Copilot", "Claude", "Codex"],
            Tuning = new Dictionary<string, IReadOnlyList<string>>
            {
                ["model"] = ["Agent default", "gpt-5.5", "claude-sonnet-5"],
                ["effort"] = ["Agent default", "low", "medium", "high"],
                ["context"] = ["Agent default"],
            },
        };
        Contract.Equal(Recorded("controls"), [.. HaContract.LaunchConfigs(Slug, Machine, card).Select(c => new Published(c.Topic, HaContract.Serialize(c.Config), true))]);
        Contract.Equal(Recorded("emptyControls"), [.. HaContract.LaunchConfigs(Slug, Machine, new HaContract.LaunchCard()).Select(c => new Published(c.Topic, HaContract.Serialize(c.Config), true))]);
        Contract.Equal(Recorded("result"), [new Published(HaContract.LaunchResultTopic(Slug), HaContract.LaunchNote("Launch failed: " + new string('x', 300)), true)]);
        Contract.Equal(Recorded("clearPrompt"), [new Published(HaContract.PromptPayloadTopic(Slug), "{}", true)]);
        var constants = Loaded.Value["constants"]!;
        Assert.Equal(HaContract.NewSessionOption, constants["newSessionOption"]!.GetValue<string>());
        Assert.Equal(HaContract.TuningDefault, constants["tuningDefault"]!.GetValue<string>());
        Assert.Equal(constants["permissions"]!.AsArray().Select(p => p!.GetValue<string>()), HaContract.PermissionOptions);
        Assert.Equal(constants["noteExpirySeconds"]!.GetValue<int>(), (int)HaContract.LaunchNoteExpiry.TotalSeconds);
        Assert.Equal(Loaded.Value["agents"]!.AsObject().Select(a => a.Value!["label"]!.GetValue<string>()), offered.Agents.Select(a => a.Label));
    }

    [Fact]
    public async Task The_card_is_published_from_the_hosts_launcher_on_the_dashboards_ids_with_its_selectors_on_their_defaults()
    {
        await using var c = await Ready();
        foreach (var (domain, key) in HaContract.LaunchEntities) { Assert.True(ha.Exists(Entity(domain, key)), key); }
        Assert.False(ha.Exists(Entity("sensor", "transfer_request")));
        Assert.Equal("Copilot", State("select", "new_agent"));
        Assert.Equal("agent-bridge", State("select", "new_workspace"));
        Assert.Equal("work", State("select", "new_profile"));
        Assert.Equal("gpt-5.5", State("select", "new_model"));
        Assert.Equal(HaContract.TuningDefault, State("select", "new_effort"));
        Assert.Equal(HaContract.NewSessionOption, State("select", "new_resume"));
        Assert.Equal(HaContract.PermissionAsk, State("select", "new_permissions"));

        // The capability flags of a machine serving the card, as the daemon reports them.
        var global = JsonNode.Parse(ha.Retained[HaContract.GlobalAttributesTopic(Slug)])!;
        var expected = Loaded.Value["capabilities"]!.DeepClone().AsObject();
        // The connector made the machine's Detailed activity switch (stage 7), so the row has one.
        expected["detailed"] = true;
        expected["dev"] = false;
        Contract.AssertJson(expected, global["capabilities"]);
    }

    [Fact]
    public async Task Choosing_another_agent_offers_its_own_tuning()
    {
        await using var c = await Ready();
        c.Observe(ha.Choose(Entity("select", "new_agent"), "Claude"));
        await Pass(c);
        var model = await ha.GetStateAsync(Entity("select", "new_model"));
        Assert.Equal([HaContract.TuningDefault], model!.Attributes.GetProperty("options").EnumerateArray().Select(o => o.GetString()));
        Assert.Equal(HaContract.TuningDefault, State("select", "new_model"));
    }

    [Fact]
    public async Task A_persons_press_launches_what_the_card_shows_and_the_note_goes_once_it_has_started()
    {
        await using var c = await Ready();
        ha.Choose(Entity("select", "new_agent"), "Agency");
        ha.Choose(Entity("select", "new_workspace"), "scratch");
        ha.Choose(Entity("select", "new_effort"), "high");
        ha.Choose(Entity("select", "new_permissions"), HaContract.PermissionAllowAll);
        ha.Type(Entity("text", "new_prompt"), "short");
        Payload(new JsonObject { ["text"] = "  The whole long prompt  " });

        await Launch(c);

        var r = Assert.Single(launched);
        Assert.Equal("agency", r.Agent);
        Assert.Equal("scratch", r.Workspace);
        Assert.Equal("work", r.Profile);
        Assert.Equal("gpt-5.5", r.Model);
        Assert.Equal("high", r.Effort);
        // Agent default is the agent's own: no flag at all.
        Assert.Equal("", r.Context);
        Assert.Equal(LaunchProtocol.AllowAll, r.Permissions);
        Assert.Equal("The whole long prompt", r.Prompt);
        Assert.True(Guid.TryParse(r.Request, out _));
        Assert.Contains(ha.Log, p => p.Topic == HaContract.LaunchResultTopic(Slug) && p.Payload == "Starting Agency in scratch (work)...");
        Assert.Equal("", Note);
        // Both prompt boxes are emptied, so the next press does not start it again.
        Assert.Equal(HaDecision.ReplyBlank, State("text", "new_prompt"));
        Assert.Equal("{}", ha.Retained[HaContract.PromptPayloadTopic(Slug)]);
    }

    [Fact]
    public async Task A_resume_chosen_on_the_card_reopens_that_session_in_its_own_agent()
    {
        await using var c = await Ready();
        ha.Choose(Entity("select", "new_agent"), "Claude");
        ha.Choose(Entity("select", "new_resume"), "Fix the relay - agent-bridge");
        await Launch(c);
        var r = Assert.Single(launched);
        Assert.Equal("copilot", r.Agent);
        Assert.Equal("6f000000-0000-4000-8000-000000000001", r.Resume);
        Assert.Null(r.Workspace);
        Assert.Contains(ha.Log, p => p.Topic == HaContract.LaunchResultTopic(Slug) && p.Payload == "Resuming Fix the relay - agent-bridge...");
    }

    [Theory]
    [InlineData(LaunchOutcome.Refused, "refused")]
    [InlineData(LaunchOutcome.Busy, "busy")]
    [InlineData(LaunchOutcome.Failed, "failed")]
    [InlineData(LaunchOutcome.Unconfirmed, "unconfirmed")]
    public async Task What_the_host_says_of_a_launch_that_did_not_start_is_the_cards_note_and_the_requests_outcome(LaunchOutcome outcome, string word)
    {
        answer = _ => Task.FromResult(new LaunchResult(outcome, $"the host says {word}"));
        await using var c = await Ready();
        ha.Type(Entity("text", "new_prompt"), "keep me");
        Payload(new JsonObject { ["text"] = "go", ["request"] = "req-0001-abcd", ["settings"] = new JsonObject() });
        await Launch(c, AgentUser);

        Assert.Equal($"the host says {word}", Note);
        var o = Outcome();
        Assert.Equal("req-0001-abcd", o["request"]!.GetValue<string>());
        Assert.Equal(word, o["outcome"]!.GetValue<string>());
        Assert.Equal($"the host says {word}", o["note"]!.GetValue<string>());
        // The card's box is a person's: never cleared for an agent's launch.
        Assert.Equal("keep me", State("text", "new_prompt"));
    }

    [Fact]
    public async Task An_agents_request_launches_with_its_own_settings_and_its_outcome_says_what_started()
    {
        await using var c = await Ready();
        ha.Choose(Entity("select", "new_permissions"), HaContract.PermissionAllowAll);
        Payload(new JsonObject { ["text"] = "Fix the build", ["request"] = "req-0001-abcd", ["settings"] = new JsonObject { ["agent"] = "Claude", ["workspace"] = "scratch" } });
        await Launch(c, AgentUser);

        var r = Assert.Single(launched);
        Assert.Equal("claude", r.Agent);
        Assert.Equal("scratch", r.Workspace);
        // A setting left out is the machine's default, never what the card shows.
        Assert.Null(r.Permissions);
        Assert.Equal("Fix the build", r.Prompt);
        var o = Outcome();
        Assert.Equal("started", o["outcome"]!.GetValue<string>());
        Assert.Equal("6f000000-0000-4000-8000-0000000000aa", o["session"]!.GetValue<string>());
        Assert.Equal("Claude", o["launched"]!["agent"]!.GetValue<string>());
        Assert.Equal("scratch", o["launched"]!["workspace"]!.GetValue<string>());
        Assert.Equal(HaContract.PermissionAsk, o["launched"]!["permissions"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(o["pressed"]!.GetValue<string>()));
        // Received first, as the daemon answers before it clears the slot.
        Assert.Contains(ha.Log, p => p.Topic == HaContract.LaunchOutcomeTopic(Slug) && p.Payload.Contains("Received; checking its settings before launching.", StringComparison.Ordinal));
        Assert.Equal("{}", ha.Retained[HaContract.PromptPayloadTopic(Slug)]);

        // The session it started is the agent's from the moment it appears: the purple edge.
        sessions.Set(FakeSessions.Copilot("6f000000-0000-4000-8000-0000000000aa"));
        await Pass(c);
        var activity = JsonNode.Parse(ha.Retained[HaContract.Topics(HaContract.NodeId("6f000000-0000-4000-8000-0000000000aa")).ActivityAttributes])!;
        Assert.Equal("agent", activity["driver"]!.GetValue<string>());
    }

    public static TheoryData<string> Refusals => [.. Loaded.Value["refusals"]!.AsObject().Select(r => r.Key)];

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_request_naming_what_this_machine_does_not_offer_is_refused_in_the_daemons_words(string name)
    {
        var recorded = Loaded.Value["refusals"]![name]!.AsObject();
        await using var c = await Ready();
        Payload(new JsonObject { ["text"] = "go", ["request"] = "req-0100-abcd", ["settings"] = recorded["settings"]!.DeepClone() });
        await Launch(c, AgentUser);
        if (recorded["refused"]!.GetValue<bool>())
        {
            Assert.Empty(launched);
            Assert.Equal(recorded["note"]!.GetValue<string>(), Note);
            Assert.Equal("refused", Outcome()["outcome"]!.GetValue<string>());
            Assert.Equal(recorded["note"]!.GetValue<string>(), Outcome()["note"]!.GetValue<string>());
        }
        else
        {
            var r = Assert.Single(launched);
            Assert.Equal(("copilot", "scratch", "gpt-5.5", "", LaunchProtocol.AllowAll), (r.Agent, r.Workspace, r.Model, r.Effort, r.Permissions));
        }
    }

    [Theory]
    [InlineData("unknownSetting")]
    [InlineData("notText")]
    [InlineData("notObject")]
    public async Task A_request_whose_settings_cannot_be_read_is_refused_with_the_daemons_reason(string name)
    {
        var settings = name switch
        {
            "unknownSetting" => (JsonNode)new JsonObject { ["colour"] = "blue" },
            "notText" => new JsonObject { ["model"] = 5 },
            _ => JsonValue.Create("agent=Claude"),
        };
        await using var c = await Ready();
        Payload(new JsonObject { ["request"] = "req-0005-abcd", ["settings"] = settings });
        await Launch(c, AgentUser);
        var problem = Loaded.Value["requests"]![name]!["request"]!["problem"]!.GetValue<string>();
        Assert.Empty(launched);
        Assert.Equal(problem, Note);
        Assert.Equal(problem, Outcome()["note"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_request_with_an_id_that_cannot_be_echoed_is_refused_and_launches_nothing()
    {
        await using var c = await Ready();
        Payload(new JsonObject { ["request"] = "bad id!" });
        await Launch(c, AgentUser);
        Assert.Empty(launched);
        Assert.Equal("Launch refused: the request carried an id that is not valid, so nothing was started.", Note);
        Assert.Equal("{}", ha.Retained[HaContract.PromptPayloadTopic(Slug)]);
    }

    [Fact]
    public async Task A_request_published_after_the_press_waits_for_its_own_and_one_served_already_is_not_served_again()
    {
        await using var c = await Ready();
        // Published a second after the press it would otherwise be taken by.
        var press = ha.Press(Entity("button", "new_session"), AgentUser);
        Payload(new JsonObject { ["request"] = "req-0003-abcd" });
        c.Observe(press);
        await Pass(c);
        await c.DeliveredAsync();
        Assert.Empty(launched);

        await Launch(c, AgentUser);
        Assert.Single(launched);
        // The same request again, as a payload that could not be cleared would leave it; pressed
        // after the ten seconds in which an agent's press without a request of its own is a race.
        ha.Now = ha.Now.AddSeconds(11);
        Payload(new JsonObject { ["text"] = "again", ["request"] = "req-0003-abcd", ["at"] = ha.Now.ToString("o", System.Globalization.CultureInfo.InvariantCulture) + "x" });
        await Launch(c, AgentUser);
        Assert.Equal(2, launched.Count);
        // From the card: neither the request's words nor a second answer to it.
        Assert.Single(JsonNode.Parse(ha.Retained[HaContract.LaunchOutcomeTopic(Slug)])!["recent"]!.AsArray());
        Assert.Null(launched[1].Prompt);
        Assert.Equal("copilot", launched[1].Agent);
    }

    [Fact]
    public async Task An_agents_second_press_moments_after_its_request_was_taken_launches_nothing_but_a_later_one_does()
    {
        await using var c = await Ready();
        Payload(new JsonObject { ["request"] = "req-0006-abcd" });
        await Launch(c, AgentUser);
        Assert.Single(launched);
        // The payload is cleared, so this press carries no request of its own: the race the
        // daemon guards against, of two presses for the one request.
        await Launch(c, AgentUser);
        Assert.Single(launched);
        ha.Now = ha.Now.AddSeconds(11);
        await Launch(c, AgentUser);
        Assert.Equal(2, launched.Count);
    }

    [Fact]
    public async Task A_request_that_reached_the_machine_more_than_five_minutes_after_its_press_is_refused()
    {
        await using var c = await Ready();
        // Home Assistant's clock set six minutes behind the connector's, so the press is old by
        // the time it is read here, while the request was published just before it.
        ha.Now = time.GetUtcNow().AddMinutes(-6);
        Payload(new JsonObject { ["request"] = "req-0007-abcd" });
        c.Observe(ha.Press(Entity("button", "new_session"), AgentUser));
        await Pass(c);
        await c.DeliveredAsync();
        Assert.Empty(launched);
        const string late = "Launch refused: the request waited more than 5 minutes for this machine, so it was not started late.";
        Assert.Equal(late, Note);
        Assert.Equal(("refused", late), (Outcome()["outcome"]!.GetValue<string>(), Outcome()["note"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_press_whose_launch_request_cannot_be_read_waits_rather_than_launching_from_the_card()
    {
        await using var c = await Ready();
        ha.Choose(Entity("select", "new_permissions"), HaContract.PermissionAllowAll);
        Payload(new JsonObject { ["request"] = "req-0008-abcd", ["settings"] = new JsonObject { ["permissions"] = HaContract.PermissionAsk } });
        var payload = Entity("sensor", "new_prompt_payload");
        ha.GetFailsFor = id => id == payload;
        await Launch(c, AgentUser);
        Assert.Empty(launched);
        ha.GetFailsFor = null;
        await Pass(c);
        await c.DeliveredAsync();
        await Pass(c, 0.01);
        var r = Assert.Single(launched);
        Assert.Equal(LaunchProtocol.Ask, r.Permissions);
        Assert.Equal("req-0008-abcd", Outcome()["request"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_prompt_written_while_a_launch_starts_is_not_cleared_when_that_launch_is_done()
    {
        var release = new TaskCompletionSource<LaunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        answer = _ => release.Task;
        await using var c = await Ready();
        ha.Type(Entity("text", "new_prompt"), "first");
        Payload(new JsonObject { ["text"] = "the whole first prompt" });
        c.Observe(ha.Press(Entity("button", "new_session")));
        await Pass(c);
        for (var i = 0; i < 500 && Volatile.Read(ref launchedCount) == 0; i++) { await Task.Delay(10); }
        // Meanwhile a person types the next prompt and an agent publishes its request.
        ha.Type(Entity("text", "new_prompt"), "second");
        Payload(new JsonObject { ["text"] = "the agent's", ["request"] = "req-0010-abcd" });
        release.SetResult(new LaunchResult(LaunchOutcome.Started) { Session = "6f000000-0000-4000-8000-0000000000cc" });
        await c.DeliveredAsync();
        await Pass(c, 0.01);
        Assert.Equal("the whole first prompt", launched[0].Prompt);
        Assert.Equal("second", State("text", "new_prompt"));
        Assert.Contains("req-0010-abcd", ha.Retained[HaContract.PromptPayloadTopic(Slug)], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_launch_whose_starting_note_cannot_be_shown_still_starts_and_its_request_hears_so()
    {
        await using var c = await Ready();
        ha.PublishFailsFor = (topic, payload) => topic == HaContract.LaunchResultTopic(Slug) && payload.StartsWith("Starting", StringComparison.Ordinal);
        Payload(new JsonObject { ["request"] = "req-0011-abcd" });
        await Launch(c, AgentUser);
        Assert.Single(launched);
        Assert.Equal("started", Outcome()["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_session_an_agent_launched_that_appeared_before_its_launch_was_reported_turns_purple()
    {
        var release = new TaskCompletionSource<LaunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        answer = _ => release.Task;
        await using var c = await Ready();
        Payload(new JsonObject { ["request"] = "req-0012-abcd" });
        c.Observe(ha.Press(Entity("button", "new_session"), AgentUser));
        await Pass(c);
        for (var i = 0; i < 500 && Volatile.Read(ref launchedCount) == 0; i++) { await Task.Delay(10); }
        const string id = "6f000000-0000-4000-8000-0000000000dd";
        sessions.Set(FakeSessions.Copilot(id));
        await Pass(c);
        var topic = HaContract.Topics(HaContract.NodeId(id)).ActivityAttributes;
        Assert.Equal("human", JsonNode.Parse(ha.Retained[topic])!["driver"]!.GetValue<string>());
        release.SetResult(new LaunchResult(LaunchOutcome.Started) { Session = id });
        await c.DeliveredAsync();
        await Pass(c, 0.01);
        await Pass(c);
        Assert.Equal("agent", JsonNode.Parse(ha.Retained[topic])!["driver"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_press_made_while_a_launch_started_is_busy_even_when_that_launch_finished_before_it_was_read()
    {
        var release = new TaskCompletionSource<LaunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        answer = _ => release.Task;
        await using var c = await Ready();
        c.Observe(ha.Press(Entity("button", "new_session")));
        await Pass(c);
        for (var i = 0; i < 500 && Volatile.Read(ref launchedCount) == 0; i++) { await Task.Delay(10); }
        Payload(new JsonObject { ["request"] = "req-0013-abcd" });
        c.Observe(ha.Press(Entity("button", "new_session"), AgentUser));
        release.SetResult(new LaunchResult(LaunchOutcome.Started) { Session = "6f000000-0000-4000-8000-0000000000ee" });
        await c.DeliveredAsync();
        await Pass(c);
        lock (launched) { Assert.Single(launched); }
        Assert.Equal(("req-0013-abcd", "busy"), (Outcome()["request"]!.GetValue<string>(), Outcome()["outcome"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_request_names_what_this_machine_offers_in_any_case_and_the_host_gets_the_offered_spelling()
    {
        await using var c = await Ready();
        Payload(new JsonObject { ["request"] = "req-0014-abcd", ["settings"] = new JsonObject
        {
            ["agent"] = "AGENCY", ["workspace"] = "SCRATCH", ["profile"] = "Work", ["model"] = "GPT-5.5", ["effort"] = "agent DEFAULT",
        } });
        await Launch(c, AgentUser);
        var r = Assert.Single(launched);
        Assert.Equal(("agency", "scratch", "work", "gpt-5.5", ""), (r.Agent, r.Workspace, r.Profile, r.Model, r.Effort));

        ha.Now = ha.Now.AddSeconds(11);
        Payload(new JsonObject { ["request"] = "req-0015-abcd", ["settings"] = new JsonObject { ["resume"] = "6F000000-0000-4000-8000-000000000001" } });
        await Launch(c, AgentUser);
        Assert.Equal("6f000000-0000-4000-8000-000000000001", launched[1].Resume);
    }

    [Fact]
    public async Task The_turn_an_agent_launched_with_is_the_agents_and_the_next_one_is_the_persons()
    {
        var release = new TaskCompletionSource<LaunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        answer = _ => release.Task;
        await using var c = await Ready();
        Payload(new JsonObject { ["text"] = "Fix the build", ["request"] = "req-0016-abcd" });
        c.Observe(ha.Press(Entity("button", "new_session"), AgentUser));
        await Pass(c);
        for (var i = 0; i < 500 && Volatile.Read(ref launchedCount) == 0; i++) { await Task.Delay(10); }
        // Registered, its opening turn under way, before the launch is reported.
        const string id = "6f000000-0000-4000-8000-0000000000ff";
        var session = FakeSessions.Copilot(id);
        sessions.Set(session with { Activity = new Activity { TurnCount = 1, UserMessage = "Fix the build", Response = "On it" } });
        await Pass(c);
        release.SetResult(new LaunchResult(LaunchOutcome.Started) { Session = id });
        await c.DeliveredAsync();
        await Pass(c, 0.01);
        await Pass(c);
        var topic = HaContract.Topics(HaContract.NodeId(id)).ActivityAttributes;
        Assert.Equal("agent", JsonNode.Parse(ha.Retained[topic])!["driver"]!.GetValue<string>());
        // The person at the machine types next.
        sessions.Set(session with { Activity = new Activity { TurnCount = 2, UserMessage = "Now the tests", Response = "On it" } });
        await Pass(c);
        Assert.Equal("human", JsonNode.Parse(ha.Retained[topic])!["driver"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_restarted_connector_keeps_the_outcomes_and_never_serves_a_request_they_name_again()
    {
        var outcomes = new JsonObject
        {
            ["recent"] = new JsonArray(
                new JsonObject { ["request"] = "req-0017-abcd", ["outcome"] = "starting", ["session"] = "", ["note"] = "Received; checking its settings before launching.", ["pressed"] = "2026-10-29T19:59:00.0000000Z", ["at"] = "2026-10-29T19:59:00.0000000Z", ["launched"] = null },
                new JsonObject { ["request"] = "req-0018-abcd", ["outcome"] = "started", ["session"] = "6f000000-0000-4000-8000-000000000011", ["note"] = "", ["pressed"] = "2026-10-29T19:58:00.0000000Z", ["at"] = "2026-10-29T19:58:00.0000000Z", ["launched"] = null }),
        };
        ha.Seed(HaContract.LaunchOutcomeTopic(Slug), HaContract.Serialize(outcomes));
        await using var c = await Ready();
        var recent = JsonNode.Parse(ha.Retained[HaContract.LaunchOutcomeTopic(Slug)])!["recent"]!.AsArray();
        Assert.Equal(["req-0017-abcd", "req-0018-abcd"], recent.Select(o => o!["request"]!.GetValue<string>()));
        Assert.Equal("unconfirmed", recent[0]!["outcome"]!.GetValue<string>());

        // The payload a lost clear left behind: not served a second time.
        Payload(new JsonObject { ["text"] = "the request's words", ["request"] = "req-0018-abcd" });
        await Launch(c, AgentUser);
        var r = Assert.Single(launched);
        Assert.Null(r.Prompt);
        Assert.Equal(2, JsonNode.Parse(ha.Retained[HaContract.LaunchOutcomeTopic(Slug)])!["recent"]!.AsArray().Count);
    }

    [Fact]
    public async Task Taking_the_machine_back_from_agent_ha_bridge_takes_its_newer_outcomes_with_it()
    {
        await using var c = await Ready();
        Payload(new JsonObject { ["request"] = "req-0019-abcd" });
        await Launch(c, AgentUser);
        Assert.Single(launched);

        // agent-ha-bridge has the machine for a while: it serves a request of its own, and its
        // clear of the payload is lost.
        daemon = "agent-ha-bridge is running";
        await Pass(c);
        ha.Seed(HaContract.LaunchOutcomeTopic(Slug), HaContract.Serialize(new JsonObject
        {
            ["recent"] = new JsonArray(new JsonObject { ["request"] = "req-0020-abcd", ["outcome"] = "started", ["session"] = "6f000000-0000-4000-8000-000000000022", ["note"] = "", ["pressed"] = "", ["at"] = "", ["launched"] = null }),
        }));
        daemon = null;
        await Pass(c);
        await Pass(c, 2);
        await Pass(c, 6);

        ha.Now = ha.Now.AddSeconds(11);
        Payload(new JsonObject { ["text"] = "the daemon's request", ["request"] = "req-0020-abcd" });
        await Launch(c, AgentUser);
        Assert.Equal(2, launched.Count);
        Assert.Null(launched[1].Prompt);
        Assert.Equal(["req-0020-abcd"], JsonNode.Parse(ha.Retained[HaContract.LaunchOutcomeTopic(Slug)])!["recent"]!.AsArray().Select(o => o!["request"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_launch_still_starting_across_a_pause_is_not_called_unconfirmed_and_its_outcome_follows()
    {
        var release = new TaskCompletionSource<LaunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        answer = _ => release.Task;
        await using var c = await Ready();
        Payload(new JsonObject { ["request"] = "req-0021-abcd" });
        c.Observe(ha.Press(Entity("button", "new_session"), AgentUser));
        await Pass(c);
        for (var i = 0; i < 500 && Volatile.Read(ref launchedCount) == 0; i++) { await Task.Delay(10); }
        daemon = "agent-ha-bridge is running";
        await Pass(c);
        daemon = null;
        await Pass(c);
        await Pass(c, 2);
        await Pass(c, 6);
        Assert.Equal("starting", Outcome()["outcome"]!.GetValue<string>());
        release.SetResult(new LaunchResult(LaunchOutcome.Started) { Session = "6f000000-0000-4000-8000-000000000033" });
        await c.DeliveredAsync();
        await Pass(c);
        Assert.Equal("started", Outcome()["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_stale_request_is_skipped_text_and_all_and_the_press_launches_from_the_card()
    {
        await using var c = await Ready();
        Payload(new JsonObject { ["text"] = "old words", ["request"] = "req-0004-abcd" });
        // And left there: a payload that could not be cleared is still not read for its words.
        ha.PublishFailsFor = (topic, payload) => topic == HaContract.PromptPayloadTopic(Slug) && payload == "{}";
        ha.Now = ha.Now.AddMinutes(2);
        ha.Type(Entity("text", "new_prompt"), "fresh words");
        await Launch(c);
        var r = Assert.Single(launched);
        Assert.Equal("fresh words", r.Prompt);
        Assert.Equal("copilot", r.Agent);
    }

    [Fact]
    public async Task A_press_already_on_the_button_at_start_is_not_a_launch()
    {
        var c = await Ready();
        ha.Press(Entity("button", "new_session"));
        await using var restarted = Connector();
        await c.DisposeAsync();
        await Pass(restarted);
        await Pass(restarted, 31);
        await restarted.DeliveredAsync();
        await Pass(restarted);
        Assert.Empty(launched);
    }

    [Fact]
    public async Task A_press_while_a_launch_is_still_starting_launches_nothing_more_and_a_request_hears_it_is_busy()
    {
        var release = new TaskCompletionSource<LaunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        answer = _ => release.Task;
        await using var c = await Ready();
        c.Observe(ha.Press(Entity("button", "new_session")));
        await Pass(c);
        // The launch is dispatched off the pass, so wait for it to reach the launcher.
        for (var i = 0; i < 500 && Volatile.Read(ref launchedCount) == 0; i++) { await Task.Delay(10); }
        Payload(new JsonObject { ["request"] = "req-0009-abcd" });
        c.Observe(ha.Press(Entity("button", "new_session"), AgentUser));
        await Pass(c);
        lock (launched) { Assert.Single(launched); }
        Assert.Equal("busy", Outcome()["outcome"]!.GetValue<string>());
        // A person's press, from the card, is not a second launch either.
        c.Observe(ha.Press(Entity("button", "new_session")));
        await Pass(c);
        lock (launched) { Assert.Single(launched); }
        release.SetResult(new LaunchResult(LaunchOutcome.Started) { Session = "6f000000-0000-4000-8000-0000000000bb" });
        await c.DeliveredAsync();
        await Pass(c, 0.01);
        lock (launched) { Assert.Single(launched); }
    }

    [Fact]
    public async Task A_Launch_pressed_just_as_the_machine_is_published_again_is_launched()
    {
        await using var c = await Ready();
        // Only the liveness sensor's config goes; the machine is published again around the card,
        // and Launch is pressed just then, with the watch down.
        var (topic, _) = HaContract.OnlineConfig(Slug, Machine);
        await ha.PublishAsync(topic, "", true);
        var pressed = false;
        ha.BeforePublish = t =>
        {
            if (t != topic || pressed) { return; }
            pressed = true;
            ha.Press(Entity("button", "new_session"));
        };
        for (var i = 0; i < 400 && launchedCount == 0; i++)
        {
            await Pass(c);
            await c.DeliveredAsync();
        }
        Assert.True(pressed);
        Assert.Equal(1, launchedCount);
    }

    [Fact]
    public async Task A_note_left_for_ten_minutes_is_cleared()
    {
        answer = _ => Task.FromResult(new LaunchResult(LaunchOutcome.Failed, "Launch failed: no agent"));
        await using var c = await Ready();
        await Launch(c);
        Assert.Equal("Launch failed: no agent", Note);
        // Home Assistant's clock runs an hour ahead of the connector's here.
        await Pass(c, 3600 + 9 * 60);
        Assert.Equal("Launch failed: no agent", Note);
        await Pass(c, 120);
        Assert.Equal("", Note);
    }

    [Fact]
    public async Task Without_a_launcher_that_launches_here_the_card_is_withdrawn_and_no_launch_is_offered()
    {
        available = false;
        ha.Seed(HaContract.DiscoveryTopic("button", HaContract.MachineNode(Slug), "new_session"), $$"""{"unique_id":"agent_bridge_{{Slug}}_new_session","name":"Start new session"}""");
        await using var c = Connector();
        await Pass(c);
        await Pass(c);
        Assert.False(ha.Retained.ContainsKey(HaContract.DiscoveryTopic("button", HaContract.MachineNode(Slug), "new_session")));
        var global = JsonNode.Parse(ha.Retained[HaContract.GlobalAttributesTopic(Slug)])!;
        Assert.False(global["capabilities"]!["newSession"]!.GetValue<bool>());
        Assert.Equal(0, global["capabilities"]!["launchRequest"]!.GetValue<int>());
    }
}
