using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Usage;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Xunit;

// session.launch and launch.options end to end: a client through the in-process relay, the
// real SessionService and LaunchService, fixture agent homes read by the real SessionSource,
// and real git for isolated workspaces. Starting an agent is the one thing faked: the fake
// starter plays the agent by writing the session folder and process an agent would have, so
// no test ever starts Copilot, Claude or Codex. Nothing here is ever typed into a console.
public sealed partial class LaunchFlowTests : IAsyncLifetime
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    readonly Fixture f = new();
    readonly FakeProbe probe = new();
    readonly FakeScreens screens = new();
    readonly FakeStarter starter;
    readonly FakeLocator locator = new();
    readonly FakeRunner agency = new();
    TestRelay relay = null!;
    SessionSource source = null!;
    Device host = null!;
    Device client = null!;
    string app = "", api = "", elsewhere = "";

    public LaunchFlowTests() => starter = new FakeStarter(f);

    public async Task InitializeAsync()
    {
        app = Directory.CreateDirectory(Path.Combine(f.Root, "repos", "app")).FullName;
        api = Directory.CreateDirectory(Path.Combine(f.Root, "repos", "api")).FullName;
        elsewhere = Directory.CreateDirectory(Path.Combine(f.Root, "elsewhere")).FullName;
        locator.Installed["copilot"] = @"C:\fake\copilot.exe";
        var o = f.Options();
        source = new SessionSource(new SessionSourceOptions
        {
            CopilotHome = o.CopilotHome,
            ClaudeHome = o.ClaudeHome,
            CodexHome = o.CodexHome,
            ClaudeRegistrationRoots = o.ClaudeRegistrationRoots,
            CodexRegistrationRoots = o.CodexRegistrationRoots,
            UseFileSystemWatcher = false,
            CoalesceInterval = TimeSpan.Zero,
        }, f.Processes, TimeProvider.System);
        await source.RefreshAsync();
        // With the in-process blob store, which a session transfer goes through.
        relay = await TestRelay.StartAsync(o => o.Blobs.Backend = "local");
        var token = relay.TokenFor(1, "octo");
        host = await relay.ConnectAsync(token, "Test host", "host");
        host.Hub.JsonOptions = ProtocolJson.Options;
        client = await relay.ConnectAsync(token, "Phone", "pwa");
        Serve(Config());
    }

    public async Task DisposeAsync()
    {
        await relay.DisposeAsync();
        await source.DisposeAsync();
        foreach (var file in Directory.EnumerateFiles(f.Root, "*", SearchOption.AllDirectories)) { File.SetAttributes(file, FileAttributes.Normal); }
        f.Dispose();
    }

    LaunchConfig Config(Func<LaunchConfig, LaunchConfig>? change = null)
    {
        var config = LaunchConfig.Default with
        {
            Workspaces = [new("App", app, false), new("API", api, false)],
            WorktreeRoot = Path.Combine(f.Root, "wt"),
        };
        return change is null ? config : change(config);
    }

    LaunchService Launcher(LaunchConfig config, TimeSpan? wait = null, IWorktreeUsage? usage = null, Func<DateTime>? now = null, SessionTransfers? transfers = null, ISessionFetcher? fetcher = null, AttachmentStore? attachments = null, TimeSpan? readyWait = null, IAgentHaBridgeProbe? gate = null, TimeSpan? trustConfirm = null, TimeSpan? follow = null) => new(source, gate ?? probe, screens, starter, locator, new AgencyCatalog(agency), new LaunchServiceOptions
    {
        CopilotHome = f.CopilotHome,
        ClaudeHome = f.ClaudeHome,
        CodexHome = f.CodexHome,
        Config = config,
        PlatformSupported = true,
        RegistrationWait = wait ?? TimeSpan.FromSeconds(5),
        PollInterval = TimeSpan.FromMilliseconds(25),
        ScreenAfter = TimeSpan.Zero,
        ScreenEvery = TimeSpan.FromMilliseconds(25),
        ReadyWait = readyWait ?? TimeSpan.FromSeconds(5),
        TrustConfirmWait = trustConfirm ?? TimeSpan.FromSeconds(5),
        FollowFor = follow ?? TimeSpan.FromMinutes(10),
        TrustFollowFor = follow ?? TimeSpan.FromMinutes(30),
        ExitGrace = TimeSpan.FromMilliseconds(100),
        // The fixture's folders are under temp, which is a system folder to a real launch.
        IsSystemDirectory = _ => false,
        WorktreeUsage = usage,
        LocalNow = now ?? (() => DateTime.Now),
        Transfers = fetcher ?? transfers,
        TransferStaging = transfers?.Staging ?? Path.Combine(f.Root, "profile", "transfers"),
    }, attachments: attachments);

    void Serve(LaunchConfig config, TimeSpan? wait = null, IWorktreeUsage? usage = null, Func<DateTime>? now = null, TimeSpan? readyWait = null) =>
        host.Hub.Handler = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0"), launcher: Launcher(config, wait, usage, now, readyWait: readyWait)).HandleAsync;

    async Task<SecureChannel> OpenAsync()
    {
        await client.Relay.WaitForPeerAsync(p => p.Id == host.Id, Patience);
        return await client.Hub.OpenAsync(host.Id);
    }

    static async Task<JsonElement> RequestAsync(SecureChannel ch, string op, object data)
    {
        var response = await ch.RequestAsync(op, data);
        Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText());
        return response.GetProperty("result");
    }

    sealed record Outcome(string Outcome_, string? Reason, string? Session, JsonElement Launched);

    static async Task<Outcome> LaunchAsync(SecureChannel ch, object? settings = null, string? request = null)
    {
        var data = JsonSerializer.SerializeToNode(settings ?? new { }, ProtocolJson.Options)!.AsObject();
        data["request"] = request ?? Guid.NewGuid().ToString();
        var r = await RequestAsync(ch, LaunchProtocol.Launch, data);
        string? S(string name) => r.TryGetProperty(name, out var v) ? v.GetString() : null;
        return new Outcome(S("outcome")!, S("reason"), S("session"), r.TryGetProperty("launched", out var l) ? l.Clone() : default);
    }

    static async Task<string[]> CapabilitiesAsync(SecureChannel ch) =>
        [.. (await RequestAsync(ch, Ops.Hello, new { })).GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!)];

    [Fact]
    public async Task Hello_offers_launching_only_while_agent_ha_bridge_is_not_running_and_the_host_can_start_agents_here()
    {
        var ch = await OpenAsync();
        Assert.Contains(LaunchProtocol.Capability, await CapabilitiesAsync(ch));

        probe.Reason = "agent-ha-bridge is running for this user";
        Assert.DoesNotContain(LaunchProtocol.Capability, await CapabilitiesAsync(ch));
        probe.Reason = null;

        starter.Unavailable_ = "the host is running as administrator";
        Assert.DoesNotContain(LaunchProtocol.Capability, await CapabilitiesAsync(ch));
        starter.Unavailable_ = null;

        Serve(Config(c => c with { Enabled = false }));
        Assert.DoesNotContain(LaunchProtocol.Capability, await CapabilitiesAsync(ch));
    }

    [Fact]
    public async Task The_options_list_installed_agents_approved_workspaces_and_only_resumable_sessions_in_them()
    {
        locator.Installed["claude"] = @"C:\fake\claude.exe";
        Resumable("11111111-1111-4111-8111-111111111111", app, "Fix the flaky test");
        Resumable("22222222-2222-4222-8222-222222222222", elsewhere, "Somewhere nobody approved");
        // Running now: never offered, since two agents on one transcript would corrupt it.
        Resumable("33333333-3333-4333-8333-333333333333", api, "Still running");
        Live("33333333-3333-4333-8333-333333333333", 900);
        Serve(Config(c => c with { Agents = new Dictionary<string, AgentConfig> { ["copilot"] = new() { Model = "gpt-6.1-sol" } } }));
        var ch = await OpenAsync();
        var options = await RequestAsync(ch, LaunchProtocol.Options, new { });

        Assert.Equal(["copilot", "claude"], options.GetProperty("agents").EnumerateArray().Select(a => a.GetProperty("id").GetString()));
        var copilot = options.GetProperty("agents")[0];
        Assert.Equal(["gpt-6.1-sol", "auto"], copilot.GetProperty("models").EnumerateArray().Select(m => m.GetString()));
        Assert.Contains("xhigh", copilot.GetProperty("efforts").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal("gpt-6.1-sol", copilot.GetProperty("defaults").GetProperty("model").GetString());
        Assert.Equal(["App", "API"], options.GetProperty("workspaces").EnumerateArray().Select(w => w.GetProperty("label").GetString()));
        var resumable = Assert.Single(options.GetProperty("resumable").EnumerateArray());
        Assert.Equal(("11111111-1111-4111-8111-111111111111", "copilot", "Fix the flaky test - app", "App"),
            (resumable.GetProperty("id").GetString(), resumable.GetProperty("launcher").GetString(), resumable.GetProperty("title").GetString(), resumable.GetProperty("workspace").GetString()));
        Assert.Equal(("copilot", "App", "ask"), (options.GetProperty("defaults").GetProperty("agent").GetString(), options.GetProperty("defaults").GetProperty("workspace").GetString(), options.GetProperty("defaults").GetProperty("permissions").GetString()));
    }

    // Found by the shadow comparison (#40): approving ~\repos offered every session in every
    // repository under it, where agent-ha-bridge resumes only a session that ran in an
    // approved folder itself (Test-BridgeWorkspacePathApproved).
    [Fact]
    public async Task A_session_is_resumable_only_from_an_approved_folder_itself_not_one_below_it()
    {
        var below = Directory.CreateDirectory(Path.Combine(app, "src")).FullName;
        Resumable("11111111-1111-4111-8111-111111111111", app, "At the top");
        Resumable("66666666-6666-4666-8666-666666666666", below, "Further down");
        var ch = await OpenAsync();

        var options = await RequestAsync(ch, LaunchProtocol.Options, new { });

        Assert.Equal(["At the top - app"], options.GetProperty("resumable").EnumerateArray().Select(r => r.GetProperty("title").GetString()));
        Assert.Equal("refused", (await LaunchAsync(ch, new { resume = "66666666-6666-4666-8666-666666666666" })).Outcome_);
        Assert.Empty(starter.Specs);
    }

    // Found by the shadow comparison (#40): a Claude session in a folder nobody approved put
    // "Copilot: " on every label of a list that offers only Copilot sessions, where
    // agent-ha-bridge names the agent only when what it could offer is of more than one kind.
    [Fact]
    public async Task Resumable_sessions_name_their_agent_only_when_the_approved_folders_hold_more_than_one_kind()
    {
        locator.Installed["claude"] = @"C:\fake\claude.exe";
        Resumable("11111111-1111-4111-8111-111111111111", app, "Fix the flaky test");
        ClaudeResumable("44444444-4444-4444-8444-444444444444", elsewhere, "Somewhere nobody approved");
        var ch = await OpenAsync();

        string[] Titles(JsonElement options) => [.. options.GetProperty("resumable").EnumerateArray().Select(r => r.GetProperty("title").GetString()!)];
        Assert.Equal(["Fix the flaky test - app"], Titles(await RequestAsync(ch, LaunchProtocol.Options, new { })));

        ClaudeResumable("55555555-5555-4555-8555-555555555555", api, "Plan the release");
        // A fresh service: each keeps what it read of the stores for a minute.
        Serve(Config());
        Assert.Equal(["Claude: Plan the release - api", "Copilot: Fix the flaky test - app"], Titles(await RequestAsync(ch, LaunchProtocol.Options, new { })).Order());
    }

    // Found by the shadow comparison (#40): agent-ha-bridge tells repeated labels apart before it
    // leaves out the running sessions, ignoring case, so an older namesake of a running session
    // carries its id there even though the running one is not offered.
    [Fact]
    public async Task A_resumable_session_named_like_a_newer_or_running_one_carries_its_id_as_agent_ha_bridge_labels_it()
    {
        Resumable("11111111-1111-4111-8111-111111111111", app, "Fix the login page", DateTimeOffset.UtcNow.AddDays(-1));
        Resumable("22222222-2222-4222-8222-222222222222", app, "FIX THE LOGIN PAGE", DateTimeOffset.UtcNow.AddDays(-2));
        // Running, and the newest: its own record names it as the others are named.
        Live("33333333-3333-4333-8333-333333333333", 900);
        Resumable("33333333-3333-4333-8333-333333333333", app, "Fix the login page");
        var ch = await OpenAsync();

        var options = await RequestAsync(ch, LaunchProtocol.Options, new { });

        Assert.Equal(["Fix the login page - app (11111111)", "FIX THE LOGIN PAGE - app (22222222)"],
            options.GetProperty("resumable").EnumerateArray().Select(r => r.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task A_new_session_starts_in_its_workspace_with_the_whole_prompt_and_the_answer_names_the_session_it_became()
    {
        var ch = await OpenAsync();
        var prompt = "Read the design doc,\nthen open a PR — “carefully” & quickly. 🚀 " + new string('x', 4000);
        var o = await LaunchAsync(ch, new { agent = "copilot", workspace = "API", prompt });

        Assert.Equal("started", o.Outcome_);
        var spec = Assert.Single(starter.Specs);
        Assert.Equal((@"C:\fake\copilot.exe", api), (spec.Executable, spec.WorkingDirectory));
        Assert.Equal(["--session-id", o.Session!, "--banner", "-i", LaunchProtocol.Flatten(prompt)], spec.Arguments);
        // Everything it runs knows it is inside a launched session, and so may not manage the host.
        Assert.Equal(o.Session, spec.Environment![Launchers.SessionVariable]);
        Assert.True(Guid.TryParseExact(o.Session, "D", out _));
        Assert.NotNull(source.Get(o.Session!));
        Assert.Equal(("copilot", "API", api, "ask"), (o.Launched.GetProperty("agent").GetString(), o.Launched.GetProperty("workspace").GetString(), o.Launched.GetProperty("cwd").GetString(), o.Launched.GetProperty("permissions").GetString()));
        Assert.Empty(screens.Typed);
    }

    [Fact]
    public async Task A_resent_launch_gets_the_first_outcome_and_starts_nothing_more()
    {
        var ch = await OpenAsync();
        var request = Guid.NewGuid().ToString();
        var first = await LaunchAsync(ch, new { prompt = "go" }, request);
        var again = await LaunchAsync(ch, new { prompt = "go" }, request);

        Assert.Equal(first, again with { Launched = first.Launched });
        Assert.Single(starter.Specs);
    }

    [Fact]
    public async Task Settings_this_machine_does_not_offer_are_refused_before_anything_starts()
    {
        var ch = await OpenAsync();
        Assert.Equal(("refused", "Unknown workspace 'Secret'; this machine has App, API"), Pair(await LaunchAsync(ch, new { workspace = "Secret" })));
        Assert.Equal(("refused", "Unknown agent 'codex'; this machine has copilot"), Pair(await LaunchAsync(ch, new { agent = "codex", prompt = "x" })));
        Assert.StartsWith("Unknown model 'gpt-9' for Copilot; it offers auto", (await LaunchAsync(ch, new { model = "gpt-9" })).Reason);
        Assert.StartsWith("Unknown effort 'ultra' for Copilot", (await LaunchAsync(ch, new { effort = "ultra" })).Reason);
        Assert.StartsWith("a prompt cannot start with /", (await LaunchAsync(ch, new { prompt = "/allow-all" })).Reason);
        Assert.StartsWith("No resumable session", (await LaunchAsync(ch, new { resume = "44444444-4444-4444-8444-444444444444" })).Reason);

        Serve(Config(c => c with { Workspaces = [] }));
        Assert.Equal(("refused", "no workspace is approved on this machine yet; approve one at the machine (agent-bridge-host workspace add <folder>)"), Pair(await LaunchAsync(ch)));
        Assert.Empty(starter.Specs);
    }

    [Fact]
    public async Task The_machines_defaults_fill_what_a_launch_leaves_out_and_an_empty_value_asks_for_the_agents_own()
    {
        Serve(Config(c => c with
        {
            DefaultWorkspace = "API",
            Permissions = LaunchProtocol.AllowAll,
            Agents = new Dictionary<string, AgentConfig> { ["copilot"] = new() { Model = "gpt-6.1-sol", Effort = "high", ExtraArgs = ["--no-auto-update"] } },
        }));
        var ch = await OpenAsync();
        var defaults = await LaunchAsync(ch);
        Assert.Equal("started", defaults.Outcome_);
        Assert.Equal(api, starter.Specs[0].WorkingDirectory);
        Assert.Equal(["--session-id", defaults.Session!, "--banner", "--model", "gpt-6.1-sol", "--reasoning-effort", "high", "--allow-all", "--no-auto-update"], starter.Specs[0].Arguments);

        var own = await LaunchAsync(ch, new { model = "", effort = "", permissions = "ask" });
        Assert.Equal(["--session-id", own.Session!, "--banner", "--no-auto-update"], starter.Specs[1].Arguments);
    }

    [Fact]
    public async Task Agency_starts_with_the_chosen_profile_and_a_profile_it_does_not_list_is_refused()
    {
        locator.Installed["agency"] = @"C:\fake\agency.exe";
        agency.Profiles = "Profiles:\n  home\n    MCPs: none\n  work\n    MCPs: github\n";
        var ch = await OpenAsync();
        var options = await RequestAsync(ch, LaunchProtocol.Options, new { });
        Assert.Equal("agency", options.GetProperty("defaults").GetProperty("agent").GetString());
        Assert.Equal(["home", "work"], options.GetProperty("agents")[0].GetProperty("profiles").EnumerateArray().Select(p => p.GetString()));

        var o = await LaunchAsync(ch, new { agent = "agency", profile = "work", prompt = "go" });
        Assert.Equal("started", o.Outcome_);
        Assert.Equal(["copilot", "--profile-only", "work", "--session-id", o.Session!, "--banner", "-i", "go"], starter.Specs[0].Arguments);
        Assert.StartsWith("Unknown profile 'play'; this machine has home, work", (await LaunchAsync(ch, new { agent = "agency", profile = "play" })).Reason);
    }

    [Fact]
    public async Task A_folder_trust_question_is_left_for_the_machine_and_other_launches_are_busy_meanwhile()
    {
        starter.Registers = false;
        screens.Rows = ["Confirm folder trust", app, "Do you trust the files in this folder?", "❯ 1. Yes", "  2. Yes, and remember this folder for future sessions", "  3. No (Esc)"];
        var ch = await OpenAsync();
        var first = await LaunchAsync(ch, new { workspace = "App", prompt = "go" });
        Assert.Equal("unconfirmed", first.Outcome_);
        Assert.Contains("is asking whether to trust App. Only a person at the machine can answer that", first.Reason);
        Assert.NotNull(first.Session);

        var second = await LaunchAsync(ch, new { workspace = "API" });
        Assert.Equal(("busy", "Busy: Copilot in App is waiting for a person to answer whether to trust that folder, so nothing was launched. Try again in a couple of minutes."), Pair(second));
        Assert.Contains("waiting for a person", (await RequestAsync(ch, LaunchProtocol.Options, new { })).GetProperty("busy").GetString());
        Assert.Single(starter.Specs);
        // Read, never typed into.
        Assert.Empty(screens.Typed);
        Assert.True(screens.Reads > 0);

        // Answered at the machine: the session registers, and launching is open again.
        screens.Rows = CopilotReady;
        starter.Register(starter.Specs[0]);
        await WaitAsync(async () => (await RequestAsync(ch, LaunchProtocol.Options, new { })).TryGetProperty("busy", out _) == false);
        starter.Registers = true;
        Assert.Equal("started", (await LaunchAsync(ch, new { workspace = "API" })).Outcome_);
    }

    [Fact]
    public async Task A_Copilot_that_registers_and_then_asks_whether_to_trust_its_folder_is_left_for_the_machine_and_other_launches_are_busy()
    {
        // Copilot writes its lock before its window is ready, and asks about the folder after.
        screens.Rows = ["  Current   Sessions   Issues   Pull requests   Gists"];
        var ch = await OpenAsync();
        var asking = Task.Run(() => LaunchAsync(ch, new { workspace = "App" }));
        await WaitAsync(() => Task.FromResult(screens.Reads > 2));
        screens.Rows = ["Confirm folder trust", app, "Do you trust the files in this folder?", "❯ 1. Yes", "  2. Yes, and remember this folder for future sessions", "  3. No (Esc)"];

        var first = await asking;
        Assert.Equal("unconfirmed", first.Outcome_);
        Assert.Contains("is asking whether to trust App. Only a person at the machine can answer that", first.Reason);
        Assert.Equal(("busy", "Busy: Copilot in App is waiting for a person to answer whether to trust that folder, so nothing was launched. Try again in a couple of minutes."),
            Pair(await LaunchAsync(ch, new { workspace = "API" })));
        Assert.Empty(screens.Typed);

        // Answered at the machine: its window shows it ready, and launching is open again.
        screens.Rows = CopilotReady;
        await WaitAsync(async () => (await RequestAsync(ch, LaunchProtocol.Options, new { })).TryGetProperty("busy", out _) == false);
        Assert.Equal("started", (await LaunchAsync(ch, new { workspace = "API" })).Outcome_);
    }

    [Fact]
    public async Task A_Copilot_that_registers_but_whose_window_is_never_seen_ready_is_unconfirmed_and_still_named()
    {
        screens.Rows = null;
        Serve(Config(), readyWait: TimeSpan.FromMilliseconds(300));
        var ch = await OpenAsync();

        var o = await LaunchAsync(ch, new { workspace = "App" });

        Assert.Equal(("unconfirmed", "Copilot started in App, but its window was not seen ready, so whether it is waiting on a question there is not known; check its window"), Pair(o));
        Assert.Equal(starter.Specs[0].Arguments[1], o.Session);
        Assert.Empty(screens.Typed);
    }

    [Fact]
    public async Task A_Copilot_that_registers_but_whose_window_is_not_ready_when_the_wait_ends_is_said_to_have_appeared()
    {
        screens.Rows = null;
        Serve(Config(), wait: TimeSpan.FromMilliseconds(400));
        var ch = await OpenAsync();

        var o = await LaunchAsync(ch, new { workspace = "App" });

        Assert.Equal(("unconfirmed", "Copilot started in App, but its window was not seen ready, so whether it is waiting on a question there is not known; check its window"), Pair(o));
    }

    [Fact]
    public async Task A_Copilot_launch_answered_before_its_window_was_seen_ready_is_still_followed_and_unlocks_its_worktree_once_it_ends()
    {
        Git(f.Root, "init", "-q", "-b", "main", app);
        File.WriteAllText(Path.Combine(app, "README.md"), "hi\n");
        Git(app, "add", ".");
        Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "one");
        screens.Rows = null;
        Serve(Config(c => c with { Workspaces = [new("App", app, true), new("API", api, false)] }), readyWait: TimeSpan.FromMilliseconds(300));
        var ch = await OpenAsync();
        Assert.Equal("unconfirmed", (await LaunchAsync(ch, new { workspace = "App" })).Outcome_);
        var cwd = starter.Specs[0].WorkingDirectory;
        Assert.True(File.Exists(Path.Combine(Worktrees.AdminFolder(cwd)!, "locked")));

        // A trust question drawn after that is still seen, and turns other launches away.
        screens.Rows = ["Confirm folder trust", "Do you trust the files in this folder?", "❯ 1. Yes"];
        await WaitAsync(async () => (await RequestAsync(ch, LaunchProtocol.Options, new { })).TryGetProperty("busy", out _));
        Assert.Equal("busy", (await LaunchAsync(ch, new { workspace = "API" })).Outcome_);

        // Declined at the machine, it closes: its worktree is unlocked, since it did register.
        starter.Exit(starter.Pids[0]);
        f.Processes.Exit(AgentKind.Copilot, starter.Pids[0]);
        await WaitAsync(() => Task.FromResult(!File.Exists(Path.Combine(Worktrees.AdminFolder(cwd)!, "locked"))));
        Assert.Empty(screens.Typed);
    }

    [Fact]
    public async Task A_Copilot_launcher_that_hands_its_session_on_and_exits_is_not_started_while_the_session_asks_whether_to_trust_its_folder()
    {
        starter.ExitsAtOnce = true;
        starter.HandsOn = true;
        // Only the session's own window can be read; the launcher's went with it.
        screens.Unreadable = pid => starter.Pids.Contains(pid);
        screens.Rows = ["Confirm folder trust", "Do you trust the files in this folder?", "❯ 1. Yes"];
        var ch = await OpenAsync();

        var first = await LaunchAsync(ch, new { workspace = "App" });

        Assert.Equal("unconfirmed", first.Outcome_);
        Assert.Contains("is asking whether to trust App", first.Reason);
        Assert.Equal("busy", (await LaunchAsync(ch, new { workspace = "API" })).Outcome_);

        // Answered at the machine: the session's window shows it ready, and launching is open again.
        screens.Rows = CopilotReady;
        await WaitAsync(async () => (await RequestAsync(ch, LaunchProtocol.Options, new { })).TryGetProperty("busy", out _) == false);
        Assert.Empty(screens.Typed);
    }

    [Fact]
    public async Task A_launch_whose_agent_exits_before_its_session_registers_has_failed()
    {
        starter.Registers = false;
        starter.ExitsAtOnce = true;
        var ch = await OpenAsync();
        var o = await LaunchAsync(ch, new { prompt = "go" });
        Assert.Equal(("failed", "Copilot in App closed before it started; start it at the machine to see why"), Pair(o));
    }

    [Fact]
    public async Task A_session_that_has_not_registered_when_the_wait_ends_is_unconfirmed_and_still_named()
    {
        starter.Registers = false;
        Serve(Config(), wait: TimeSpan.FromMilliseconds(400));
        var ch = await OpenAsync();
        var o = await LaunchAsync(ch, new { prompt = "go" });
        Assert.Equal("unconfirmed", o.Outcome_);
        Assert.Contains("had not appeared after 0 s", o.Reason);
        Assert.Equal(starter.Specs[0].Arguments[1], o.Session);
    }

    [Fact]
    public async Task A_resume_reopens_the_session_in_its_own_folder_with_its_own_id()
    {
        const string Id = "55555555-5555-4555-8555-555555555555";
        Resumable(Id, app, "Ship the release");
        var ch = await OpenAsync();
        Assert.StartsWith("a resumed session reopens in its own folder", (await LaunchAsync(ch, new { resume = Id, workspace = "App" })).Reason);

        var o = await LaunchAsync(ch, new { resume = Id, prompt = "carry on", model = "auto" });
        Assert.Equal(("started", Id), (o.Outcome_, o.Session));
        Assert.Equal(app, starter.Specs[0].WorkingDirectory);
        Assert.Equal(["--session-id", Id, "--banner", "--model", "auto", "-i", "carry on"], starter.Specs[0].Arguments);
        Assert.Equal(Id, o.Launched.GetProperty("resume").GetString());

        // Live now: not on the list, and not resumed on top of itself.
        Assert.Equal(("refused", "that session is already running; two agents on one session would corrupt it"), Pair(await LaunchAsync(ch, new { resume = Id })));
        Assert.Single(starter.Specs);
    }

    [Fact]
    public async Task An_isolated_workspace_starts_each_session_in_a_worktree_of_its_own_and_unlocks_it_once_it_registers()
    {
        Git(f.Root, "init", "-q", "-b", "main", app);
        File.WriteAllText(Path.Combine(app, "README.md"), "hi\n");
        Git(app, "add", ".");
        Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "one");
        Serve(Config(c => c with { Workspaces = [new("App", app, true)] }));
        var ch = await OpenAsync();

        var o = await LaunchAsync(ch, new { prompt = "go" });
        Assert.Equal("started", o.Outcome_);
        var cwd = starter.Specs[0].WorkingDirectory;
        Assert.StartsWith(Path.Combine(f.Root, "wt", "app-"), cwd);
        Assert.True(Worktrees.IsManaged(cwd));
        Assert.True(o.Launched.GetProperty("worktree").GetBoolean());
        Assert.Equal(cwd, o.Launched.GetProperty("cwd").GetString());
        await WaitAsync(() => Task.FromResult(!File.Exists(Path.Combine(Worktrees.AdminFolder(cwd)!, "locked"))));

        // Not a repository: no worktree, so no launch, never the folder itself.
        Serve(Config(c => c with { Workspaces = [new("API", api, true)] }));
        var refused = await LaunchAsync(ch, new { prompt = "go" });
        Assert.StartsWith("Launch refused: requested isolation failed. Cannot establish the repository for isolation", refused.Reason);
        Assert.Single(starter.Specs);
    }

    [Fact]
    public async Task A_finished_worktree_is_removed_before_the_cap_is_counted_so_the_next_isolated_launch_goes_ahead()
    {
        Git(f.Root, "init", "-q", "-b", "main", app);
        File.WriteAllText(Path.Combine(app, "README.md"), "hi\n");
        Git(app, "add", ".");
        Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "one");
        Serve(Config(c => c with { Workspaces = [new("App", app, true)], WorktreeLimit = 1 }), usage: new NoSessions());
        var ch = await OpenAsync();

        Assert.Equal("started", (await LaunchAsync(ch, new { prompt = "go" })).Outcome_);
        var first = starter.Specs[0].WorkingDirectory;
        await WaitAsync(() => Task.FromResult(!File.Exists(Path.Combine(Worktrees.AdminFolder(first)!, "locked"))));
        // Its session has ended and it has sat idle past launch.worktrees.idleHours.
        File.WriteAllText(Path.Combine(Worktrees.AdminFolder(first)!, Worktrees.Marker), DateTimeOffset.Now.AddHours(-13).ToString("o"));

        Assert.Equal("started", (await LaunchAsync(ch, new { prompt = "again" })).Outcome_);
        Assert.False(Directory.Exists(first));
        Assert.True(Worktrees.IsManaged(starter.Specs[1].WorkingDirectory));
    }

    sealed class NoSessions : IWorktreeUsage
    {
        public WorktreeUsage Read() => new(true, [], "");
    }

    [Fact]
    public async Task Nothing_is_launched_while_agent_ha_bridge_runs_for_this_user()
    {
        var ch = await OpenAsync();
        probe.Reason = "agent-ha-bridge is running for this user (AgentBridgeDaemon); answer at the machine or through it";
        Assert.Equal("refused", (await LaunchAsync(ch, new { prompt = "go" })).Outcome_);
        Assert.Empty(starter.Specs);
    }

    [Fact]
    public async Task Codex_is_launched_only_with_a_first_message_and_its_launch_is_bound_to_the_session_it_chose()
    {
        locator.Installed["codex"] = @"C:\fake\codex.cmd";
        var ch = await OpenAsync();
        Assert.Equal(("refused", "Codex starts its session only on a first message; give it a prompt"), Pair(await LaunchAsync(ch, new { agent = "codex" })));

        var o = await LaunchAsync(ch, new { agent = "codex", prompt = "add pagination" });
        Assert.Equal("started", o.Outcome_);
        Assert.Equal(starter.CodexId, o.Session);
        Assert.Equal(["--no-daemon", "-c", "model_reasoning_summary=detailed", "--", "add pagination"], starter.Specs[0].Arguments);
    }

    [Fact]
    public async Task A_Codex_asking_whether_to_trust_its_folder_is_left_for_the_machine_and_other_launches_are_busy_meanwhile()
    {
        locator.Installed["codex"] = @"C:\fake\codex.cmd";
        starter.Registers = false;
        screens.Rows = ["  Folder access", $"  {app}", "  Trust this folder? Codex can read, edit, and run files here, subject to your permission settings.", "› 1. Trust and continue", "  2. Quit"];
        var ch = await OpenAsync();

        var first = await LaunchAsync(ch, new { agent = "codex", workspace = "App", prompt = "add pagination" });

        Assert.Equal("unconfirmed", first.Outcome_);
        Assert.Contains("Codex started, and is asking whether to trust App. Only a person at the machine can answer that", first.Reason);
        Assert.Equal(("busy", "Busy: Codex in App is waiting for a person to answer whether to trust that folder, so nothing was launched. Try again in a couple of minutes."),
            Pair(await LaunchAsync(ch, new { agent = "codex", workspace = "API", prompt = "two" })));
        Assert.Single(starter.Specs);
        Assert.Empty(screens.Typed);
    }

    [Fact]
    public async Task Two_codex_launches_at_once_each_get_the_session_their_own_agent_chose()
    {
        const string First = "71111111-1111-4111-8111-111111111111", Second = "72222222-2222-4222-8222-222222222222";
        locator.Installed["codex"] = @"C:\fake\codex.cmd";
        starter.Registers = false;
        var ch = await OpenAsync();
        var first = LaunchAsync(ch, new { agent = "codex", prompt = "one" });
        await WaitAsync(() => Task.FromResult(starter.Pids.Count == 1));
        var second = LaunchAsync(ch, new { agent = "codex", prompt = "two" });
        await WaitAsync(() => Task.FromResult(starter.Pids.Count == 2));

        // The second's agent registers first: it is the second's, and the first goes on waiting.
        starter.RegisterCodex(starter.Specs[1], starter.Pids[1], 7002, Second);
        Assert.Equal(("started", Second), ((await second).Outcome_, (await second).Session));
        Assert.False(first.IsCompleted);

        starter.RegisterCodex(starter.Specs[0], starter.Pids[0], 7001, First);
        Assert.Equal(("started", First), ((await first).Outcome_, (await first).Session));
    }

    [Fact]
    public async Task A_session_being_resumed_is_not_resumed_again_by_another_press_until_its_agent_has_gone()
    {
        const string Id = "77777777-7777-4777-8777-777777777777";
        Resumable(Id, app, "Ship it");
        starter.Registers = false;
        Serve(Config(), wait: TimeSpan.FromMilliseconds(400));
        var ch = await OpenAsync();

        // Two presses at once, from two devices, say: one agent starts, the other is turned away.
        var a = LaunchAsync(ch, new { resume = Id });
        var b = LaunchAsync(ch, new { resume = Id });
        Assert.Equal(["refused", "unconfirmed"], new[] { (await a).Outcome_, (await b).Outcome_ }.Order());
        Assert.Single(starter.Specs);

        // Still starting after its launch has answered: not offered, and not started again.
        var options = await RequestAsync(ch, LaunchProtocol.Options, new { });
        Assert.DoesNotContain(options.GetProperty("resumable").EnumerateArray(), r => r.GetProperty("id").GetString() == Id);
        Assert.Equal(("refused", "that session is already running; two agents on one session would corrupt it"), Pair(await LaunchAsync(ch, new { resume = Id })));
        Assert.Single(starter.Specs);

        // Once its agent has gone, it can be resumed again.
        starter.Exit(starter.Pids[0]);
        Assert.Equal("unconfirmed", (await LaunchAsync(ch, new { resume = Id })).Outcome_);
        Assert.Equal(2, starter.Specs.Count);
    }

    static (string, string?) Pair(Outcome o) => (o.Outcome_, o.Reason);

    [Fact]
    public async Task While_Copilot_discovery_is_held_none_of_its_sessions_is_offered_for_resume_and_resuming_one_is_busy()
    {
        const string Id = "88888888-8888-4888-8888-888888888888";
        Resumable(Id, app, "Ship it");
        // A Copilot no session accounts for: it may be that very session, running unseen.
        f.Processes.Run(AgentKind.Copilot, 950, "copilot", DateTimeOffset.UtcNow);
        await source.RefreshAsync();
        var ch = await OpenAsync();

        Assert.Empty((await RequestAsync(ch, LaunchProtocol.Options, new { })).GetProperty("resumable").EnumerateArray());
        var held = await LaunchAsync(ch, new { resume = Id });
        Assert.Equal("busy", held.Outcome_);
        Assert.Contains("an unrecognised copilot process (pid 950)", held.Reason);
        Assert.Empty(starter.Specs);
        // A new session decides nothing from an absence, so it still starts.
        Assert.Equal("started", (await LaunchAsync(ch, new { prompt = "go" })).Outcome_);

        f.Processes.Exit(AgentKind.Copilot, 950);
        await source.RefreshAsync();
        Assert.Contains((await RequestAsync(ch, LaunchProtocol.Options, new { })).GetProperty("resumable").EnumerateArray(), r => r.GetProperty("id").GetString() == Id);
        Assert.Equal("started", (await LaunchAsync(ch, new { resume = Id })).Outcome_);
    }

    [Fact]
    public async Task A_new_Codex_session_is_not_launched_while_Codex_discovery_is_held_since_it_is_bound_by_appearing_first()
    {
        locator.Installed["codex"] = @"C:\fake\codex.cmd";
        Directory.CreateDirectory(f.CodexRegistrations);
        f.Processes.Run(AgentKind.Codex, 960, "codex", DateTimeOffset.UtcNow);
        await source.RefreshAsync();
        var ch = await OpenAsync();

        var o = await LaunchAsync(ch, new { agent = "codex", prompt = "add pagination" });

        Assert.Equal("busy", o.Outcome_);
        Assert.Contains("an unrecognised codex process (pid 960)", o.Reason);
        Assert.Empty(starter.Specs);
    }

    [Fact]
    public async Task A_hold_that_begins_while_the_worktree_is_made_stops_the_launch_before_anything_starts()
    {
        Git(f.Root, "init", "-q", "-b", "main", app);
        File.WriteAllText(Path.Combine(app, "README.md"), "hi\n");
        Git(app, "add", ".");
        Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "one");
        locator.Installed["codex"] = @"C:\fake\codex.cmd";
        Directory.CreateDirectory(f.CodexRegistrations);
        // The worktree's name is the moment it is made; a Codex nothing accounts for appears then.
        Serve(Config(c => c with { Workspaces = [new("App", app, true)] }), now: () =>
        {
            f.Processes.Run(AgentKind.Codex, 970, "codex", DateTimeOffset.UtcNow);
            source.RefreshAsync().GetAwaiter().GetResult();
            return DateTime.Now;
        });
        var ch = await OpenAsync();

        var o = await LaunchAsync(ch, new { agent = "codex", prompt = "add pagination" });

        Assert.Equal("busy", o.Outcome_);
        Assert.Contains("an unrecognised codex process (pid 970)", o.Reason);
        Assert.Empty(starter.Specs);
    }

    [Fact]
    public async Task A_session_found_and_its_hold_cleared_while_the_options_are_gathered_is_not_offered_for_resume()
    {
        const string Id = "99999999-9999-4999-8999-999999999999";
        Resumable(Id, app, "Ship it");
        locator.Installed["agency"] = @"C:\fake\agency.exe";
        // A Copilot nothing accounts for yet: that very session, about to be seen.
        f.Processes.Run(AgentKind.Copilot, 950, "copilot", DateTimeOffset.UtcNow);
        await source.RefreshAsync();
        Assert.NotNull(source.Held);
        agency.OnSessions = () =>
        {
            File.WriteAllText(Path.Combine(f.CopilotHome, "session-state", Id, "inuse.950.lock"), "");
            source.RefreshAsync().GetAwaiter().GetResult();
        };
        var ch = await OpenAsync();

        var options = await RequestAsync(ch, LaunchProtocol.Options, new { });

        Assert.Null(source.Held);
        Assert.Equal(Id, Assert.Single(source.Snapshot()).Id);
        Assert.DoesNotContain(options.GetProperty("resumable").EnumerateArray(), r => r.GetProperty("id").GetString() == Id);
    }

    void Resumable(string id, string cwd, string summary, DateTimeOffset? updated = null)
    {
        var dir = Directory.CreateDirectory(Path.Combine(f.CopilotHome, "session-state", id)).FullName;
        File.WriteAllText(Path.Combine(dir, "workspace.yaml"), $"id: {id}\ncwd: '{cwd}'\nsummary: {summary}\n"
            + (updated is { } u ? $"updated_at: {u.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}\n" : ""));
    }

    // A Claude session as its own store keeps it: a project folder's transcript, named by its
    // first prompt's AI title.
    void ClaudeResumable(string id, string cwd, string title)
    {
        var project = Directory.CreateDirectory(Path.Combine(f.ClaudeHome, "projects", "-" + Path.GetFileName(cwd))).FullName;
        File.WriteAllText(Path.Combine(project, id + ".jsonl"),
            JsonSerializer.Serialize(new { type = "user", cwd, message = new { content = new[] { new { type = "text", text = title } } } }) + "\n"
            + JsonSerializer.Serialize(new { type = "ai-title", aiTitle = title }) + "\n");
    }

    void Live(string id, int pid)
    {
        f.CopilotSession(id, pid, lockWrite: DateTimeOffset.UtcNow);
        f.Processes.Run(AgentKind.Copilot, pid, $"copilot --session-id {id}", DateTimeOffset.UtcNow.AddSeconds(-5));
        source.RefreshAsync().GetAwaiter().GetResult();
    }

    static void Git(string dir, params string[] args)
    {
        var r = AgentBridge.Host.Delivery.Launch.Git.Run(dir, args);
        Assert.True(r.Ok, $"git {string.Join(' ', args)}: {r.Said}");
    }

    static async Task WaitAsync(Func<Task<bool>> done)
    {
        var until = DateTime.UtcNow + Patience;
        while (!await done())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            await Task.Delay(25);
        }
    }

    sealed class FakeProbe : IAgentHaBridgeProbe
    {
        public string? Reason;

        public string? Active() => Reason;
    }

    // Copilot's window ready for a prompt, as 1.0.105 draws it: its input box and key footer.
    static readonly IReadOnlyList<string> CopilotReady = ["╻▄▄▄▄▄▄▄▄", "┃", "╹▀▀▀▀▀▀▀▀", " ← open sidebar · Interactive · Allow All · / commands · ? help · tab next tab"];

    // Shows the rows it is given as every session's screen, and records anything typed, which
    // a launch must never do.
    sealed class FakeScreens : IConsoleInjector
    {
        public volatile IReadOnlyList<string>? Rows = CopilotReady;
        public readonly List<int> Typed = [];
        public readonly List<KeyPlan> Plans = [];
        public int Reads;
        // What the agent does with keys that reached it (a folder-trust answer).
        public Action<KeyPlan>? OnTyped;
        public InjectionResult? Fails;
        public Exception? Throws;
        // Runs while a plan waits for the typing slot: what changes at the machine meanwhile.
        public Action? BeforeTyping;
        // What the worker says once the keys went, when not "typed": an Esc that closed the
        // agent can end its attach first (interrupted).
        public InjectionResult? Then;

        // As the worker does: nothing is typed unless the rows a send expects are still there.
        public Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default)
        {
            lock (Typed) { Typed.Add(processId); Plans.Add(plan); }
            if (Throws is { } broken) { throw broken; }
            if (Fails is { } failed) { return Task.FromResult(failed); }
            // As the worker asks once it holds the typing slot.
            BeforeTyping?.Invoke();
            if (plan.Proceed?.Invoke() is { } stop) { return Task.FromResult(new InjectionResult(InjectionStatus.NothingTyped, $"stopped before typing: {stop}")); }
            var rows = Rows ?? [];
            foreach (var expect in plan.Sends.SelectMany(s => s.Expect ?? []))
            {
                if (expect.Row >= rows.Count || rows[expect.Row].TrimEnd() != expect.Text.TrimEnd()) { return Task.FromResult(new InjectionResult(InjectionStatus.NothingTyped, "fail screen 0")); }
            }
            OnTyped?.Invoke(plan);
            return Task.FromResult(Then ?? new InjectionResult(InjectionStatus.Typed, "ok"));
        }

        // A window that cannot be read: one whose process has gone.
        public Func<int, bool>? Unreadable;

        public Task<ConsoleScreen> ReadScreenAsync(int processId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Reads);
            if (Unreadable?.Invoke(processId) == true) { throw new IOException($"no console for pid {processId}"); }
            return Task.FromResult(new ConsoleScreen(Rows ?? [], null));
        }
    }

    sealed class FakeLocator : IAgentLocator
    {
        public readonly Dictionary<string, string> Installed = [];

        public string? Find(Launcher launcher, AgentConfig config) => Installed.GetValueOrDefault(launcher.Id);
    }

    sealed class FakeRunner : ICommandRunner
    {
        public string Profiles = "No profiles configured.\n";
        // Runs while the host waits for Agency's session list.
        public Action? OnSessions;

        public Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (arguments[0] == "config") { return Task.FromResult(new CommandResult(true, false, 0, Profiles)); }
            OnSessions?.Invoke();
            return Task.FromResult(new CommandResult(true, false, 0, """{"sessions":[]}"""));
        }
    }

    // Plays the agent: on start, it writes the session an agent would register (a Copilot
    // session folder and its process; for Codex, a registration under a session id of its own),
    // unless told it does not register.
    sealed class FakeStarter(Fixture f) : IAgentStarter
    {
        public readonly List<StartSpec> Specs = [];
        public string? Unavailable_;
        public volatile bool Registers = true;
        public bool ExitsAtOnce;
        // The launcher hands its session to a process of its own (its pid + 3000) and exits.
        public bool HandsOn;
        public readonly string CodexId = "66666666-6666-4666-8666-666666666666";
        public readonly List<int> Pids = [];
        readonly HashSet<int> running = [];
        readonly Dictionary<int, int> parents = [];
        int next = 4000;

        public string? Unavailable() => Unavailable_;

        public string? CommandLine(StartSpec spec, out string? problem) => WindowsCommandLine.For(spec.Executable, spec.Arguments, out problem)?.CommandLine;

        public Started Start(StartSpec spec)
        {
            int pid;
            lock (Specs) { Specs.Add(spec); pid = ++next; Pids.Add(pid); if (!ExitsAtOnce) { running.Add(pid); } if (HandsOn) { running.Add(pid + 3000); } }
            if (Registers) { Register(spec, HandsOn ? pid + 3000 : pid); }
            return new Started(pid, DateTimeOffset.UtcNow);
        }

        public bool Running(Started started)
        {
            lock (Specs) { return running.Contains(started.ProcessId); }
        }

        // As the process tree does: the process started, or one under it.
        public bool? Owns(Started started, int processId)
        {
            lock (Specs)
            {
                if (!running.Contains(started.ProcessId)) { return null; }
                return processId == started.ProcessId || parents.GetValueOrDefault(processId) == started.ProcessId;
            }
        }

        public void Exit(int pid)
        {
            lock (Specs) { running.Remove(pid); }
        }

        public void Register(StartSpec spec) => Register(spec, 9000 + Specs.IndexOf(spec));

        // Codex's own process, under the launcher started for it, registering the session it chose.
        public void RegisterCodex(StartSpec spec, int launcher, int pid, string id)
        {
            lock (Specs) { parents[pid] = launcher; }
            RegisterCodex(spec, pid, id);
        }

        void RegisterCodex(StartSpec spec, int pid, string id)
        {
            var transcript = f.CodexTranscript(id);
            Fixture.Append(transcript, Codex.Line("session_meta", new { id, cwd = spec.WorkingDirectory }, 0));
            f.CodexRegistration(id, pid, transcript, updated: DateTimeOffset.UtcNow);
            f.Processes.Run(AgentKind.Codex, pid, "codex", DateTimeOffset.UtcNow);
        }

        void Register(StartSpec spec, int pid)
        {
            if (spec.Executable.EndsWith("codex.cmd", StringComparison.Ordinal))
            {
                RegisterCodex(spec, pid, CodexId);
                return;
            }
            var args = spec.Arguments.ToList();
            var id = args[args.IndexOf("--session-id") + 1];
            f.CopilotSession(id, pid, lockWrite: DateTimeOffset.UtcNow.AddSeconds(1));
            f.Processes.Run(AgentKind.Copilot, pid, $"copilot --session-id {id}", DateTimeOffset.UtcNow);
        }
    }
}
