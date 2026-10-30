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

            await using (LocalServer.Start(LocalEndpoint.For(profile), LocalApi.Handler(new SessionService(source, machine), control, new FixedCallerGuard(new Caller(CallerKind.Agent, "the shadow comparison, run from inside an agent session"))))) // the strictest caller short of the app: reading is all the comparison asks
            {
                var seen = await view.ObserveAsync();

                Assert.True(seen.Available);
                Assert.Equal(("working", "copilot"), (seen.Sessions[Id].Status, seen.Sessions[Id].Agent));
                Assert.Equal((true, "0.1.0", true), (seen.SeesAgentHaBridge, seen.Version, seen.Dev));
                Assert.Equal(("the host does not offer usage", "the host did not answer launch.options"), (seen.UsageProblem, seen.ResumableProblem));
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
    void Config()
    {
        Directory.CreateDirectory(Paths.Home);
        File.WriteAllText(Path.Combine(Paths.Home, "config.json"), new JsonObject
        {
            ["homeAssistant"] = new JsonObject { ["baseUrl"] = "https://ha.fixture.invalid:8123", ["token"] = Token, ["agentTokenEnvVar"] = "AGENT_HA_AGENT_TOKEN" },
            ["mqtt"] = new JsonObject { ["password"] = "FIXTURE-MQTT" },
            ["newSession"] = new JsonObject { ["resumeCount"] = 12 },
        }.ToJsonString());
    }

    // Publish-CopilotMqttUsage's state for one agent, and the resume select's.
    static string UsageState(string client, double percent, DateTimeOffset at, string account = Account) => new JsonObject
    {
        ["entity_id"] = $"sensor.agent_bridge_fixture_usage_{client}",
        ["state"] = percent.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["attributes"] = new JsonObject
        {
            ["client"] = client,
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
        using var reader = new DaemonHaReader("FIXTURE", inner);
        using var http = new HttpClient(new ReadOnlyHomeAssistantHandler(reader.Entities, inner));
        var ha = new Uri("https://ha.fixture.invalid/api/states/");

        Assert.Equal(
            ["sensor.agent_bridge_fixture_usage_copilot", "sensor.agent_bridge_fixture_usage_claude", "sensor.agent_bridge_fixture_usage_codex", "select.agent_bridge_fixture_new_resume"],
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
            "select.agent_bridge_fixture_new_resume" => ResumeState("Fix the  login page - web", "Write the report - docs", "Old work - repo", "Agency on OTHER-PC: Shared from there (abcdef12)"),
            _ => null,
        });
        using var reader = new DaemonHaReader("FIXTURE", inner);
        var host = new FixedHost(new ShadowView
        {
            SeesAgentHaBridge = true,
            Usage = new Dictionary<string, ShadowUsage>
            {
                ["copilot"] = Reading(55, now),
                ["claude"] = Reading(10, now),
                ["codex"] = Reading(3, now),
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
            ],
            c.Differences.Select(d => (d.Kind, d.Session, d.Detail)).OrderBy(x => x.Kind, StringComparer.Ordinal).ToList());
        // Read once a minute, not each sample; and not until five minutes is any of it a mismatch.
        Assert.Equal(6 * 4, inner.Seen.Count);
        // Copilot's 40 against 55 is one pair of readings, never confirmed by a second.
        Assert.Equal(4, ledger.Today!.Mismatches.Count);
        Assert.Equal((6, 6), (ledger.Today.UsageCompared, ledger.Today.ResumableCompared));
        var report = File.ReadAllText(Path.Combine(reports, ShadowLedger.FileName("2026-10-28")));
        foreach (var secret in new[] { Token, "FIXTURE-MQTT", Account, "someone-else", "login page", "Something new", "fixture.invalid" })
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
        using var reader = new DaemonHaReader("FIXTURE", inner);
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
