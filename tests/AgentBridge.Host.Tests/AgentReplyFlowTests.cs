using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Host.Delivery.Windows;
using AgentBridge.Protocol;
using Xunit;

// session.reply for Claude Code and Codex, end to end below the channel (which is the same for
// every agent: ReplyFlowTests): fixture sessions read by the real SessionSource, replied to by
// the real ReplyService into a fake console that shows a screen, honours KeySend.Expect as the
// injector does, and plays the agent by writing what it records. The screens are the rows
// Claude Code 2.1.312 and Codex 0.165 showed live.
public sealed class AgentReplyFlowTests : IAsyncLifetime
{
    const string ClaudeId = "c1a0de00-0000-4000-8000-000000000011";
    const string CodexId = "c0de0000-0000-4000-8000-000000000012";
    const string Peer = "phone";
    static readonly string Rule = new('─', 60);

    readonly Fixture f = new();
    readonly Probe probe = new();
    readonly Screen console = new();
    SessionSource source = null!;
    AttachmentStore store = null!;
    string claude = "";
    string rollout = "";
    int second = 10;

    public async Task InitializeAsync()
    {
        claude = f.ClaudeTranscript(ClaudeId);
        f.ClaudeRegistration(ClaudeId, 300, claude);
        Fixture.Append(claude, Claude.User("plan it", 1));
        f.Processes.Run(AgentKind.Claude, 300);

        rollout = f.CodexTranscript(CodexId);
        f.CodexRegistration(CodexId, 400, rollout, status: "idle");
        Fixture.Append(rollout, Codex.Event(new { type = "task_started" }, 1), Codex.Event(new { type = "task_complete" }, 2));
        f.Processes.Run(AgentKind.Codex, 400);

        source = new SessionSource(f.Options(), f.Processes, TimeProvider.System);
        await source.RefreshAsync();
        store = new AttachmentStore(new AttachmentOptions { StagingRoot = Path.Combine(f.Root, "profile", "attachments") }, sweep: false);
    }

    public async Task DisposeAsync()
    {
        store.Dispose();
        await source.DisposeAsync();
        f.Dispose();
    }

    ReplyService Replier() => new(source, f.Processes, probe, new NoGuard(), console, new ReplyOptions
    {
        CopilotHome = f.CopilotHome,
        PlatformSupported = true,
        ConfirmTimeout = TimeSpan.FromSeconds(2),
        PollInterval = TimeSpan.FromMilliseconds(25),
        SubmitAfter = TimeSpan.FromMilliseconds(200),
    }, attachments: store);

    ReplyService? replier;

    Task<ReplyResult> ReplyAsync(string id, string text, params string[] files) => (replier ??= Replier()).ReplyAsync(new ReplyRequest
    {
        Id = id,
        Request = Guid.NewGuid().ToString(),
        Text = text,
        Attachments = files.Length == 0 ? null : [.. files.Select(a => new AttachmentRef { Id = a })],
    }, Peer);

    // ---- screens, as the agents drew them ----

    static List<string> ClaudeIdle(string typed = "") =>
        ["> plan it", "", "● Planned.", "", Rule, typed.Length == 0 ? ">" : "> " + typed, Rule, "  ⏵⏵ auto mode on (shift+tab to cycle)"];

    static List<string> ClaudePermission() =>
        ["> Create perm.txt", "", "● Write(perm.txt)", "", Rule, " Create file", " perm.txt", " Do you want to create perm.txt?", " > 1. Yes", "   2. Yes, and switch to accept edits (shift+tab)", "   3. No", "", " Esc to cancel · Tab to amend"];

    static List<string> ClaudeQuestion() =>
        ["> Ask me", Rule, " ☐ Color", "", "Do you prefer red or blue?", "", "> 1. Red", "     You prefer red.", "  2. Blue", "  3. Type something.", Rule, "  4. Chat about this", "", "Enter to select · ↑/↓ to navigate · Esc to cancel"];

    static List<string> CodexIdle(string? typed = null) =>
        ["› fix it", "", "• Fixed.", "", typed is null ? "› Ask Codex to do anything" : "› " + typed, "", "  GPT-6.1-Sol default · ~\\repo · Fix it", typed is null ? "  ? for shortcuts                                1 warning · f2 to view" : "                                                1 warning · f2 to view"];

