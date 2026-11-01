using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;

// A launched agent asking whether to trust its folder, on the Launch page (pwa/test/launch.spec.mjs,
// "a folder-trust question"): shown in full, answered with Trust or Quit through the relay with a
// command this app signs, and otherwise left for the machine with why.
public sealed class TrustTests : IAsyncLifetime
{
    const string Long = @"C:\Users\dana\repos\a-rather-long-folder-name-that-will-not-fit-on-one-line\and-a-subfolder\web";
    readonly FakeHost localHost = new();
    readonly FakeHost remoteHost = new();
    readonly LockedDispatcher ui = new();
    readonly FleetTests.FakeFleetLink link = new();
    HostConnection local = null!;
    MainViewModel main = null!;
    Fleet fleet = null!;
    volatile IReadOnlyList<TrustQuestion> remoteTrust = [Question()];
    volatile IReadOnlyList<TrustQuestion> localTrust = [];
    Func<JsonElement, object> answer = _ => new TrustAnswerResult(TrustAnswerOutcome.Started) { Session = "there-new" };
    string? remoteReadOnly;

    static TrustQuestion Question(bool answerable = true, string? atMachine = null) => new()
    {
        Id = "q-1", Agent = "codex", Kind = AgentKind.Codex, Label = "Codex", Workspace = "there", Folder = Long,
        Root = @"c:\users\dana\repos\a-rather-long-folder-name-that-will-not-fit-on-one-line",
        RootNote = "Note: You’re in a subdirectory of a Git project. Trusting will apply to the repository root:",
        Warning = "Trust this folder? Codex can read, edit, and run files here, subject to your permission settings.",
        AskedAt = Make.T0, Answerable = answerable, AtMachine = atMachine,
    };

    static LaunchOptions Options(string label, IReadOnlyList<TrustQuestion> trust) => new()
    {
        Agents = [new LaunchAgent { Id = "codex", Label = "Codex", Kind = AgentKind.Codex }],
        Workspaces = [new LaunchWorkspace { Label = label, Path = $"/{label}" }],
        Defaults = new LaunchDefaults { Agent = "codex", Workspace = label, Permissions = LaunchProtocol.Ask },
        Trust = trust.Count == 0 ? null : trust,
    };

    public async Task InitializeAsync()
    {
        localHost.Capabilities.Add(LaunchProtocol.Capability);
        localHost.Capabilities.Add(TrustProtocol.Capability);
        localHost.On(LaunchProtocol.Options, _ => Options("here", localTrust));
        remoteHost.On(LaunchProtocol.Options, _ => Options("there", remoteTrust));
        remoteHost.On(TrustProtocol.Answer, d => answer(d));
        remoteHost.On(Ops.Hello, _ => new HelloResult(1, new MachineInfo("remote-id", "Build server", "windows", "0.1.0") { ReadOnly = remoteReadOnly },
            remoteReadOnly is null
                ? [Capabilities.SessionsRead, Capabilities.SessionsAnswer, LaunchProtocol.Capability, TrustProtocol.Capability]
                : [Capabilities.SessionsRead]));
        localHost.Start();
        remoteHost.Start();
        link.Endpoints["remote-id"] = remoteHost.Endpoint;
        link.Endpoints["host-id"] = localHost.Endpoint;
        local = localHost.Connect(ui);
        await Wait.UntilAsync(() => local.State == LinkState.Connected && local.Status is not null, "this machine's host");
        main = new MainViewModel(Build(Machine.LocalKey, local), new UsageViewModel());
        fleet = new Fleet(link, main, ui, h => Build(h.Id, new HostConnection(ct => link.OpenAsync(h.Id, ct), ui) { RetryDelay = TimeSpan.FromMilliseconds(100), CapabilityRefresh = TimeSpan.FromMilliseconds(200) }));
    }

    Machine Build(string key, HostConnection c) =>
        new(key, c, new CardContext { Deliveries = new Deliveries(c, ui) { RetryDelay = TimeSpan.FromMilliseconds(100) }, Folds = new FoldStore(null), Capabilities = () => c.Capabilities, Host = c });

    public async Task DisposeAsync()
    {
        await fleet.DisposeAsync();
        await local.DisposeAsync();
        await localHost.DisposeAsync();
        await remoteHost.DisposeAsync();
    }

    async Task<(LaunchViewModel Page, TrustCard Card)> OnRemoteAsync()
    {
        link.Set(FleetState.Connected, ("remote-id", "Build server"), ("host-id", "Test PC"));
        var page = LaunchPage.Open(main, ui);
        await Wait.UntilAsync(() => page.Machines.Count == 2, "both machines");
        ui.Post(() => page.SelectedMachine = page.Machines.Single(m => m.Value == "remote-id"));
        await Wait.UntilAsync(() => page.TrustQuestions.Count == 1, "the question");
        return (page, page.TrustQuestions[0]);
    }

