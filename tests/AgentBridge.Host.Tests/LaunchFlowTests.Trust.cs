using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

// launch.trust end to end: a person answering a launched agent's folder-trust question from their
// phone, through the relay, with a command it signed. The agent is played by the fake screens and
// starter: what it draws, and what it does with keys that reach it.
public sealed partial class LaunchFlowTests
{
    // Codex's question as 0.165 drew it on the test VM (2026-10-31), blank rows and all.
    string[] CodexAsks(string root) =>
    [
        "",
        "  Folder access",
        $"  {app}",
        "",
        "  Note: You’re in a subdirectory of a Git project. Trusting will apply to the repository root:",
        $"  {root}",
        "",
        "  Trust this folder? Codex can read, edit, and run files here, subject to your permission settings. Folder settings",
        "  can run code automatically, even without a model request. Continue only if you trust these files. Your trust",
        "  decision will be saved.",
        "",
        "› 1. Trust and continue",
        "  2. Quit",
        "",
        "  enter continue · esc quit",
    ];

    // Claude's, as 2.x drew it there: "No, exit" first, and highlighted.
    string[] ClaudeAsks(bool onYes = false) =>
    [
        "────────────────────",
        " Accessing workspace:",
        "",
        $" {app}",
        "",
        " Quick safety check: Is this a project you created or one you trust? (Like your own code, a well-known open source",
        " project, or work from your team). If not, take a moment to review what's in this folder first.",
        "",
        " Claude Code'll be able to read, edit, and execute files here.",
        "",
        " Security guide",
        "",
        onYes ? "   No, exit" : " > No, exit",
        onYes ? " > Yes, I trust this folder" : "   Yes, I trust this folder",
        "",
        " Enter to confirm · Esc to cancel",
    ];

    string[] CopilotAsks() =>
    [
        "╭──────────────────────────╮",
        "│ Confirm folder trust     │",
        "│ ╭──────────────────────╮ │",
        $"│ │ {app} │ │",
        "│ ╰──────────────────────╯ │",
        "│                          │",
        "│ Copilot can read files in this folder and, with your permission, edit them or run code and shell commands. It will   │",
        "│ remember your permissions for the rest of this session.                                                              │",
        "│                          │",
        "│ Do you trust the files in this folder?                                                                               │",
        "│                          │",
        "│ ❯ 1. Yes                 │",
        "│   2. Yes, and remember this folder for future sessions │",
        "│   3. No (Esc)            │",
        "│                          │",
        "│ ↑/↓ to navigate · enter to select · esc to cancel │",
        "╰──────────────────────────╯",
    ];

    FolderTrustAnswers ServeTrust(LaunchConfig config, TimeSpan? trustConfirm = null, TimeSpan? follow = null)
    {
        var launcher = trustLauncher = Launcher(config, trustConfirm: trustConfirm, follow: follow);
        var answers = new FolderTrustAnswers(host.Key, launcher);
        answers.Attach(host.Relay);
        host.Hub.Handler = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0"), launcher: launcher) { TrustAnswers = answers }.HandleAsync;
        return answers;
    }

    LaunchService trustLauncher = null!;

    // Allowed, with the phone approved at the machine.
    LaunchConfig Trusting() => Config(c => c with { RemoteFolderTrust = true, RemoteFolderTrustDevices = [Wire.DeviceId(client.Key.Public)] });

    // What the phone sends: the question, the choice, and a command it signed for exactly that.
    static object Answer(SecureChannel ch, string question, string choice, P256KeyPair signer, string? signedChoice = null, string? request = null, string? signedQuestion = null)
    {
        var now = DateTimeOffset.UtcNow;
        var command = SignedCommands.Sign(signer, ch.PeerStatic!, new CommandBody
        {
            V = 1,
            Action = TrustProtocol.Action(signedChoice ?? choice),
            Request = request ?? Guid.NewGuid().ToString(),
            Principal = new CommandPrincipal { Kind = "user", Device = Wire.DeviceId(signer.Public) },
            Target = new CommandTarget { Machine = ch.PeerId, Session = signedQuestion ?? question },
            To = ch.PeerId,
            IssuedAt = now.ToUnixTimeSeconds(),
            ExpiresAt = now.AddMinutes(5).ToUnixTimeSeconds(),
        });
        return new { question, choice, command };
    }