    static List<string> CodexApproval() =>
        ["› push it", "", "  Would you like to run the following command?", "", "  $ git push", "", "› 1. Yes, proceed (y)", "  2. Yes, and don't ask again for commands that start with `git push` (p)", "  3. No, and tell Codex what to do differently (esc)", "", "  Press enter to confirm or esc to cancel"];

    // ---- what the agents record ----

    string ClaudeQueued(string text) => new JsonObject { ["type"] = "queue-operation", ["operation"] = "enqueue", ["timestamp"] = Copilot.At(second++), ["sessionId"] = ClaudeId, ["content"] = text }.ToJsonString();

    string ClaudePrompt(string text, string source = "typed")
    {
        var e = JsonNode.Parse(Claude.User(text, second++))!.AsObject();
        e["promptSource"] = source;
        return e.ToJsonString();
    }

    string ClaudeRead(string path) => new JsonObject
    {
        ["type"] = "attachment",
        ["isSidechain"] = false,
        ["attachment"] = new JsonObject { ["type"] = "file", ["filename"] = path, ["content"] = new JsonObject { ["type"] = "image" } },
        ["timestamp"] = Copilot.At(second++),
    }.ToJsonString();

    string CodexPrompt(string text, params string[] images) => Codex.Event(new
    {
        type = "item_completed",
        item = new
        {
            type = "UserMessage",
            content = images.Select(p => (object)new { type = "local_image", path = p }).Append(new { type = "text", text, text_elements = Array.Empty<object>() }).ToArray(),
        },
    }, second++);

    static string Typed(KeyPlan plan) => string.Concat(plan.Sends.Select(s => s.Text));

    // ---- Claude Code ----

    [Fact]
    public async Task A_Claude_reply_is_typed_into_its_empty_input_box_as_one_line_then_Enter_and_delivered_once_recorded_as_typed()
    {
        console.Rows = ClaudeIdle();
        console.OnInject = plan => Fixture.Append(claude, ClaudePrompt(Typed(plan)));

        var result = await ReplyAsync(ClaudeId, "Looks good.\nShip it 🚀");

        Assert.Equal(new ReplyResult(AnswerOutcome.Delivered), result);
        var (pid, plan) = Assert.Single(console.Plans);
        Assert.Equal(300, pid);
        var send = Assert.Single(plan.Sends);
        Assert.Equal(("Looks good. Ship it 🚀", true), (send.Text, send.Enter));
        // Typed only while the box still reads as it did: the rule, the empty row, the rule.
        Assert.Equal([(4, Rule), (5, ">"), (6, Rule)], send.Expect!.Select(r => (r.Row, r.Text)));
    }

    [Fact]
    public async Task A_Claude_reply_typed_while_it_works_is_delivered_by_the_queue_entry_that_records_it()
    {
        console.Rows = ClaudeIdle();
        console.OnInject = plan => Fixture.Append(claude, ClaudeQueued(Typed(plan)));

        Assert.Equal(AnswerOutcome.Delivered, (await ReplyAsync(ClaudeId, "Also add tests.")).Outcome);
        Assert.Single(console.Plans);
    }

    [Fact]
    public async Task A_queued_prompt_taken_later_does_not_confirm_a_second_reply_of_the_same_words()
    {
        console.Rows = ClaudeIdle();
        console.OnInject = plan => Fixture.Append(claude, ClaudeQueued(Typed(plan)));
        Assert.Equal(AnswerOutcome.Delivered, (await ReplyAsync(ClaudeId, "again")).Outcome);

        // As the second reply is typed, Claude takes the first from its queue, recording it once
        // more, and swallows the second's keys.
        console.OnInject = _ => Fixture.Append(claude, ClaudePrompt("again", source: "queued"));

        var second = await ReplyAsync(ClaudeId, "again");

        Assert.Equal(AnswerOutcome.Unconfirmed, second.Outcome);
    }

    public static TheoryData<string> PromptScreens => ["permission", "question"];

    [Theory]
    [MemberData(nameof(PromptScreens))]
    public async Task A_Claude_reply_is_refused_with_nothing_typed_while_a_prompt_holds_its_screen(string prompt)
    {
        console.Rows = prompt == "permission" ? ClaudePermission() : ClaudeQuestion();

        var result = await ReplyAsync(ClaudeId, "1");

        Assert.Equal(AnswerOutcome.Refused, result.Outcome);
        Assert.Contains("input box is not on its screen", result.Reason);
        Assert.Empty(console.Plans);
    }

