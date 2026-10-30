using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;

// The Launch page (pwa/test/launch.spec.mjs): what a machine offers, every setting and the whole
// prompt sent, each press to one outcome, and the new session's card opened.
public sealed class LaunchTests : IAsyncLifetime
{
    readonly FakeHost host = new();
    readonly HoldingDispatcher ui = new();
    readonly List<JsonElement> launched = [];
    HostConnection connection = null!;
    MainViewModel main = null!;
    LaunchViewModel launch = null!;
    LaunchOptions options = Options();
    Func<JsonElement, object> outcome = r => new LaunchResult(LaunchOutcome.Started) { Session = "new-1", Launched = new LaunchedWith { Agent = "copilot", Permissions = "ask", Worktree = true } };
    int optionsAsked;

    static LaunchOptions Options() => new()
    {
        Agents =
        [
            new LaunchAgent { Id = "copilot", Label = "Copilot", Kind = AgentKind.Copilot, Models = ["gpt-6.1-sol", "claude-opus-5.5"], Efforts = ["low", "high"], Defaults = new LaunchTuning { Model = "gpt-6.1-sol" } },
            new LaunchAgent { Id = "codex", Label = "Codex", Kind = AgentKind.Codex, Models = ["gpt-6.1-sol"], NeedsPrompt = true },
            new LaunchAgent { Id = "someday", Label = "An agent from a newer host", Kind = AgentKind.Unknown },
        ],
        Workspaces = [new LaunchWorkspace { Label = "bridge", Path = @"C:\src\bridge", Isolate = true }],
        Resumable = [new ResumableSession { Id = "old-1", Agent = AgentKind.Copilot, Launcher = "copilot", Title = "Fix the flaky test", Workspace = "bridge" }],
        Defaults = new LaunchDefaults { Agent = "copilot", Workspace = "bridge", Permissions = LaunchProtocol.Ask },
    };

    public async Task InitializeAsync()
    {
        host.Capabilities.Add(LaunchProtocol.Capability);
        host.On(LaunchProtocol.Options, _ => { Interlocked.Increment(ref optionsAsked); return options; });
        host.On(LaunchProtocol.Launch, data => { lock (launched) { launched.Add(data.Clone()); } return outcome(data); });
        host.Start();
        connection = host.Connect(ui);
        await Wait.UntilAsync(() => connection.State == LinkState.Connected, "connected");
        main = new MainViewModel(connection, new CardContext { Deliveries = new Deliveries(connection, ui) { RetryDelay = TimeSpan.FromMilliseconds(100) }, Folds = new FoldStore(null), Capabilities = () => connection.Capabilities, Host = connection }, new UsageViewModel());
        launch = new LaunchViewModel(main, ui);
        launch.Opened();
        await Wait.UntilAsync(() => launch.ShowForm, "the machine's choices");
    }

    public async Task DisposeAsync()
    {
        await connection.DisposeAsync();
        await host.DisposeAsync();
    }

    static readonly string Long = "Read docs/design.md,\nthen implement the next slice. " + new string('x', 400);

    JsonElement[] Sent() { lock (launched) { return [.. launched]; } }

    static Choice Pick(IReadOnlyList<Choice> list, string value) => list.Single(c => c.Value == value);

    [Fact]
    public async Task A_launch_sends_every_setting_and_the_whole_prompt_then_opens_the_new_sessions_card()
    {
        var opened = new List<string>();
        main.OpenRequested += c => opened.Add(c.Id);
        Assert.Equal(["copilot", "codex"], launch.Agents.Select(a => a.Value));
        Assert.Equal(["", "gpt-6.1-sol", "claude-opus-5.5"], launch.Models.Select(m => m.Value));
        Assert.Equal("gpt-6.1-sol", launch.Model!.Value);
        Assert.Equal("bridge (new worktree)", launch.Workspace!.Label);
        Assert.False(launch.ShowContext);

        ui.Post(() =>
        {
            launch.Model = Pick(launch.Models, "claude-opus-5.5");
            launch.Effort = Pick(launch.Efforts, "high");
            launch.Permissions = Pick(launch.PermissionChoices, LaunchProtocol.AllowAll);
            launch.Prompt = Long;
            launch.Launch();
        });

        await Wait.UntilAsync(() => launch.State == "started", "started");
        var sent = Assert.Single(Sent());
        Assert.Equal(("copilot", "bridge", "claude-opus-5.5", "high", "allowAll"), (sent.GetProperty("agent").GetString(), sent.GetProperty("workspace").GetString(), sent.GetProperty("model").GetString(), sent.GetProperty("effort").GetString(), sent.GetProperty("permissions").GetString()));
        Assert.Equal(Long, sent.GetProperty("prompt").GetString());
        Assert.False(sent.TryGetProperty("context", out _));
        Assert.False(sent.TryGetProperty("resume", out _));
        Assert.Equal("Session started in a new worktree.", launch.StatusText);
        Assert.Equal("", launch.Prompt);
        Assert.True(launch.ShowOpen);

        // The card arrives after the outcome, and is opened then.
        await host.SendAsync(Events.SessionUpsert, Make.Session("new-1", updated: Make.T0.AddMinutes(10)));
        await Wait.UntilAsync(() => opened.Contains("new-1"), "the new session's card to open");
    }

    [Fact]
    public async Task Resuming_reopens_a_session_in_its_own_folder_so_no_workspace_is_sent_and_it_leaves_the_list()
    {
        ui.Post(() => launch.Resume = Pick(launch.Resumes, "old-1"));
        Assert.False(launch.ShowWorkspace);
        Assert.Equal("A resumed session reopens in its own folder.", launch.Note);
        Assert.Equal(["copilot"], launch.Agents.Select(a => a.Value));
        options = options with { Resumable = [] };

        ui.Post(launch.Launch);

        await Wait.UntilAsync(() => launch.State == "started", "started");
        var sent = Assert.Single(Sent());
        Assert.Equal("old-1", sent.GetProperty("resume").GetString());
        Assert.False(sent.TryGetProperty("workspace", out _));
        await Wait.UntilAsync(() => launch.Resumes.Count == 1 && !launch.ShowResume, "the resumed session to leave the list");
        Assert.True(launch.ShowWorkspace);
    }