    static async Task<(string Outcome, string? Reason, string? Session)> AnswerAsync(SecureChannel ch, object answer)
    {
        var r = await RequestAsync(ch, TrustProtocol.Answer, answer);
        return (r.GetProperty("outcome").GetString()!, r.TryGetProperty("reason", out var why) ? why.GetString() : null, r.TryGetProperty("session", out var s) ? s.GetString() : null);
    }

    static async Task<JsonElement> LaunchTrustAsync(SecureChannel ch, object settings)
    {
        var data = JsonSerializer.SerializeToNode(settings, ProtocolJson.Options)!.AsObject();
        data["request"] = Guid.NewGuid().ToString();
        return await RequestAsync(ch, LaunchProtocol.Launch, data);
    }

    [Fact]
    public async Task A_person_trusts_a_waiting_Codex_folder_from_their_phone_and_the_launch_completes()
    {
        locator.Installed["codex"] = @"C:\fake\codex.cmd";
        starter.Registers = false;
        var root = Path.GetDirectoryName(app)!.ToLowerInvariant();
        screens.Rows = CodexAsks(root);
        ServeTrust(Trusting());
        var ch = await OpenAsync();
        Assert.Contains(TrustProtocol.Capability, await CapabilitiesAsync(ch));

        var launched = await LaunchTrustAsync(ch, new { agent = "codex", workspace = "App", prompt = "add pagination" });
        Assert.Equal("unconfirmed", launched.GetProperty("outcome").GetString());
        Assert.Contains("A person can answer that here or at the machine", launched.GetProperty("reason").GetString());
        var q = launched.GetProperty("trust");
        Assert.True(q.GetProperty("answerable").GetBoolean());
        Assert.Equal(("codex", "codex", "Codex", "App", app), (q.GetProperty("agent").GetString(), q.GetProperty("kind").GetString(), q.GetProperty("label").GetString(), q.GetProperty("workspace").GetString(), q.GetProperty("folder").GetString()));
        Assert.Equal(root, q.GetProperty("root").GetString());
        Assert.Equal("Note: You’re in a subdirectory of a Git project. Trusting will apply to the repository root:", q.GetProperty("rootNote").GetString());
        Assert.Equal("Trust this folder? Codex can read, edit, and run files here, subject to your permission settings. Folder settings can run code automatically, even without a model request. Continue only if you trust these files. Your trust decision will be saved.",
            q.GetProperty("warning").GetString());
        var options = await RequestAsync(ch, LaunchProtocol.Options, new { });
        Assert.Equal(q.GetProperty("id").GetString(), Assert.Single(options.GetProperty("trust").EnumerateArray()).GetProperty("id").GetString());
        Assert.True(options.TryGetProperty("busy", out _));

        // Codex takes the Enter, its question goes, and its session starts.
        screens.OnTyped = plan =>
        {
            screens.Rows = ["› add pagination"];
            starter.RegisterCodex(starter.Specs[0], starter.Pids[0], 7001, starter.CodexId);
        };
        var answer = await AnswerAsync(ch, Answer(ch, q.GetProperty("id").GetString()!, "trust", client.Key));

        Assert.Equal(("started", null, starter.CodexId), answer);
        var typed = Assert.Single(screens.Plans);
        var send = Assert.Single(typed.Sends);
        Assert.Equal(("", true), (send.Text, send.Enter));
        // Only while the folder, root, words and options read exactly as they were, highlight and all.
        Assert.Equal(Enumerable.Range(2, 11), send.Expect!.Select(e => e.Row));
        Assert.Equal(starter.Pids[0], Assert.Single(screens.Typed));
        await WaitAsync(async () =>
        {
            var now = await RequestAsync(ch, LaunchProtocol.Options, new { });
            return !now.TryGetProperty("busy", out _) && !now.TryGetProperty("trust", out _);
        });
    }

