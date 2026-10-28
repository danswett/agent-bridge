using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using Xunit;

// session.answer for Claude Code and Codex, and for the prompts only the machine can answer,
// end to end below the channel: fixture sessions read by the real SessionSource, answered by
// the real AnswerService into a fake injector that plays the agent by writing what it would
// record. The channel itself is the same for every agent (AnswerFlowTests).
public sealed class AgentAnswerFlowTests : IAsyncLifetime
{
    const string ClaudeId = "c1a0de00-0000-4000-8000-000000000001";
    const string CodexId = "c0de0000-0000-4000-8000-000000000002";
    const string CopilotId = "c0b11070-0000-4000-8000-000000000003";

    readonly Fixture f = new();
    readonly Probe probe = new();
    readonly Guard guard = new();
    readonly Injector injector = new();
    SessionSource source = null!;
    string claude = "";
    string rollout = "";
    string ledger = "";
    int second = 10;

    static readonly JsonObject Input = Claude.Ask(("Which database?", false, ["PostgreSQL", "SQLite", "DuckDB"]), ("Which features?", true, ["Auth", "Billing", "Search"]));

    public async Task InitializeAsync()
    {
        claude = f.ClaudeTranscript(ClaudeId);
        f.ClaudeRegistration(ClaudeId, 300, claude);
        Fixture.Append(claude, Claude.User("plan it", 1), Claude.Assistant(2, "tool_use", Claude.ToolUse("toolu_1", "AskUserQuestion", Input)));
        f.Processes.Run(AgentKind.Claude, 300);

        rollout = f.CodexTranscript(CodexId);
        f.CodexRegistration(CodexId, 400, rollout, status: "waiting", updated: Fixture.T0.AddSeconds(5));
        f.CodexApprovalMarker(CodexId, "node-1", "Needs approval: git push", Fixture.T0.AddSeconds(5));
        Fixture.Append(rollout, Codex.Event(new { type = "task_started" }, 1), Codex.Event(new { type = "user_message", message = "push it" }, 1));
        f.Processes.Run(AgentKind.Codex, 400);

        ledger = Path.Combine(f.Root, "profile", "answer-attempts.json");
        source = new SessionSource(f.Options(), f.Processes, TimeProvider.System);
        await source.RefreshAsync();
    }

    public async Task DisposeAsync()
    {
        await source.DisposeAsync();
        f.Dispose();
    }

    AnswerService Answerer() => new(source, f.Processes, probe, guard, injector, new AnswerOptions
    {
        CopilotHome = f.CopilotHome,
        PlatformSupported = true,
        ConfirmTimeout = TimeSpan.FromSeconds(2),
        PollInterval = TimeSpan.FromMilliseconds(25),
        ClaudeReviewAfter = TimeSpan.FromMilliseconds(300),
        AttemptLedgerPath = ledger,
    });