    [Fact]
    public void An_agent_that_starts_only_on_a_first_message_is_not_launched_without_one()
    {
        ui.Post(() => launch.Agent = Pick(launch.Agents, "codex"));
        Assert.Equal("Codex starts on a first message: what should it do?", launch.PromptPlaceholder);
        // The model picked for Copilot does not carry over to Codex.
        Assert.Equal("", launch.Model!.Value);

        ui.Post(launch.Launch);

        Assert.Equal("Not launched: Codex starts its session only on a first message; give it a prompt.", launch.Problem);
        Assert.Equal("", launch.StatusText);
        Assert.Empty(Sent());
    }

    [Fact]
    public async Task Busy_says_why_and_starts_nothing_and_the_next_press_is_a_new_launch()
    {
        outcome = _ => new LaunchResult(LaunchOutcome.Busy, "Claude in bridge is waiting for a person to answer whether to trust that folder");
        ui.Post(launch.Launch);
        await Wait.UntilAsync(() => launch.State == "busy", "busy");
        Assert.Equal("Not launched: Claude in bridge is waiting for a person to answer whether to trust that folder. Try again in a moment.", launch.StatusText);
        Assert.Equal(Tone.Warn, launch.StatusTone);

        outcome = _ => new LaunchResult(LaunchOutcome.Started) { Session = "new-2" };
        ui.Post(launch.Launch);
        await Wait.UntilAsync(() => launch.State == "started", "started");
        var requests = Sent().Select(s => s.GetProperty("request").GetString()).ToList();
        Assert.Equal(2, requests.Distinct().Count());
    }

    [Fact]
    public async Task A_launch_whose_response_is_lost_is_sent_again_with_the_same_request_and_starts_once()
    {
        var gate = new TaskCompletionSource();
        var first = true;
        outcome = _ =>
        {
            // Held until the pipe has closed, so this first outcome is surely lost.
            if (first) { first = false; gate.SetResult(); SpinWait.SpinUntil(() => host.Channels.All(c => !c.IsOpen), FakeHost.Patience); }
            return new LaunchResult(LaunchOutcome.Started) { Session = "new-1" };
        };
        ui.Post(launch.Launch);
        await gate.Task.WaitAsync(FakeHost.Patience);
        await host.StopAsync();
        host.Start();

        await Wait.UntilAsync(() => launch.State == "started", "started");
        var requests = Sent().Select(s => s.GetProperty("request").GetString()).ToList();
        Assert.True(requests.Count >= 2);
        Assert.Single(requests.Distinct());
    }

    [Fact]
    public async Task A_resent_launch_that_comes_back_busy_was_not_launched_and_says_so()
    {
        var gate = new TaskCompletionSource();
        var first = true;
        outcome = _ =>
        {
            if (first) { first = false; gate.SetResult(); SpinWait.SpinUntil(() => host.Channels.All(c => !c.IsOpen), FakeHost.Patience); return new LaunchResult(LaunchOutcome.Started) { Session = "never-seen" }; }
            return new LaunchResult(LaunchOutcome.Busy, "Busy: Copilot in bridge is waiting for a person to answer whether to trust that folder");
        };
        ui.Post(launch.Launch);
        await gate.Task.WaitAsync(FakeHost.Patience);
        await host.StopAsync();
        host.Start();

        await Wait.UntilAsync(() => launch.State == "busy", "busy");
        Assert.Equal(2, Sent().Length);
    }

    [Fact]
    public async Task An_outcome_this_app_does_not_know_reads_as_unconfirmed()
    {
        outcome = _ => new { outcome = "queued" };
        ui.Post(launch.Launch);

        await Wait.UntilAsync(() => launch.State == "unconfirmed", "unconfirmed");
        Assert.Equal("Not confirmed whether the session started. Check at the machine before launching again.", launch.StatusText);
    }

    [Fact]
    public async Task A_prompt_typed_while_a_launch_is_on_its_way_is_kept_when_it_starts()
    {
        var gate = new TaskCompletionSource();
        outcome = _ => { gate.Task.Wait(FakeHost.Patience); return new LaunchResult(LaunchOutcome.Started) { Session = "new-1" }; };
        ui.Post(() => { launch.Prompt = "First."; launch.Launch(); });
        await Wait.UntilAsync(() => Sent().Length == 1, "the launch to go");

        ui.Post(() => launch.Prompt = "Next.");
        gate.SetResult();

        await Wait.UntilAsync(() => launch.State == "started", "started");
        Assert.Equal("Next.", launch.Prompt);
    }

    [Fact]
    public async Task The_choices_are_fetched_only_once_the_page_is_opened()
    {
        var asked = optionsAsked;
        var unseen = new LaunchViewModel(main, ui);
        await Task.Delay(500);
        Assert.Equal(asked, optionsAsked);

        unseen.Opened();
        await Wait.UntilAsync(() => unseen.ShowForm, "the machine's choices");
        Assert.Equal(asked + 1, optionsAsked);
    }

    [Fact]
    public void A_prompt_too_long_once_sent_is_not_launched()
    {
        ui.Post(() => { launch.Prompt = new string('\\', 11000); launch.Launch(); });

        Assert.StartsWith("Not launched: the prompt is too long once sent", launch.Problem);
        Assert.Empty(Sent());
    }

