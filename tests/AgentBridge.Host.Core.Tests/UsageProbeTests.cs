using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Host.Core.Usage;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// The agents asked for their own allowance (#200): Claude Code's /usage and Codex's app-server, each
// a scripted fake. Neither real agent is started, and no credential exists anywhere here.
public sealed class UsageProbeTests : IDisposable
{
    static readonly DateTimeOffset Now = new(2026, 10, 28, 20, 0, 0, TimeSpan.Zero);
    const string Email = "dev@example.invalid";
    const string Workspace = "ws-fixture-0001";

    readonly Fixture f = new();
    readonly FakeTimeProvider time = new(Now);
    readonly FakeLauncher launcher = new();
    readonly List<string> log = [];

    public void Dispose() => f.Dispose();

    string Probe => Path.Combine(f.Root, "probe");
    string AccountPath => Path.Combine(f.Root, "home", ".claude.json");

    UsageOptions Options(string? socket = null) => new()
    {
        CopilotHome = f.CopilotHome,
        ClaudeHome = f.ClaudeHome,
        ClaudeAccountPath = AccountPath,
        CodexHome = f.CodexHome,
        CodexControlSocket = socket ?? Path.Combine(f.Root, "no-daemon.sock"),
    };

    UsageProbes Probes(ICodexDaemon? daemon = null, FakeLeases? leases = null) => new()
    {
        Launcher = launcher,
        Processes = f.Processes,
        Find = agent => agent switch { AgentKind.Claude => "/fake/claude", AgentKind.Codex => "/fake/codex", _ => null },
        WorkingDirectory = Probe,
        Daemon = daemon ?? new FakeDaemon(null),
        ParentOf = _ => null,
        Lease = leases is null ? _ => new FakeLeases.Held(() => { }) : leases.Take,
    };

    void Log(string line) { lock (log) { log.Add(line); } }

    // ---- Claude ----

    const string ClaudeBody = """{"limits":[{"kind":"session","percent":12,"resets_at":"2026-10-28T22:00:00Z"},{"kind":"weekly_all","percent":41,"resets_at":"2026-11-02T00:00:00Z"}]}""";

