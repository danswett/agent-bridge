using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Xunit;

// The host's local API end to end: fixture sessions read by the real SessionSource, served by
// the same SessionService the relay uses, over a real pipe (a Unix socket off Windows), with
// the host's management ops beside them.
public sealed class LocalApiTests : IAsyncLifetime
{
    const string Id = "88888888-8888-4888-8888-888888888888";
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    readonly Fixture f = new();
    readonly string profile;
    readonly List<IAsyncDisposable> owned = [];
    SessionSource source = null!;
    SessionService service = null!;
    HostControl control = null!;
    LocalServer server = null!;
    string events = "";
    int stops;
    int restarts;
    // Who each connection is. The app, unless a test says otherwise.
    readonly Callers callers = new();
    readonly List<string> logged = [];
    readonly List<bool> applied = [];
    Func<Uri, Action<string, string>, CancellationToken, Task<SignInResult>> signIn = (_, _, _) => Task.FromException<SignInResult>(new InvalidOperationException("no relay in these tests"));
    (SignInResult Account, Uri Relay)? saved;
    RelayAccount? credential;

    public LocalApiTests()
    {
        // Short: a Unix socket path must fit about 100 bytes, and the fixture's root is long.
        profile = Path.Combine(Path.GetTempPath(), "abh-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(profile);
    }

    public async Task InitializeAsync()
    {
        events = f.CopilotSession(Id, 100, name: "Served session");
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.ToolStart("c1", "powershell", new { command = "dir" }, 3));
        f.Processes.Run(AgentKind.Copilot, 100);
        source = new SessionSource(f.Options(), f.Processes, TimeProvider.System);
        await source.RefreshAsync();
        var machine = new MachineInfo("host-id", "Test host", "windows", "0.1.0");
        service = new SessionService(source, machine, push: new PushNotifier(source, machine, PushStore.Load(null, "acct")));
        control = new HostControl(new HostControlOptions
        {
            Profile = "test",
            Folder = profile,
            Version = "0.1.0",
            Dev = true,
            Machine = machine,
            Running = new HostSettingsView(RemoteApproval: true),
            ApplyRemoteFolderTrust = on => { lock (applied) { applied.Add(on); } },
            Relay = () => new Uri("https://relay.example/"),
            Credential = () => credential,
            SignIn = (relay, show, ct) => signIn(relay, show, ct),
            SignedIn = (account, relay) => saved = (account, relay),
            Stop = () => Interlocked.Increment(ref stops),
            Restart = () => Interlocked.Increment(ref restarts),
        });
        server = LocalServer.Start(LocalEndpoint.For(profile), LocalApi.Handler(service, control, callers, m => { lock (logged) { logged.Add(m); } }));
        owned.Add(server);
    }

    public async Task DisposeAsync()
    {
        foreach (var o in owned) { await o.DisposeAsync(); }
        await source.DisposeAsync();
        f.Dispose();
        try { Directory.Delete(profile, recursive: true); } catch (IOException) { }
    }

    async Task<LocalChannel> ConnectAsync()
    {
        var ch = await LocalClient.ConnectAsync(LocalEndpoint.For(profile), Patience);
        owned.Add(ch);
        return ch;
    }

    static async Task<ChannelEvent> NextAsync(LocalChannel ch)
    {
        using var cts = new CancellationTokenSource(Patience);
        return await ch.Events.ReadAsync(cts.Token);
    }

    [Fact]
    public async Task Hello_on_the_pipe_offers_the_sessions_ops_and_host_management_but_not_web_push()
    {
        var ch = await ConnectAsync();

        var hello = await ch.HelloAsync();

        Assert.Equal("Test host", hello.Machine.Name);
        Assert.Contains(Capabilities.SessionsRead, hello.Capabilities);
        Assert.Contains(HostOps.Capability, hello.Capabilities);
        Assert.DoesNotContain(Capabilities.Push, hello.Capabilities);
    }