    [Fact]
    public async Task A_question_on_another_machine_is_shown_in_full_and_Trust_answers_it_with_a_command_signed_for_it_then_opens_the_session()
    {
        var opened = new List<string>();
        main.OpenRequested += c => opened.Add(c.Id);
        var (page, card) = await OnRemoteAsync();
        Assert.Equal("Codex on Build server is asking whether to trust a folder", card.Title);
        Assert.Equal(Long, card.Folder);
        Assert.Equal((true, Question().Root, Question().RootNote), (card.ShowRoot, card.Root, card.RootNote));
        Assert.Equal(Question().Warning, card.Warning);
        Assert.True(card.CanAnswer);
        Assert.False(card.ShowAtMachine);

        remoteTrust = [];
        ui.Post(card.Trust);

        await Wait.UntilAsync(() => card.State == "started", "started");
        var sent = Assert.Single(remoteHost.Sent(TrustProtocol.Answer));
        Assert.Equal(("q-1", "trust", "trust1"), (sent.GetProperty("question").GetString(), sent.GetProperty("choice").GetString(), sent.GetProperty("command").GetProperty("body").GetString()));
        // Signed by this app for that machine, question and choice, with the request it went as.
        var signed = Assert.Single(link.Trusted);
        Assert.Equal(("remote-id", "q-1", "trust"), (signed.Machine, signed.Question, signed.Choice));
        Assert.Equal(main.MachineOf("remote-id")!.Deliveries.Get("trust:q-1")!.Request, signed.Request);
        Assert.Equal("Trusted. Codex started.", card.StatusText);
        Assert.False(card.CanAnswer);
        // Its card arrives after the outcome, and is opened then.
        await remoteHost.SendAsync(Events.SessionUpsert, Make.Session("there-new", updated: Make.T0.AddMinutes(10)));
        await Wait.UntilAsync(() => opened.Contains("there-new"), "the new session's card to open");
    }

    [Fact]
    public async Task A_launch_that_starts_on_Trust_clears_its_prompt_and_a_question_it_did_not_meet_leaves_it()
    {
        remoteTrust = [];
        remoteHost.On(LaunchProtocol.Launch, _ =>
        {
            remoteTrust = [Question()];
            return new LaunchResult(LaunchOutcome.Unconfirmed, "Codex started, and is asking whether to trust there") { Trust = Question() };
        });
        link.Set(FleetState.Connected, ("remote-id", "Build server"), ("host-id", "Test PC"));
        var page = LaunchPage.Open(main, ui);
        await Wait.UntilAsync(() => page.Machines.Count == 2, "both machines");
        ui.Post(() => page.SelectedMachine = page.Machines.Single(m => m.Value == "remote-id"));
        await Wait.UntilAsync(() => page.CanLaunch, "the form");
        ui.Post(() => page.Prompt = "fix the build");
        ui.Post(page.Launch);
        await Wait.UntilAsync(() => page.TrustQuestions.Count == 1 && page.TrustQuestions[0].CanAnswer, "the question");

        remoteTrust = [];
        ui.Post(page.TrustQuestions[0].Trust);
        await Wait.UntilAsync(() => page.TrustQuestions.Count == 0 || page.TrustQuestions[0].State == "started", "started");
        await Wait.UntilAsync(() => page.Prompt == "", "the prompt cleared");

        // A question asked at the machine, answered here: what is being written stays.
        remoteTrust = [Question() with { Id = "q-2" }];
        answer = _ => new TrustAnswerResult(TrustAnswerOutcome.Started) { Session = "there-2" };
        ui.Post(() => page.Prompt = "add tests");
        await page.RefreshAsync();
        await Wait.UntilAsync(() => page.TrustQuestions.Any(q => q.Id == "q-2" && q.CanAnswer), "the other question");
        ui.Post(page.TrustQuestions.Single(q => q.Id == "q-2").Trust);
        await Wait.UntilAsync(() => page.TrustQuestions.Single(q => q.Id == "q-2").State == "started", "it started");
        Assert.Equal("add tests", page.Prompt);
    }