    [Fact]
    public async Task A_reload_that_fails_says_so_rather_than_keeping_old_choices()
    {
        host.On(LaunchProtocol.Options, _ => throw new InvalidOperationException("the catalog could not be read"));
        await launch.RefreshAsync();

        Assert.False(launch.ShowForm);
        Assert.StartsWith("Couldn't get the choices from Test PC", launch.Note);
        Assert.True(launch.ShowReload);
    }

    [Fact]
    public async Task Try_again_after_a_failed_send_shows_what_became_of_it_not_an_older_error()
    {
        var fail = true;
        outcome = _ => fail ? throw new InvalidOperationException("the launcher could not be found") : new LaunchResult(LaunchOutcome.Started) { Session = "new-1" };
        ui.Post(launch.Launch);
        await Wait.UntilAsync(() => launch.ShowResend, "the failure");
        ui.Post(() => { launch.Agent = Pick(launch.Agents, "codex"); launch.Prompt = ""; launch.Launch(); });
        // Said apart: the failure, and what Try again would send, stay as they were.
        Assert.StartsWith("Not launched: Codex", launch.Problem);
        Assert.Contains("Try again sends that same launch to Test PC: copilot in bridge, ask permission.", launch.StatusText);

        fail = false;
        ui.Post(launch.Resend);

        await Wait.UntilAsync(() => launch.State == "started", "started");
        Assert.StartsWith("Session started", launch.StatusText);
    }

    [Fact]
    public async Task A_launch_the_machine_reports_failed_can_be_tried_again_as_a_new_request()
    {
        outcome = _ => new LaunchResult(LaunchOutcome.Failed, "the agent process exited at once");
        ui.Post(launch.Launch);
        await Wait.UntilAsync(() => launch.State == "failed", "failed");
        Assert.True(launch.ShowResend);

        outcome = _ => new LaunchResult(LaunchOutcome.Started) { Session = "new-1" };
        ui.Post(launch.Resend);

        await Wait.UntilAsync(() => launch.State == "started", "started");
        var requests = Sent().Select(s => s.GetProperty("request").GetString()).ToList();
        Assert.Equal(2, requests.Distinct().Count());
    }

    [Fact]
    public async Task Try_again_after_a_host_error_never_sends_the_launch_to_a_host_that_restarted_since()
    {
        outcome = _ => throw new InvalidOperationException("the launcher could not be found");
        ui.Post(launch.Launch);
        await Wait.UntilAsync(() => launch.ShowResend, "the failure");
        // The host may have started something before it failed; a new process would not know.
        await host.StopAsync();
        host.Status = FakeHost.StatusOf() with { ProcessId = 4343 };
        host.Start();
        await Wait.UntilAsync(() => connection.HostInstance?.StartsWith("4343@", StringComparison.Ordinal) == true && launch.ShowResend, "the restarted host");

        ui.Post(launch.Resend);

        await Wait.UntilAsync(() => launch.State == "unconfirmed", "unconfirmed");
        Assert.Single(Sent());
    }

    [Fact]
    public async Task A_launch_pressed_as_the_connection_drops_says_it_is_waiting_for_it()
    {
        // The page has not heard yet that the host went.
        ui.Hold();
        await host.StopAsync();
        await Wait.UntilAsync(() => host.Channels.All(c => !c.IsOpen), "the pipe to close");

        launch.Launch();

        Assert.Equal("waiting", launch.State);
        Assert.StartsWith("Waiting for the connection", launch.StatusText);
        ui.Release();
    }

    [Fact]
    public async Task A_profile_and_context_go_as_picked_and_start_again_from_defaults_for_another_agent()
    {
        options = options with
        {
            Agents =
            [
                new LaunchAgent { Id = "copilot", Label = "Copilot", Kind = AgentKind.Copilot, Profiles = ["work", "home"], Contexts = ["200k", "1m"], Defaults = new LaunchTuning { Profile = "home" } },
                new LaunchAgent { Id = "claude", Label = "Claude", Kind = AgentKind.Claude, Contexts = ["1m"] },
            ],
        };
        await launch.RefreshAsync();
        Assert.True(launch.ShowProfile && launch.ShowContext);
        Assert.Equal("home", launch.Profile!.Value);

        ui.Post(() => { launch.Profile = Pick(launch.Profiles, "work"); launch.Context = Pick(launch.Contexts, "1m"); });
        // Another agent: no profiles, its own contexts from its default; and back, from Copilot's defaults.
        ui.Post(() => launch.Agent = Pick(launch.Agents, "claude"));
        Assert.False(launch.ShowProfile);
        Assert.Equal("", launch.Context!.Value);
        Assert.Null(launch.Settings().Profile);
        ui.Post(() => launch.Agent = Pick(launch.Agents, "copilot"));
        Assert.Equal(("home", ""), (launch.Profile!.Value, launch.Context!.Value));

        ui.Post(() => { launch.Profile = Pick(launch.Profiles, "work"); launch.Context = Pick(launch.Contexts, "200k"); launch.Launch(); });

        await Wait.UntilAsync(() => launch.State == "started", "started");
        var sent = Assert.Single(Sent());
        Assert.Equal(("copilot", "work", "200k"), (sent.GetProperty("agent").GetString(), sent.GetProperty("profile").GetString(), sent.GetProperty("context").GetString()));
    }

