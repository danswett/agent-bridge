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

            await using (LocalServer.Start(LocalEndpoint.For(profile), LocalApi.Handler(new SessionService(source, machine), control)))
            {
                var seen = await view.ObserveAsync();

                Assert.True(seen.Available);
                Assert.Equal(("working", "copilot"), (seen.Sessions[Id].Status, seen.Sessions[Id].Agent));
                Assert.Equal((true, "0.1.0", true), (seen.SeesAgentHaBridge, seen.Version, seen.Dev));
            }
        }
        finally { try { Directory.Delete(profile, recursive: true); } catch (IOException) { } }
    }
}