    [Fact]
    public async Task Trusting_from_a_phone_moves_Claudes_highlight_to_trust_and_sees_it_there_before_Enter()
    {
        locator.Installed["claude"] = @"C:\fake\claude.exe";
        starter.Registers = false;
        screens.Rows = ClaudeAsks();
        ServeTrust(Trusting());
        var ch = await OpenAsync();
        var q = (await LaunchTrustAsync(ch, new { agent = "claude", workspace = "App", prompt = "go" })).GetProperty("trust");
        Assert.Equal("Quick safety check: Is this a project you created or one you trust? (Like your own code, a well-known open source project, or work from your team). If not, take a moment to review what's in this folder first.\nClaude Code'll be able to read, edit, and execute files here.",
            q.GetProperty("warning").GetString());
        Assert.False(q.TryGetProperty("root", out _));

        screens.OnTyped = plan =>
        {
            if (!plan.Sends[0].Enter) { screens.Rows = ClaudeAsks(onYes: true); return; }
            screens.Rows = ["> go"];
            starter.Register(starter.Specs[0]);
        };
        var answer = await AnswerAsync(ch, Answer(ch, q.GetProperty("id").GetString()!, "trust", client.Key));

        Assert.Equal("started", answer.Outcome);
        Assert.Equal(2, screens.Plans.Count);
        Assert.Equal((KeyPlan.Down, false), (screens.Plans[0].Sends[0].Text, screens.Plans[0].Sends[0].Enter));
        Assert.Equal(("", true), (screens.Plans[1].Sends[0].Text, screens.Plans[1].Sends[0].Enter));
        // The Enter waited for the highlight to be seen on "Yes, I trust this folder".
        Assert.Contains(screens.Plans[1].Sends[0].Expect!, e => e.Text == " > Yes, I trust this folder");
    }

    [Fact]
    public async Task Claudes_Bypass_Permissions_warning_is_said_for_what_it_is_and_is_never_offered_as_a_folder_trust_question()
    {
        locator.Installed["claude"] = @"C:\fake\claude.exe";
        starter.Registers = false;
        // As Claude draws it for Allow all (--dangerously-skip-permissions), #149.
        screens.Rows = [" WARNING: Claude Code running in Bypass Permissions mode", "", " In Bypass Permissions mode, Claude Code will not ask for your approval before running potentially dangerous commands.",
            "", " ❯ 1. No, exit", "   2. Yes, I accept"];
        ServeTrust(Trusting());
        var ch = await OpenAsync();
        var launched = await LaunchTrustAsync(ch, new { agent = "claude", workspace = "App", prompt = "go" });
        Assert.Equal("unconfirmed", launched.GetProperty("outcome").GetString());
        Assert.Equal("Claude started in App, and is asking you to confirm Bypass Permissions mode (Allow all). Only a person at the machine can answer that; the session appears here once they do",
            launched.GetProperty("reason").GetString());
        Assert.False(launched.TryGetProperty("trust", out _));

        var options = await RequestAsync(ch, LaunchProtocol.Options, new { });
        Assert.False(options.TryGetProperty("trust", out _));
        Assert.Equal("Busy: Claude in App is waiting for a person at the machine to confirm Bypass Permissions mode (Allow all), so nothing was launched. Try again in a couple of minutes.",
            options.GetProperty("busy").GetString());
        var busy = await LaunchTrustAsync(ch, new { workspace = "API" });
        Assert.Equal("busy", busy.GetProperty("outcome").GetString());
        Assert.False(busy.TryGetProperty("trust", out _));
        Assert.Empty(screens.Typed);
    }