    [Fact]
    public async Task Subscribing_on_the_pipe_sends_the_snapshot_then_live_changes_as_the_relay_does()
    {
        var ch = await ConnectAsync();

        var result = await ch.CallAsync<SubscribeResult>(Ops.SessionsSubscribe);
        Assert.Equal(1, result.Count);
        var upsert = await NextAsync(ch);
        var synced = await NextAsync(ch);
        Assert.Equal((Events.SessionUpsert, Id), (upsert.Name, upsert.Data.GetProperty("id").GetString()));
        Assert.Equal((Events.SessionsSynced, result.Revision), (synced.Name, synced.Data.GetProperty("revision").GetInt64()));

        Fixture.Append(events, Copilot.ToolDone("c1", 4), Copilot.Assistant("listed the folder", 5));
        await source.RefreshAsync();
        var live = await NextAsync(ch);
        Assert.Equal("listed the folder", live.Data.GetProperty("activity").GetProperty("response").GetString());
        Assert.Equal([1L, 2L, 3L], new[] { upsert, synced, live }.Select(e => e.Seq));
        Assert.Equal(1, service.Subscribers);
    }

    [Fact]
    public async Task Getting_a_session_returns_its_tool_history()
    {
        var ch = await ConnectAsync();

        var session = await ch.CallAsync<Session>(Ops.SessionGet, new { id = Id });

        Assert.Equal("powershell", Assert.Single(session.Activity.History!).Name);
    }

    [Fact]
    public async Task An_answer_on_the_pipe_gets_the_same_outcome_a_phone_would()
    {
        var ch = await ConnectAsync();

        var result = await ch.CallAsync<AnswerResult>(Ops.SessionAnswer, new { id = Id, question = "q1", request = Guid.NewGuid().ToString(), text = "yes" });

        Assert.Equal(AnswerOutcome.Refused, result.Outcome);
        Assert.Equal("this host does not take answers; answer at the machine", result.Reason);
    }

    [Fact]
    public async Task Web_push_registration_is_refused_on_the_pipe()
    {
        var ch = await ConnectAsync();

        var refused = await Assert.ThrowsAsync<HostErrorException>(() => ch.CallAsync(Ops.PushList));

        Assert.Equal("notifications are for devices that reach this host through the relay", refused.Message);
    }

    [Fact]
    public async Task Host_status_says_what_is_running_and_that_nobody_has_signed_in()
    {
        var ch = await ConnectAsync();

        var status = await ch.CallAsync<HostStatus>(HostOps.Status);

        Assert.Equal(("test", "0.1.0", true, Environment.ProcessId), (status.Profile, status.Version, status.Dev, status.ProcessId));
        Assert.Equal(RelayState.SignedOut, status.Relay.State);
        Assert.Null(status.Account);
        Assert.Equal(SignInState.Idle, status.SignIn.State);
        Assert.Equal(new HostSettingsView(true), status.Settings);
        Assert.False(status.RestartRequired);
    }

    [Fact]
    public async Task A_status_subscription_hears_each_change_after_its_response()
    {
        var ch = await ConnectAsync();
        var first = await ch.CallAsync<HostStatus>(HostOps.Subscribe);
        Assert.Equal(RelayState.SignedOut, first.Relay.State);

        control.SetRelay(RelayState.Connected, "as octo");

        var ev = await NextAsync(ch);
        Assert.Equal(HostOps.StatusEvent, ev.Name);
        var status = ev.Data.Deserialize<HostStatus>(ProtocolJson.Options)!;
        Assert.Equal((RelayState.Connected, "as octo"), (status.Relay.State, status.Relay.Detail));
    }

