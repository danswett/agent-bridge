using System.Text.Json.Nodes;
using AgentBridge.Host;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Shadow;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// Shadow mode (docs/host-shadow.md) against agent-ha-bridge's files laid out as its daemon and
// hooks write them, under a synthetic HOME. Every state file, marker and transcript here is
// invented to the daemon's shapes; none is copied from a real installation.
public sealed class ShadowTests : IDisposable
{
    const string Id = "77777777-7777-4777-8777-777777777777";
    readonly Fixture f = new();
    readonly string reports = Path.Combine(Path.GetTempPath(), "abhs-" + Guid.NewGuid().ToString("N")[..8]);
    readonly List<string> log = [];

    AgentHaBridgePaths Paths => new(Path.Combine(f.Root, "home", ".agent-ha-bridge"), f.CopilotHome);

    public void Dispose()
    {
        f.Dispose();
        try { Directory.Delete(reports, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    // What Write-DaemonState and Set-BridgeDaemonAlive leave: the state, keyed by session id, and
    // a heartbeat touched on every pass.
    void Daemon(JsonObject state, TimeSpan? heartbeatAge = null)
    {
        Directory.CreateDirectory(Paths.Runtime);
        File.WriteAllText(Paths.StateFile, state.ToJsonString());
        File.WriteAllText(Paths.Heartbeat, "4242");
        File.SetLastWriteTimeUtc(Paths.Heartbeat, (f.Time.GetUtcNow() - (heartbeatAge ?? TimeSpan.FromSeconds(5))).UtcDateTime);
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.VersionFile)!);
        File.WriteAllText(Paths.VersionFile, "1.33.32\n");
    }

    static JsonObject Entry(string status, string? response = null, string[]? agents = null, string[]? shells = null) => new()
    {
        ["Offset"] = 1234,
        ["Name"] = "Copilot: Fixture session",
        ["Machine"] = "FIXTURE",
        ["Status"] = status,
        ["Kind"] = "copilot",
        ["Model"] = "fixture-model",
        ["Compactions"] = 0,
        ["LastResponse"] = response ?? "",
        ["LastHistory"] = new JsonArray(),
        ["BackgroundAgents"] = new JsonArray([.. (agents ?? []).Select(a => (JsonNode)a)]),
        ["BackgroundShells"] = new JsonArray([.. (shells ?? []).Select(s => (JsonNode)s)]),
    };

    ShadowRunner Runner(IShadowHost host, ShadowLedger? ledger = null) =>
        new(new DaemonReader(Paths, f.Time), host, ledger, log.Add);

    static IShadowHost InProcess(AgentBridge.Host.Core.ISessionSource source, string? agentHaBridge = "agent-ha-bridge is running") =>
        new SourceShadowHost(source, new Probe(agentHaBridge), "0.1.0", dev: true);

    sealed class Probe(string? active) : IAgentHaBridgeProbe
    {
        public string? Active() => active;
    }

    static ShadowView View(params ShadowSession[] sessions) => new() { Sessions = sessions.ToDictionary(s => s.Id), SeesAgentHaBridge = true };

    [Fact]
    public async Task Both_sides_showing_the_same_session_in_the_same_state_is_a_clean_sample()
    {
        Fixture.Append(f.CopilotSession(Id, 100), Copilot.User("do it", 1), Copilot.TurnStart(2), Copilot.Assistant("all done", 3), Copilot.TurnEnd(4));
        f.Processes.Run(AgentKind.Copilot, 100);
        Daemon(new JsonObject { [Id] = Entry("idle", response: "all  done\n") });
        await using var source = f.Source();
        await source.RefreshAsync();

        var c = await Runner(InProcess(source)).SampleAsync();

        Assert.True(c.Compared);
        Assert.Empty(c.Differences);
        Assert.Equal((1, 1), (c.DaemonSessions, c.HostSessions));
    }

    [Fact]
    public void A_daemon_waiting_on_background_work_is_the_hosts_idle_with_that_work_counted_beside_it()
    {
        var daemon = View(
            new ShadowSession { Id = "a", Agent = "copilot", Status = "agents", Agents = 2 },
            new ShadowSession { Id = "b", Agent = "copilot", Status = "shell", Shells = 1 });
        var host = View(
            new ShadowSession { Id = "a", Agent = "copilot", Status = "idle", Agents = 2 },
            new ShadowSession { Id = "b", Agent = "copilot", Status = "idle", Shells = 2 });

        var only = Assert.Single(ShadowCompare.Compare(daemon, host).Differences);

        Assert.Equal((ShadowKinds.BackgroundShells, ShadowHash.Session("b"), "daemon=1 host=2"), (only.Kind, only.Session, only.Detail));
    }

    [Fact]
    public void A_permission_prompt_the_host_offers_is_neither_a_question_nor_a_status_the_daemon_is_expected_to_share()
    {
        var asked = new Session
        {
            Id = Id,
            Agent = AgentKind.Copilot,
            Status = SessionStatus.Waiting,
            StatusSince = Fixture.T0,
            UpdatedAt = Fixture.T0,
            Activity = new Activity(),
            Question = new Question { Id = "p1", Kind = QuestionKind.Approval, Message = "Run git push?", AskedAt = Fixture.T0, Fields = [new Field { Name = "decision", Type = FieldType.Choice }] },
        };
        var daemon = View(new ShadowSession { Id = Id, Agent = "copilot", Status = "working" });

        Assert.Empty(ShadowCompare.Compare(daemon, View(HostSide.From(asked))).Differences);
        // Waiting on anything else is still a disagreement.
        Assert.Equal(ShadowKinds.Status, Assert.Single(ShadowCompare.Compare(daemon, View(HostSide.From(asked with { Question = null }))).Differences).Kind);
    }

    [Fact]
    public async Task A_copilot_question_the_daemon_has_armed_is_its_waiting_though_its_state_still_says_working()
    {
        // As seen live: agent-ha-bridge's status for a Copilot session comes from the transcript and
        // stays working through ask_user; its cards show "waiting for you" from the armed question.
        var schema = new
        {
            message = "Which one?",
            requestedSchema = new
            {
                properties = new { pick = new { type = "string", title = "Pick", oneOf = new[] { new { @const = "a", title = "A" }, new { @const = "b", title = "B" } } } },
                required = new[] { "pick" },
            },
        };
        Fixture.Append(f.CopilotSession(Id, 100), Copilot.User("go", 1), Copilot.TurnStart(2),
            Copilot.Assistant("asking", 3, tools: ["q1"]), Copilot.ToolStart("q1", "ask_user", schema, 3));
        f.Processes.Run(AgentKind.Copilot, 100);
        Daemon(new JsonObject { [Id] = Entry("working", response: "asking") });
        var marker = Path.Combine(f.CopilotHome, "session-state", Id, "agent-pending-decision.json");
        File.WriteAllText(marker, new JsonObject
        {
            ["decisionId"] = $"copilot_1-{Fixture.T0.AddSeconds(3).ToUnixTimeMilliseconds()}",
            ["toolCallId"] = "",
            ["question"] = "Which one?",
            ["choices"] = new JsonArray("A", "B"),
            ["fields"] = new JsonArray(new JsonObject { ["Name"] = "pick", ["Options"] = new JsonArray("A", "B"), ["Values"] = new JsonArray("a", "b"), ["IsText"] = false, ["MultiSelect"] = false }),
            ["armedAt"] = Fixture.T0.AddSeconds(3).ToString("o"),
        }.ToJsonString());
        await using var source = f.Source();
        await source.RefreshAsync();

        var c = await Runner(InProcess(source)).SampleAsync();

        Assert.True(c.Compared);
        Assert.Empty(c.Differences);
        // Without the armed question, the daemon's working is a disagreement with the host's waiting.
        File.Delete(marker);
        var unarmed = await Runner(InProcess(source)).SampleAsync();
        Assert.Contains(unarmed.Differences, d => (d.Kind, d.Detail) == (ShadowKinds.Status, "daemon=working host=waiting"));
    }

    [Fact]
    public void An_armed_question_is_the_daemons_waiting_and_a_half_written_marker_judges_neither_way()
    {
        var asked = new ShadowQuestion { AskedAt = Fixture.T0, Fields = [] };
        ShadowView Daemon(string status, ShadowQuestion? question = null, bool unknown = false) =>
            View(new ShadowSession { Id = "a", Agent = "copilot", Status = status, Question = question, QuestionUnknown = unknown });
        ShadowView Host(string status, ShadowQuestion? question = null) =>
            View(new ShadowSession { Id = "a", Agent = "copilot", Status = status, Question = question });

        Assert.Empty(ShadowCompare.Compare(Daemon("working", asked), Host("waiting", asked)).Differences);
        // The host not yet waiting on a question the daemon has armed is a disagreement about both.
        var differences = ShadowCompare.Compare(Daemon("idle", asked), Host("working")).Differences;
        Assert.Contains(differences, d => (d.Kind, d.Detail) == (ShadowKinds.Status, "daemon=waiting host=working"));
        Assert.Contains(differences, d => d.Kind == ShadowKinds.QuestionMissingOnHost);
        Assert.Empty(ShadowCompare.Compare(Daemon("working", unknown: true), Host("waiting", asked)).Differences);
        Assert.Empty(ShadowCompare.Compare(Daemon("working", unknown: true), Host("working")).Differences);
        Assert.Equal(ShadowKinds.Status, Assert.Single(ShadowCompare.Compare(Daemon("working", unknown: true), Host("idle")).Differences).Kind);
    }

    [Fact]
    public async Task A_question_is_compared_by_its_fields_kinds_and_options_and_the_report_never_carries_its_words_or_the_session_id()
    {
        var schema = new
        {
            message = "SECRET-QUESTION which one?",
            requestedSchema = new
            {
                properties = new { pick = new { type = "string", title = "Pick", oneOf = new[] { new { @const = "a", title = "SECRET-A" }, new { @const = "b", title = "SECRET-B" } } } },
                required = new[] { "pick" },
            },
        };
        Fixture.Append(f.CopilotSession(Id, 100), Copilot.User("SECRET-PROMPT", 1), Copilot.TurnStart(2),
            Copilot.Assistant("SECRET-RESPONSE", 3, tools: ["q1"]), Copilot.ToolStart("q1", "ask_user", schema, 3));
        f.Processes.Run(AgentKind.Copilot, 100);
        Daemon(new JsonObject { [Id] = Entry("waiting", response: "SECRET-RESPONSE") });
        // Write-CopilotDecisionMarker's marker beside the session's state, with a third option the
        // host does not have, and the hint agent-ha-bridge appends to the message.
        File.WriteAllText(Path.Combine(f.CopilotHome, "session-state", Id, "agent-pending-decision.json"), new JsonObject
        {
            ["decisionId"] = $"copilot_7777777-{Fixture.T0.AddSeconds(3).ToUnixTimeMilliseconds()}",
            ["toolCallId"] = "",
            ["question"] = "SECRET-QUESTION  which one?\n\nAnswer in the terminal if you prefer.",
            ["choices"] = new JsonArray("SECRET-A", "SECRET-B", "SECRET-C"),
            ["combos"] = new JsonArray(),
            ["fields"] = new JsonArray(new JsonObject
            {
                ["Name"] = "pick",
                ["Label"] = "Pick",
                ["Options"] = new JsonArray("SECRET-A", "SECRET-B", "SECRET-C"),
                ["Values"] = new JsonArray("a", "b", "c"),
                ["IsText"] = false,
                ["MultiSelect"] = false,
            }),
            ["terminalOnly"] = false,
            ["mode"] = "multiple_choice",
            ["armedAt"] = Fixture.T0.AddSeconds(3).ToString("o"),
            ["injectedAnswer"] = "",
        }.ToJsonString());
        await using var source = f.Source();
        await source.RefreshAsync();
        f.Time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var ledger = new ShadowLedger(reports, TimeSpan.Zero, TimeSpan.FromSeconds(15), f.Time);

        var c = await Runner(InProcess(source), ledger).SampleAsync();

        var only = Assert.Single(c.Differences);
        Assert.Equal((ShadowKinds.QuestionOptions, "field 1: daemon=3 host=2"), (only.Kind, only.Detail));
        var report = File.ReadAllText(Path.Combine(reports, ShadowLedger.FileName("2026-10-28")));
        Assert.Contains(ShadowHash.Session(Id), report);
        Assert.DoesNotContain(Id, report);
        Assert.DoesNotContain("7777777", report);
        Assert.DoesNotContain("SECRET", report);
        Assert.DoesNotContain("pick", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mismatch: question.options on " + ShadowHash.Session(Id), Assert.Single(log));
    }

    // Seen on DEV-VM1 (#40): a shell ended and started again a minute later, the daemon a pass behind
    // each time; 1 against 0 and then 0 against 1 ran on as one 60 s disagreement.
    [Fact]
    public void A_count_that_turns_the_other_way_is_two_disagreements_each_judged_against_the_grace()
    {
        f.Time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var ledger = new ShadowLedger(reports, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(15), f.Time);
        ShadowDifference Shells(int daemon, int host) => new(ShadowKinds.BackgroundShells, "s1", $"daemon={daemon} host={host}");
        void Sample(params ShadowDifference[] diffs)
        {
            ledger.Record(new ShadowComparison(true, 1, 1, diffs, ["s1"]), new ShadowView(), new ShadowView());
            f.Time.Advance(TimeSpan.FromSeconds(15));
        }

        // 45 s the daemon's way, then 45 s the host's: two lags, neither lasting the grace.
        for (var i = 0; i < 4; i++) { Sample(Shells(1, 0)); }
        for (var i = 0; i < 4; i++) { Sample(Shells(0, 1)); }
        Sample();
        var day = ShadowLedger.Read(reports, "2026-10-28")!;
        Assert.Empty(day.Mismatches);
        Assert.Equal((2, 45L), (day.Transient[ShadowKinds.BackgroundShells].Count, day.Transient[ShadowKinds.BackgroundShells].LongestSeconds));

        // The same way throughout, however the counts move, is one disagreement, and lasts it.
        for (var i = 0; i < 3; i++) { Sample(Shells(2, 0)); }
        for (var i = 0; i < 3; i++) { Sample(Shells(3, 1)); }
        Sample();
        var m = Assert.Single(ShadowLedger.Read(reports, "2026-10-28")!.Mismatches);
        Assert.Equal(75L, m.Seconds);
        Assert.Equal(["daemon=2 host=0", "daemon=3 host=1"], m.Details);

        // A question's field count is its shape: wrong either way, it is one disagreement.
        ShadowDifference Fields(int daemon, int host) => new(ShadowKinds.QuestionFields, "s1", $"daemon={daemon} host={host}");
        for (var i = 0; i < 4; i++) { Sample(Fields(2, 1)); }
        for (var i = 0; i < 4; i++) { Sample(Fields(1, 2)); }
        Sample();
        Assert.Contains(ShadowLedger.Read(reports, "2026-10-28")!.Mismatches, x => x.Kind == ShadowKinds.QuestionFields && x.Seconds == 105);
    }

    [Fact]
    public void A_disagreement_shorter_than_the_grace_is_timing_skew_and_one_that_lasts_it_is_a_mismatch()
    {
        f.Time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var ledger = new ShadowLedger(reports, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(15), f.Time);
        var status = new ShadowDifference(ShadowKinds.Status, "s1", "daemon=working host=idle");
        IReadOnlyList<ShadowEpisode> Sample(params ShadowDifference[] diffs)
        {
            var made = ledger.Record(new ShadowComparison(true, 1, 1, diffs, ["s1"]), new ShadowView(), new ShadowView());
            f.Time.Advance(TimeSpan.FromSeconds(15));
            return made;
        }

        Sample(status);
        Sample(status);
        Sample();
        var made = Enumerable.Range(0, 6).SelectMany(_ => Sample(status)).ToList();
        Sample();

        var day = ShadowLedger.Read(reports, "2026-10-28")!;
        Assert.Equal((1, 15L), (day.Transient[ShadowKinds.Status].Count, day.Transient[ShadowKinds.Status].LongestSeconds));
        var mismatch = Assert.Single(day.Mismatches);
        Assert.Equal(mismatch.Kind, Assert.Single(made).Kind);
        Assert.Equal((75L, 6, false), (mismatch.Seconds, mismatch.Samples, mismatch.Open));
        Assert.Equal(["daemon=working host=idle"], mismatch.Details);
        Assert.Equal(("mismatches", 10, 10, 150L), (day.Verdict, day.Samples, day.Compared, day.ComparedSeconds));

        // A run restarted the same day at another interval adds its own time, and does not
        // rescale what was compared before it.
        new ShadowLedger(reports, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5), f.Time)
            .Record(new ShadowComparison(true, 1, 1, [], ["s1"]), new ShadowView(), new ShadowView());
        Assert.Equal((11, 155L), (ShadowLedger.Read(reports, "2026-10-28")!.Compared, ShadowLedger.Read(reports, "2026-10-28")!.ComparedSeconds));
    }

    // Seen live (#40): a session whose background agents talk all the time changes its latest
    // response every few seconds, and the daemon, writing on its own pass, kept showing the one
    // the host had shown a moment before, past the grace.
    [Fact]
    public void A_response_one_side_showed_a_moment_ago_is_timing_and_one_neither_showed_is_a_difference()
    {
        var memory = new ShadowMemory();
        var at = f.Time.GetUtcNow();
        ShadowDifference? Response(string daemon, string host)
        {
            ShadowSession S(string tail) => new() { Id = Id, Status = "idle", ResponseTail = ShadowHash.Tail(tail) };
            var c = ShadowCompare.Compare(View(S(daemon)), View(S(host)), memory, at);
            at += TimeSpan.FromSeconds(15);
            return c.Differences.SingleOrDefault(d => d.Kind == ShadowKinds.Response);
        }

        Assert.Null(Response("the first words", "the first words"));
        Assert.Equal(ShadowCompare.Timing, Response("the first words", "the second words")!.Known);
        Assert.Equal(ShadowCompare.Timing, Response("the second words", "the third words")!.Known);
        Assert.Null(Response("words neither side showed", "the third words")!.Known);
        // Remembered for minutes, not for ever: a daemon stuck on an old response differs.
        at += TimeSpan.FromMinutes(5);
        Assert.Null(Response("the second words", "the fourth words")!.Known);
        // Remembered from when it was last shown: one both showed for five minutes was still
        // showing a moment ago when one side moves on (found in review, #77).
        for (var i = 0; i < 20; i++) { Assert.Null(Response("a long-standing answer", "a long-standing answer")); }
        Assert.Equal(ShadowCompare.Timing, Response("a long-standing answer", "a newer answer")!.Known);
    }

    // #175: agent-ha-bridge's card shows whichever agent wrote last, by the owner's decision, and the
    // host's Home Assistant card the main agent's own (subagents.md §7). With the two agreeing on the
    // latest writer's text, that is a labelled intentional difference; any other disagreement about
    // the response is the parser comparison's, and still a mismatch.
    [Fact]
    public void The_main_agents_own_text_on_the_hosts_card_is_labelled_intentional_and_an_unrelated_response_mismatch_still_counts()
    {
        var host = HostSide.From(new Session
        {
            Id = Id,
            Agent = AgentKind.Copilot,
            Status = SessionStatus.Idle,
            StatusSince = Fixture.T0,
            UpdatedAt = Fixture.T0,
            Activity = new Activity
            {
                Response = "Delegate: all 40 tests pass",
                Source = new ActivitySource { Response = "agent-3a91" },
                Own = new OwnActivity { Response = "Done: the release is cut." },
            },
        });
        ShadowDifference? Response(string daemonText, ShadowSession h) =>
            ShadowCompare.Compare(View(new ShadowSession { Id = Id, Agent = "copilot", Status = "idle", ResponseTail = ShadowHash.Tail(daemonText) }), View(h))
                .Differences.SingleOrDefault(d => d.Kind == ShadowKinds.Response);

        Assert.Equal(ShadowCompare.MainAgentOnly, Response("Delegate:  all 40 tests pass\n", host)!.Known);
        // The daemon showing other words than the latest writer's is a mismatch, sub-agents or not.
        Assert.Null(Response("Something else entirely", host)!.Known);
        // Even the main agent's own: the two disagree on what was written last.
        Assert.Null(Response("Done: the release is cut.", host)!.Known);
        // With the main agent the latest writer, there is nothing to label.
        Assert.Null(Response("Done: the release is cut.", HostSide.From(new Session
        {
            Id = Id, Agent = AgentKind.Copilot, Status = SessionStatus.Idle, StatusSince = Fixture.T0, UpdatedAt = Fixture.T0,
            Activity = new Activity { Response = "Done: the release is cut." },
        })));
    }

    // #175: the agents behind the host's background.agents, entry by entry, for ShadowWitness.Agents.
    [Fact]
    public void The_hosts_counted_agents_are_read_from_its_list_only_when_the_list_holds_them_all()
    {
        Subagent A(string id, SubagentStatus status, SubagentKind kind = SubagentKind.Background) => new() { Id = id, Kind = kind, Status = status };
        IReadOnlyList<string>? Running(IReadOnlyList<Subagent>? list, int? more = null) => HostSide.From(new Session
        {
            Id = Id, Agent = AgentKind.Copilot, Status = SessionStatus.Idle, StatusSince = Fixture.T0, UpdatedAt = Fixture.T0,
            Activity = new Activity(), Subagents = list, SubagentsMore = more,
        }).RunningSubagents;

        Assert.Equal([], Running(null));
        Assert.Equal(["a1", "a3"], Running([A("a1", SubagentStatus.Working), A("a2", SubagentStatus.Working, SubagentKind.Task), A("a3", SubagentStatus.Waiting), A("a4", SubagentStatus.Idle)], more: 4));
        // Capped with nothing finished after the running ones: some may have been left out.
        Assert.Null(Running([A("a1", SubagentStatus.Working)], more: 2));
        Assert.Null(Running(null, more: 3));
    }

    // The live shadow's first minutes (#40): a session's subagents' shells counted by the daemon as
    // its own (agent-ha-bridge#261) are a known difference, tallied apart, and never a mismatch.
    [Fact]
    public async Task A_difference_with_the_signature_of_a_known_daemon_bug_is_tallied_as_known_and_never_becomes_a_mismatch()
    {
        f.Time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var transcript = Paths.CopilotTranscript(Id)!;
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        File.WriteAllText(transcript, System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "tool.execution_complete",
            agentId = "fixture-agent",
            data = new { toolCallId = "call-1", success = true, result = new { content = "<command with shellId: 41 moved to background by the user>" } },
        }) + "\n");
        var entry = Entry("shell", shells: ["41"]);
        entry["Offset"] = new FileInfo(transcript).Length;
        Daemon(new JsonObject { [Id] = entry });
        var started = new DateTimeOffset(2026, 10, 27, 9, 30, 0, TimeSpan.Zero);
        File.WriteAllText(Paths.ProcessRecord, new JsonObject { ["pid"] = 4242, ["startedUtcTicks"] = started.UtcTicks }.ToJsonString());
        var host = new FixedHost(View(new ShadowSession { Id = Id, Agent = "copilot", Status = "idle", Model = "fixture-model" }));
        var ledger = new ShadowLedger(reports, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(15), f.Time);
        var runner = new ShadowRunner(new DaemonReader(Paths, f.Time), host, ledger, log.Add, witness: new ShadowWitness(Paths), time: f.Time);