    [Fact]
    public async Task Quitting_from_a_phone_closes_the_agent_and_the_launch_reads_declined()
    {
        screens.Rows = CopilotAsks();
        ServeTrust(Trusting());
        var ch = await OpenAsync();
        var q = (await LaunchTrustAsync(ch, new { workspace = "App", prompt = "go" })).GetProperty("trust");
        Assert.Equal("Copilot can read files in this folder and, with your permission, edit them or run code and shell commands. It will remember your permissions for the rest of this session.\nDo you trust the files in this folder?",
            q.GetProperty("warning").GetString());

        screens.OnTyped = _ => starter.Exit(starter.Pids[0]);
        var answer = await AnswerAsync(ch, Answer(ch, q.GetProperty("id").GetString()!, "quit", client.Key));

        Assert.Equal(("declined", "Copilot was not trusted with App, and closed; nothing was launched", null), answer);
        var send = Assert.Single(Assert.Single(screens.Plans).Sends);
        Assert.Equal((KeyPlan.Escape, false), (send.Text, send.Enter));
        // Asked again on that outcome: no longer busy, and the question is gone.
        var after = await RequestAsync(ch, LaunchProtocol.Options, new { });
        Assert.False(after.TryGetProperty("busy", out _));
        Assert.False(after.TryGetProperty("trust", out _));
    }

    [Fact]
    public async Task With_the_setting_off_a_trust_question_is_shown_read_only_and_answering_it_is_refused()
    {
        screens.Rows = CopilotAsks();
        ServeTrust(Config());
        var ch = await OpenAsync();
        var launched = await LaunchTrustAsync(ch, new { workspace = "App", prompt = "go" });
        Assert.Contains("Only a person at the machine can answer that", launched.GetProperty("reason").GetString());
        var q = launched.GetProperty("trust");
        Assert.False(q.GetProperty("answerable").GetBoolean());
        Assert.Contains("agent-bridge-host config set launch.remoteFolderTrust true", q.GetProperty("atMachine").GetString());
        Assert.Equal(app, q.GetProperty("folder").GetString());

        var answer = await AnswerAsync(ch, Answer(ch, q.GetProperty("id").GetString()!, "trust", client.Key));
        Assert.Equal("refused", answer.Outcome);
        Assert.StartsWith("Answer at the machine. To answer from your devices", answer.Reason);
        Assert.Empty(screens.Typed);

        // Allowed from the app at the machine while it waits (HostControl applies it at once), but
        // from no device until one is approved there.
        trustLauncher.RemoteFolderTrust = true;
        var allowed = (await RequestAsync(ch, LaunchProtocol.Options, new { })).GetProperty("trust")[0];
        Assert.False(allowed.GetProperty("answerable").GetBoolean());
        Assert.Contains("No device is approved yet", allowed.GetProperty("atMachine").GetString());
        Assert.Equal("refused", (await AnswerAsync(ch, Answer(ch, q.GetProperty("id").GetString()!, "trust", client.Key))).Outcome);
        trustLauncher.TrustDevices = [Wire.DeviceId(client.Key.Public)];
        Assert.True((await RequestAsync(ch, LaunchProtocol.Options, new { })).GetProperty("trust")[0].GetProperty("answerable").GetBoolean());
        Assert.Empty(screens.Typed);
    }

    [Fact]
    public async Task A_device_that_only_says_it_is_a_phone_cannot_answer_until_a_person_approves_it_at_the_machine()
    {
        screens.Rows = CopilotAsks();
        ServeTrust(Trusting());
        // Anything holding the account's token can connect as "pwa" with a key of its own: an
        // agent on another machine, say.
        await using var posing = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "Not a phone", "pwa");
        await posing.Relay.WaitForPeerAsync(p => p.Id == host.Id, Patience);
        var ch = await posing.Hub.OpenAsync(host.Id);
        var posingId = Wire.DeviceId(posing.Key.Public);

        var q = (await LaunchTrustAsync(ch, new { workspace = "App", prompt = "go" })).GetProperty("trust");
        Assert.False(q.GetProperty("answerable").GetBoolean());
        Assert.Contains($"approve this device ({posingId[..8]})", q.GetProperty("atMachine").GetString());
        var answer = await AnswerAsync(ch, Answer(ch, q.GetProperty("id").GetString()!, "trust", posing.Key));
        Assert.Equal(("refused", LaunchService.NotApproved(posingId), null), answer);
        Assert.Empty(screens.Typed);