    [Fact]
    public async Task Trusting_the_question_a_busy_launch_names_leaves_the_prompt_it_did_not_send()
    {
        remoteTrust = [];
        remoteHost.On(LaunchProtocol.Launch, _ =>
        {
            remoteTrust = [Question()];
            return new LaunchResult(LaunchOutcome.Busy, "Codex in there is waiting for a person to answer whether to trust that folder") { Trust = Question() };
        });
        link.Set(FleetState.Connected, ("remote-id", "Build server"), ("host-id", "Test PC"));
        var page = LaunchPage.Open(main, ui);
        await Wait.UntilAsync(() => page.Machines.Count == 2, "both machines");
        ui.Post(() => page.SelectedMachine = page.Machines.Single(m => m.Value == "remote-id"));
        await Wait.UntilAsync(() => page.CanLaunch, "the form");
        ui.Post(() => page.Prompt = "fix the build");
        ui.Post(page.Launch);
        await Wait.UntilAsync(() => page.State == "busy" && page.TrustQuestions.Count == 1 && page.TrustQuestions[0].CanAnswer, "the question");

        var card = page.TrustQuestions[0];
        remoteTrust = [];
        ui.Post(card.Trust);
        await Wait.UntilAsync(() => card.State == "started", "started");
        await Task.Delay(200);
        Assert.Equal("fix the build", page.Prompt);
    }

    [Fact]
    public async Task Quit_says_the_agent_was_not_trusted_and_closed()
    {
        answer = _ => new TrustAnswerResult(TrustAnswerOutcome.Declined, "Codex was not trusted with there, and closed; nothing was launched");
        var (_, card) = await OnRemoteAsync();
        ui.Post(card.Quit);
        await Wait.UntilAsync(() => card.State == "declined", "declined");
        Assert.Equal("quit", Assert.Single(remoteHost.Sent(TrustProtocol.Answer)).GetProperty("choice").GetString());
        Assert.Equal("Not trusted. Codex was not trusted with there, and closed; nothing was launched.", card.StatusText);
    }

    [Fact]
    public async Task Where_the_machine_does_not_allow_it_the_question_says_to_answer_at_the_machine_and_offers_no_buttons()
    {
        const string why = "Answer at the machine. To answer from your devices, turn on \"Answer folder-trust questions from my devices\" in the Agent Bridge app's Settings on that machine.";
        remoteTrust = [Question(answerable: false, atMachine: why)];
        var (_, card) = await OnRemoteAsync();
        Assert.Equal(why, card.AtMachine);
        Assert.False(card.CanAnswer);
        // Only this machine's setting can be changed from here.
        Assert.False(card.ShowAllowHere);
        Assert.Equal(Long, card.Folder);
    }

    [Fact]
    public async Task An_answer_whose_effect_was_not_seen_says_to_answer_at_the_machine_and_is_never_offered_again()
    {
        answer = _ => new TrustAnswerResult(TrustAnswerOutcome.Unconfirmed, "Not confirmed: answer at the machine. The question is still in Codex's window.");
        var (page, card) = await OnRemoteAsync();
        ui.Post(card.Trust);
        await Wait.UntilAsync(() => card.State == "unconfirmed", "unconfirmed");
        Assert.Equal("Not confirmed: answer at the machine. The question is still in Codex's window.", card.StatusText);
        Assert.False(card.CanAnswer);

        // The machine still lists it as answerable: from here it is not offered again.
        await page.RefreshAsync();
        Assert.False(page.TrustQuestions.Single().CanAnswer);
        Assert.Single(remoteHost.Sent(TrustProtocol.Answer));
    }

    [Fact]
    public async Task An_error_offers_only_that_same_answer_again_which_the_machine_acts_on_once()
    {
        answer = _ => throw new InvalidOperationException("the worker went away");
        var (_, card) = await OnRemoteAsync();
        ui.Post(card.Trust);
        await Wait.UntilAsync(() => card.ShowResend, "Try again");
        Assert.False(card.CanAnswer);
        Assert.Contains("Build server returned an error", card.StatusText);

        answer = _ => new TrustAnswerResult(TrustAnswerOutcome.Started) { Session = "there-new" };
        ui.Post(card.Resend);
        await Wait.UntilAsync(() => card.State == "started", "started");
        Assert.Equal(2, remoteHost.Sent(TrustProtocol.Answer).Count);
        // The same request both times: the machine answers a repeat with its first outcome.
        Assert.Single(link.Trusted.Select(t => t.Request).Distinct());
    }

    [Fact]
    public async Task A_refused_answer_to_a_question_that_has_since_gone_is_not_offered_again()
    {
        answer = _ => new TrustAnswerResult(TrustAnswerOutcome.Refused, "that question is no longer waiting: it was answered, or the agent closed");
        var (page, card) = await OnRemoteAsync();
        // Answered at the machine meanwhile: it is no longer listed.
        remoteTrust = [];
        ui.Post(card.Trust);
        await Wait.UntilAsync(() => card.State == "refused", "refused");
        await page.RefreshAsync();

        Assert.False(card.CanAnswer);
        Assert.StartsWith("Not answered: that question is no longer waiting", card.StatusText);
        Assert.Equal("Build server no longer lists this question as waiting.", card.AtMachine);
    }