    [Fact]
    public async Task The_status_is_announced_only_when_it_changes()
    {
        ui.Post(launch.Launch);
        await Wait.UntilAsync(() => launch.State == "started", "started");
        var raised = 0;
        launch.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LaunchViewModel.StatusText)) { Interlocked.Increment(ref raised); } };

        // Choices read again, and the machine's status updated: nothing new to say.
        await launch.RefreshAsync();
        await host.SendAsync(HostOps.StatusEvent, FakeHost.StatusOf());
        await Task.Delay(200);

        Assert.Equal(0, Volatile.Read(ref raised));
    }

    [Fact]
    public async Task A_machine_with_no_approved_folder_says_how_to_approve_one_at_the_machine()
    {
        options = options with { Workspaces = [], Resumable = [] };
        await launch.RefreshAsync();

        Assert.Equal("No folder on Test PC is approved for launching yet. At the machine, run: agent-bridge-host workspace add <folder>", launch.Note);
        Assert.True(launch.NoteWarns);
        ui.Post(launch.Launch);
        Assert.Equal("Not launched: no workspace is approved on this machine yet; approve one at the machine (agent-bridge-host workspace add <folder>).", launch.Problem);
        Assert.Empty(Sent());
    }

    [Fact]
    public async Task What_the_machine_says_is_busy_is_shown_before_any_press()
    {
        options = options with { Busy = "Busy: Copilot in bridge is waiting for a person to answer whether to trust that folder" };
        await launch.RefreshAsync();

        Assert.Equal("Test PC is busy: Busy: Copilot in bridge is waiting for a person to answer whether to trust that folder.", launch.Busy);
    }

    [Fact]
    public async Task A_machine_that_cannot_launch_is_not_offered()
    {
        Assert.True(launch.Available);
        host.Capabilities.Remove(LaunchProtocol.Capability);
        await host.StopAsync();
        host.Start();

        await Wait.UntilAsync(() => !launch.Available, "Launch to go");
        Assert.Empty(launch.Machines);
    }
}

// Launching on another machine of the account: its own choices, and the launch goes to it.
public sealed class RemoteLaunchTests : IAsyncLifetime
{
    readonly FakeHost localHost = new();
    readonly FakeHost remoteHost = new();
    readonly LockedDispatcher ui = new();
    // The other machine's deliveries' notices, which a test can hold as a busy UI thread would.
    readonly HoldingDispatcher remoteUi = new();
    readonly FleetTests.FakeFleetLink link = new();
    HostConnection local = null!;
    MainViewModel main = null!;
    Fleet fleet = null!;

    public async Task InitializeAsync()
    {
        foreach (var (h, label) in new[] { (localHost, "here"), (remoteHost, "there") })
        {
            h.Capabilities.Add(LaunchProtocol.Capability);
            h.On(LaunchProtocol.Options, _ => new LaunchOptions
            {
                Agents = [new LaunchAgent { Id = "claude", Label = "Claude", Kind = AgentKind.Claude }],
                Workspaces = [new LaunchWorkspace { Label = label, Path = $"/{label}" }, new LaunchWorkspace { Label = $"{label}-2", Path = $"/{label}-2" }],
                Defaults = new LaunchDefaults { Agent = "claude", Workspace = label, Permissions = LaunchProtocol.Ask },
            });
            h.On(LaunchProtocol.Launch, _ => new LaunchResult(LaunchOutcome.Started) { Session = $"{label}-new" });
        }
        remoteHost.On(Ops.Hello, _ => new HelloResult(1, new MachineInfo("remote-id", "Build server", "linux", "0.1.0"),
            [Capabilities.SessionsRead, Capabilities.SessionsAnswer, UsageProtocol.Capability, LaunchProtocol.Capability, TransferProtocol.Capability]));
        localHost.Start();
        remoteHost.Start();
        link.Endpoints["remote-id"] = remoteHost.Endpoint;
        local = localHost.Connect(ui);
        await Wait.UntilAsync(() => local.State == LinkState.Connected, "this machine's host");
        main = new MainViewModel(Build(Machine.LocalKey, local), new UsageViewModel());
        fleet = new Fleet(link, main, ui, h => Build(h.Id, new HostConnection(ct => link.OpenAsync(h.Id, ct), ui) { RetryDelay = TimeSpan.FromMilliseconds(100) }, remoteUi));
        link.Set(FleetState.Connected, ("remote-id", "Build server"));
    }

    Machine Build(string key, HostConnection c, IUiDispatcher? deliveries = null) =>
        new(key, c, new CardContext { Deliveries = new Deliveries(c, deliveries ?? ui), Folds = new FoldStore(null), Capabilities = () => c.Capabilities, Host = c });

    public async Task DisposeAsync()
    {
        await fleet.DisposeAsync();
        await local.DisposeAsync();
        await localHost.DisposeAsync();
        await remoteHost.DisposeAsync();
    }

    [Fact]
    public async Task Another_machine_offers_its_own_choices_and_a_launch_there_goes_to_its_host_only()
    {
        var launch = new LaunchViewModel(main, ui);
        launch.Opened();
        await Wait.UntilAsync(() => launch.Machines.Count == 2 && launch.ShowForm, "both machines");
        Assert.Equal(["Test PC (this machine)", "Build server"], launch.Machines.Select(m => m.Label));
        Assert.Equal("here", launch.Workspace!.Value);

        ui.Post(() => launch.SelectedMachine = launch.Machines[1]);
        await Wait.UntilAsync(() => launch.Workspace?.Value == "there", "the other machine's choices");
        Assert.Equal("Start a session on Build server", launch.Title);
        ui.Post(launch.Launch);

        await Wait.UntilAsync(() => launch.State == "started", "started");
        Assert.Equal("there", Assert.Single(remoteHost.Sent(LaunchProtocol.Launch)).GetProperty("workspace").GetString());
        Assert.Empty(localHost.Sent(LaunchProtocol.Launch));
    }