    [Fact]
    public async Task Settings_from_the_app_are_written_beside_what_the_person_had_and_need_a_restart()
    {
        File.WriteAllText(Path.Combine(profile, "config.json"), """{ "workspaces": ["C:\\src"], "permissions": { "other": 1 } }""");
        var ch = await ConnectAsync();

        var result = await ch.CallAsync<SettingsResult>(HostOps.Settings, new { remoteApproval = false, name = "  Desk\u0007top  " });

        Assert.Equal(new HostSettingsView(false, "Desktop"), result.Settings);
        Assert.True(result.RestartRequired);
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(profile, "config.json")))!;
        Assert.Equal("C:\\src", saved["workspaces"]![0]!.GetValue<string>());
        Assert.Equal(1, saved["permissions"]!["other"]!.GetValue<int>());
        Assert.False(saved["permissions"]!["remoteApproval"]!.GetValue<bool>());
        Assert.Equal("Desktop", saved["name"]!.GetValue<string>());
        Assert.False(HostSettings.Load(profile).Settings.RemoteApproval);
        Assert.True((await ch.CallAsync<HostStatus>(HostOps.Status)).RestartRequired);
    }

    [Fact]
    public async Task Answering_folder_trust_from_a_persons_devices_is_turned_on_from_the_app_beside_the_launch_settings_there()
    {
        File.WriteAllText(Path.Combine(profile, "config.json"), """{ "launch": { "workspaces": ["C:\\src"] } }""");
        var ch = await ConnectAsync();
        Assert.False((await ch.CallAsync<HostStatus>(HostOps.Status)).Settings.RemoteFolderTrust);

        var result = await ch.CallAsync<SettingsResult>(HostOps.Settings, new { remoteFolderTrust = true });

        Assert.True(result.Settings.RemoteFolderTrust);
        // Applied to the running host at once, so it can be allowed while a question waits.
        Assert.Equal([true], applied);
        Assert.False(result.RestartRequired);
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(profile, "config.json")))!;
        Assert.Equal("C:\\src", saved["launch"]!["workspaces"]![0]!.GetValue<string>());
        Assert.True(HostSettings.Load(profile).Settings.Launch.RemoteFolderTrust);
    }

    [Fact]
    public async Task Settings_are_never_written_over_a_config_file_that_cannot_be_read()
    {
        File.WriteAllText(Path.Combine(profile, "config.json"), "{ not json");
        var ch = await ConnectAsync();

        var refused = await Assert.ThrowsAsync<HostErrorException>(() => ch.CallAsync(HostOps.Settings, new { remoteApproval = true }));

        Assert.Contains("is not valid JSON", refused.Message);
        Assert.Equal("{ not json", File.ReadAllText(Path.Combine(profile, "config.json")));
    }

    [Fact]
    public async Task Signing_in_shows_the_device_code_and_keeps_the_account_once_the_person_enters_it()
    {
        var entered = new TaskCompletionSource();
        signIn = async (relay, show, ct) =>
        {
            show("ABCD-1234", "https://github.com/login/device");
            await entered.Task.WaitAsync(ct);
            return new SignInResult("token", "acct-1", "octo");
        };
        var ch = await ConnectAsync();
        await ch.CallAsync<HostStatus>(HostOps.Subscribe);

        var pending = await ch.CallAsync<SignInStatus>(HostOps.SignIn, new { relay = "https://relay.example" });
        Assert.Equal((SignInState.Pending, "ABCD-1234", "https://github.com/login/device"), (pending.State, pending.UserCode, pending.VerificationUri));
        var again = await ch.CallAsync<SignInStatus>(HostOps.SignIn, new { });
        Assert.Equal("ABCD-1234", again.UserCode);

        entered.SetResult();
        HostStatus status;
        do { status = (await NextAsync(ch)).Data.Deserialize<HostStatus>(ProtocolJson.Options)!; }
        while (status.SignIn.State != SignInState.Idle);
        Assert.Equal(RelayState.Connecting, status.Relay.State);
        Assert.Equal(("octo", new Uri("https://relay.example")), (saved!.Value.Account.Login, saved.Value.Relay));
    }

    [Fact]
    public async Task A_sign_in_that_fails_says_why_and_a_relay_that_is_not_https_is_refused()
    {
        signIn = (_, _, _) => Task.FromException<SignInResult>(new InvalidOperationException("sign-in failed: access_denied"));
        var ch = await ConnectAsync();

        var failed = await ch.CallAsync<SignInStatus>(HostOps.SignIn, new { });
        Assert.Equal((SignInState.Failed, "sign-in failed: access_denied"), (failed.State, failed.Error));

        var refused = await Assert.ThrowsAsync<HostErrorException>(() => ch.CallAsync(HostOps.SignIn, new { relay = "http://relay.example" }));
        Assert.Equal("the relay must be an https URL", refused.Message);
        Assert.Null(saved);
    }

    [Fact]
    public async Task The_app_reads_the_relay_account_so_it_can_reach_the_other_machines_without_signing_in_again()
    {
        var ch = await ConnectAsync();
        var none = await Assert.ThrowsAsync<HostErrorException>(() => ch.CallAsync(HostOps.RelayAccount));
        Assert.Equal("this host is not signed in to the relay", none.Message);

        credential = new RelayAccount("https://relay.example/", "token-1", "acct-1", "octo");
        var account = await ch.CallAsync<RelayAccount>(HostOps.RelayAccount);

        Assert.Equal(new RelayAccount("https://relay.example/", "token-1", "acct-1", "octo"), account);
    }

    [Fact]
    public async Task Stop_and_restart_answer_first_and_then_act()
    {
        var ch = await ConnectAsync();

        var stopping = await ch.CallAsync(HostOps.Stop);
        Assert.True(stopping.GetProperty("stopping").GetBoolean());
        await Wait.UntilAsync(() => Volatile.Read(ref stops) == 1, "the stop to follow its response", Patience);
        var restarting = await ch.CallAsync(HostOps.Restart);
        Assert.True(restarting.GetProperty("restarting").GetBoolean());
        await Wait.UntilAsync(() => Volatile.Read(ref restarts) == 1, "the restart to follow its response", Patience);
    }

    [Fact]
    public async Task Management_ops_never_reach_a_channel_through_the_relay()
    {
        await using var relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(1, "octo");
        var host = await relay.ConnectAsync(token, "Test host", "host");
        host.Hub.JsonOptions = ProtocolJson.Options;
        host.Hub.Handler = service.HandleAsync;
        var phone = await relay.ConnectAsync(token, "Phone", "pwa");
        await phone.Relay.WaitForPeerAsync(p => p.Id == host.Id, Patience);
        var ch = await phone.Hub.OpenAsync(host.Id);

        foreach (var op in new[] { HostOps.Status, HostOps.Stop, HostOps.Settings, HostOps.SignIn, HostOps.RelayAccount })
        {
            var response = await ch.RequestAsync(op, new { });
            Assert.Equal($"unknown op {op}", response.GetProperty("error").GetString());
        }
        Assert.Equal(0, Volatile.Read(ref stops));
    }

    // ---- §6.3 invariant 13: no agent session stops or restarts its host ----

    [Fact]
    public async Task A_process_inside_a_live_agent_session_cannot_stop_restart_reconfigure_or_sign_in_the_host_but_the_app_can()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        // This test's own parent stands in for the session's CLI, discovered as any session is.
        var parent = ProcessChain.Of(Environment.ProcessId)[1];
        const string Agent = "77777777-7777-4777-8777-777777777777";
        f.CopilotSession(Agent, parent.Id, lockWrite: DateTimeOffset.UtcNow);
        f.Processes.Run(AgentKind.Copilot, parent.Id, startedAt: parent.StartedAt);
        await source.RefreshAsync();
        // No agent names: whatever really runs this test (an agent CLI, at a developer's desk)
        // must not decide it.
        callers.Current = new CallerGuard(() => KnownAgents.Of(source, null, f.Processes), Environment.ProcessPath, agentNames: []);
        var ch = await ConnectAsync();

        var hello = await ch.HelloAsync();
        Assert.DoesNotContain(HostOps.Capability, hello.Capabilities);
        Assert.DoesNotContain(Capabilities.SessionsAnswer, hello.Capabilities);
        foreach (var op in new[] { HostOps.Restart, HostOps.Stop, HostOps.Settings, HostOps.SignIn, HostOps.SignInCancel, HostOps.RelayAccount })
        {
            var refused = await Assert.ThrowsAsync<HostErrorException>(() => ch.CallAsync(op, new { }));
            Assert.Equal($"refused: this connection is from pid {Environment.ProcessId}, inside the copilot session 77777777 (pid {parent.Id}); stopping, restarting, signing in, changing settings and reading the relay account are for the person, from the Agent Bridge app", refused.Message);
        }
        var answer = await Assert.ThrowsAsync<HostErrorException>(() => ch.CallAsync(Ops.SessionAnswer, new { id = Id, question = "q1", request = Guid.NewGuid().ToString(), text = "yes" }));
        Assert.StartsWith("refused: this connection is from pid", answer.Message);
        // Reading is still allowed: the same user can read the transcripts anyway.
        Assert.Equal(2, (await ch.CallAsync<SessionList>(Ops.SessionsList)).Sessions.Count);
        Assert.Equal(Environment.ProcessId, (await ch.CallAsync<HostStatus>(HostOps.Status)).ProcessId);
        await Task.Delay(200);
        Assert.Equal((0, 0), (Volatile.Read(ref stops), Volatile.Read(ref restarts)));
        lock (logged) { Assert.Contains($"local pipe: refused host.restart from pid {Environment.ProcessId}, inside the copilot session 77777777 (pid {parent.Id})", logged); }

        // Once that session has gone, the same process is the app again, and restarts the host.
        f.Processes.Exit(AgentKind.Copilot, parent.Id);
        await source.RefreshAsync();
        Assert.True((await ch.CallAsync(HostOps.Restart)).GetProperty("restarting").GetBoolean());
        await Wait.UntilAsync(() => Volatile.Read(ref restarts) == 1, "the app's restart", Patience);
    }

    [Fact]
    public async Task A_process_that_is_itself_an_agent_CLI_is_refused_even_before_any_session_of_it_is_found()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        callers.Current = new CallerGuard(() => [new KnownProcess(Environment.ProcessId, ProcessChain.Of(Environment.ProcessId)[0].StartedAt, "a copilot process")], Environment.ProcessPath, agentNames: []);
        var ch = await ConnectAsync();

        var refused = await Assert.ThrowsAsync<HostErrorException>(() => ch.CallAsync(HostOps.Restart));

        Assert.Equal($"refused: this connection is from a copilot process (pid {Environment.ProcessId}); stopping, restarting, signing in, changing settings and reading the relay account are for the person, from the Agent Bridge app", refused.Message);
        Assert.Equal(0, Volatile.Read(ref restarts));
    }

    [Fact]
    public async Task Only_the_app_beside_the_host_manages_it_and_any_other_process_of_the_user_still_reads_and_answers()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        callers.Current = new CallerGuard(() => [], Path.Combine(AppContext.BaseDirectory, CallerGuard.AppExecutable), agentNames: []);
        var ch = await ConnectAsync();

        var hello = await ch.HelloAsync();
        Assert.DoesNotContain(HostOps.Capability, hello.Capabilities);
        var refused = await Assert.ThrowsAsync<HostErrorException>(() => ch.CallAsync(HostOps.Stop));
        Assert.EndsWith("which is not the Agent Bridge app beside this host; only the Agent Bridge app beside this host may stop, restart, sign in, change settings or read the relay account", refused.Message);
        var answered = await ch.CallAsync<AnswerResult>(Ops.SessionAnswer, new { id = Id, question = "q1", request = Guid.NewGuid().ToString(), text = "yes" });
        Assert.Equal("this host does not take answers; answer at the machine", answered.Reason);
        await Task.Delay(200);
        Assert.Equal(0, Volatile.Read(ref stops));
    }

    [Fact]
    public async Task The_hosts_own_command_line_may_restart_it_to_migrate_but_manage_nothing_else()
    {
        callers.Current = new FixedCallerGuard(new Caller(CallerKind.HostCli, "agent-bridge-host's command line (pid 30)"));
        var ch = await ConnectAsync();

        foreach (var op in new[] { HostOps.Stop, HostOps.Settings, HostOps.SignIn })
        {
            var refused = await Assert.ThrowsAsync<HostErrorException>(() => ch.CallAsync(op, new { }));
            Assert.Contains("only the Agent Bridge app beside this host may", refused.Message);
        }
        await ch.CallAsync(HostOps.Restart);

        await Wait.UntilAsync(() => Volatile.Read(ref restarts) == 1, "the restart to follow its response", Patience);
        Assert.Equal(0, Volatile.Read(ref stops));
    }

    // Swapped by a test before it connects; asked afresh for every op.
    sealed class Callers : ICallerGuard
    {
        public ICallerGuard Current { get; set; } = new FixedCallerGuard(new Caller(CallerKind.App, "the Agent Bridge app"));
        public Caller Classify(IProtocolChannel channel, bool fresh = false) => Current.Classify(channel, fresh);
    }

    [Theory]
    [InlineData(1_000L)]
    [InlineData(long.MaxValue / 2)]
    public void Agent_ha_bridge_is_looked_for_on_the_first_status_and_then_at_most_every_five_seconds(long uptime)
    {
        var looks = 0;
        var now = uptime;
        var probe = HostControl.Cached(() => { looks++; return "agent-ha-bridge is running"; }, () => now);

        Assert.Equal("agent-ha-bridge is running", probe());
        Assert.Equal(1, looks);
        now += 4_000;
        probe();
        Assert.Equal(1, looks);
        now += 2_000;
        probe();
        Assert.Equal(2, looks);
    }
}