        // The approved phone sees the same question as answerable.
        var phone = await OpenAsync();
        Assert.True((await RequestAsync(phone, LaunchProtocol.Options, new { })).GetProperty("trust")[0].GetProperty("answerable").GetBoolean());
    }

    [Fact]
    public async Task Only_the_persons_own_device_with_a_command_for_that_choice_and_question_can_answer()
    {
        screens.Rows = CopilotAsks();
        var answers = ServeTrust(Trusting());
        await using var other = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "Other host", "host");
        var ch = await OpenAsync();
        var id = (await LaunchTrustAsync(ch, new { workspace = "App", prompt = "go" })).GetProperty("trust").GetProperty("id").GetString()!;

        // Signed for quit, sent as trust; signed for another question.
        Assert.Equal(("refused", "the command is for launch.trust:quit, not launch.trust:trust", null), await AnswerAsync(ch, Answer(ch, id, "trust", client.Key, signedChoice: "quit")));
        Assert.Equal(("refused", "the command is for another question", null), await AnswerAsync(ch, Answer(ch, id, "trust", client.Key, signedQuestion: Guid.NewGuid().ToString())));

        // Another machine of the account: carrying the phone's command, or signing its own.
        await other.Relay.WaitForPeerAsync(p => p.Id == host.Id, Patience);
        var fromOther = await other.Hub.OpenAsync(host.Id);
        Assert.Equal(("refused", "the command must come from the device that signed it", null), await AnswerAsync(fromOther, Answer(ch, id, "trust", client.Key)));
        Assert.Equal(("refused", "only a person's phone, browser or Windows app can answer a folder-trust question", null), await AnswerAsync(fromOther, Answer(fromOther, id, "trust", other.Key)));
        // Even approved, a machine is shown the question read-only, saying why.
        trustLauncher.TrustDevices = [Wire.DeviceId(client.Key.Public), Wire.DeviceId(other.Key.Public)];
        var shownToMachine = (await RequestAsync(fromOther, LaunchProtocol.Options, new { })).GetProperty("trust")[0];
        Assert.False(shownToMachine.GetProperty("answerable").GetBoolean());
        Assert.Equal("Answer at the machine: only a person's phone, browser or Windows app can answer a folder-trust question.", shownToMachine.GetProperty("atMachine").GetString());

        // Not through anything but the relay: this machine's pipe is where agents and the MCP server are.
        var piped = await new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0")) { TrustAnswers = answers }.HandleAsync(new PipeChannel(), TrustProtocol.Answer, JsonSerializer.SerializeToElement(Answer(ch, id, "trust", client.Key), ProtocolJson.Options));
        Assert.Equal(TrustAnswerOutcome.Refused, Assert.IsType<TrustAnswerResult>(piped).Outcome);

        // Nor while agent-ha-bridge runs here (or the machine is read-only).
        probe.Reason = "agent-ha-bridge is running for this user";
        Assert.Equal("refused", (await AnswerAsync(ch, Answer(ch, id, "trust", client.Key))).Outcome);
        Assert.DoesNotContain(TrustProtocol.Capability, await CapabilitiesAsync(ch));
        Assert.Empty(screens.Typed);
    }

    [Fact]
    public async Task A_window_that_changed_before_the_key_went_is_refused_and_an_answer_not_seen_to_work_is_never_typed_again()
    {
        screens.Rows = CopilotAsks();
        ServeTrust(Trusting(), trustConfirm: TimeSpan.FromMilliseconds(500));
        var ch = await OpenAsync();
        var id = (await LaunchTrustAsync(ch, new { workspace = "App", prompt = "go" })).GetProperty("trust").GetProperty("id").GetString()!;

        screens.Fails = new InjectionResult(InjectionStatus.NothingTyped, "fail screen 0");
        var changed = await AnswerAsync(ch, Answer(ch, id, "trust", client.Key));
        Assert.Equal("refused", changed.Outcome);
        Assert.Contains("its window changed before anything was typed", changed.Reason);

        // Typed, but the question stays: said so, and left for the machine.
        screens.Fails = null;
        var request = Guid.NewGuid().ToString();
        var stuck = await AnswerAsync(ch, Answer(ch, id, "trust", client.Key, request: request));
        Assert.Equal(("unconfirmed", "Not confirmed: answer at the machine. The question is still in Copilot's window.", null), stuck);
        Assert.Equal(2, screens.Plans.Count);
        // The same press again gets the same outcome, and types nothing; nor does a new one.
        Assert.Equal(stuck, await AnswerAsync(ch, Answer(ch, id, "trust", client.Key, request: request)));
        Assert.Equal(("refused", "an answer was already typed into its window; answer at the machine", null), await AnswerAsync(ch, Answer(ch, id, "quit", client.Key)));
        Assert.Equal(2, screens.Plans.Count);
        // And the card no longer offers buttons for it.
        var shown = (await RequestAsync(ch, LaunchProtocol.Options, new { })).GetProperty("trust")[0];
        Assert.False(shown.GetProperty("answerable").GetBoolean());
        Assert.Contains("an answer was already typed into its window", shown.GetProperty("atMachine").GetString());
    }

    [Fact]
    public async Task A_question_that_changes_while_the_highlight_moves_never_gets_Enter()
    {
        locator.Installed["claude"] = @"C:\fake\claude.exe";
        starter.Registers = false;
        screens.Rows = ClaudeAsks();
        ServeTrust(Trusting());
        var ch = await OpenAsync();
        var id = (await LaunchTrustAsync(ch, new { agent = "claude", workspace = "App", prompt = "go" })).GetProperty("trust").GetProperty("id").GetString()!;

        // The highlight lands on trust, but the words above it are no longer the ones shown.
        screens.OnTyped = _ => screens.Rows = [.. ClaudeAsks(onYes: true).Select(r => r.Replace("Claude Code'll be able", "Claude Code will be able"))];
        var answer = await AnswerAsync(ch, Answer(ch, id, "trust", client.Key));

        Assert.Equal(("unconfirmed", "Not confirmed: answer at the machine. The question in its window changed after the highlight moved, so Enter was not pressed.", null), answer);
        Assert.False(Assert.Single(screens.Plans).Sends[0].Enter);
    }

    [Fact]
    public async Task A_device_unapproved_after_the_highlight_moved_does_not_get_Enter_pressed()
    {
        locator.Installed["claude"] = @"C:\fake\claude.exe";
        starter.Registers = false;
        screens.Rows = ClaudeAsks();
        ServeTrust(Trusting());
        var ch = await OpenAsync();
        var id = (await LaunchTrustAsync(ch, new { agent = "claude", workspace = "App", prompt = "go" })).GetProperty("trust").GetProperty("id").GetString()!;

        // Another device stays approved: the list is not empty, but this one is not on it.
        screens.OnTyped = _ =>
        {
            screens.Rows = ClaudeAsks(onYes: true);
            trustLauncher.TrustDevices = [new string('b', 32)];
        };
        var answer = await AnswerAsync(ch, Answer(ch, id, "trust", client.Key));

        Assert.Equal(("unconfirmed", "Not confirmed: answer at the machine. The highlight was moved, but Enter was not pressed: this device is no longer approved to answer from.", null), answer);
        Assert.False(Assert.Single(screens.Plans).Sends[0].Enter);
    }

    [Fact]
    public async Task What_changes_while_an_answer_waits_for_the_typing_slot_stops_it_before_any_key()
    {
        screens.Rows = CopilotAsks();
        ServeTrust(Trusting());
        var ch = await OpenAsync();
        var id = (await LaunchTrustAsync(ch, new { workspace = "App", prompt = "go" })).GetProperty("trust").GetProperty("id").GetString()!;

        // Another delivery held the slot, and agent-ha-bridge started meanwhile.
        screens.BeforeTyping = () => probe.Reason = "agent-ha-bridge is running for this user";
        var typed = false;
        screens.OnTyped = _ => typed = true;
        var answer = await AnswerAsync(ch, Answer(ch, id, "quit", client.Key));

        Assert.Equal("refused", answer.Outcome);
        Assert.Contains("agent-ha-bridge is running for this user", answer.Reason);
        Assert.False(typed);
    }

    [Fact]
    public async Task A_Quit_whose_Esc_closed_the_agent_before_the_worker_said_so_is_still_seen_as_declined()
    {
        screens.Rows = CopilotAsks();
        ServeTrust(Trusting());
        var ch = await OpenAsync();
        var id = (await LaunchTrustAsync(ch, new { workspace = "App", prompt = "go" })).GetProperty("trust").GetProperty("id").GetString()!;

        screens.OnTyped = _ => starter.Exit(starter.Pids[0]);
        screens.Then = new InjectionResult(InjectionStatus.Interrupted, "the console closed");
        var answer = await AnswerAsync(ch, Answer(ch, id, "quit", client.Key));

        Assert.Equal("declined", answer.Outcome);
    }

    [Fact]
    public async Task An_answer_still_being_seen_to_work_is_followed_past_the_follow_deadline()
    {
        locator.Installed["codex"] = @"C:\fake\codex.cmd";
        starter.Registers = false;
        screens.Rows = CodexAsks(Path.GetDirectoryName(app)!.ToLowerInvariant());
        ServeTrust(Trusting(), trustConfirm: TimeSpan.FromSeconds(8), follow: TimeSpan.FromSeconds(2));
        var ch = await OpenAsync();
        var id = (await LaunchTrustAsync(ch, new { agent = "codex", workspace = "App", prompt = "go" })).GetProperty("trust").GetProperty("id").GetString()!;

        // Codex takes the Enter, but its session appears only after the follow deadline has passed.
        screens.OnTyped = _ =>
        {
            screens.Rows = ["› go"];
            Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => starter.RegisterCodex(starter.Specs[0], starter.Pids[0], 7001, starter.CodexId), TaskScheduler.Default);
        };
        var answer = await AnswerAsync(ch, Answer(ch, id, "trust", client.Key));

        Assert.Equal(("started", null, starter.CodexId), answer);
    }

    [Fact]
    public async Task Agent_ha_bridge_starting_after_the_highlight_moved_stops_the_answer_before_Enter()
    {
        locator.Installed["claude"] = @"C:\fake\claude.exe";
        starter.Registers = false;
        screens.Rows = ClaudeAsks();
        ServeTrust(Trusting());
        var ch = await OpenAsync();
        var id = (await LaunchTrustAsync(ch, new { agent = "claude", workspace = "App", prompt = "go" })).GetProperty("trust").GetProperty("id").GetString()!;

        screens.OnTyped = _ =>
        {
            screens.Rows = ClaudeAsks(onYes: true);
            probe.Reason = "agent-ha-bridge is running for this user";
        };
        var answer = await AnswerAsync(ch, Answer(ch, id, "trust", client.Key));

        Assert.Equal("unconfirmed", answer.Outcome);
        Assert.Contains("Enter was not pressed", answer.Reason);
        Assert.Contains("agent-ha-bridge is running for this user", answer.Reason);
        // The move went; Enter never did.
        Assert.False(Assert.Single(screens.Plans).Sends[0].Enter);
    }

    [Fact]
    public async Task A_Copilot_that_handed_its_session_on_is_answered_in_the_sessions_own_window_and_seen_to_close()
    {
        starter.ExitsAtOnce = true;
        starter.HandsOn = true;
        // Only the session's own window can be read; the launcher's went with it.
        screens.Unreadable = pid => starter.Pids.Contains(pid);
        screens.Rows = CopilotAsks();
        ServeTrust(Trusting());
        var ch = await OpenAsync();
        var q = (await LaunchTrustAsync(ch, new { workspace = "App", prompt = "go" })).GetProperty("trust");
        await WaitAsync(async () => (await RequestAsync(ch, LaunchProtocol.Options, new { })).GetProperty("trust")[0].GetProperty("answerable").GetBoolean());

        screens.OnTyped = _ => starter.Exit(starter.Pids[0] + 3000);
        var answer = await AnswerAsync(ch, Answer(ch, (await RequestAsync(ch, LaunchProtocol.Options, new { })).GetProperty("trust")[0].GetProperty("id").GetString()!, "quit", client.Key));

        Assert.Equal("declined", answer.Outcome);
        Assert.Equal(starter.Pids[0] + 3000, Assert.Single(screens.Typed));
    }

    [Fact]
    public async Task A_question_whose_words_change_is_another_question_and_a_command_for_the_first_does_not_answer_it()
    {
        locator.Installed["codex"] = @"C:\fake\codex.cmd";
        starter.Registers = false;
        var root = Path.GetDirectoryName(app)!.ToLowerInvariant();
        screens.Rows = CodexAsks(root);
        ServeTrust(Trusting());
        var ch = await OpenAsync();
        var first = (await LaunchTrustAsync(ch, new { agent = "codex", workspace = "App", prompt = "go" })).GetProperty("trust").GetProperty("id").GetString()!;

        // Now it says trusting applies to a wider folder.
        screens.Rows = CodexAsks(Path.GetDirectoryName(root)!);
        await WaitAsync(async () => (await RequestAsync(ch, LaunchProtocol.Options, new { })).GetProperty("trust")[0].GetProperty("id").GetString() != first);

        var answer = await AnswerAsync(ch, Answer(ch, first, "trust", client.Key));
        Assert.Equal("refused", answer.Outcome);
        Assert.Empty(screens.Typed);
    }

    [Fact]
    public async Task A_highlight_moved_whose_Enter_could_not_go_or_a_worker_that_failed_part_way_is_unconfirmed_never_refused()
    {
        locator.Installed["claude"] = @"C:\fake\claude.exe";
        starter.Registers = false;
        screens.Rows = ClaudeAsks();
        ServeTrust(Trusting());
        var ch = await OpenAsync();
        var id = (await LaunchTrustAsync(ch, new { agent = "claude", workspace = "App", prompt = "go" })).GetProperty("trust").GetProperty("id").GetString()!;

        screens.OnTyped = _ =>
        {
            screens.Rows = ClaudeAsks(onYes: true);
            screens.Fails = new InjectionResult(InjectionStatus.NothingTyped, "fail screen 0");
        };
        var moved = await AnswerAsync(ch, Answer(ch, id, "trust", client.Key));
        Assert.Equal(("unconfirmed", "Not confirmed: answer at the machine. The highlight was moved, but its window changed before Enter, so Enter was not pressed.", null), moved);

        // Another launch, whose worker dies typing: the same press again gets the same outcome.
        starter.Exit(starter.Pids[0]);
        screens.Rows = ClaudeAsks(onYes: true);
        screens.Fails = null;
        screens.OnTyped = null;
        await WaitAsync(async () => !(await RequestAsync(ch, LaunchProtocol.Options, new { })).TryGetProperty("trust", out _));
        await LaunchTrustAsync(ch, new { agent = "claude", workspace = "App", prompt = "again" });
        screens.Rows = ClaudeAsks(onYes: true);
        await WaitAsync(async () => (await RequestAsync(ch, LaunchProtocol.Options, new { })).TryGetProperty("trust", out _));
        var other = (await RequestAsync(ch, LaunchProtocol.Options, new { })).GetProperty("trust")[0].GetProperty("id").GetString()!;
        screens.Throws = new IOException("the worker went away");
        var request = Guid.NewGuid().ToString();
        var broken = await AnswerAsync(ch, Answer(ch, other, "trust", client.Key, request: request));
        Assert.Equal("unconfirmed", broken.Outcome);
        Assert.Contains("the worker went away", broken.Reason);
        Assert.Equal(broken, await AnswerAsync(ch, Answer(ch, other, "trust", client.Key, request: request)));
    }

    // A channel on this machine's pipe, as an agent or the MCP server would open one.
    sealed class PipeChannel : IProtocolChannel
    {
        public string PeerId => "local-1234";
        public string PeerName => "agent";
        public string PeerKind => "local";
        public bool IsOpen => true;
        public Task Closed => Task.Delay(Timeout.Infinite);
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }
}