    [Fact]
    public async Task A_launch_still_opens_its_session_after_switching_to_another_machine()
    {
        var gate = new TaskCompletionSource();
        remoteHost.On(LaunchProtocol.Launch, _ => { gate.Task.Wait(FakeHost.Patience); return new LaunchResult(LaunchOutcome.Started) { Session = "there-new" }; });
        var opened = new List<string>();
        main.OpenRequested += c => opened.Add(c.Id);
        var launch = new LaunchViewModel(main, ui);
        launch.Opened();
        await Wait.UntilAsync(() => launch.Machines.Count == 2 && launch.ShowForm, "both machines");
        ui.Post(() => launch.SelectedMachine = launch.Machines[1]);
        await Wait.UntilAsync(() => launch.Workspace?.Value == "there", "the other machine's choices");
        ui.Post(() => { launch.Prompt = "Go."; launch.Launch(); });
        await Wait.UntilAsync(() => remoteHost.Sent(LaunchProtocol.Launch).Count == 1, "the launch to go");

        ui.Post(() => launch.SelectedMachine = launch.Machines[0]);
        await Wait.UntilAsync(() => launch.Workspace?.Value == "here", "this machine's choices");
        gate.SetResult();
        await remoteHost.SendAsync(Events.SessionUpsert, Make.Session("there-new", updated: Make.T0.AddMinutes(10)));

        await Wait.UntilAsync(() => opened.Contains("there-new"), "the other machine's new session to open");
        Assert.Equal("", launch.Prompt);
        // What became of it is shown whichever machine is picked now.
        Assert.Equal("Session started.", launch.StatusText);
    }

    [Fact]
    public async Task A_launch_that_failed_just_before_its_machine_left_says_so_and_another_can_go()
    {
        remoteHost.On(LaunchProtocol.Launch, _ => throw new InvalidOperationException("the launcher could not be found"));
        var launch = new LaunchViewModel(main, ui);
        launch.Opened();
        await Wait.UntilAsync(() => launch.Machines.Count == 2 && launch.ShowForm, "both machines");
        ui.Post(() => launch.SelectedMachine = launch.Machines[1]);
        await Wait.UntilAsync(() => launch.Workspace?.Value == "there", "the other machine's choices");
        // Its notices wait in the queue: the failure is in before the page hears of it.
        remoteUi.Hold();
        var remote = main.MachineOf("remote-id")!;
        ui.Post(launch.Launch);
        await Wait.UntilAsync(() => remote.Deliveries.Get("launch")?.State == DeliveryState.Failed, "the failure");

        link.Set(FleetState.Connected);

        await Wait.UntilAsync(() => launch.State == "failed", "the failure shown");
        Assert.StartsWith("Build server returned an error: ", launch.StatusText);
        Assert.Contains("The launch may have started", launch.StatusText);
        await Wait.UntilAsync(() => launch.CanLaunch, "another launch to be possible");
        remoteUi.Release();
    }

    [Fact]
    public async Task A_machine_that_drops_out_for_a_moment_comes_back_with_what_was_picked_on_it()
    {
        var launch = new LaunchViewModel(main, ui);
        launch.Opened();
        await Wait.UntilAsync(() => launch.Machines.Count == 2 && launch.ShowForm, "both machines");
        ui.Post(() => launch.SelectedMachine = launch.Machines[1]);
        await Wait.UntilAsync(() => launch.Workspace?.Value == "there", "the other machine's choices");
        ui.Post(() => { launch.Workspace = launch.Workspaces.Single(w => w.Value == "there-2"); launch.Permissions = launch.PermissionChoices.Single(c => c.Value == LaunchProtocol.AllowAll); });

        link.Set(FleetState.Connected);
        await Wait.UntilAsync(() => launch.Machines.Count == 1, "the machine to leave");
        link.Set(FleetState.Connected, ("remote-id", "Build server"));

        await Wait.UntilAsync(() => launch.SelectedMachine?.Value == "remote-id" && launch.ShowForm, "the machine to come back");
        Assert.Equal(("there-2", LaunchProtocol.AllowAll), (launch.Workspace!.Value, launch.Permissions!.Value));
    }

    [Fact]
    public async Task What_was_picked_survives_a_failed_reload_and_the_machine_dropping_out_after_it()
    {
        var fail = false;
        remoteHost.On(LaunchProtocol.Options, _ => fail ? throw new InvalidOperationException("the catalog could not be read") : new LaunchOptions
        {
            Agents = [new LaunchAgent { Id = "claude", Label = "Claude", Kind = AgentKind.Claude }],
            Workspaces = [new LaunchWorkspace { Label = "there", Path = "/there" }, new LaunchWorkspace { Label = "there-2", Path = "/there-2" }],
            Defaults = new LaunchDefaults { Agent = "claude", Workspace = "there", Permissions = LaunchProtocol.Ask },
        });
        var launch = new LaunchViewModel(main, ui);
        launch.Opened();
        await Wait.UntilAsync(() => launch.Machines.Count == 2 && launch.ShowForm, "both machines");
        ui.Post(() => launch.SelectedMachine = launch.Machines[1]);
        await Wait.UntilAsync(() => launch.Workspace?.Value == "there", "the other machine's choices");
        ui.Post(() => launch.Workspace = launch.Workspaces.Single(w => w.Value == "there-2"));
        fail = true;
        await launch.RefreshAsync();
        Assert.False(launch.ShowForm);
        fail = false;

        link.Set(FleetState.Connected);
        await Wait.UntilAsync(() => launch.Machines.Count == 1, "the machine to leave");
        link.Set(FleetState.Connected, ("remote-id", "Build server"));

        await Wait.UntilAsync(() => launch.SelectedMachine?.Value == "remote-id" && launch.ShowForm, "the machine to come back");
        Assert.Equal("there-2", launch.Workspace!.Value);
    }

    const string Brought = "0f3c4d5e-6a7b-4c8d-9e0f-a1b2c3d4e5f6";
    const string LaptopId = "3f9a2c71b0de4a5f8c6d7e8f9a0b1c2d";

    static TransferSource Laptop() => new(LaptopId, "Laptop", new byte[65],
        [new ResumableSession { Id = Brought, Agent = AgentKind.Claude, Launcher = "claude", Title = "Fix the flaky test" }]);