        for (var i = 0; i < 8; i++) { await runner.SampleAsync(); Advance(TimeSpan.FromSeconds(15)); }

        var day = ledger.Today!;
        Assert.Empty(day.Mismatches);
        Assert.Empty(day.Transient);
        var known = day.Known[ShadowKinds.BackgroundShells]["agent-ha-bridge#261"];
        Assert.Equal(8, known.Samples);
        Assert.Equal([ShadowHash.Session(Id)], known.Sessions);
        Assert.Equal(started, day.Coverage!.DaemonStartedAt);
    }
    [Fact]
    public async Task A_daemon_whose_heartbeat_is_stale_is_down_and_nothing_else_is_compared_until_three_beats_have_gone()
    {
        Fixture.Append(f.CopilotSession(Id, 100), Copilot.User("do it", 1));
        f.Processes.Run(AgentKind.Copilot, 100);
        Daemon(new JsonObject { [Id] = Entry("idle") }, heartbeatAge: TimeSpan.FromMinutes(5));
        await using var source = f.Source();
        await source.RefreshAsync();
        var ledger = new ShadowLedger(reports, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), f.Time);
        var runner = Runner(InProcess(source), ledger);

        var first = await runner.SampleAsync();
        for (var i = 0; i < 5; i++) { f.Time.Advance(TimeSpan.FromSeconds(30)); await runner.SampleAsync(); }
        Assert.Empty(ledger.Today!.Mismatches);
        f.Time.Advance(TimeSpan.FromSeconds(30));
        await runner.SampleAsync();

        Assert.False(first.Compared);
        Assert.Equal(new ShadowDifference(ShadowKinds.MachineDaemon, null, "the daemon's heartbeat is over a minute old"), Assert.Single(first.Differences));
        Assert.Equal((ShadowKinds.MachineDaemon, 180L), (Assert.Single(ledger.Today!.Mismatches).Kind, ledger.Today.Mismatches[0].Seconds));
        Assert.Equal(0, ledger.Today.Compared);
    }

    [Fact]
    public async Task A_host_that_does_not_see_the_running_daemon_is_a_mismatch_because_it_would_deliver_beside_it()
    {
        Daemon(new JsonObject());
        await using var source = f.Source();
        await source.RefreshAsync();

        var c = await Runner(InProcess(source, agentHaBridge: null)).SampleAsync();

        Assert.Equal(ShadowKinds.MachineG5, Assert.Single(c.Differences).Kind);
    }

    [Fact]
    public async Task Reading_the_daemons_files_changes_nothing_under_the_home()
    {
        Fixture.Append(f.CopilotSession(Id, 100), Copilot.User("do it", 1));
        f.Processes.Run(AgentKind.Copilot, 100);
        Daemon(new JsonObject { [Id] = Entry("working") });
        Directory.CreateDirectory(Path.Combine(Paths.Runtime, "agent-bridge-spool"));
        File.WriteAllText(Path.Combine(Paths.Runtime, "agent-bridge-spool", "event.json"), "{}");
        File.WriteAllText(Path.Combine(Paths.Home, "config.json"), "{}");
        var before = f.Listing();
        await using var source = f.Source();
        await source.RefreshAsync();

        await Runner(InProcess(source), new ShadowLedger(reports, TimeSpan.Zero, TimeSpan.FromSeconds(15), f.Time)).SampleAsync();

        Assert.Equal(before, f.Listing());
    }

    [Fact]
    public void The_summary_counts_mismatches_by_kind_over_the_days_once_each_and_says_which_days_were_clean()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 26, 23, 59, 30, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var ledger = new ShadowLedger(reports, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), time);
        void Sample(params ShadowDifference[] diffs)
        {
            ledger.Record(new ShadowComparison(true, 1, 1, diffs, ["s1"]), new ShadowView { Version = "1.33.32" }, new ShadowView { Version = "0.1.0" });
            time.Advance(TimeSpan.FromSeconds(30));
        }
        // Begun before midnight and only a mismatch after it: in both days' files, one mismatch.
        for (var i = 0; i < 10; i++) { Sample(new ShadowDifference(ShadowKinds.Status, "s1", "daemon=working host=idle")); }
        Sample();
        // A short one the next morning: timing skew, not a mismatch.
        Sample(new ShadowDifference(ShadowKinds.Model, "s1", "daemon=a host=b"));
        Sample();
        time.SetUtcNow(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
        Sample();

        var summary = ShadowSummary.Build(reports, new DateOnly(2026, 10, 29), days: 4);

        Assert.Equal(["mismatches", "mismatches", "clean", "no data"], summary.Days.Select(d => d.Verdict));
        var kind = Assert.Single(summary.Kinds);
        Assert.Equal((ShadowKinds.Status, 1, 1, 270L), (kind.Kind, kind.Count, kind.Sessions, kind.LongestSeconds));
        Assert.Equal((ShadowKinds.Model, 1), (Assert.Single(summary.Transient).Kind, summary.Transient[0].Count));
        var text = summary.Format();
        Assert.Contains("1 mismatch by kind", text);
        Assert.Contains("Clean days: 1 of 4, 0 of them compared for at least 20 h.", text);
        Assert.Contains("usage", text);
    }

    [Fact]
    public async Task The_running_host_is_read_through_its_own_pipe_and_a_host_that_is_not_running_says_so()
    {
        Fixture.Append(f.CopilotSession(Id, 100), Copilot.User("do it", 1), Copilot.TurnStart(2));
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source();
        await source.RefreshAsync();
        var profile = Path.Combine(Path.GetTempPath(), "abh-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(profile);
        try
        {
            var machine = new MachineInfo("host-id", "Test host", "windows", "0.1.0");
            var control = new HostControl(new HostControlOptions
            {
                Profile = "test",
                Folder = profile,
                Version = "0.1.0",
                Dev = true,
                Machine = machine,
                Running = new HostSettingsView(RemoteApproval: true),
                AgentHaBridge = () => "agent-ha-bridge is running for this user",
                SignIn = (_, _, _) => Task.FromException<AgentBridge.Transport.SignInResult>(new InvalidOperationException()),
                SignedIn = (_, _) => { },
                Stop = () => { },
                Restart = () => { },
            });
            var view = new PipeShadowHost(LocalEndpoint.For(profile));
            Assert.Equal("the host is not running for this profile", (await view.ObserveAsync()).Problem);

            // The strictest caller short of the app: reading is all the comparison asks.
            var api = LocalApi.Handler(new SessionService(source, machine), control, new FixedCallerGuard(new Caller(CallerKind.Agent, "the shadow comparison, run from inside an agent session")));
            var asked = new List<string>();
            await using (LocalServer.Start(LocalEndpoint.For(profile), (channel, op, data) => { lock (asked) { asked.Add(op); } return api(channel, op, data); }))
            {
                var seen = await view.ObserveAsync();

                Assert.True(seen.Available);
                Assert.Equal(("working", "copilot"), (seen.Sessions[Id].Status, seen.Sessions[Id].Agent));
                Assert.Equal((true, "0.1.0", true), (seen.SeesAgentHaBridge, seen.Version, seen.Dev));
                Assert.Equal(("the host does not offer usage", "the host did not answer launch.options"), (seen.UsageProblem, seen.ResumableProblem));
                // The slow ask first, so the sessions are as recent as the daemon's files read next.
                Assert.True(asked.IndexOf(LaunchProtocol.Options) < asked.IndexOf(Ops.SessionsList), string.Join(", ", asked));
            }
        }
        finally { try { Directory.Delete(profile, recursive: true); } catch (IOException) { } }
    }

    // ---- usage and the resume list, from Home Assistant ----

    const string Token = "FIXTURE-TOKEN-0f9e8d7c6b5a";
    const string Account = "fixture-login";

    // Home Assistant as far as the comparison may see it: the states it holds, by entity id, and
    // every request that reached it.
    sealed class FakeHomeAssistant(Func<string, string?> state) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Auth)> Seen { get; } = [];
        public Exception? Fail { get; set; }
        public System.Net.HttpStatusCode? Status { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString()));
            if (Fail is { } e) { throw e; }
            if (Status is { } s) { return Task.FromResult(new HttpResponseMessage(s)); }
            var entity = Uri.UnescapeDataString(request.RequestUri.AbsolutePath.Split('/')[^1]);
            return Task.FromResult(state(entity) is { } json
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    // agent-ha-bridge's config.json as its installer writes it, with more in it than the
    // comparison reads.
    void Config(Action<JsonObject>? more = null)
    {
        Directory.CreateDirectory(Paths.Home);
        var config = new JsonObject
        {
            ["homeAssistant"] = new JsonObject { ["baseUrl"] = "https://ha.fixture.invalid:8123", ["token"] = Token, ["agentTokenEnvVar"] = "AGENT_HA_AGENT_TOKEN" },
            ["mqtt"] = new JsonObject { ["password"] = "FIXTURE-MQTT" },
            ["newSession"] = new JsonObject { ["resumeCount"] = 12 },
        };
        more?.Invoke(config);
        File.WriteAllText(Path.Combine(Paths.Home, "config.json"), config.ToJsonString());
    }

    // Publish-CopilotMqttUsage's state for one agent, and the resume select's.
    static string UsageState(string client, double percent, DateTimeOffset at, string account = Account, string slug = "fixture") => new JsonObject
    {
        ["entity_id"] = $"sensor.agent_bridge_{slug}_usage_{client}",
        ["state"] = percent.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["attributes"] = new JsonObject
        {
            ["client"] = client,
            ["machine_slug"] = slug,
            ["account"] = account,
            ["account_id"] = "",
            ["windows"] = new JsonArray(new JsonObject { ["key"] = "monthly", ["label"] = "Monthly", ["percent"] = percent }),
            ["measured_at"] = at.ToString("o"),
        },
    }.ToJsonString();

    static string ResumeState(params string[] labels) => new JsonObject
    {
        ["entity_id"] = "select.agent_bridge_fixture_new_resume",
        ["state"] = "New session",
        ["attributes"] = new JsonObject { ["options"] = new JsonArray([.. labels.Prepend("New session").Select(l => (JsonNode)l)]) },
    }.ToJsonString();

    // Time passing with the daemon still beating.
    void Advance(TimeSpan by)
    {
        f.Time.Advance(by);
        File.SetLastWriteTimeUtc(Paths.Heartbeat, (f.Time.GetUtcNow() - TimeSpan.FromSeconds(5)).UtcDateTime);
    }

    sealed class FixedHost(ShadowView view) : IShadowHost
    {
        public Task<ShadowView> ObserveAsync(CancellationToken ct = default) => Task.FromResult(view);
    }

    static ShadowUsage Reading(double percent, DateTimeOffset at, string account = Account) => new()
    {
        Reading = true,
        Account = account,
        Windows = new Dictionary<string, double> { ["monthly"] = percent },
        MeasuredAt = at,
    };

    [Fact]
    public async Task Home_Assistant_is_only_ever_sent_a_get_of_this_machines_usage_sensors_and_resume_select()
    {
        var inner = new FakeHomeAssistant(_ => "{}");
        using var reader = new DaemonHaReader("FIXTURE", inner, time: f.Time);
        using var http = new HttpClient(new ReadOnlyHomeAssistantHandler(reader.Entities, inner));
        var ha = new Uri("https://ha.fixture.invalid/api/states/");

        Assert.Equal(
            ["sensor.agent_bridge_fixture_usage_copilot", "sensor.agent_bridge_fixture_usage_claude", "sensor.agent_bridge_fixture_usage_codex",
                "sensor.agent_bridge_fixture_usage_copilot_2", "sensor.agent_bridge_fixture_usage_copilot_3", "sensor.agent_bridge_fixture_usage_copilot_4",
                "select.agent_bridge_fixture_new_resume"],
            reader.Entities);
        foreach (var refused in new Func<Task>[]
        {
            () => http.PostAsync(new Uri(ha, "sensor.agent_bridge_fixture_usage_copilot"), new StringContent("{}")),
            () => http.DeleteAsync(new Uri(ha, "sensor.agent_bridge_fixture_usage_copilot")),
            () => http.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(ha, "sensor.agent_bridge_fixture_usage_copilot")) { Content = new StringContent("{}") }),
            () => http.GetAsync(new Uri(ha, "sensor.agent_bridge_other_usage_copilot")),
            () => http.GetAsync(new Uri(ha, "button.agent_bridge_fixture_new_session")),
            () => http.GetAsync(new Uri(ha, "sensor.agent_bridge_fixture_usage_copilot?x=1")),
            () => http.GetAsync(new Uri("https://ha.fixture.invalid/api/services/button/press")),
            () => http.GetAsync(new Uri("https://ha.fixture.invalid/api/states")),
            () => http.GetAsync(new Uri("https://ha.fixture.invalid/other/api/states/sensor.agent_bridge_fixture_usage_copilot")),
            () => http.GetAsync(new Uri("https://ha.fixture.invalid/api/states/sensor.agent_bridge_fixture_usage_copilot/history")),
        })
        {
            await Assert.ThrowsAsync<HomeAssistantRequestRefusedException>(refused);
        }
        Assert.Empty(inner.Seen);

        Config();
        var reading = await reader.ReadAsync(AgentHaBridgeConfig.Read(Paths).Access!);

        Assert.All(inner.Seen, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Equal(reader.Entities.Select(e => "/api/states/" + e), inner.Seen.Select(r => r.Path));
        Assert.All(inner.Seen, r => Assert.Equal("Bearer " + Token, r.Auth));
        Assert.Equal(0, reading.Usage!.Count(u => u.Value.Reading));
    }

    [Fact]
    public async Task The_daemons_usage_and_resume_list_are_compared_with_the_hosts_and_no_report_carries_the_token_an_account_or_a_label()
    {
        f.Time.SetLocalTimeZone(TimeZoneInfo.Utc);
        Daemon(new JsonObject());
        Config();
        var now = f.Time.GetUtcNow();
        var inner = new FakeHomeAssistant(entity => entity switch
        {
            "sensor.agent_bridge_fixture_usage_copilot" => UsageState("copilot", 40, now - TimeSpan.FromMinutes(2)),
            "sensor.agent_bridge_fixture_usage_claude" => UsageState("claude", 10, now, account: "someone-else"),
            // Further Copilot logins: one the host reads too, and one it is not configured with.
            "sensor.agent_bridge_fixture_usage_copilot_2" => UsageState("copilot_2", 20, now, account: "further-login"),
            "sensor.agent_bridge_fixture_usage_copilot_3" => UsageState("copilot_3", 30, now, account: "unlisted-login"),
            "select.agent_bridge_fixture_new_resume" => ResumeState("Fix the  login page - web", "Write the report - docs", "Old work - repo", "Agency on OTHER-PC: Shared from there (abcdef12)"),
            _ => null,
        });
        using var reader = new DaemonHaReader("FIXTURE", inner, time: f.Time);
        var host = new FixedHost(new ShadowView
        {
            SeesAgentHaBridge = true,
            Usage = new Dictionary<string, ShadowUsage>
            {
                ["copilot"] = Reading(55, now),
                ["claude"] = Reading(10, now),
                ["codex"] = Reading(3, now),
                ["copilot_2"] = Reading(20, now, "Further-Login"),
            },
            // The host offers fewer (its own resume count): only its own are looked for. Another
            // machine's shared session on the daemon's list is not one it offers yet.
            Resumable = ["Fix the login page - web", "Something new - web"],
        });
        var ledger = new ShadowLedger(reports, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), f.Time);
        var runner = new ShadowRunner(new DaemonReader(Paths, f.Time), host, ledger, log.Add, new CachedDaemonHomeAssistant(Paths, reader, f.Time));

        var c = await runner.SampleAsync();
        for (var i = 0; i < 4; i++) { Advance(TimeSpan.FromSeconds(60)); await runner.SampleAsync(); }
        Assert.Empty(ledger.Today!.Mismatches);
        Advance(TimeSpan.FromSeconds(60));
        await runner.SampleAsync();

        Assert.True(c.UsageCompared && c.ResumableCompared);
        Assert.Empty(c.NotCompared);
        Assert.Equal(
            [
                (ShadowKinds.ResumableCount, null, "daemon=3 host=2"),
                (ShadowKinds.ResumableMissingOnDaemon, null, "1 of the host's 2"),
                (ShadowKinds.UsageAccount, "claude", "the account differs"),
                (ShadowKinds.UsageMissingOnDaemon, "codex", "the host has a reading and the daemon none"),
                (ShadowKinds.UsageMissingOnHost, "copilot_3", "the daemon has a reading and the host none"),
            ],
            c.Differences.Select(d => (d.Kind, d.Session, d.Detail)).OrderBy(x => x.Kind, StringComparer.Ordinal).ToList());
        // Read once a minute, not each sample; and not until five minutes is any of it a mismatch.
        // Each read is this machine's seven entities and, for Codex, which the host reads and this
        // daemon has no sensor for, the one machine its resume list names.
        Assert.Equal(6 * 8, inner.Seen.Count);
        Assert.Equal(6, inner.Seen.Count(r => r.Path == "/api/states/sensor.agent_bridge_other_pc_usage_codex"));
        // Copilot's 40 against 55 is one pair of readings, never confirmed by a second.
        Assert.Equal(5, ledger.Today!.Mismatches.Count);
        Assert.Equal((6, 6), (ledger.Today.UsageCompared, ledger.Today.ResumableCompared));
        var report = File.ReadAllText(Path.Combine(reports, ShadowLedger.FileName("2026-10-28")));
        foreach (var secret in new[] { Token, "FIXTURE-MQTT", Account, "someone-else", "further-login", "unlisted-login", "login page", "Something new", "fixture.invalid" })
        {
            Assert.DoesNotContain(secret, report);
            Assert.DoesNotContain(secret, string.Join("\n", log));
        }
    }

    [Fact]
    public async Task Home_Assistant_unreachable_or_refusing_leaves_usage_and_the_resume_list_not_compared_rather_than_mismatched()
    {
        f.Time.SetLocalTimeZone(TimeZoneInfo.Utc);
        Daemon(new JsonObject());
        Config();
        var inner = new FakeHomeAssistant(_ => null) { Fail = new HttpRequestException("connection refused to ha.fixture.invalid with " + Token) };
        using var reader = new DaemonHaReader("FIXTURE", inner, time: f.Time);
        var host = new FixedHost(new ShadowView
        {
            SeesAgentHaBridge = true,
            Usage = new Dictionary<string, ShadowUsage> { ["copilot"] = Reading(55, f.Time.GetUtcNow()) },
            Resumable = ["Fix the login page - web"],
        });
        var ledger = new ShadowLedger(reports, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), f.Time);
        var ha = new CachedDaemonHomeAssistant(Paths, reader, f.Time);
        var runner = new ShadowRunner(new DaemonReader(Paths, f.Time), host, ledger, log.Add, ha);

        var unreachable = await runner.SampleAsync();
        inner.Fail = null;
        inner.Status = System.Net.HttpStatusCode.Unauthorized;
        Advance(TimeSpan.FromSeconds(60));
        var refused = await runner.SampleAsync();
        File.Delete(Path.Combine(Paths.Home, "config.json"));
        Advance(TimeSpan.FromSeconds(60));
        var unconfigured = await runner.SampleAsync();

        foreach (var c in new[] { unreachable, refused, unconfigured })
        {
            Assert.True(c.Compared);
            Assert.Empty(c.Differences);
            Assert.False(c.UsageCompared || c.ResumableCompared);
        }
        Assert.Equal(["usage: Home Assistant is unreachable", "resumable: Home Assistant is unreachable"], unreachable.NotCompared);
        Assert.Equal("usage: Home Assistant refused agent-ha-bridge's token", refused.NotCompared[0]);
        Assert.Equal("usage: agent-ha-bridge's config could not be read", unconfigured.NotCompared[0]);
        var day = ledger.Today!;
        Assert.Equal(("clean", 0, 0, 1), (day.Verdict, day.UsageCompared, day.ResumableCompared, day.NotComparedWhy["usage: Home Assistant is unreachable"]));
        var report = File.ReadAllText(Path.Combine(reports, ShadowLedger.FileName("2026-10-28")));
        Assert.DoesNotContain(Token, report);
        Assert.DoesNotContain("fixture.invalid", report);
    }

    // Answers the headers, then never finishes the body.
    sealed class StallingHomeAssistant : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) });

        sealed class StallingStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    [Fact]
    public async Task A_home_assistant_that_stalls_partway_through_a_state_is_unreachable_once_the_deadline_passes()
    {
        Config();
        using var reader = new DaemonHaReader("FIXTURE", new StallingHomeAssistant(), TimeSpan.FromMilliseconds(200));

        var read = reader.ReadAsync(AgentHaBridgeConfig.Read(Paths).Access!);

        Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.Equal(("Home Assistant is unreachable", "Home Assistant is unreachable"), ((await read).UsageProblem, (await read).ResumableProblem));
    }

    // The host reads every 15 minutes while the daemon runs and the daemon every 2, so both are
    // fresh only now and then. A run keeps the last verdict between those moments: a lasting
    // difference outlasts the grace, and a rise the host has not read yet never counts.
    [Fact]
    public void A_run_counts_a_percentage_difference_once_two_fresh_pairs_of_readings_show_it_and_keeps_it_until_one_agrees()
    {
        f.Time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var start = f.Time.GetUtcNow();
        var ledger = new ShadowLedger(reports, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), f.Time);
        var memory = new ShadowMemory();
        ShadowView Side(double percent, DateTimeOffset at) => new()
        {
            SeesAgentHaBridge = true,
            Usage = new Dictionary<string, ShadowUsage> { ["copilot"] = Reading(percent, at) },
            Resumable = [],
        };
        DateTimeOffset Last(TimeSpan every) => start + TimeSpan.FromTicks((f.Time.GetUtcNow() - start).Ticks / every.Ticks * every.Ticks);
        void Minutes(int n, Func<DateTimeOffset, double> daemon, Func<DateTimeOffset, double> host)
        {
            for (var i = 0; i < n; i++)
            {
                var (d, h) = (Last(TimeSpan.FromMinutes(2)), Last(TimeSpan.FromMinutes(15)));
                ledger.Record(ShadowCompare.Compare(Side(daemon(d), d), Side(host(h), h), memory), new ShadowView(), new ShadowView());
                f.Time.Advance(TimeSpan.FromMinutes(1));
            }
        }

        // Usage rises at minute 2, and again at minute 16 just after a host read; each time the
        // daemon's next readings disagree with the host's old one until the host reads again.
        static double Used(TimeSpan since) => since >= TimeSpan.FromMinutes(16) ? 30 : since >= TimeSpan.FromMinutes(2) ? 20 : 10;
        Minutes(45, at => Used(at - start), at => Used(at - start));
        Assert.Empty(ledger.Today!.Mismatches);
        Assert.False(ledger.Today.Transient.ContainsKey(ShadowKinds.UsagePercent));

        // Now the host reads 15 points high for good.
        Minutes(60, _ => 40, _ => 55);
        var m = Assert.Single(ledger.Today.Mismatches);
        Assert.Equal((ShadowKinds.UsagePercent, "copilot", true), (m.Kind, m.Session, m.Open));
    }

    // Sync-DaemonUsage publishes a change at its next poll but an unchanged figure only every half
    // hour (two intervals with a dashboard.publication), so the sensor's measured_at can be half an
    // hour old while the daemon is still confirming it at every poll.
    [Fact]
    public async Task A_daemon_figure_published_a_while_ago_holds_until_its_last_poll_while_the_daemon_still_republishes_it()
    {
        var now = f.Time.GetUtcNow();
        var measured = now;
        var inner = new FakeHomeAssistant(e => e == "sensor.agent_bridge_fixture_usage_copilot" ? UsageState("copilot", 40, measured) : null);
        using var reader = new DaemonHaReader("FIXTURE", inner, time: f.Time);
        async Task<DateTimeOffset?> Confirmed(int minutesAgo)
        {
            measured = now - TimeSpan.FromMinutes(minutesAgo);
            return (await reader.ReadAsync(AgentHaBridgeConfig.Read(Paths).Access!)).Usage!["copilot"].ConfirmedAt;
        }

        // Asked at least every fifteen minutes, so what it published holds until sixteen ago (a
        // minute's slack), for as long as half an hour and a poll say it is still republishing.
        Config();
        Assert.Equal(now - TimeSpan.FromMinutes(16), await Confirmed(29));
        Assert.Null(await Confirmed(10));
        Assert.Null(await Confirmed(47));

        // Its own pace from its config, and a publication's shorter re-assert.
        Config(c =>
        {
            c["usage"] = new JsonObject { ["idleIntervalSeconds"] = "600" };
            c["dashboard"] = new JsonObject { ["publication"] = new JsonObject { ["writer"] = "FIXTURE" } };
        });
        Assert.Equal(now - TimeSpan.FromMinutes(11), await Confirmed(14));
        Assert.Null(await Confirmed(16));
    }

    // Seen on DEV-VM1 (#40): another machine read the account's usage, so this machine's daemon
    // stood down and its sensor kept an 11-hour-old figure. Every sample compared it all the same,
    // and no reading was ever near enough the host's to count.
    [Fact]
    public async Task A_daemon_figure_older_than_its_own_pace_is_not_compared_since_the_daemon_is_not_reading_it_here()
    {
        Config();
        var now = f.Time.GetUtcNow();
        var measured = now - TimeSpan.FromHours(11);
        var inner = new FakeHomeAssistant(e => e == "sensor.agent_bridge_fixture_usage_copilot" ? UsageState("copilot", 79, measured) : null);
        using var reader = new DaemonHaReader("FIXTURE", inner, time: f.Time);
        async Task<ShadowComparison> Compare()
        {
            var daemon = new ShadowView { Usage = (await reader.ReadAsync(AgentHaBridgeConfig.Read(Paths).Access!)).Usage, Resumable = [] };
            var host = new ShadowView { SeesAgentHaBridge = true, Usage = new Dictionary<string, ShadowUsage> { ["copilot"] = Reading(40, now) }, Resumable = [] };
            return ShadowCompare.Compare(daemon, host);
        }

        var lapsed = await Compare();
        Assert.False(lapsed.UsageCompared);
        Assert.Contains(ShadowCompare.UsageLapsed, lapsed.NotCompared);
        Assert.DoesNotContain(lapsed.Differences, d => d.Kind.StartsWith("usage.", StringComparison.Ordinal));
        Assert.Empty(lapsed.UsageFresh);
        // Nor is it a reading the host is missing.
        var daemonOnly = ShadowCompare.Compare(
            new ShadowView { Usage = (await reader.ReadAsync(AgentHaBridgeConfig.Read(Paths).Access!)).Usage, Resumable = [] },
            new ShadowView { SeesAgentHaBridge = true, Usage = new Dictionary<string, ShadowUsage>(), Resumable = [] });
        Assert.False(daemonOnly.UsageCompared);
        Assert.DoesNotContain(daemonOnly.Differences, d => d.Kind.StartsWith("usage.", StringComparison.Ordinal));

        // Within its pace (half an hour and a poll), the same figures are compared, and differ.
        measured = now - TimeSpan.FromMinutes(1);
        var current = await Compare();
        Assert.True(current.UsageCompared);
        Assert.DoesNotContain(ShadowCompare.UsageLapsed, current.NotCompared);
        Assert.Contains(current.Differences, d => d.Kind == ShadowKinds.UsagePercent);
        Assert.Equal(["copilot"], current.UsageFresh);
    }

    // Seen on DEV-VM1 (#40): this machine's daemon stood down for DSWETT-HOME, which read the
    // account; the account's figure is that machine's sensor, read by its one exact entity.
    [Fact]
    public async Task A_daemon_standing_down_is_compared_by_the_reading_of_the_machine_it_names_that_reads_the_account()
    {
        var now = f.Time.GetUtcNow();
        var readerAccount = Account;
        string? Ha(string entity) => entity switch
        {
            "sensor.agent_bridge_fixture_usage_copilot" => UsageState("copilot", 79, now - TimeSpan.FromHours(11)),
            "select.agent_bridge_fixture_new_resume" => ResumeState("Fix the login page - web", "Agency on OTHER-PC: Shared from there (abcdef12)"),
            "sensor.agent_bridge_other_pc_usage_copilot" => UsageState("copilot", 41, now - TimeSpan.FromMinutes(1), account: readerAccount, slug: "other_pc"),
            // Named by the daemon's dashboard writer, and reading the account too, but less lately.
            "sensor.agent_bridge_writer_pc_usage_copilot" => UsageState("copilot", 30, now - TimeSpan.FromMinutes(10), slug: "writer_pc"),
            _ => null,
        };
        var inner = new FakeHomeAssistant(Ha);
        using var reader = new DaemonHaReader("FIXTURE", inner, time: f.Time);
        Config(c => c["dashboard"] = new JsonObject { ["publication"] = new JsonObject { ["writer"] = "WRITER-PC" } });
        async Task<ShadowComparison> Compare()
        {
            var daemon = new ShadowView { Usage = (await reader.ReadAsync(AgentHaBridgeConfig.Read(Paths).Access!)).Usage, Resumable = [] };
            var host = new ShadowView { SeesAgentHaBridge = true, Usage = new Dictionary<string, ShadowUsage> { ["copilot"] = Reading(40, now) }, Resumable = [] };
            return ShadowCompare.Compare(daemon, host);
        }

        var compared = await Compare();
        Assert.True(compared.UsageCompared);
        Assert.Equal(["copilot"], compared.UsageFresh);
        Assert.DoesNotContain(compared.Differences, d => d.Kind.StartsWith("usage.", StringComparison.Ordinal));
        // Only GETs, of this machine's entities and each named machine's usage sensor for that agent.
        Assert.All(inner.Seen, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Equal(
            [.. reader.Entities.Select(e => "/api/states/" + e), .. new[] { "writer_pc", "other_pc" }.SelectMany(m => new[] { "copilot", "copilot_2", "copilot_3", "copilot_4" }.Select(s => $"/api/states/sensor.agent_bridge_{m}_usage_{s}"))],
            inner.Seen.Select(r => r.Path));
        // And allowed for that read only.
        Assert.Empty(reader.ReaderEntities);

        // Another account's reading is not this account's: the other one reading it is.
        readerAccount = "someone-else";
        f.Time.Advance(TimeSpan.FromMinutes(1));
        var other = (await reader.ReadAsync(AgentHaBridgeConfig.Read(Paths).Access!)).Usage!["copilot"];
        Assert.Equal((30d, false), (other.Windows["monthly"], other.Lapsed));
        // And with neither, the daemon's own lapsed figure stands.
        f.Time.Advance(TimeSpan.FromMinutes(15));
        Assert.True((await reader.ReadAsync(AgentHaBridgeConfig.Read(Paths).Access!)).Usage!["copilot"].Lapsed);
    }

    // Found in review (#94): the lapsed figure here and the reader's took turns in one history, so
    // an unchanged reader figure never held from one read to the next. And a Copilot login is the
    // same account however it is cased.
    [Fact]
    public async Task The_account_readers_unchanged_figure_holds_across_reads_and_its_login_matches_in_any_case()
    {
        var now = f.Time.GetUtcNow();
        var measured = now;
        var inner = new FakeHomeAssistant(entity => entity switch
        {
            "sensor.agent_bridge_fixture_usage_copilot" => UsageState("copilot", 79, now - TimeSpan.FromHours(11)),
            "select.agent_bridge_fixture_new_resume" => ResumeState("Agency on OTHER-PC: Shared from there (abcdef12)"),
            "sensor.agent_bridge_other_pc_usage_copilot" => UsageState("copilot", 41, measured, account: Account.ToUpperInvariant(), slug: "other_pc"),
            _ => null,
        });
        using var reader = new DaemonHaReader("FIXTURE", inner, time: f.Time);
        Config();
        Assert.Equal(41d, (await reader.ReadAsync(AgentHaBridgeConfig.Read(Paths).Access!)).Usage!["copilot"].Windows["monthly"]);
        // Republished unchanged four minutes later: it has held since it was first read.
        f.Time.Advance(TimeSpan.FromMinutes(4));
        measured = f.Time.GetUtcNow();
        Assert.Equal(now, (await reader.ReadAsync(AgentHaBridgeConfig.Read(Paths).Access!)).Usage!["copilot"].HeldSince);
    }

    // Found in review (#94): a further Copilot login is the reader's on whichever of its Copilot
    // sensors it carries it, and a reading stamped ahead of now is no current reader.
    [Fact]
    public async Task A_further_logins_reader_is_found_on_any_of_its_copilot_sensors_and_a_reading_from_the_future_is_not_one()
    {
        var now = f.Time.GetUtcNow();
        var ahead = now + TimeSpan.FromDays(1);
        var inner = new FakeHomeAssistant(entity => entity switch
        {
            "sensor.agent_bridge_fixture_usage_copilot_2" => UsageState("copilot", 50, now - TimeSpan.FromHours(11), account: "second-login", slug: "fixture"),
            "select.agent_bridge_fixture_new_resume" => ResumeState("Agency on OTHER-PC: Shared from there (abcdef12)", "Agency on AHEAD-PC: Shared too (12abcdef)"),
            "sensor.agent_bridge_other_pc_usage_copilot_3" => UsageState("copilot", 52, now - TimeSpan.FromMinutes(1), account: "second-login", slug: "other_pc"),
            "sensor.agent_bridge_ahead_pc_usage_copilot_2" => UsageState("copilot", 90, ahead, account: "second-login", slug: "ahead_pc"),
            _ => null,
        });
        using var reader = new DaemonHaReader("FIXTURE", inner, time: f.Time);
        Config();
        var second = (await reader.ReadAsync(AgentHaBridgeConfig.Read(Paths).Access!)).Usage!["copilot_2"];
        Assert.Equal((52d, false, (DateTimeOffset?)null), (second.Windows["monthly"], second.Lapsed, second.ConfirmedAt));
    }

    // Seen on DEV-VM1 (#40): the host was given a further Copilot login this machine's daemon was not,
    // so the daemon published no copilot_2 here, while DSWETT-HOME read that login. Its sensor, of the
    // host's login, is the daemon's figure; a login no named machine reads is still missing.
    [Fact]
    public async Task A_place_the_daemon_has_no_sensor_for_is_compared_by_the_named_machine_reading_the_hosts_login()
    {
        var now = f.Time.GetUtcNow();
        var inner = new FakeHomeAssistant(entity => entity switch
        {
            "sensor.agent_bridge_fixture_usage_copilot" => UsageState("copilot", 40, now),
            "select.agent_bridge_fixture_new_resume" => ResumeState("Agency on OTHER-PC: Shared from there (abcdef12)"),
            "sensor.agent_bridge_other_pc_usage_copilot_2" => UsageState("copilot", 52, now - TimeSpan.FromMinutes(1), account: "second-login", slug: "other_pc"),
            _ => null,
        });
        using var reader = new DaemonHaReader("FIXTURE", inner, time: f.Time);
        Config();
        async Task<ShadowComparison> Compare(string login)
        {
            var host = new Dictionary<string, ShadowUsage> { ["copilot"] = Reading(40, now), ["copilot_2"] = Reading(52, now, login) };
            var daemon = (await reader.ReadAsync(AgentHaBridgeConfig.Read(Paths).Access!, host)).Usage;
            return ShadowCompare.Compare(new ShadowView { Usage = daemon, Resumable = [] }, new ShadowView { SeesAgentHaBridge = true, Usage = host, Resumable = [] });
        }

        var compared = await Compare("SECOND-LOGIN");
        Assert.Equal(["copilot", "copilot_2"], compared.UsageFresh.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(compared.Differences, d => d.Kind.StartsWith("usage.", StringComparison.Ordinal));
        Assert.All(inner.Seen, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Empty(reader.ReaderEntities);

        f.Time.Advance(TimeSpan.FromMinutes(1));
        var unread = await Compare("third-login");
        Assert.Contains(unread.Differences, d => d.Kind == ShadowKinds.UsageMissingOnDaemon && d.Session == ShadowHash.Token("copilot_2"));

        // Without the host's reading, nobody else's sensor is read for a place this daemon has none.
        f.Time.Advance(TimeSpan.FromMinutes(1));
        inner.Seen.Clear();
        await reader.ReadAsync(AgentHaBridgeConfig.Read(Paths).Access!);
        Assert.Equal([.. reader.Entities.Select(e => "/api/states/" + e)], inner.Seen.Select(r => r.Path));
    }

    // Seen on DEV-VM1 (#40), 2026-10-30 22:40 PT: DSWETT-HOME, the only reader of the host's further
    // login, went 35 minutes without reading it. A lapsed figure is not compared, as this daemon's
    // own is not; it is not the daemon having no reading.
    [Fact]
    public async Task A_place_whose_login_reader_has_lapsed_is_not_compared_rather_than_missing_on_the_daemon()
    {
        var now = f.Time.GetUtcNow();
        var named = true;
        var inner = new FakeHomeAssistant(entity => entity switch
        {
            "sensor.agent_bridge_fixture_usage_copilot" => UsageState("copilot", 40, now),
            "select.agent_bridge_fixture_new_resume" => named ? ResumeState("Agency on OTHER-PC: Shared from there (abcdef12)") : ResumeState(),
            "sensor.agent_bridge_other_pc_usage_copilot_2" => UsageState("copilot", 52, now - TimeSpan.FromHours(11), account: "second-login", slug: "other_pc"),
            _ => null,
        });
        using var reader = new DaemonHaReader("FIXTURE", inner, time: f.Time);
        Config();
        var host = new Dictionary<string, ShadowUsage> { ["copilot"] = Reading(40, now), ["copilot_2"] = Reading(52, now, "second-login") };
        async Task<(IReadOnlyDictionary<string, ShadowUsage>? Daemon, ShadowComparison Compared)> Read()
        {
            var daemon = (await reader.ReadAsync(AgentHaBridgeConfig.Read(Paths).Access!, host)).Usage;
            return (daemon, ShadowCompare.Compare(new ShadowView { Usage = daemon, Resumable = [] }, new ShadowView { SeesAgentHaBridge = true, Usage = host, Resumable = [] }));
        }

        var (daemon, compared) = await Read();
        Assert.DoesNotContain(compared.Differences, d => d.Kind.StartsWith("usage.", StringComparison.Ordinal));
        Assert.Contains(ShadowCompare.UsageLapsed, compared.NotCompared);
        Assert.True(daemon!["copilot_2"].Lapsed);

        // Found in review (#136): the shared session that named the reader is gone by the next
        // read, and the reader found last is still looked at.
        named = false;
        f.Time.Advance(TimeSpan.FromMinutes(1));
        (daemon, compared) = await Read();
        Assert.DoesNotContain(compared.Differences, d => d.Kind.StartsWith("usage.", StringComparison.Ordinal));
        Assert.True(daemon!["copilot_2"].Lapsed);
    }

    // Found in review (#105): an empty sensor of this daemon's own cleared the history of the
    // reader's figure standing in for it, so that figure never held from one read to the next; and
    // with an old measured_at, its reader was looked for by its own account, here none.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_readers_unchanged_figure_holds_across_reads_where_the_daemons_own_sensor_is_empty(bool stale)
    {
        var now = f.Time.GetUtcNow();
        var measured = now;
        var inner = new FakeHomeAssistant(entity => entity switch
        {
            "sensor.agent_bridge_fixture_usage_copilot_2" => new JsonObject
            {
                ["entity_id"] = entity,
                ["state"] = "unknown",
                ["attributes"] = stale
                    ? new JsonObject { ["client"] = "copilot", ["machine_slug"] = "fixture", ["measured_at"] = (now - TimeSpan.FromHours(11)).ToString("o") }
                    : new JsonObject { ["client"] = "copilot", ["machine_slug"] = "fixture" },
            }.ToJsonString(),
            "select.agent_bridge_fixture_new_resume" => ResumeState("Agency on OTHER-PC: Shared from there (abcdef12)"),
            "sensor.agent_bridge_other_pc_usage_copilot_2" => UsageState("copilot", 52, measured, account: "second-login", slug: "other_pc"),
            _ => null,
        });
        using var reader = new DaemonHaReader("FIXTURE", inner, time: f.Time);
        Config();
        var host = new Dictionary<string, ShadowUsage> { ["copilot_2"] = Reading(52, now, "second-login") };
        Assert.Equal(52d, (await reader.ReadAsync(AgentHaBridgeConfig.Read(Paths).Access!, host)).Usage!["copilot_2"].Windows["monthly"]);
        f.Time.Advance(TimeSpan.FromMinutes(4));
        measured = f.Time.GetUtcNow();
        Assert.Equal(now, (await reader.ReadAsync(AgentHaBridgeConfig.Read(Paths).Access!, host)).Usage!["copilot_2"].HeldSince);
    }

    [Fact]
    public async Task A_named_machine_that_cannot_be_read_leaves_the_daemons_lapsed_figure_not_compared_and_the_rest_compared()
    {
        var now = f.Time.GetUtcNow();
        var inner = new FakeHomeAssistant(entity => entity switch
        {
            "sensor.agent_bridge_fixture_usage_copilot" => UsageState("copilot", 79, now - TimeSpan.FromHours(11)),
            "select.agent_bridge_fixture_new_resume" => ResumeState("Agency on OTHER-PC: Shared from there (abcdef12)"),
            "sensor.agent_bridge_other_pc_usage_copilot" => throw new HttpRequestException("unreachable"),
            _ => null,
        });
        using var reader = new DaemonHaReader("FIXTURE", inner, time: f.Time);
        Config();
        var reading = await reader.ReadAsync(AgentHaBridgeConfig.Read(Paths).Access!);
        Assert.NotNull(reading.Resumable);
        var compared = ShadowCompare.Compare(
            new ShadowView { Usage = reading.Usage, Resumable = reading.Resumable },
            new ShadowView { SeesAgentHaBridge = true, Usage = new Dictionary<string, ShadowUsage> { ["copilot"] = Reading(40, now) }, Resumable = [] });
        Assert.False(compared.UsageCompared);
        Assert.Contains(ShadowCompare.UsageLapsed, compared.NotCompared);
        Assert.True(compared.ResumableCompared);
    }

    // Found in review (#73): the daemon republishes an unchanged figure only every half hour, or
    // with a dashboard.publication at every poll with a new measured_at; either way a host reading
    // every fifteen minutes, seven after the daemon's polls, never landed within three minutes of
    // one, so a host fifteen points high for good was never seen. The daemon's figure now holds
    // from when it first stood to its last poll.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_run_compares_the_hosts_figure_with_the_daemons_unchanged_one_and_finds_a_lasting_difference(bool publication)
    {
        f.Time.SetLocalTimeZone(TimeZoneInfo.Utc);
        Daemon(new JsonObject());
        Config(c => { if (publication) { c["dashboard"] = new JsonObject { ["publication"] = new JsonObject { ["writer"] = "FIXTURE" } }; } });
        var start = f.Time.GetUtcNow();
        var quarter = TimeSpan.FromMinutes(15);
        var reassert = publication ? TimeSpan.FromMinutes(4) : TimeSpan.FromMinutes(30);
        (double Value, DateTimeOffset At)? published = null;
        (double Value, DateTimeOffset At)? host = null;
        var inner = new FakeHomeAssistant(e => e == "sensor.agent_bridge_fixture_usage_copilot" && published is { } p ? UsageState("copilot", p.Value, p.At) : null);
        using var reader = new DaemonHaReader("FIXTURE", inner, time: f.Time);
        var ledger = new ShadowLedger(reports, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), f.Time);
        var memory = new ShadowMemory();
        async Task Minutes(int n, Func<TimeSpan, double> used, double hostHigh)
        {
            for (var i = 0; i < n; i++)
            {
                var now = f.Time.GetUtcNow();
                var since = now - start;
                // The daemon asks every fifteen minutes (no session of the agent open), publishing a
                // change, or the same figure once the re-assert has passed.
                if (since.Ticks % quarter.Ticks == 0 && (published is not { } p || p.Value != used(since) || now - p.At >= reassert))
                {
                    published = (used(since), now);
                }
                // The host asks every fifteen minutes too, seven after the daemon.
                if ((since - TimeSpan.FromMinutes(7)).Ticks % quarter.Ticks == 0) { host = (used(since) + hostHigh, now); }
                var daemon = (await reader.ReadAsync(AgentHaBridgeConfig.Read(Paths).Access!)).Usage!;
                if (daemon.Count > 0 && host is { } h)
                {
                    var side = (IReadOnlyDictionary<string, ShadowUsage> usage) => new ShadowView { SeesAgentHaBridge = true, Usage = usage, Resumable = [] };
                    ledger.Record(ShadowCompare.Compare(side(daemon), side(new Dictionary<string, ShadowUsage> { ["copilot"] = Reading(h.Value, h.At) }), memory), new ShadowView(), new ShadowView());
                }
                f.Time.Advance(TimeSpan.FromMinutes(1));
            }
        }

        // Usage rising now and then, which each side sees at its own next read.
        await Minutes(120, since => since >= TimeSpan.FromMinutes(50) ? 30 : since >= TimeSpan.FromMinutes(20) ? 20 : 10, 0);
        Assert.Empty(ledger.Today!.Mismatches);
        Assert.False(ledger.Today.Transient.ContainsKey(ShadowKinds.UsagePercent));

        await Minutes(90, _ => 40, 15);
        var m = Assert.Single(ledger.Today.Mismatches);
        Assert.Equal((ShadowKinds.UsagePercent, "copilot", true), (m.Kind, m.Session, m.Open));
    }
    [Fact]
    public void Usage_percentages_are_compared_only_between_readings_taken_close_together_and_within_five_points()
    {
        var at = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        ShadowView Side(double percent, DateTimeOffset when) => new()
        {
            SeesAgentHaBridge = true,
            Usage = new Dictionary<string, ShadowUsage> { ["copilot"] = Reading(percent, when) },
            Resumable = [],
        };

        Assert.Empty(ShadowCompare.Compare(Side(40, at), Side(44, at)).Differences);
        Assert.Empty(ShadowCompare.Compare(Side(40, at), Side(90, at + TimeSpan.FromMinutes(13))).Differences);
        Assert.Equal(ShadowKinds.UsagePercent, Assert.Single(ShadowCompare.Compare(Side(40, at), Side(46, at + TimeSpan.FromMinutes(2))).Differences).Kind);
    }
}