    static AnswerRequest Request(string session, string question, object fields) => new()
    {
        Id = session,
        Question = question,
        Request = Guid.NewGuid().ToString(),
        Fields = JsonSerializer.SerializeToElement(fields).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()),
    };

    static readonly Dictionary<string, object> Chosen = new() { ["Which database?"] = "SQLite", ["Which features?"] = new[] { "Auth", "Search" } };

    string Recorded(string database = "SQLite", string features = "Auth, Search") =>
        Claude.Answered("toolu_1", second++, new Dictionary<string, string> { ["Which database?"] = database, ["Which features?"] = features });

    [Fact]
    public async Task A_Claude_answer_the_transcript_then_records_by_its_tool_use_id_is_delivered()
    {
        injector.OnInject = _ => Fixture.Append(claude, Recorded());

        var result = await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen));

        Assert.Equal(new AnswerResult(AnswerOutcome.Delivered), result);
        var (pid, plan) = Assert.Single(injector.Plans);
        Assert.Equal(300, pid);
        Assert.Equal([KeyPlan.Down, "1", "3", KeyPlan.Down + KeyPlan.Down + KeyPlan.Down + KeyPlan.Down], plan.Sends.Select(s => s.Text));
    }

    [Fact]
    public async Task A_Claude_prompt_left_on_its_review_screen_gets_exactly_one_more_Enter()
    {
        // Nothing is recorded until the Enter that submits the review screen.
        injector.OnInject = plan => { if (plan.Sends is [{ Text: "", Enter: true }]) { Fixture.Append(claude, Recorded()); } };

        var result = await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen));

        Assert.Equal(AnswerOutcome.Delivered, result.Outcome);
        Assert.Equal(2, injector.Plans.Count);
        Assert.Equal([("", true)], injector.Plans[1].Plan.Sends.Select(s => (s.Text, s.Enter)));
    }

    [Fact]
    public async Task A_Claude_prompt_that_never_records_an_answer_gets_one_Enter_and_is_unconfirmed()
    {
        var result = await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen));

        Assert.Equal(AnswerOutcome.Unconfirmed, result.Outcome);
        Assert.Contains("did not record an answer", result.Reason);
        Assert.Equal(2, injector.Plans.Count);
    }

    [Fact]
    public async Task A_Claude_transcript_that_cannot_be_read_types_nothing_and_never_earns_the_review_Enter()
    {
        // Gone after the keys: nothing says the question is still unanswered, so no Enter.
        injector.OnInject = _ => File.Delete(claude);
        var typed = await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen));
        Assert.Equal(AnswerOutcome.Unconfirmed, typed.Outcome);
        Assert.Single(injector.Plans);

        // Gone before them: nothing is typed at all.
        Fixture.Append(claude, Claude.Assistant(second++, "tool_use", Claude.ToolUse("toolu_2", "AskUserQuestion", Input)));
        await source.RefreshAsync();
        File.Delete(claude);
        var refused = await Answerer().AnswerAsync(Request(ClaudeId, "toolu_2", Chosen));
        Assert.Equal(AnswerOutcome.Refused, refused.Outcome);
        Assert.Contains("could not be read", refused.Reason);
        Assert.Single(injector.Plans);
    }

    [Fact]
    public async Task A_different_answer_recorded_by_Claude_is_unconfirmed_never_delivered()
    {
        injector.OnInject = _ => Fixture.Append(claude, Recorded(database: "DuckDB"));

        var result = await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen));

        Assert.Equal(AnswerOutcome.Unconfirmed, result.Outcome);
        Assert.Contains("different answer", result.Reason);
    }

    [Fact]
    public async Task A_Claude_question_answered_at_the_machine_a_moment_ago_is_stale_and_nothing_is_typed()
    {
        // In the transcript before the host's own view has caught up.
        Fixture.Append(claude, Recorded());

        var result = await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen));

        Assert.Equal(new AnswerResult(AnswerOutcome.Stale, "the question has already been answered"), result);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_prompt_once_typed_into_is_never_typed_into_again_by_a_new_request_or_after_a_restart()
    {
        // The first attempt reaches the prompt, which records nothing.
        Assert.Equal(AnswerOutcome.Unconfirmed, (await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen))).Outcome);
        var typed = injector.Plans.Count;

        var again = await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen));
        Assert.Equal(AnswerOutcome.Refused, again.Outcome);
        Assert.Contains("already typed into this prompt once", again.Reason);

        var codex = Answerer();
        Assert.Equal(AnswerOutcome.Unconfirmed, (await codex.AnswerAsync(Request(CodexId, "node-1", new { decision = "approve" }))).Outcome);
        Assert.Equal(AnswerOutcome.Refused, (await Answerer().AnswerAsync(Request(CodexId, "node-1", new { decision = "deny" }))).Outcome);
        Assert.Equal(typed + 1, injector.Plans.Count);
    }

    [Fact]
    public async Task Nothing_reaching_the_console_releases_the_prompt_to_be_answered_again()
    {
        injector.Result = new InjectionResult(InjectionStatus.NothingTyped, "could not attach to the session's console");
        Assert.Equal(new AnswerResult(AnswerOutcome.Refused, "could not attach to the session's console"), await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen)));

        injector.Result = new InjectionResult(InjectionStatus.Typed, "ok");
        injector.OnInject = _ => Fixture.Append(claude, Recorded());
        Assert.Equal(AnswerOutcome.Delivered, (await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen))).Outcome);
    }

    [Fact]
    public async Task An_attempt_that_cannot_be_recorded_types_nothing()
    {
        Directory.CreateDirectory(ledger + ".tmp");

        var result = await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen));

        Assert.Equal(AnswerOutcome.Refused, result.Outcome);
        Assert.Contains("could not record the attempt", result.Reason);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task An_elevated_Claude_or_Codex_session_is_refused_untyped()
    {
        guard.Reason = "the session runs as administrator";

        Assert.Equal(new AnswerResult(AnswerOutcome.Refused, guard.Reason), await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen)));
        Assert.Equal(new AnswerResult(AnswerOutcome.Refused, guard.Reason), await Answerer().AnswerAsync(Request(CodexId, "node-1", new { decision = "approve" })));
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task Agent_ha_bridge_starting_before_the_keys_go_stops_a_Claude_answer_and_a_Codex_approval()
    {
        probe.Answers = new Queue<string?>([null, "agent-ha-bridge is running for this user", null, "agent-ha-bridge is running for this user"]);

        Assert.Equal(new AnswerResult(AnswerOutcome.Refused, "agent-ha-bridge is running for this user"), await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen)));
        Assert.Equal(new AnswerResult(AnswerOutcome.Refused, "agent-ha-bridge is running for this user"), await Answerer().AnswerAsync(Request(CodexId, "node-1", new { decision = "approve" })));
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_Codex_approval_types_y_or_n_and_is_unconfirmed_since_Codex_records_no_answer_to_it()
    {
        var marker = File.ReadAllBytes(Path.Combine(f.CodexRegistrations, CodexId + ".approval.json"));

        var approved = await Answerer().AnswerAsync(Request(CodexId, "node-1", new { decision = "approve" }));

        Assert.Equal(AnswerOutcome.Unconfirmed, approved.Outcome);
        Assert.Contains("typed approve", approved.Reason);
        var (pid, plan) = Assert.Single(injector.Plans);
        Assert.Equal((400, "y", true), (pid, plan.Sends[0].Text, plan.Sends[0].Enter));
        // The marker is agent-ha-bridge's: read, never written.
        Assert.Equal(marker, File.ReadAllBytes(Path.Combine(f.CodexRegistrations, CodexId + ".approval.json")));

        f.CodexApprovalMarker(CodexId, "node-2");
        await source.RefreshAsync();
        Assert.Equal(AnswerOutcome.Unconfirmed, (await Answerer().AnswerAsync(Request(CodexId, "node-2", new { decision = "deny" }))).Outcome);
        Assert.Equal("n", injector.Plans[1].Plan.Sends[0].Text);
    }

    [Fact]
    public async Task A_Codex_approval_whose_marker_changed_went_or_cannot_be_read_just_before_typing_types_nothing()
    {
        var answerer = Answerer();
        // Each is written after the host last read the session, so only the check just
        // before typing can see it.
        f.CodexApprovalMarker(CodexId, "node-9");
        Assert.Equal(new AnswerResult(AnswerOutcome.Stale, "Codex is asking for a different approval now"), await answerer.AnswerAsync(Request(CodexId, "node-1", new { decision = "approve" })));

        File.Delete(Path.Combine(f.CodexRegistrations, CodexId + ".approval.json"));
        Assert.Equal(new AnswerResult(AnswerOutcome.Stale, "Codex is no longer asking for that approval"), await answerer.AnswerAsync(Request(CodexId, "node-1", new { decision = "approve" })));

        File.WriteAllText(Path.Combine(f.CodexRegistrations, CodexId + ".approval.json"), "{\"SessionId\":");
        var unreadable = await answerer.AnswerAsync(Request(CodexId, "node-1", new { decision = "approve" }));
        Assert.Equal(AnswerOutcome.Refused, unreadable.Outcome);
        Assert.Contains("could not be read", unreadable.Reason);

        f.CodexApprovalMarker(CodexId, "node-1", attempted: true);
        var attempted = await answerer.AnswerAsync(Request(CodexId, "node-1", new { decision = "approve" }));
        Assert.Equal(AnswerOutcome.Refused, attempted.Outcome);
        Assert.Contains("agent-ha-bridge has already typed", attempted.Reason);

        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task Prompts_only_the_machine_can_answer_are_refused_untyped_and_keep_replies_out()
    {
        // A Codex approval seen only in the rollout, a Claude permission prompt its hook
        // reported, and a Copilot permission request.
        File.Delete(Path.Combine(f.CodexRegistrations, CodexId + ".approval.json"));
        Fixture.Append(rollout, Codex.Event(new { type = "exec_approval_request", call_id = "call7", command = new[] { "git", "push" } }, 6));
        f.ClaudeRegistration(ClaudeId, 300, claude, hookStatus: "waiting", hookAt: Fixture.T0.AddMinutes(5));
        Fixture.Append(claude, Recorded(), Claude.Assistant(second++, "tool_use", Claude.ToolUse("tu_bash", "Bash", new JsonObject { ["command"] = "deploy.sh" })));
        var events = f.CopilotSession(CopilotId, 500);
        Fixture.Append(events, Copilot.User("clean up", 1), Copilot.ToolStart("c9", "powershell", new { command = "rm -rf build" }, 2),
            Copilot.Event("permission.requested", new { requestId = "r1", permissionRequest = new { kind = "shell", toolCallId = "c9", toolTitle = "Run a command" } }, 3));
        f.Processes.Run(AgentKind.Copilot, 500);
        await source.RefreshAsync();

        var codex = await Answerer().AnswerAsync(Request(CodexId, "call7", new { decision = "approve" }));
        var claudePermission = await Answerer().AnswerAsync(Request(ClaudeId, "tu_bash", new { decision = "approve" }));
        // A permission request that does not say what it would allow.
        var copilot = await Answerer().AnswerAsync(Request(CopilotId, source.Get(CopilotId)!.Question!.Id, new { decision = "approve" }));

        Assert.Equal(new AnswerResult(AnswerOutcome.Refused, "Codex permission prompts can only be answered at the machine"), codex);
        Assert.Equal(new AnswerResult(AnswerOutcome.Refused, "Claude permission prompts can only be answered at the machine"), claudePermission);
        Assert.Equal(new AnswerResult(AnswerOutcome.Refused, "Copilot permission prompts can only be answered at the machine"), copilot);
        Assert.Empty(injector.Plans);

        var replies = new ReplyService(source, f.Processes, probe, guard, injector, new ReplyOptions { CopilotHome = f.CopilotHome, PlatformSupported = true });
        var reply = await replies.ReplyAsync(new ReplyRequest { Id = CopilotId, Request = Guid.NewGuid().ToString(), Text = "yes" });
        Assert.Equal(new ReplyResult(AnswerOutcome.Refused, "the session is waiting at a prompt that can only be answered at the machine; answer it there first"), reply);
        Assert.Empty(injector.Plans);
    }

    sealed class Probe : IAgentHaBridgeProbe
    {
        public Queue<string?>? Answers;

        public string? Active() => Answers is { Count: > 0 } queue ? queue.Dequeue() : null;
    }

    sealed class Guard : ITargetGuard
    {
        public string? Reason;

        public string? Refuse(int processId) => Reason;
    }

    sealed class Injector : IConsoleInjector
    {
        public readonly List<(int Pid, KeyPlan Plan)> Plans = [];
        public Action<KeyPlan>? OnInject;
        public InjectionResult Result = new(InjectionStatus.Typed, "ok");

        public Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default)
        {
            lock (Plans) { Plans.Add((processId, plan)); }
            if (Result.Status == InjectionStatus.Typed) { OnInject?.Invoke(plan); }
            return Task.FromResult(Result);
        }
    }
}