    [Fact]
    public async Task A_session_from_a_third_machine_is_brought_to_another_with_a_command_signed_for_the_machine_it_is_on()
    {
        link.Sources.Add(Laptop());
        remoteHost.On(LaunchProtocol.Launch, _ => new LaunchResult(LaunchOutcome.Started)
        {
            Session = "there-new",
            Launched = new LaunchedWith { Agent = "claude", Permissions = LaunchProtocol.Ask, Workspace = "there-2", Transfer = new LaunchedTransfer { From = LaptopId, Session = Brought, Bytes = 4096 } },
        });
        var launch = new LaunchViewModel(main, ui);
        launch.Opened();
        await Wait.UntilAsync(() => launch.Machines.Count == 2 && launch.ShowForm, "both machines");
        ui.Post(() => launch.SelectedMachine = launch.Machines[1]);
        await Wait.UntilAsync(() => launch.Resumes.Any(r => r.Value == $"@{LaptopId}/{Brought}"), "the other machine's session offered");

        ui.Post(() => launch.Resume = launch.Resumes.Single(r => r.Value == $"@{LaptopId}/{Brought}"));
        Assert.Equal("Fix the flaky test · on Laptop", launch.Resume!.Label);
        Assert.Equal(["claude"], launch.Agents.Select(a => a.Value));
        Assert.True(launch.ShowWorkspace);
        Assert.StartsWith("This copies the session's whole conversation, with its checkpoints and files, from Laptop to Build server", launch.Note);
        ui.Post(() => { launch.Workspace = launch.Workspaces.Single(w => w.Value == "there-2"); launch.Launch(); });

        await Wait.UntilAsync(() => launch.State == "started", "started");
        var sent = Assert.Single(remoteHost.Sent(LaunchProtocol.Launch));
        var transfer = sent.GetProperty("transfer");
        Assert.Equal((LaptopId, Brought, "body1"), (transfer.GetProperty("from").GetString(), transfer.GetProperty("session").GetString(), transfer.GetProperty("command").GetProperty("body").GetString()));
        Assert.Equal("there-2", sent.GetProperty("workspace").GetString());
        Assert.False(sent.TryGetProperty("resume", out _));
        // Signed for the machine the session is on, to go to the one launching it.
        Assert.Equal((LaptopId, Brought, "remote-id"), Assert.Single(link.Signed));
        var delivery = main.MachineOf("remote-id")!.Deliveries.Get("launch")!;
        Assert.Equal((Transfers.LaunchTimeout, Transfers.GiveUp), (delivery.Timeout, delivery.GiveUp));
        Assert.Equal("Session brought over and started. The original is still on its machine.", launch.StatusText);
    }

    [Fact]
    public async Task A_session_brought_to_this_machine_goes_to_its_host_through_the_relay_never_the_pipe()
    {
        link.Sources.Add(Laptop());
        link.Endpoints["host-id"] = localHost.Endpoint;
        localHost.Capabilities.Add(TransferProtocol.Capability);
        link.Set(FleetState.Connected, ("remote-id", "Build server"), ("host-id", "Test PC"));
        var launch = new LaunchViewModel(main, ui);
        launch.Opened();
        await Wait.UntilAsync(() => launch.Resumes.Any(r => r.Value == $"@{LaptopId}/{Brought}"), "the other machine's session offered here");

        ui.Post(() => { launch.Resume = launch.Resumes.Single(r => r.Value == $"@{LaptopId}/{Brought}"); launch.Launch(); });

        await Wait.UntilAsync(() => launch.State == "started", "started");
        Assert.Equal((LaptopId, Brought, "host-id"), Assert.Single(link.Signed));
        Assert.NotNull(fleet.CurrentLoopback!.Deliveries.Get("launch"));
        Assert.Null(main.MachineOf(Machine.LocalKey)!.Deliveries.Get("launch"));
        Assert.Equal("here", Assert.Single(localHost.Sent(LaunchProtocol.Launch)).GetProperty("workspace").GetString());
    }

    [Fact]
    public async Task A_transfer_to_this_machine_still_on_its_way_when_the_account_changes_is_unconfirmed_not_left_waiting()
    {
        var gate = new TaskCompletionSource();
        link.Sources.Add(Laptop());
        link.Endpoints["host-id"] = localHost.Endpoint;
        localHost.Capabilities.Add(TransferProtocol.Capability);
        localHost.On(LaunchProtocol.Launch, _ => { gate.Task.Wait(FakeHost.Patience); return new LaunchResult(LaunchOutcome.Started) { Session = "here-new" }; });
        link.Set(FleetState.Connected, ("remote-id", "Build server"), ("host-id", "Test PC"));
        var launch = new LaunchViewModel(main, ui);
        launch.Opened();
        await Wait.UntilAsync(() => launch.Resumes.Any(r => r.Value == $"@{LaptopId}/{Brought}"), "the other machine's session offered here");
        ui.Post(() => { launch.Resume = launch.Resumes.Single(r => r.Value == $"@{LaptopId}/{Brought}"); launch.Launch(); });
        await Wait.UntilAsync(() => localHost.Sent(LaunchProtocol.Launch).Count == 1, "the launch to go");

        link.Switch();

        await Wait.UntilAsync(() => launch.State == "unconfirmed", "the launch to be unconfirmed");
        Assert.Equal("Not confirmed: the relay connection to this machine's host changed before it said whether the launch started. Check at this machine before launching again.", launch.StatusText);
        Assert.True(launch.CanLaunch);
        gate.SetResult();
    }