    void ClaudeSaved(DateTimeOffset fetched, double session = 12)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AccountPath)!);
        var body = ClaudeBody.Replace("\"percent\":12", $"\"percent\":{session}", StringComparison.Ordinal);
        File.WriteAllText(AccountPath, "{\"oauthAccount\":{\"accountUuid\":\"AAAA-1111\",\"organizationUuid\":\"BBBB-2222\"},"
            + $"\"cachedUsageUtilization\":{{\"fetchedAtMs\":{fetched.ToUnixTimeMilliseconds()},\"accountUuid\":\"AAAA-1111\",\"utilization\":{body}}}}}");
    }

    const string SignedInClaudeAi = """{"loggedIn":true,"authMethod":"claude.ai","subscriptionType":"max"}""";
    const string SignedOutClaude = """{"loggedIn":false,"authMethod":"none"}""";

    // auth status answers at once; the /usage probe is held until the test ends it (or, with
    // refresh, saves a reading measured when it ran and exits 0 at once).
    void ClaudeScript(string auth = SignedInClaudeAi, int authExit = 0, bool refresh = false)
    {
        launcher.Next = c =>
        {
            var p = new FakeProbe(c, time);
            if (c.Arguments is ["auth", "status"]) { p.Answer(auth, authExit); return p; }
            if (refresh)
            {
                ClaudeSaved(time.GetUtcNow(), session: 20);
                p.End(0);
            }
            return p;
        };
    }

    IEnumerable<FakeProbe> UsageProbesStarted => launcher.Started.Where(p => p.Command.Arguments.Contains("/usage"));

    ClaudeUsageCollector Claude() => new(Options(), time, Probes(), Log);

    static async Task Settled(ClaudeUsageCollector c)
    {
        await Until(() => !c.Probing || c.Cycle.IsCompleted);
        await c.Cycle;
    }

    [Fact]
    public async Task A_stale_Claude_reading_starts_exactly_one_probe_and_a_pass_while_it_runs_starts_no_second()
    {
        ClaudeSaved(Now.AddMinutes(-20));
        ClaudeScript();
        var claude = Claude();

        var first = await claude.CollectAsync();
        await Until(() => UsageProbesStarted.Any());
        time.Advance(TimeSpan.FromMinutes(1));
        var second = await claude.CollectAsync();

        var probe = Assert.Single(UsageProbesStarted);
        Assert.Equal(["-p", "/usage", "--safe-mode", "--no-session-persistence", "--output-format", "json"], probe.Command.Arguments);
        Assert.Equal(("/fake/claude", Probe), (probe.Command.Program, probe.Command.WorkingDirectory));
        Assert.False(probe.Command.Stdio);
        // This profile is not the default one, so the probe is told where it is.
        Assert.Equal(f.ClaudeHome, probe.Command.Environment["CLAUDE_CONFIG_DIR"]);
        // The last reading stands while the probe runs.
        Assert.Equal(12, first!.Windows[0].Percent);
        Assert.Equal(12, second!.Windows[0].Percent);

        // Claude Code saves what it read, and the next read shows it.
        ClaudeSaved(time.GetUtcNow(), session: 20);
        probe.End(0);
        await Settled(claude);
        Assert.Equal(DateTimeOffset.MinValue, claude.WakeAt);
        var fresh = await claude.CollectAsync();
        Assert.Equal((20, UsageSource.Cache, "max"), (fresh!.Windows[0].Percent, fresh.Source, fresh.Plan));
        Assert.Single(UsageProbesStarted);
    }

    [Fact]
    public async Task A_fresh_Claude_reading_starts_no_probe_and_not_even_auth_status()
    {
        ClaudeSaved(Now.AddMinutes(-14));
        ClaudeScript();
        var claude = Claude();

        Assert.NotNull(await claude.CollectAsync());

        Assert.Empty(launcher.Started);
        // It says when one will be due: the moment the reading is a quarter of an hour old.
        Assert.Equal(Now.AddMinutes(1), claude.WakeAt);
    }

    [Fact]
    public async Task A_failed_or_rate_limited_probe_backs_off_15_30_60_then_120_minutes_and_the_last_reading_stands()
    {
        ClaudeSaved(Now.AddHours(-3));
        // Exits 0 without reading afresh: what /usage does when the endpoint is rate limited.
        launcher.Next = c =>
        {
            var p = new FakeProbe(c, time);
            if (c.Arguments is ["auth", "status"]) { p.Answer(SignedInClaudeAi, 0); }
            else { p.End(0); }
            return p;
        };
        var claude = Claude();
        var starts = new List<TimeSpan>();
        async Task PassAt(TimeSpan at)
        {
            time.SetUtcNow(Now + at);
            var before = UsageProbesStarted.Count();
            var usage = await claude.CollectAsync();
            await Settled(claude);
            if (UsageProbesStarted.Count() > before) { starts.Add(at); }
            Assert.Equal((12, null), (usage!.Windows[0].Percent, usage.Error));
        }

        foreach (var minutes in new[] { 0, 14, 15, 44, 45, 104, 105, 224, 225, 344, 345 }) { await PassAt(TimeSpan.FromMinutes(minutes)); }

        Assert.Equal(new[] { 0, 15, 45, 105, 225, 345 }.Select(m => TimeSpan.FromMinutes(m)), starts);
        Assert.Contains(log, l => l.Contains("did not read the allowance afresh", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_probe_is_not_killed_before_or_after_its_timeout_and_host_shutdown_leaves_it_running()
    {
        ClaudeSaved(Now.AddMinutes(-20));
        ClaudeScript();
        var claude = Claude();
        await claude.CollectAsync();
        await Until(() => UsageProbesStarted.Any());
        var probe = UsageProbesStarted.Single();

        await AdvanceUntil(time, () => false, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(119));
        Assert.DoesNotContain(log, l => l.Contains("left running", StringComparison.Ordinal));
        await AdvanceUntil(time, () => log.Any(l => l.Contains("left running", StringComparison.Ordinal)), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        Assert.True(probe.Running);
        Assert.False(probe.Disposed);

        // The host stops: the monitor goes, and the probe is let be.
        await using (var monitor = new UsageMonitor([claude], new FakeSessions(), Options(), time, log: Log)) { monitor.Start(); }
        Assert.True(probe.Running);

        // And while it runs, no second one starts, however long it has been.
        time.Advance(TimeSpan.FromHours(3));
        await claude.CollectAsync();
        Assert.Single(UsageProbesStarted);

        probe.End(0);
        await Until(() => !claude.Probing);
        Assert.True(probe.Disposed);
    }

    [Fact]
    public async Task A_signed_out_auth_status_gives_the_signed_out_failure_and_stops_the_probes_until_it_signs_in()
    {
        ClaudeSaved(Now.AddHours(-2));
        ClaudeScript(auth: SignedOutClaude, authExit: 1);
        var claude = Claude();

        await claude.CollectAsync();
        await Settled(claude);
        var usage = await claude.CollectAsync();

        Assert.Equal("Claude Code is signed out on this machine", usage!.Error);
        Assert.Equal(12, usage.Windows[0].Percent);
        Assert.Empty(UsageProbesStarted);
        Assert.Contains(log, l => l.Contains("signed out", StringComparison.Ordinal));

        // Not asked again within the hour, and never probed while it says so.
        time.Advance(TimeSpan.FromMinutes(30));
        await claude.CollectAsync();
        await Settled(claude);
        Assert.Single(launcher.Started);

        // Signed in again an hour later: the next look finds it, and the probe runs.
        ClaudeScript(refresh: true);
        time.Advance(TimeSpan.FromMinutes(31));
        await claude.CollectAsync();
        await Settled(claude);
        Assert.Single(UsageProbesStarted);
        usage = await claude.CollectAsync();
        Assert.Equal((20, null), (usage!.Windows[0].Percent, usage.Error));
    }

    [Fact]
    public async Task A_Claude_signed_in_with_an_api_key_is_never_probed()
    {
        ClaudeSaved(Now.AddHours(-2));
        ClaudeScript(auth: """{"loggedIn":true,"authMethod":"api_key"}""");
        var claude = Claude();

        await claude.CollectAsync();
        await Settled(claude);

        Assert.Empty(UsageProbesStarted);
        Assert.Null((await claude.CollectAsync())!.Error);
    }

    [Fact]
    public async Task A_Claude_probe_already_running_on_the_machine_is_not_joined_by_a_second()
    {
        ClaudeSaved(Now.AddMinutes(-20));
        ClaudeScript(refresh: true);
        f.Processes.Run(AgentKind.Claude, 4242, "claude.exe -p /usage --safe-mode --no-session-persistence --output-format json");
        var claude = Claude();

        await claude.CollectAsync();
        await Settled(claude);

        Assert.Empty(UsageProbesStarted);
    }

    [Fact]
    public async Task No_Claude_probe_starts_while_the_machines_processes_cannot_be_listed()
    {
        ClaudeSaved(Now.AddMinutes(-20));
        ClaudeScript(refresh: true);
        f.Processes.Unreadable(AgentKind.Claude);
        var claude = Claude();

        await claude.CollectAsync();
        await Settled(claude);

        Assert.Empty(UsageProbesStarted);
        Assert.Contains(log, l => l.Contains("could not all be read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_Claude_probe_starts_beside_a_Claude_process_whose_command_line_cannot_be_read()
    {
        ClaudeSaved(Now.AddMinutes(-20));
        ClaudeScript(refresh: true);
        f.Processes.Run(AgentKind.Claude, 4300, commandLine: null);
        var claude = Claude();

        await claude.CollectAsync();
        await Settled(claude);

        Assert.Empty(UsageProbesStarted);
    }

    [Fact]
    public async Task No_Claude_probe_starts_beside_a_node_runtime_whose_command_line_cannot_be_read()
    {
        ClaudeSaved(Now.AddMinutes(-20));
        ClaudeScript(refresh: true);
        // An npm install's probe left by a host that has since stopped, read this time as a bare node.
        f.Processes.Run(AgentKind.Claude, 4400, "node C:/npm/node_modules/@anthropic-ai/claude-code/cli.js -p /usage --safe-mode --no-session-persistence --output-format json");
        f.Processes.Unidentify(AgentKind.Claude, 4400);
        var claude = Claude();

        await claude.CollectAsync();
        await Settled(claude);

        Assert.Empty(UsageProbesStarted);
    }

    [Fact]
    public async Task Usage_polling_set_to_two_minutes_still_has_an_agent_asked_only_for_a_reading_15_minutes_old()
    {
        ClaudeScript(refresh: true);
        var options = new UsageOptions
        {
            CopilotHome = f.CopilotHome, ClaudeHome = f.ClaudeHome, ClaudeAccountPath = AccountPath, CodexHome = f.CodexHome,
            Interval = TimeSpan.FromMinutes(2), IdleInterval = TimeSpan.FromMinutes(2),
        };

        ClaudeSaved(Now.AddMinutes(-3));
        var claude = new ClaudeUsageCollector(options, time, Probes(), Log);
        await claude.CollectAsync();
        await Settled(claude);
        Assert.Empty(UsageProbesStarted);

        ClaudeSaved(Now.AddMinutes(-16));
        await claude.CollectAsync();
        await Settled(claude);
        Assert.Single(UsageProbesStarted);
    }

    [Fact]
    public async Task A_host_serving_another_profile_starts_no_Claude_probe_while_the_first_ones_runs_even_past_its_timeout()
    {
        ClaudeSaved(Now.AddMinutes(-20));
        ClaudeScript();
        var leases = new FakeLeases();
        var first = new ClaudeUsageCollector(Options(), time, Probes(leases: leases), Log);
        var second = new ClaudeUsageCollector(Options(), time, Probes(leases: leases), Log);

        await first.CollectAsync();
        await Until(() => UsageProbesStarted.Any());
        await AdvanceUntil(time, () => first.Cycle.IsCompleted, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(3));
        Assert.True(first.Cycle.IsCompleted);
        await second.CollectAsync();
        await Settled(second);

        Assert.Single(UsageProbesStarted);
        Assert.Contains(log, l => l.Contains("another host here", StringComparison.Ordinal));
        Assert.Equal(1, leases.Count);

        // Its lease goes when the probe does, not when the host gave up waiting for it.
        UsageProbesStarted.Single().End(0);
        await Until(() => leases.Count == 0);
    }

    // ---- Codex ----

    static object Window(int percent, int minutes, DateTimeOffset resets) => new { usedPercent = percent, windowDurationMins = minutes, resetsAt = resets.ToUnixTimeSeconds() };

    static object Limits(bool? allowed = true, string? workspace = Workspace) => new
    {
        ordinaryUsageAllowed = allowed,
        accountId = workspace,
        rateLimits = new { limitId = "codex", primary = Window(5, 300, Now.AddHours(1)), secondary = Window(9, 10080, Now.AddDays(6)), planType = "plus" },
        rateLimitsByLimitId = new Dictionary<string, object>
        {
            ["codex"] = new { limitId = "codex", primary = Window(23, 300, Now.AddHours(2)), secondary = Window(61, 10080, Now.AddDays(3)), planType = "plus" },
        },
    };

    static object Account(string? email = Email) => new { account = new { type = "chatgpt", email, planType = "plus" }, requiresOpenaiAuth = true };

    FakeAppServer AppServer(Action<FakeAppServer>? script = null)
    {
        var server = new FakeAppServer(time);
        script?.Invoke(server);
        launcher.Next = c =>
        {
            var p = new FakeProbe(c, time);
            _ = server.ServeAsync(p);
            return p;
        };
        return server;
    }

    CodexUsageProbe CodexProbe(FakeDaemon? daemon = null, string? socket = null) => new(Options(socket), Probes(daemon), time, Log);

    void CodexUsed() => Directory.CreateDirectory(Path.Combine(f.CodexHome, "sessions"));

    // Waits through the grace period, so the probe is done with the process.
    async Task<CodexProbeOutcome> RunToEnd(CodexProbeRun run)
    {
        var outcome = await run.Reported;
        await AdvanceUntil(time, () => run.Finished.IsCompleted, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(11));
        await run.Finished;
        return outcome;
    }

    [Fact]
    public async Task A_scripted_app_server_gives_both_windows_with_labels_resets_the_plan_and_a_key()
    {
        AppServer();

        var outcome = await RunToEnd(CodexProbe().Start());

        Assert.Equal(CodexProbeKind.Reading, outcome.Kind);
        var usage = outcome.Reading!;
        Assert.Equal((UsageSource.Api, "plus", Now), (usage.Source, usage.Plan, usage.MeasuredAt));
        Assert.Equal([("primary", "5h limit", 23.0), ("secondary", "Weekly", 61.0)], usage.Windows.Select(w => (w.Key, w.Label, w.Percent)));
        Assert.Equal([Now.AddHours(2), Now.AddDays(3)], usage.Windows.Select(w => w.ResetsAt!.Value));
        Assert.All(usage.Windows, w => Assert.Null(w.StartsAt));
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"codex:{Email}:{Workspace}")))[..12];
        Assert.Equal(expected, usage.AccountKey);
        // Started as the app-server over stdio, in the host's own folder.
        var probe = Assert.Single(launcher.Started);
        Assert.Equal(["app-server"], probe.Command.Arguments);
        Assert.Equal((true, Probe), (probe.Command.Stdio, probe.Command.WorkingDirectory));
        Assert.Equal(f.CodexHome, probe.Command.Environment["CODEX_HOME"]);
    }

    [Fact]
    public async Task The_Codex_probe_sends_only_the_account_reads_and_never_a_thread_or_turn()
    {
        var server = AppServer();

        await RunToEnd(CodexProbe().Start());

        Assert.Equal(["initialize", "initialized", "account/read", "account/rateLimits/read", "account/read"], server.Received.Select(r => r.Method));
        Assert.DoesNotContain(server.Received, r => r.Method.StartsWith("thread/", StringComparison.Ordinal) || r.Method.StartsWith("turn/", StringComparison.Ordinal));
        var limits = server.Received.Single(r => r.Method == "account/rateLimits/read").Params;
        Assert.Equal("""{"excludeResetCreditDetails":true}""", limits.GetRawText());
        Assert.All(server.Received.Where(r => r.Method == "account/read"), r => Assert.Equal("""{"refreshToken":false}""", r.Params.GetRawText()));
        Assert.Equal("agent_bridge", server.Received[0].Params.GetProperty("clientInfo").GetProperty("name").GetString());
    }

    [Fact]
    public void Two_users_of_one_workspace_get_different_keys_and_an_answer_missing_any_part_gets_none()
    {
        static JsonElement? Snapshot(bool? allowed, string? workspace) => JsonSerializer.SerializeToElement(Limits(allowed, workspace));

        var mine = CodexUsageProbe.Key(Email, Email, Snapshot(true, Workspace));
        var theirs = CodexUsageProbe.Key("other@example.invalid", "other@example.invalid", Snapshot(true, Workspace));

        Assert.NotNull(mine);
        Assert.NotNull(theirs);
        Assert.NotEqual(mine, theirs);
        Assert.Null(CodexUsageProbe.Key(Email, Email, Snapshot(true, null)));
        Assert.Null(CodexUsageProbe.Key(null, null, Snapshot(true, Workspace)));
        Assert.Null(CodexUsageProbe.Key(Email, Email, Snapshot(null, Workspace)));
    }

    [Fact]
    public async Task A_login_switch_during_the_probe_withholds_the_key()
    {
        // The two account/reads name different people.
        AppServer(s => s.Answers["account/read"] = n => FakeAppServer.Result(Account(n == 1 ? Email : "other@example.invalid")));
        var switched = await RunToEnd(CodexProbe().Start());
        Assert.Equal(CodexProbeKind.Reading, switched.Kind);
        Assert.Null(switched.Reading!.AccountKey);

        // The handler could not match the snapshot to the active login.
        launcher.Started.Clear();
        AppServer(s => s.Answers["account/rateLimits/read"] = _ => FakeAppServer.Result(Limits(allowed: null)));
        var unmatched = await RunToEnd(CodexProbe().Start());
        Assert.Null(unmatched.Reading!.AccountKey);
        Assert.Equal(2, unmatched.Reading.Windows.Count);
    }

    [Fact]
    public async Task An_unchanged_login_keeps_its_key_when_Codex_announces_account_updated_during_the_probe()
    {
        var server = AppServer(s => s.Announce = true);

        var outcome = await RunToEnd(CodexProbe().Start());

        Assert.True(server.Announced > 0);
        Assert.NotNull(outcome.Reading!.AccountKey);
    }

    [Fact]
    public async Task A_switch_and_back_withholds_the_key_when_another_Codex_process_runs_during_the_probe()
    {
        AppServer();
        var looks = 0;
        // Both account/reads name the same person, but a Codex process appears between them.
        f.Processes.Queried = agent =>
        {
            if (agent == AgentKind.Codex && ++looks == 3) { f.Processes.Run(AgentKind.Codex, 9100, "codex.exe login"); }
        };

        var outcome = await RunToEnd(CodexProbe().Start());

        Assert.Equal(CodexProbeKind.Reading, outcome.Kind);
        Assert.Null(outcome.Reading!.AccountKey);
    }

    [Fact]
    public async Task A_switch_and_back_withholds_the_key_when_an_unreadable_runtime_appears_during_the_probe()
    {
        AppServer();
        var looks = 0;
        f.Processes.Queried = agent =>
        {
            // Added as the watch starts looking, and seen as unreadable by its later looks.
            if (agent == AgentKind.Codex && ++looks == 2)
            {
                f.Processes.Run(AgentKind.Codex, 9101, "node codex.js login");
                f.Processes.Unidentify(AgentKind.Codex, 9101);
            }
        };

        var outcome = await RunToEnd(CodexProbe().Start());

        Assert.Equal(CodexProbeKind.Reading, outcome.Kind);
        Assert.Null(outcome.Reading!.AccountKey);
    }

    [Fact]
    public async Task No_standalone_app_server_starts_beside_a_runtime_whose_command_line_cannot_be_read()
    {
        AppServer();
        f.Processes.Run(AgentKind.Codex, 9102, "node codex.js");
        f.Processes.Unidentify(AgentKind.Codex, 9102);

        var outcome = await CodexProbe().Start().Reported.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(CodexProbeKind.Skipped, outcome.Kind);
        Assert.Empty(launcher.Started);
    }

    [Fact]
    public async Task The_probe_never_closes_standard_input_before_the_answers_and_the_grace_period()
    {
        var release = new TaskCompletionSource();
        var server = AppServer(s => s.Before["account/rateLimits/read"] = release.Task);
        var run = CodexProbe().Start();
        await Until(() => server.Received.Any(r => r.Method == "account/rateLimits/read"));

        // No answer within the reporting timeout: nothing is reported, and the app-server is left be.
        var outcome = await AdvanceUntilDone(run.Reported, TimeSpan.FromSeconds(61));
        Assert.Equal(CodexProbeKind.Failed, outcome.Kind);
        var probe = launcher.Started.Single();
        Assert.Null(probe.InputClosedAt);
        Assert.True(probe.Running);

        // It answers late, with its input still open and while it still runs.
        time.Advance(TimeSpan.FromSeconds(60));
        release.SetResult();
        await Until(() => server.Received.Count(r => r.Method == "account/read") == 2 && server.Answered >= 4);
        Assert.Null(probe.InputClosedAt);
        Assert.True(probe.Running);
        var answeredAt = server.LastAnsweredAt;

        await AdvanceUntil(time, () => run.Finished.IsCompleted, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2));
        Assert.True(probe.InputClosedAt - answeredAt >= TimeSpan.FromSeconds(30), $"input closed {probe.InputClosedAt - answeredAt} after the last answer");
        // It exits by itself on end of input; nothing ended it.
        Assert.False(probe.Killed);
    }

    [Fact]
    public async Task A_hung_app_server_is_given_up_on_only_at_the_ten_minute_ceiling_and_the_transcript_reading_stays()
    {
        CodexUsed();
        var transcript = f.CodexTranscript("t");
        Fixture.Append(transcript, Codex.Event(new { type = "token_count", rate_limits = new { primary = new { used_percent = 33.0, window_minutes = 300 } } }, 1));
        var never = new TaskCompletionSource();
        var server = AppServer(s => s.Before["account/rateLimits/read"] = never.Task);
        var codex = new CodexUsageCollector(Options(), time, Probes(), Log);

        var first = await codex.CollectAsync();
        await Until(() => server.Received.Any(r => r.Method == "account/rateLimits/read"));
        var probe = launcher.Started.Single();
        await AdvanceUntil(time, () => false, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(9.5));
        Assert.Null(probe.InputClosedAt);
        await AdvanceUntil(time, () => codex.Running.IsCompleted, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1));

        Assert.NotNull(probe.InputClosedAt);
        Assert.Contains(log, l => l.Contains("given up on as hung", StringComparison.Ordinal));
        var after = await codex.CollectAsync();
        Assert.Equal((UsageSource.Transcript, 33.0, null), (after!.Source, after.Windows[0].Percent, after.Error));
        Assert.Equal(first!.MeasuredAt, after.MeasuredAt);
    }

    [Fact]
    public async Task A_Codex_that_refreshes_for_40_s_before_initialize_answers_still_gives_a_reading()
    {
        // Two 20 s cloud-config attempts at start-up, each refreshing, before initialize answers (E13).
        var startup = new TaskCompletionSource();
        var server = AppServer(s => s.Before["initialize"] = startup.Task);
        var run = CodexProbe().Start();
        await Until(() => server.Received.Any(r => r.Method == "initialize"));

        await AdvanceUntil(time, () => false, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(40));
        Assert.False(run.Reported.IsCompleted);
        Assert.Null(launcher.Started.Single().InputClosedAt);
        startup.SetResult();

        Assert.Equal(CodexProbeKind.Reading, (await RunToEnd(run)).Kind);
    }

    [Fact]
    public async Task Stopping_the_host_waits_for_a_Codex_probes_answers_and_its_grace_before_its_input_goes_with_it()
    {
        CodexUsed();
        var release = new TaskCompletionSource();
        var server = AppServer(s => s.Before["account/rateLimits/read"] = release.Task);
        var codex = new CodexUsageCollector(Options(), time, Probes(), Log);
        var monitor = new UsageMonitor([codex], new FakeSessions(), Options(), time, log: Log);
        await monitor.PassAsync();
        await Until(() => server.Received.Any(r => r.Method == "account/rateLimits/read"));

        var stopping = monitor.DisposeAsync().AsTask();
        await Task.Delay(100);
        Assert.False(stopping.IsCompleted);
        Assert.Contains(log, l => l.Contains("waiting up to 90 s for Codex's app-server", StringComparison.Ordinal));

        release.SetResult();
        await Until(() => server.Answered == 4);
        var answeredAt = server.LastAnsweredAt;
        await AdvanceUntil(time, () => false, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(25));
        // Answered, but still within the grace: the host has not gone, and the input is open.
        Assert.False(stopping.IsCompleted);
        Assert.Null(launcher.Started.Single().InputClosedAt);
        await AdvanceUntil(time, () => stopping.IsCompleted, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        var probe = launcher.Started.Single();
        Assert.True(probe.InputClosedAt - answeredAt >= TimeSpan.FromSeconds(30), $"input closed {probe.InputClosedAt - answeredAt} after the last answer");
        Assert.False(probe.Killed);
    }

    [Fact]
    public async Task Stopping_the_host_after_the_reporting_timeout_still_waits_for_a_late_Codex_probes_answers()
    {
        CodexUsed();
        var release = new TaskCompletionSource();
        var server = AppServer(s => s.Before["account/rateLimits/read"] = release.Task);
        var codex = new CodexUsageCollector(Options(), time, Probes(), Log);
        var monitor = new UsageMonitor([codex], new FakeSessions(), Options(), time, log: Log);
        await monitor.PassAsync();
        await Until(() => server.Received.Any(r => r.Method == "account/rateLimits/read"));
        await AdvanceUntil(time, () => log.Any(l => l.Contains("did not answer within", StringComparison.Ordinal)), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(65));
        Assert.Contains(log, l => l.Contains("did not answer within", StringComparison.Ordinal));

        var stopping = monitor.DisposeAsync().AsTask();
        await Task.Delay(100);
        Assert.False(stopping.IsCompleted);

        release.SetResult();
        await Until(() => server.Answered == 4);
        var answeredAt = server.LastAnsweredAt;
        Assert.False(stopping.IsCompleted);
        await AdvanceUntil(time, () => stopping.IsCompleted, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(40));
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(launcher.Started.Single().InputClosedAt - answeredAt >= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task No_standalone_Codex_probe_starts_while_the_host_is_held_for_a_stop_or_an_update()
    {
        CodexUsed();
        AppServer();
        var codex = new CodexUsageCollector(Options(), time, Probes(), Log);
        var monitor = new UsageMonitor([codex], new FakeSessions(), Options(), time, log: Log);

        Assert.False(monitor.HoldProbes(TimeSpan.FromMinutes(15)));
        await monitor.PassAsync();
        await Task.Delay(100);
        Assert.Empty(launcher.Started);

        // An update that never came holds nothing for good.
        time.Advance(TimeSpan.FromMinutes(16));
        await monitor.PassAsync();
        await Until(() => launcher.Started.Count == 1);
    }

    [Fact]
    public async Task Holding_the_probes_says_a_Codex_probe_is_busy_until_its_answers_and_its_grace_are_done()
    {
        CodexUsed();
        var release = new TaskCompletionSource();
        var server = AppServer(s => s.Before["account/rateLimits/read"] = release.Task);
        var codex = new CodexUsageCollector(Options(), time, Probes(), Log);
        var monitor = new UsageMonitor([codex], new FakeSessions(), Options(), time, log: Log);
        await monitor.PassAsync();
        await Until(() => server.Received.Any(r => r.Method == "account/rateLimits/read"));

        Assert.True(monitor.HoldProbes(TimeSpan.FromMinutes(15)));
        release.SetResult();
        await Until(() => server.Answered == 4);
        await AdvanceUntil(time, () => false, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(25));
        Assert.True(monitor.HoldProbes(TimeSpan.FromMinutes(15)));

        await AdvanceUntil(time, () => !monitor.HoldProbes(TimeSpan.FromMinutes(15)), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        Assert.False(monitor.HoldProbes(TimeSpan.FromMinutes(15)));
        Assert.NotNull(launcher.Started.Single().InputClosedAt);
    }

    [Fact]
    public async Task A_host_serving_another_profile_with_the_same_Codex_home_starts_no_app_server_until_the_first_ones_has_exited()
    {
        var leases = new FakeLeases();
        var release = new TaskCompletionSource();
        var server = AppServer(s => s.Before["account/rateLimits/read"] = release.Task);
        var first = new CodexUsageProbe(Options(), Probes(leases: leases), time, Log).Start();
        await Until(() => server.Received.Any(r => r.Method == "account/rateLimits/read"));

        var second = await new CodexUsageProbe(Options(), Probes(leases: leases), time, Log).Start().Reported.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(CodexProbeKind.Skipped, second.Kind);
        Assert.Single(launcher.Started);

        // The first answers, waits out its grace and exits; only then may another start.
        release.SetResult();
        await RunToEnd(first);
        await Until(() => leases.Count == 0);
        Assert.Equal(CodexProbeKind.Reading, (await RunToEnd(new CodexUsageProbe(Options(), Probes(leases: leases), time, Log).Start())).Kind);
        Assert.Equal(2, launcher.Started.Count);
    }

    [Fact]
    public async Task A_slow_sign_in_service_on_the_second_account_read_keeps_the_reading_without_a_key_and_backs_off()
    {
        AppServer(s => s.Answers["account/read"] = n => n == 1 ? FakeAppServer.Result(Account()) : FakeAppServer.Error("workspace routing discovery timed out"));

        var outcome = await RunToEnd(CodexProbe().Start());

        Assert.Equal(CodexProbeKind.Failed, outcome.Kind);
        Assert.Equal(23.0, outcome.Reading!.Windows[0].Percent);
        Assert.Null(outcome.Reading.AccountKey);
    }

    [Fact]
    public async Task A_failed_or_hung_conversation_leaves_no_process_watch_running_after_the_probe()
    {
        var looks = 0;
        f.Processes.Queried = agent => { if (agent == AgentKind.Codex) { Interlocked.Increment(ref looks); } };

        // codex app-server ends while it is being asked who is signed in.
        var gate = new TaskCompletionSource();
        var server = AppServer(s => s.Before["account/read"] = gate.Task);
        var failed = CodexProbe().Start();
        await Until(() => server.Received.Any(r => r.Method == "account/read"));
        launcher.Started.Single().End(1);
        Assert.Equal(CodexProbeKind.Failed, (await RunToEnd(failed)).Kind);
        await Watched();

        // codex app-server never answers the usage read, and is given up on at the ceiling.
        launcher.Started.Clear();
        server = AppServer(s => s.Before["account/rateLimits/read"] = new TaskCompletionSource().Task);
        var hung = CodexProbe().Start();
        await Until(() => server.Received.Any(r => r.Method == "account/rateLimits/read"));
        await AdvanceUntilDone(hung.Reported, TimeSpan.FromSeconds(61));
        await RunToEnd(hung);
        Assert.NotNull(launcher.Started.Single().InputClosedAt);
        await Watched();

        async Task Watched()
        {
            var after = Volatile.Read(ref looks);
            await AdvanceUntil(time, () => false, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
            Assert.Equal(after, Volatile.Read(ref looks));
        }
    }

    [Fact]
    public async Task A_sign_out_followed_by_an_api_key_login_clears_the_sign_out_and_leaves_the_transcripts()
    {
        CodexUsed();
        Fixture.Append(f.CodexTranscript("t"), Codex.Event(new { type = "token_count", rate_limits = new { primary = new { used_percent = 33.0 } } }, 1));
        AppServer(s => s.Answers["account/rateLimits/read"] = _ => FakeAppServer.Error("failed to fetch codex rate limits: 401 Unauthorized"));
        var codex = new CodexUsageCollector(Options(), time, Probes(), Log);
        await codex.CollectAsync();
        await AdvanceUntil(time, () => codex.Running.IsCompleted, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(2));
        Assert.Equal(CodexUsageProbe.SignedOutError, (await codex.CollectAsync())!.Error);

        // Signed in again with an API key, outside any session this host saw.
        AppServer(s => s.Answers["account/read"] = _ => FakeAppServer.Result(new { account = new { type = "apiKey" }, requiresOpenaiAuth = true }));
        time.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1));
        await codex.CollectAsync();
        await Until(() => launcher.Started.Count == 2);
        await AdvanceUntil(time, () => codex.Running.IsCompleted, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(2));

        var usage = await codex.CollectAsync();
        Assert.Equal((UsageSource.Transcript, null), (usage!.Source, usage.Error));
    }

    [Fact]
    public async Task The_route_follows_what_Codex_runs()
    {
        // Nothing running: the standalone app-server.
        AppServer();
        Assert.Equal(CodexProbeKind.Reading, (await RunToEnd(CodexProbe().Start())).Kind);
        Assert.Single(launcher.Started);

        // The managed daemon running: asked through it, and no process is started.
        launcher.Started.Clear();
        f.Processes.Run(AgentKind.Codex, 7728, @"C:\Users\u\.codex\packages\app-server-daemon\releases\0.166.0\bin\codex.exe app-server --listen unix:// --managed-daemon");
        var daemon = new FakeDaemon(new FakeAppServer(time));
        var viaDaemon = await RunToEnd(CodexProbe(daemon).Start());
        Assert.Equal(CodexProbeKind.Reading, viaDaemon.Kind);
        Assert.Empty(launcher.Started);
        Assert.True(daemon.Closed);
        // A daemon's login can be switched by any of its clients unseen: no key, ever.
        Assert.Null(viaDaemon.Reading!.AccountKey);

        // A session without the daemon: nothing is asked at all.
        f.Processes.Exit(AgentKind.Codex, 7728);
        f.Processes.Run(AgentKind.Codex, 5150, "codex.exe resume 0199a213-81c0-7800-8aa1-bbab2a035a53");
        var none = await RunToEnd(CodexProbe(new FakeDaemon(null)).Start());
        Assert.Equal(CodexProbeKind.Skipped, none.Kind);
        Assert.Empty(launcher.Started);
    }

    [Fact]
    public async Task The_daemon_route_speaks_WebSocket_and_ends_with_a_close_the_daemon_never_answers()
    {
        var socket = Path.Combine(Path.GetTempPath(), $"abu-{Guid.NewGuid():N}"[..12] + ".sock");
        await using var daemon = new FakeWebSocketDaemon(socket, new FakeAppServer(time));
        f.Processes.Run(AgentKind.Codex, 7728, @"C:\Users\u\.codex\packages\app-server-daemon\releases\0.166.0\bin\codex.exe app-server --listen unix:// --managed-daemon");
        var probes = Probes(new CodexDaemonSocket());

        var run = new CodexUsageProbe(Options(socket), probes, time, Log).Start();
        var outcome = await run.Reported;
        await run.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(CodexProbeKind.Reading, outcome.Kind);
        Assert.Null(outcome.Reading!.AccountKey);
        Assert.StartsWith("GET / HTTP/1.1", daemon.Request, StringComparison.Ordinal);
        Assert.Contains("Upgrade: websocket", daemon.Request, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, daemon.Frames.Count);
        Assert.All(daemon.Frames, t => Assert.Equal(WebSocketMessageType.Text, t));
        Assert.True(daemon.CloseReceived);
        Assert.Empty(launcher.Started);
    }

    [Fact]
    public async Task An_authentication_error_from_the_rate_limit_read_stops_the_probes_until_the_hold_ends_or_a_session_runs()
    {
        CodexUsed();
        Fixture.Append(f.CodexTranscript("t"), Codex.Event(new { type = "token_count", rate_limits = new { primary = new { used_percent = 33.0 } } }, 1));
        AppServer(s => s.Answers["account/rateLimits/read"] = _ => FakeAppServer.Error(
            "failed to fetch codex rate limits: GET https://chatgpt.com/backend-api/wham/usage failed: 401 Unauthorized; body omitted"));
        var codex = new CodexUsageCollector(Options(), time, Probes(), Log);

        await codex.CollectAsync();
        await AdvanceUntil(time, () => codex.Running.IsCompleted, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(2));
        var usage = await codex.CollectAsync();

        Assert.Equal("Codex needs signing in again on this machine", usage!.Error);
        Assert.Equal(33.0, usage.Windows[0].Percent);
        // codex login status, which still says signed in after such a failure (E14), is never asked.
        Assert.DoesNotContain(launcher.Started, p => p.Command.Arguments.Contains("login"));
        var probes = launcher.Started.Count;

        time.Advance(TimeSpan.FromMinutes(90));
        await codex.CollectAsync();
        Assert.Equal(probes, launcher.Started.Count);

        // A Codex session running here again ends the hold.
        codex.Pace(sessionOpen: true, busy: true);
        Assert.Null((await codex.CollectAsync())!.Error);
        await Until(() => launcher.Started.Count > probes);
    }

    [Theory]
    [InlineData("account/read", "Your access token could not be refreshed because your refresh token was already used. Please log out and sign in again.", nameof(CodexProbeKind.SignedOut))]
    [InlineData("account/read", "Your access token could not be refreshed. Please log out and sign in again.", nameof(CodexProbeKind.SignedOut))]
    [InlineData("account/read", "failed to load workspace requirements: cloud config: request timed out", nameof(CodexProbeKind.Failed))]
    [InlineData("account/read", "workspace routing discovery timed out", nameof(CodexProbeKind.Failed))]
    [InlineData("account/rateLimits/read", "failed to fetch codex rate limits: application network policy is unavailable", nameof(CodexProbeKind.Failed))]
    [InlineData("account/rateLimits/read", "chatgpt authentication required to read rate limits", nameof(CodexProbeKind.NotApplicable))]
    public async Task Codex_errors_tell_a_sign_out_from_a_slow_sign_in_service(string method, string message, string kind)
    {
        AppServer(s => s.Answers[method] = _ => FakeAppServer.Error(message));

        Assert.Equal(kind, (await RunToEnd(CodexProbe().Start())).Kind.ToString());
    }

    [Fact]
    public async Task An_api_key_login_falls_back_to_the_transcripts_without_an_error()
    {
        CodexUsed();
        Fixture.Append(f.CodexTranscript("t"), Codex.Event(new { type = "token_count", rate_limits = new { primary = new { used_percent = 33.0 } } }, 1));
        AppServer(s => s.Answers["account/read"] = _ => FakeAppServer.Result(new { account = new { type = "apiKey" }, requiresOpenaiAuth = true }));
        var codex = new CodexUsageCollector(Options(), time, Probes(), Log);

        await codex.CollectAsync();
        await AdvanceUntil(time, () => codex.Running.IsCompleted, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(2));
        var usage = await codex.CollectAsync();

        Assert.Equal((UsageSource.Transcript, null), (usage!.Source, usage.Error));
    }

    [Fact]
    public async Task The_newer_of_a_transcript_reading_and_a_probe_reading_wins_both_ways_round()
    {
        CodexUsed();
        var transcript = f.CodexTranscript("t");
        // Fixture.T0 is hours before Now: the probe's reading is newer.
        Fixture.Append(transcript, Codex.Event(new { type = "token_count", rate_limits = new { primary = new { used_percent = 33.0 } } }, 1));
        AppServer();
        var codex = new CodexUsageCollector(Options(), time, Probes(), Log);
        await codex.CollectAsync();
        await AdvanceUntil(time, () => codex.Running.IsCompleted, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(2));

        var probed = await codex.CollectAsync();
        Assert.Equal((UsageSource.Api, 23.0), (probed!.Source, probed.Windows[0].Percent));

        // A session then records a newer figure in its transcript.
        var later = (int)(time.GetUtcNow() - Fixture.T0).TotalSeconds + 60;
        Fixture.Append(transcript, Codex.Event(new { type = "token_count", rate_limits = new { primary = new { used_percent = 44.0 } } }, later));
        var recorded = await codex.CollectAsync();
        Assert.Equal((UsageSource.Transcript, 44.0, null), (recorded!.Source, recorded.Windows[0].Percent, recorded.AccountKey));
    }

    // ---- helpers ----

    static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 1000; i++)
        {
            if (condition()) { return; }
            await Task.Delay(10);
        }
        Assert.Fail("timed out waiting for a condition");
    }

    // Moves the fake clock on a step at a time, letting what each step releases run, until the
    // condition holds or max has passed.
    static async Task AdvanceUntil(FakeTimeProvider time, Func<bool> condition, TimeSpan step, TimeSpan max)
    {
        for (var gone = TimeSpan.Zero; gone < max; gone += step)
        {
            if (condition()) { return; }
            time.Advance(step);
            await Task.Delay(5);
        }
    }

    async Task<T> AdvanceUntilDone<T>(Task<T> task, TimeSpan max)
    {
        await AdvanceUntil(time, () => task.IsCompleted, TimeSpan.FromSeconds(1), max);
        return await task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // The machine-wide lease that hosts serving different bridge profiles share.
    internal sealed class FakeLeases
    {
        readonly HashSet<string> held = [];

        public int Count { get { lock (held) { return held.Count; } } }

        public IDisposable? Take(string key)
        {
            lock (held) { if (!held.Add(key)) { return null; } }
            return new Held(() => { lock (held) { held.Remove(key); } });
        }

        internal sealed class Held(Action release) : IDisposable
        {
            int done;
            public void Dispose() { if (Interlocked.Exchange(ref done, 1) == 0) { release(); } }
        }
    }

    internal sealed class FakeLauncher : IProbeLauncher
    {
        readonly List<FakeProbe> started = [];
        public Func<ProbeCommand, FakeProbe?> Next { get; set; } = _ => null;

        public List<FakeProbe> Started { get { lock (started) { return started; } } }

        public IProbeProcess? Start(ProbeCommand command)
        {
            var p = Next(command);
            if (p is not null) { lock (started) { started.Add(p); } }
            return p;
        }
    }

    // A started process, its pipes in memory. There is no way to kill one, as there is none on the
    // real one: Killed only says whether anything ended it but itself.
    internal sealed class FakeProbe : IProbeProcess
    {
        static int next = 7000;
        readonly TaskCompletionSource<int> exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource<string> output = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly Pipe toChild = new();
        readonly Pipe toHost = new();
        readonly TimeProvider time;

        public FakeProbe(ProbeCommand command, TimeProvider time)
        {
            Command = command;
            this.time = time;
            Id = Interlocked.Increment(ref next);
            if (command.Stdio)
            {
                Input = toChild.Writer.AsStream();
                Reader = toHost.Reader.AsStream();
            }
        }

        public ProbeCommand Command { get; }
        public int Id { get; }
        public Task<int> Exited => exit.Task;
        public Task<string> Output => output.Task;
        public Stream? Input { get; }
        public Stream? Reader { get; }
        public Stream ChildIn => toChild.Reader.AsStream();
        public Stream ChildOut => toHost.Writer.AsStream();
        public DateTimeOffset? InputClosedAt { get; private set; }
        public bool Disposed { get; private set; }
        public bool Killed => false;
        public bool Running => !exit.Task.IsCompleted;

        public void CloseInput()
        {
            InputClosedAt ??= time.GetUtcNow();
            toChild.Writer.Complete();
        }

        public void Answer(string text, int code)
        {
            output.TrySetResult(text);
            End(code);
        }

        public void End(int code)
        {
            output.TrySetResult("");
            toHost.Writer.Complete();
            exit.TrySetResult(code);
        }

        public void Dispose() => Disposed = true;
    }

    internal sealed record Reply(object? Result, string? Error);

    // codex app-server as far as a usage read goes: JSON lines in and out, each request answered by
    // its script, an account/updated notification on initialize when Announce is set, and an exit
    // once its input ends.
    internal sealed class FakeAppServer(TimeProvider time)
    {
        public static Reply Result(object result) => new(result, null);
        public static Reply Error(string message) => new(null, message);

        public Dictionary<string, Func<int, Reply>> Answers { get; } = new()
        {
            ["initialize"] = _ => Result(new { userAgent = "codex_fake/0.0" }),
            ["account/read"] = _ => Result(Account()),
            ["account/rateLimits/read"] = _ => Result(Limits()),
        };
        public Dictionary<string, Task> Before { get; } = [];
        public bool Announce { get; set; }
        public int Announced { get; private set; }
        public int Answered { get; private set; }
        public DateTimeOffset LastAnsweredAt { get; private set; }
        readonly List<(string Method, JsonElement Params)> received = [];
        public List<(string Method, JsonElement Params)> Received { get { lock (received) { return [.. received]; } } }

        public async Task ServeAsync(FakeProbe probe)
        {
            using var reader = new StreamReader(probe.ChildIn);
            var writer = new StreamWriter(probe.ChildOut) { AutoFlush = true, NewLine = "\n" };
            await ServeAsync(() => reader.ReadLineAsync(), writer.WriteLineAsync);
            probe.End(0);
        }

        public async Task ServeAsync(Func<Task<string?>> receive, Func<string, Task> send)
        {
            var calls = new Dictionary<string, int>();
            while (await receive() is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var method = root.GetProperty("method").GetString()!;
                lock (received) { received.Add((method, root.TryGetProperty("params", out var p) ? p.Clone() : default)); }
                if (!root.TryGetProperty("id", out var id)) { continue; }
                calls[method] = calls.GetValueOrDefault(method) + 1;
                if (Before.TryGetValue(method, out var gate)) { await gate; }
                var reply = Answers[method](calls[method]);
                var message = new JsonObject { ["id"] = id.GetInt32() };
                if (reply.Error is { } error) { message["error"] = new JsonObject { ["code"] = -32603, ["message"] = error }; }
                else { message["result"] = JsonSerializer.SerializeToNode(reply.Result); }
                if (Announce && method != "initialize")
                {
                    Announced++;
                    await send("""{"method":"account/updated","params":{"authMode":"chatgpt","planType":"plus"}}""");
                }
                await send(message.ToJsonString());
                Answered++;
                LastAnsweredAt = time.GetUtcNow();
            }
        }
    }

    internal sealed class FakeDaemon(FakeAppServer? server) : ICodexDaemon
    {
        public bool Closed { get; private set; }

        public Task<IRpcLink?> ConnectAsync(string socket, CancellationToken cancellationToken)
        {
            if (server is null) { return Task.FromResult<IRpcLink?>(null); }
            var toServer = System.Threading.Channels.Channel.CreateUnbounded<string?>();
            var toClient = System.Threading.Channels.Channel.CreateUnbounded<string?>();
            _ = server.ServeAsync(async () => await toServer.Reader.ReadAsync(), m => toClient.Writer.WriteAsync(m).AsTask());
            return Task.FromResult<IRpcLink?>(new Link(this, toServer, toClient));
        }

        sealed class Link(FakeDaemon owner, System.Threading.Channels.Channel<string?> toServer, System.Threading.Channels.Channel<string?> toClient) : IRpcLink
        {
            public Task SendAsync(string message) => toServer.Writer.WriteAsync(message).AsTask();
            public async Task<string?> ReceiveAsync() => await toClient.Reader.ReadAsync();
            public Task CloseAsync()
            {
                owner.Closed = true;
                toServer.Writer.TryWrite(null);
                return Task.CompletedTask;
            }
        }
    }

    // The managed daemon's control socket: a Unix-domain socket that takes a WebSocket upgrade, then
    // one JSON-RPC message per text frame, and drops the connection on a close frame without
    // answering it (E9).
    internal sealed class FakeWebSocketDaemon : IAsyncDisposable
    {
        readonly Socket listener;
        readonly string path;
        readonly Task serving;

        public FakeWebSocketDaemon(string path, FakeAppServer server)
        {
            this.path = path;
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(4);
            serving = ServeAsync(server);
        }

        public string Request { get; private set; } = "";
        public List<WebSocketMessageType> Frames { get; } = [];
        public bool CloseReceived { get; private set; }

        async Task ServeAsync(FakeAppServer server)
        {
            using var connection = await listener.AcceptAsync();
            await using var stream = new NetworkStream(connection, ownsSocket: false);
            var head = new StringBuilder();
            var one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one) == 1) { head.Append((char)one[0]); }
            Request = head.ToString();
            var key = Request.Split("\r\n").First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))[18..].Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));
            using var ws = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
            var buffer = new byte[64 * 1024];
            await server.ServeAsync(async () =>
            {
                var got = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (got.MessageType == WebSocketMessageType.Close)
                {
                    CloseReceived = true;
                    // Dropped, never answered.
                    connection.Shutdown(SocketShutdown.Both);
                    return null;
                }
                Frames.Add(got.MessageType);
                return Encoding.UTF8.GetString(buffer, 0, got.Count);
            }, m => ws.SendAsync(Encoding.UTF8.GetBytes(m), WebSocketMessageType.Text, true, CancellationToken.None));
        }

        public async ValueTask DisposeAsync()
        {
            listener.Dispose();
            try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}