    [Fact]
    public async Task A_machine_that_takes_nothing_from_devices_now_is_listed_only_to_show_its_question_read_only()
    {
        remoteReadOnly = "agent-ha-bridge is running for this user";
        remoteTrust = [Question(answerable: false, atMachine: "Answer at the machine: agent-ha-bridge is running for this user.")];
        var (page, card) = await OnRemoteAsync();
        Assert.Equal("Waiting on Build server", page.Title);
        Assert.False(page.ShowForm);
        Assert.False(page.CanLaunch);
        Assert.Equal("Sessions can't be started on Build server now: agent-ha-bridge is running for this user.", page.Note);
        Assert.Equal("Answer at the machine: agent-ha-bridge is running for this user.", card.AtMachine);
        Assert.False(card.CanAnswer);
    }

    [Fact]
    public async Task A_machine_that_takes_launches_again_shows_its_form_again()
    {
        remoteReadOnly = "agent-ha-bridge is running for this user";
        var (page, _) = await OnRemoteAsync();
        Assert.False(page.ShowForm);

        // agent-ha-bridge stopped there: the same machines, but this one launches again.
        remoteReadOnly = null;
        await Wait.UntilAsync(() => page.ShowForm, "the form");
        Assert.Equal("Start a session on Build server", page.Title);
    }

    [Fact]
    public async Task This_machines_question_offers_Trust_once_the_relay_connects_without_a_reload()
    {
        localTrust = [Question() with { Workspace = "here" }];
        localHost.On(TrustProtocol.Answer, d => answer(d));
        var page = LaunchPage.Open(main, ui);
        await Wait.UntilAsync(() => page.TrustQuestions.Count == 1, "the question");
        // Through the pipe alone: never answerable from here.
        Assert.False(page.TrustQuestions[0].CanAnswer);

        link.Set(FleetState.Connected, ("host-id", "Test PC"));
        await Wait.UntilAsync(() => page.TrustQuestions[0].CanAnswer, "Trust offered");
    }