    [Fact]
    public async Task A_session_picked_from_another_machine_stays_picked_when_its_machine_answers_after_the_choices()
    {
        link.Sources.Add(Laptop());
        var launch = new LaunchViewModel(main, ui);
        launch.Opened();
        await Wait.UntilAsync(() => launch.Machines.Count == 2 && launch.ShowForm, "both machines");
        ui.Post(() => launch.SelectedMachine = launch.Machines[1]);
        await Wait.UntilAsync(() => launch.Resumes.Any(r => r.Value.StartsWith('@')), "the other machine's session offered");
        ui.Post(() => launch.Resume = launch.Resumes.Single(r => r.Value.StartsWith('@')));
        ui.Post(() => launch.SelectedMachine = launch.Machines[0]);
        await Wait.UntilAsync(() => launch.Workspace?.Value == "here", "this machine's choices");
        link.HoldSources = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ui.Post(() => launch.SelectedMachine = launch.Machines[1]);
        await Wait.UntilAsync(() => launch.ShowForm && launch.Workspace?.Value == "there", "the choices, before the other machines answer");
        Assert.Equal($"@{LaptopId}/{Brought}", launch.Resume!.Value);
        ui.Post(launch.Launch);
        Assert.Equal("Not launched: the session picked on another machine is still being looked for; try again in a moment.", launch.Problem);
        Assert.Empty(remoteHost.Sent(LaunchProtocol.Launch));

        link.HoldSources.SetResult();
        await Wait.UntilAsync(() => launch.Note.StartsWith("This copies", StringComparison.Ordinal), "the session found");
        ui.Post(launch.Launch);

        await Wait.UntilAsync(() => launch.State == "started", "started");
        Assert.Equal(Brought, Assert.Single(remoteHost.Sent(LaunchProtocol.Launch)).GetProperty("transfer").GetProperty("session").GetString());
    }

    [Fact]
    public async Task Try_again_after_a_transfer_the_machine_reports_failed_signs_a_new_command()
    {
        link.Sources.Add(Laptop());
        remoteHost.On(LaunchProtocol.Launch, _ => new LaunchResult(LaunchOutcome.Failed) { Reason = "the agent did not start" });
        var launch = new LaunchViewModel(main, ui);
        launch.Opened();
        await Wait.UntilAsync(() => launch.Machines.Count == 2 && launch.ShowForm, "both machines");
        ui.Post(() => launch.SelectedMachine = launch.Machines[1]);
        await Wait.UntilAsync(() => launch.Resumes.Any(r => r.Value.StartsWith('@')), "the other machine's session offered");
        ui.Post(() => { launch.Resume = launch.Resumes.Single(r => r.Value.StartsWith('@')); launch.Launch(); });
        await Wait.UntilAsync(() => launch.State == "failed" && launch.ShowResend, "the failure");

        ui.Post(launch.Resend);

        await Wait.UntilAsync(() => remoteHost.Sent(LaunchProtocol.Launch).Count == 2, "the second launch");
        Assert.Equal(["body1", "body2"], remoteHost.Sent(LaunchProtocol.Launch).Select(s => s.GetProperty("transfer").GetProperty("command").GetProperty("body").GetString()));
    }

    [Fact]
    public async Task Try_again_is_not_offered_through_a_loopback_an_account_switch_retired()
    {
        link.Sources.Add(Laptop());
        link.Endpoints["host-id"] = localHost.Endpoint;
        localHost.Capabilities.Add(TransferProtocol.Capability);
        localHost.On(LaunchProtocol.Launch, _ => new LaunchResult(LaunchOutcome.Failed) { Reason = "the agent did not start" });
        link.Set(FleetState.Connected, ("remote-id", "Build server"), ("host-id", "Test PC"));
        var launch = new LaunchViewModel(main, ui);
        launch.Opened();
        await Wait.UntilAsync(() => launch.Resumes.Any(r => r.Value == $"@{LaptopId}/{Brought}"), "the other machine's session offered here");
        ui.Post(() => { launch.Resume = launch.Resumes.Single(r => r.Value == $"@{LaptopId}/{Brought}"); launch.Launch(); });
        await Wait.UntilAsync(() => launch.State == "failed" && launch.ShowResend, "the failure");

        link.Switch();

        await Wait.UntilAsync(() => !launch.ShowResend, "Try again to go with the loopback");
        Assert.Equal("failed", launch.State);
        Assert.True(launch.CanLaunch);
        ui.Post(launch.Resend);
        Assert.Single(localHost.Sent(LaunchProtocol.Launch));
    }

    [Fact]
    public async Task A_transfer_is_measured_with_the_command_it_carries_so_a_prompt_that_fits_goes()
    {
        link.Sources.Add(Laptop());
        var launch = new LaunchViewModel(main, ui);
        launch.Opened();
        await Wait.UntilAsync(() => launch.Machines.Count == 2 && launch.ShowForm, "both machines");
        ui.Post(() => launch.SelectedMachine = launch.Machines[1]);
        await Wait.UntilAsync(() => launch.Resumes.Any(r => r.Value.StartsWith('@')), "the other machine's session offered");
        // Quotes count twice once sent: within the prompt limit, 100 bytes short of the request's
        // with the command the fake link signs (a placeholder's would be far longer).
        var bare = new LaunchRequest
        {
            Request = Guid.Empty.ToString(), Agent = "claude", Permissions = LaunchProtocol.Ask, Workspace = "there", Prompt = "",
            Transfer = new LaunchTransfer { From = LaptopId, Session = Brought, Command = new SignedCommand { Body = "body1", By = "by", Mac = "mac" } },
        };
        var prompt = new string('"', (LaunchProtocol.MaxRequest - JsonSerializer.SerializeToUtf8Bytes(bare, ProtocolJson.Options).Length - 100) / 2);

        ui.Post(() => { launch.Resume = launch.Resumes.Single(r => r.Value.StartsWith('@')); launch.Prompt = prompt; launch.Launch(); });

        Assert.Equal("", launch.Problem);
        await Wait.UntilAsync(() => launch.State == "started", "started");
        Assert.Equal(prompt, Assert.Single(remoteHost.Sent(LaunchProtocol.Launch)).GetProperty("prompt").GetString());
    }