    [Fact]
    public async Task A_reply_is_refused_with_nothing_typed_while_words_are_already_in_the_input_box()
    {
        console.Rows = ClaudeIdle("half a thought");
        Assert.Contains("already typed in Claude's input box", (await ReplyAsync(ClaudeId, "hello")).Reason);

        console.Rows = CodexIdle("half a thought");
        Assert.Contains("already typed in Codex's input box", (await ReplyAsync(CodexId, "hello")).Reason);
        Assert.Empty(console.Plans);
    }

    [Fact]
    public async Task A_reply_is_refused_when_the_input_box_changes_just_before_its_keys_go()
    {
        console.Rows = ClaudeIdle();
        console.BeforeInject = () => console.Rows = ClaudePermission();

        var result = await ReplyAsync(ClaudeId, "hello");

        Assert.Equal(AnswerOutcome.Refused, result.Outcome);
        Assert.Contains("screen changed", result.Reason);
        Assert.Empty(console.Typed);
    }

    [Fact]
    public async Task A_Claude_reply_whose_Enter_was_taken_as_part_of_the_paste_gets_Enter_again_held_to_the_box_holding_it()
    {
        console.Rows = ClaudeIdle();
        console.OnInject = plan =>
        {
            if (plan.Sends is [{ Text: "", Enter: true }]) { Fixture.Append(claude, ClaudePrompt("a long reply")); }
            else { console.Rows = ClaudeIdle("a long reply"); }
        };

        var result = await ReplyAsync(ClaudeId, "a long reply");

        Assert.Equal(AnswerOutcome.Delivered, result.Outcome);
        Assert.Equal(2, console.Plans.Count);
        var enter = Assert.Single(console.Plans[1].Plan.Sends);
        Assert.Equal(("", true), (enter.Text, enter.Enter));
        Assert.Equal((5, "> a long reply"), (enter.Expect![1].Row, enter.Expect[1].Text));
    }

    [Fact]
    public async Task No_Enter_is_pressed_again_once_a_prompt_has_taken_the_input_box_and_the_reply_is_unconfirmed()
    {
        console.Rows = ClaudeIdle();
        console.OnInject = _ => console.Rows = ClaudePermission();

        var result = await ReplyAsync(ClaudeId, "go on");

        Assert.Equal(AnswerOutcome.Unconfirmed, result.Outcome);
        Assert.Contains("did not record the reply", result.Reason);
        Assert.Single(console.Plans);
    }

    [Fact]
    public async Task An_input_box_that_has_not_drawn_the_reply_yet_is_looked_at_again_before_Enter_is_pressed()
    {
        console.Rows = ClaudeIdle();
        console.OnInject = plan =>
        {
            if (plan.Sends is [{ Text: "", Enter: true }]) { Fixture.Append(claude, ClaudePrompt("drawn late")); return; }
            // The box still reads empty at the first look, and shows the words only after it.
            _ = Task.Run(async () => { await Task.Delay(300); console.Rows = ClaudeIdle("drawn late"); });
        };

        Assert.Equal(AnswerOutcome.Delivered, (await ReplyAsync(ClaudeId, "drawn late")).Outcome);
        Assert.Equal(2, console.Plans.Count);
    }

    [Fact]
    public async Task A_Claude_reply_that_never_records_gets_two_more_Enters_at_most()
    {
        console.Rows = ClaudeIdle();
        console.OnInject = _ => console.Rows = ClaudeIdle("stuck");

        Assert.Equal(AnswerOutcome.Unconfirmed, (await ReplyAsync(ClaudeId, "stuck")).Outcome);
        Assert.Equal(3, console.Plans.Count);
    }