    [Fact]
    public async Task This_machines_question_is_never_answered_over_the_pipe_and_can_be_allowed_from_here()
    {
        // As the pipe lists it: never answerable there.
        localTrust = [Question(answerable: false, atMachine: "Answer at the machine, or from a phone, browser or Windows app through the relay.") with { Workspace = "here" }];
        localHost.On(HostOps.Settings, _ => new SettingsResult(new HostSettingsView(true, RemoteFolderTrust: true), false));
        var page = LaunchPage.Open(main, ui);
        await Wait.UntilAsync(() => page.TrustQuestions.Count == 1, "the question");
        var card = page.TrustQuestions[0];
        Assert.False(card.CanAnswer);
        Assert.StartsWith("Answer at the machine", card.AtMachine);

        // A person at this machine allows it, with this app approved: the trusted local caller.
        await Wait.UntilAsync(() => card.ShowAllowHere, "Allow answering from this app");
        await card.AllowHereAsync();

        var sent = Assert.Single(localHost.Sent(HostOps.Settings));
        Assert.True(sent.GetProperty("remoteFolderTrust").GetBoolean());
        Assert.Equal(["a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1"], sent.GetProperty("remoteFolderTrustDevices").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(localHost.Sent(TrustProtocol.Answer));
    }

    [Fact]
    public async Task Allowing_it_here_adds_this_app_to_the_list_the_host_has_now_not_one_from_before_a_device_was_revoked()
    {
        const string phone = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2", me = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1";
        localTrust = [Question(answerable: false, atMachine: "Answer at the machine.") with { Workspace = "here" }];
        localHost.Status = FakeHost.StatusOf() with { Settings = new HostSettingsView(true, RemoteFolderTrust: true, RemoteFolderTrustDevices: [phone]) };
        await localHost.SendAsync(HostOps.StatusEvent, localHost.Status);
        // The host saves each change; its status event is not sent.
        localHost.On(HostOps.Settings, d =>
        {
            var saved = localHost.Status.Settings with
            {
                RemoteFolderTrust = d.TryGetProperty("remoteFolderTrust", out var on) ? on.GetBoolean() : localHost.Status.Settings.RemoteFolderTrust,
                RemoteFolderTrustDevices = d.TryGetProperty("remoteFolderTrustDevices", out var l) ? [.. l.EnumerateArray().Select(e => e.GetString()!)] : localHost.Status.Settings.RemoteFolderTrustDevices,
            };
            localHost.Status = localHost.Status with { Settings = saved };
            return new SettingsResult(saved, false);
        });
        var page = LaunchPage.Open(main, ui);
        await Wait.UntilAsync(() => page.TrustQuestions.Count == 1 && page.TrustQuestions[0].ShowAllowHere, "Allow answering from this app");

        // The phone revoked on the Settings page, which edits through the same editor; the status
        // this app holds still lists it.
        await TrustEditor.For(local).EditAsync(_ => new SettingsRequest(RemoteFolderTrustDevices: []));
        Assert.Equal([phone], local.Status!.Settings.RemoteFolderTrustDevices!);
        await page.TrustQuestions[0].AllowHereAsync();

        var sent = localHost.Sent(HostOps.Settings);
        Assert.Equal(2, sent.Count);
        Assert.Equal([me], sent[1].GetProperty("remoteFolderTrustDevices").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task A_question_a_launch_returned_counts_only_until_the_listing_after_it_says_whether_it_still_waits()
    {
        remoteTrust = [];
        remoteHost.On(LaunchProtocol.Launch, _ => new LaunchResult(LaunchOutcome.Busy, "Codex in there is waiting for a person to answer whether to trust that folder") { Trust = Question() });
        link.Set(FleetState.Connected, ("remote-id", "Build server"), ("host-id", "Test PC"));
        var page = LaunchPage.Open(main, ui);
        await Wait.UntilAsync(() => page.Machines.Count == 2, "both machines");
        ui.Post(() => page.SelectedMachine = page.Machines.Single(m => m.Value == "remote-id"));
        await Wait.UntilAsync(() => page.CanLaunch, "the form");
        ui.Post(() => page.Prompt = "fix the build");

        ui.Post(page.Launch);
        await Wait.UntilAsync(() => page.State == "busy", "busy");

        // Listed again after the outcome: answered at the machine meanwhile, so it is gone, never
        // offered from the outcome alone.
        await Wait.UntilAsync(() => remoteHost.Sent(LaunchProtocol.Options).Count >= 2 && page.TrustQuestions.Count == 0, "the question to go");
        Assert.Empty(remoteHost.Sent(TrustProtocol.Answer));
    }

    [Fact]
    public async Task A_busy_launch_here_naming_a_question_lists_it_through_the_relay_so_Trust_is_offered_without_a_reload()
    {
        link.Set(FleetState.Connected, ("host-id", "Test PC"));
        // As the pipe gives it: never answerable there.
        localHost.On(LaunchProtocol.Launch, _ => new LaunchResult(LaunchOutcome.Busy, "Codex in here is waiting for a person to answer whether to trust that folder")
        {
            Trust = Question(answerable: false, atMachine: "Answer at the machine, or from a phone, browser or Windows app through the relay.") with { Workspace = "here" },
        });
        localHost.On(TrustProtocol.Answer, d => answer(d));
        var page = LaunchPage.Open(main, ui);
        await Wait.UntilAsync(() => page.CanLaunch, "the form");
        Assert.Empty(page.TrustQuestions);
        localTrust = [Question() with { Workspace = "here" }];
        ui.Post(() => page.Prompt = "fix the build");

        ui.Post(page.Launch);

        await Wait.UntilAsync(() => page.State == "busy" && page.TrustQuestions.Count == 1 && page.TrustQuestions[0].CanAnswer, "Trust offered");
    }

    [Fact]
    public async Task This_machines_question_is_answered_through_the_relay_as_its_host_lists_it_there()
    {
        localTrust = [Question() with { Workspace = "here" }];
        localHost.On(TrustProtocol.Answer, d => answer(d));
        link.Set(FleetState.Connected, ("host-id", "Test PC"));
        var page = LaunchPage.Open(main, ui);
        await Wait.UntilAsync(() => page.TrustQuestions.Count == 1 && page.TrustQuestions[0].CanAnswer, "an answerable question");

        ui.Post(page.TrustQuestions[0].Trust);

        await Wait.UntilAsync(() => page.TrustQuestions[0].State == "started", "started");
        // Through the loopback: the relay's channel to this machine's own host, never the pipe.
        Assert.Equal("host-id", Assert.Single(link.Trusted).Machine);
        Assert.NotNull(fleet.CurrentLoopback);
        Assert.Equal("q-1", Assert.Single(localHost.Sent(TrustProtocol.Answer)).GetProperty("question").GetString());
    }
}