    [Fact]
    public async Task Sessions_to_bring_here_are_found_once_the_relay_connects_after_the_page_opened()
    {
        link.Sources.Add(Laptop());
        link.Endpoints["host-id"] = localHost.Endpoint;
        localHost.Capabilities.Add(TransferProtocol.Capability);
        link.Switch();
        var launch = new LaunchViewModel(main, ui);
        launch.Opened();
        await Wait.UntilAsync(() => launch.ShowForm, "this machine's choices");
        Assert.DoesNotContain(launch.Resumes, r => r.Value.StartsWith('@'));

        link.Set(FleetState.Connected, ("remote-id", "Build server"), ("host-id", "Test PC"));

        await Wait.UntilAsync(() => launch.Resumes.Any(r => r.Value == $"@{LaptopId}/{Brought}"), "the other machine's session offered once connected");
    }

    [Fact]
    public async Task Sessions_to_bring_here_are_found_once_the_relay_is_back_for_the_same_account()
    {
        link.Sources.Add(Laptop());
        link.Endpoints["host-id"] = localHost.Endpoint;
        localHost.Capabilities.Add(TransferProtocol.Capability);
        // Down for now, the same account: every machine stays as it was, and nothing is made.
        link.Set(FleetState.Connecting, ("remote-id", "Build server"), ("host-id", "Test PC"));
        var launch = new LaunchViewModel(main, ui);
        launch.Opened();
        await Wait.UntilAsync(() => launch.ShowForm, "this machine's choices");
        await Task.Delay(300);
        Assert.DoesNotContain(launch.Resumes, r => r.Value.StartsWith('@'));

        link.Set(FleetState.Connected, ("remote-id", "Build server"), ("host-id", "Test PC"));

        await Wait.UntilAsync(() => launch.Resumes.Any(r => r.Value == $"@{LaptopId}/{Brought}"), "the other machine's session offered once the relay is back");
    }

    [Fact]
    public async Task A_session_picked_to_bring_over_stays_picked_through_a_relay_reconnect()
    {
        link.Sources.Add(Laptop());
        var launch = new LaunchViewModel(main, ui);
        launch.Opened();
        await Wait.UntilAsync(() => launch.Machines.Count == 2 && launch.ShowForm, "both machines");
        ui.Post(() => launch.SelectedMachine = launch.Machines[1]);
        await Wait.UntilAsync(() => launch.Resumes.Any(r => r.Value.StartsWith('@')), "the other machine's session offered");
        ui.Post(() => launch.Resume = launch.Resumes.Single(r => r.Value.StartsWith('@')));

        // Down for now, the same account: discovery is asked again, and answers nothing.
        link.Set(FleetState.Connecting, ("remote-id", "Build server"));
        await Task.Delay(300);
        Assert.Equal($"@{LaptopId}/{Brought}", launch.Resume!.Value);
        link.Set(FleetState.Connected, ("remote-id", "Build server"));
        await Task.Delay(300);

        Assert.Equal($"@{LaptopId}/{Brought}", launch.Resume!.Value);
        Assert.StartsWith("This copies", launch.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_account_forgets_the_last_ones_sessions_and_an_answer_from_before_it_is_dropped()
    {
        link.Sources.Add(Laptop());
        link.Endpoints["host-id"] = localHost.Endpoint;
        localHost.Capabilities.Add(TransferProtocol.Capability);
        link.Set(FleetState.Connected, ("remote-id", "Build server"), ("host-id", "Test PC"));
        var launch = new LaunchViewModel(main, ui);
        launch.Opened();
        await Wait.UntilAsync(() => launch.Resumes.Any(r => r.Value == $"@{LaptopId}/{Brought}"), "the other machine's session offered here");
        ui.Post(() => launch.Resume = launch.Resumes.Single(r => r.Value == $"@{LaptopId}/{Brought}"));
        // A reload whose answer about the last account's machines is still on its way.
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        link.HoldSources = hold;
        _ = launch.RefreshAsync();
        await Task.Delay(300);

        link.Switch();

        await Wait.UntilAsync(() => !launch.Resumes.Any(r => r.Value.StartsWith('@')), "the last account's sessions to go");
        Assert.Equal("", launch.Resume?.Value ?? "");
        link.HoldSources = null;
        hold.SetResult();
        await Task.Delay(300);
        Assert.DoesNotContain(launch.Resumes, r => r.Value.StartsWith('@'));
    }
    [Fact]
    public async Task A_machine_that_leaves_with_a_launch_on_its_way_says_the_launch_is_not_confirmed()
    {
        var gate = new TaskCompletionSource();
        remoteHost.On(LaunchProtocol.Launch, _ => { gate.Task.Wait(FakeHost.Patience); return new LaunchResult(LaunchOutcome.Started) { Session = "there-new" }; });
        var launch = new LaunchViewModel(main, ui);
        launch.Opened();
        await Wait.UntilAsync(() => launch.Machines.Count == 2 && launch.ShowForm, "both machines");
        ui.Post(() => launch.SelectedMachine = launch.Machines[1]);
        await Wait.UntilAsync(() => launch.Workspace?.Value == "there", "the other machine's choices");
        ui.Post(launch.Launch);
        await Wait.UntilAsync(() => remoteHost.Sent(LaunchProtocol.Launch).Count == 1, "the launch to go");

        link.Set(FleetState.Connected);

        await Wait.UntilAsync(() => launch.StatusText.StartsWith("Not confirmed: Build server left", StringComparison.Ordinal), "the launch to be called unconfirmed");
        Assert.Equal(Tone.Warn, launch.StatusTone);
        await Wait.UntilAsync(() => launch.CanLaunch, "another launch to be possible");
        gate.SetResult();

        // It comes back: picked again, as it was picked by hand, and the warning stays until the next press.
        link.Set(FleetState.Connected, ("remote-id", "Build server"));
        await Wait.UntilAsync(() => launch.SelectedMachine?.Value == "remote-id" && launch.ShowForm, "the machine to come back");
        Assert.StartsWith("Not confirmed: Build server left", launch.StatusText);
    }
}