    [Fact]
    public async Task A_Claude_reply_with_files_mentions_each_ahead_of_the_words_and_is_delivered_once_Claude_has_read_every_one()
    {
        console.Rows = ClaudeIdle();
        var shot = Upload(Png(300), "Screen Shot.png");
        var notes = Upload("# plan\n"u8.ToArray(), "plan.md");
        console.OnInject = plan =>
        {
            var line = Typed(plan);
            Fixture.Append(claude, ClaudePrompt(line));
            foreach (var path in line.Split(' ').Where(w => w.StartsWith('@')).Select(w => w[1..])) { Fixture.Append(claude, ClaudeRead(path)); }
        };

        var result = await ReplyAsync(ClaudeId, "What do you see?", shot, notes);

        Assert.Equal(new ReplyResult(AnswerOutcome.Delivered), result);
        var line = Typed(Assert.Single(console.Plans).Plan);
        Assert.Matches(@"^@\S+-Screen-Shot\.png @\S+-plan\.md What do you see\?$", line);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task A_Claude_reply_with_a_file_queued_while_it_works_is_delivered_once_Claude_takes_it_and_reads_the_file()
    {
        console.Rows = ClaudeIdle();
        var shot = Upload(Png(300));
        console.OnInject = plan =>
        {
            var line = Typed(plan);
            Fixture.Append(claude, ClaudeQueued(line));
            _ = Task.Run(async () =>
            {
                await Task.Delay(300);
                Fixture.Append(claude, ClaudePrompt(line, source: "queued"), ClaudeRead(line.Split(' ')[0][1..]));
            });
        };

        Assert.Equal(AnswerOutcome.Delivered, (await ReplyAsync(ClaudeId, "and this", shot)).Outcome);
    }

    [Fact]
    public async Task A_Claude_reply_whose_file_is_never_read_is_unconfirmed()
    {
        console.Rows = ClaudeIdle();
        console.OnInject = plan => Fixture.Append(claude, ClaudePrompt(Typed(plan)));

        var result = await ReplyAsync(ClaudeId, "look", Upload(Png(100)));

        Assert.Equal(AnswerOutcome.Unconfirmed, result.Outcome);
        Assert.Contains("has not read every file", result.Reason);
    }

    // ---- Codex ----

    [Fact]
    public async Task A_Codex_reply_is_typed_into_its_empty_input_box_and_delivered_once_its_rollout_records_the_same_words()
    {
        console.Rows = CodexIdle();
        console.OnInject = plan => Fixture.Append(rollout, CodexPrompt(Typed(plan)));

        Assert.Equal(new ReplyResult(AnswerOutcome.Delivered), await ReplyAsync(CodexId, "Now run\nthe tests."));
        var (pid, plan) = Assert.Single(console.Plans);
        Assert.Equal(400, pid);
        var send = Assert.Single(plan.Sends);
        Assert.Equal(("Now run the tests.", true), (send.Text, send.Enter));
        Assert.Equal([4, 5, 6, 7], send.Expect!.Select(r => r.Row));
    }

    [Fact]
    public async Task An_older_Codex_that_records_a_user_message_event_confirms_a_reply_the_same_way()
    {
        console.Rows = CodexIdle();
        console.OnInject = plan => Fixture.Append(rollout, Codex.Event(new { type = "user_message", message = Typed(plan) }, second++));

        Assert.Equal(AnswerOutcome.Delivered, (await ReplyAsync(CodexId, "hello")).Outcome);
    }

    [Fact]
    public async Task A_Codex_reply_its_rollout_records_differently_or_not_at_all_is_unconfirmed()
    {
        console.Rows = CodexIdle();
        console.OnInject = _ => Fixture.Append(rollout, CodexPrompt("something else"));
        Assert.Contains("recorded a different message", (await ReplyAsync(CodexId, "hello")).Reason);

        console.OnInject = null;
        Assert.Contains("did not record the reply", (await ReplyAsync(CodexId, "hello")).Reason);
    }

    [Fact]
    public async Task A_Codex_reply_whose_Enter_became_a_new_line_gets_Enter_again()
    {
        console.Rows = CodexIdle();
        console.OnInject = plan =>
        {
            if (plan.Sends is [{ Text: "", Enter: true }]) { Fixture.Append(rollout, CodexPrompt("Run the long command please")); }
            else { console.Rows = [.. CodexIdle("Run the long command").Take(5), "  please", "", "", "  GPT-6.1-Sol default · ~\\repo · Fix it"]; }
        };

        Assert.Equal(AnswerOutcome.Delivered, (await ReplyAsync(CodexId, "Run the long command please")).Outcome);
        Assert.Equal(2, console.Plans.Count);
        Assert.Equal([4, 5], console.Plans[1].Plan.Sends.Single().Expect!.Select(r => r.Row));
    }

    [Fact]
    public async Task A_Codex_reply_is_refused_with_nothing_typed_while_an_approval_holds_its_screen()
    {
        console.Rows = CodexApproval();

        var result = await ReplyAsync(CodexId, "y");

        Assert.Equal(AnswerOutcome.Refused, result.Outcome);
        Assert.Contains("input box is not on its screen", result.Reason);
        Assert.Empty(console.Plans);
    }

    [Fact]
    public async Task A_Codex_reply_with_images_pastes_each_path_on_its_own_and_is_delivered_once_Codex_records_them_attached()
    {
        console.Rows = CodexIdle();
        var a = Upload(Png(200), "a.png");
        var b = Upload(Png(200), "b.png");
        console.OnInject = plan =>
        {
            var paths = plan.Sends.Where(s => !s.Enter).Select(s => s.Text).ToArray();
            Fixture.Append(rollout, CodexPrompt("[Image #1][Image #2]  What changed?", paths));
        };

        Assert.Equal(new ReplyResult(AnswerOutcome.Delivered), await ReplyAsync(CodexId, "What changed?", a, b));
        var sends = Assert.Single(console.Plans).Plan.Sends;
        Assert.Equal(3, sends.Count);
        Assert.All(sends.Take(2), s => Assert.Matches(@"^\S+-[ab]\.png$", s.Text));
        Assert.Equal((" What changed?", true), (sends[2].Text, sends[2].Enter));
        // Only the first paste is held to the box; each later one finds the box as the last left it.
        Assert.NotNull(sends[0].Expect);
        Assert.Null(sends[1].Expect);
    }

    [Fact]
    public async Task A_Codex_reply_whose_image_Codex_did_not_attach_is_unconfirmed()
    {
        console.Rows = CodexIdle();
        console.OnInject = _ => Fixture.Append(rollout, CodexPrompt("[Image #1] see"));

        var result = await ReplyAsync(CodexId, "see", Upload(Png(100)));

        Assert.Equal(AnswerOutcome.Unconfirmed, result.Outcome);
        Assert.Contains("did not attach every image", result.Reason);
    }

    [Fact]
    public async Task Codex_is_sent_images_only_and_a_reply_with_another_file_is_refused_keeping_the_file_for_another_try()
    {
        console.Rows = CodexIdle();
        var notes = Upload("# plan\n"u8.ToArray(), "plan.md");

        var result = await ReplyAsync(CodexId, "read this", notes);

        Assert.Equal(AnswerOutcome.Refused, result.Outcome);
        Assert.Contains("Codex takes only images", result.Reason);
        Assert.Empty(console.Plans);
        Assert.Equal(1, store.Count);
    }

    // ---- helpers ----

    string Upload(byte[] bytes, string name = "shot.png")
    {
        var id = Guid.NewGuid().ToString();
        store.Start(Peer, new AttachmentStart { Attachment = id, Name = name, Size = bytes.Length, Sha256 = Attachments.Base64Url(SHA256.HashData(bytes)) });
        store.Chunk(Peer, new AttachmentChunk { Attachment = id, Offset = 0, Data = Convert.ToBase64String(bytes) });
        return id;
    }

    static byte[] Png(int size)
    {
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }

    sealed class Probe : IAgentHaBridgeProbe
    {
        public string? Active() => null;
    }

    sealed class NoGuard : ITargetGuard
    {
        public string? Refuse(int processId) => null;
    }

    // A console as the injector sees one: its visible rows, and a plan whose first send is held
    // to rows that no longer read so types nothing, as the worker does.
    sealed class Screen : IConsoleInjector
    {
        public volatile List<string> Rows = [];
        public readonly List<(int Pid, KeyPlan Plan)> Plans = [];
        public readonly List<KeyPlan> Typed = [];
        public Action<KeyPlan>? OnInject;
        public Action? BeforeInject;

        public Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default)
        {
            BeforeInject?.Invoke();
            BeforeInject = null;
            var rows = Rows;
            if (plan.Sends[0].Expect is { } expect && expect.Any(e => e.Row >= rows.Count || rows[e.Row].TrimEnd() != e.Text))
            {
                return Task.FromResult(new InjectionResult(InjectionStatus.NothingTyped, WorkerInjector.ScreenChanged));
            }
            lock (Plans) { Plans.Add((processId, plan)); Typed.Add(plan); }
            OnInject?.Invoke(plan);
            return Task.FromResult(new InjectionResult(InjectionStatus.Typed, "ok"));
        }

        public Task<ConsoleScreen> ReadScreenAsync(int processId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConsoleScreen([.. Rows], null));
    }
}
