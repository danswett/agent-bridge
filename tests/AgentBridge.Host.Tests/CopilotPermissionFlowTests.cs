using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using Xunit;

// Approving and denying Copilot's permission prompts from another device, end to end below
// the channel: a fixture session read by the real SessionSource, answered by the real
// AnswerService into a fake injector that shows a prompt on its "screen" and plays the CLI by
// writing the request's completion. Real keys into a real console are InjectorIntegrationTests.
public sealed class CopilotPermissionFlowTests : IAsyncLifetime
{
    const string Id = "c0b11070-0000-4000-8000-0000000000aa";

    static readonly string[] Prompt =
    [
        " git push origin main --force-with-lease",
        " Do you want to run this command?",
        "",
        " \u276F 1. Yes",
        "   2. Yes, and don't ask again for `git push` in this directory",
        "   3. No, and tell Copilot what to do differently (Esc to stop)",
    ];

    readonly Fixture f = new();
    readonly Probe probe = new();
    readonly Guard guard = new();
    readonly Injector injector = new() { Screen = Prompt };
    SessionSource source = null!;
    string events = "";
    string ledger = "";
    int second = 10;

    public async Task InitializeAsync()
    {
        events = f.CopilotSession(Id, 500);
        Fixture.Append(events, Copilot.User("ship it", 1), Copilot.TurnStart(2), Copilot.ToolStart("c1", "powershell", new { command = "git push" }, 3),
            Copilot.Permission("r1", "c1", Copilot.Prompts["commands"], 4));
        f.Processes.Run(AgentKind.Copilot, 500);
        ledger = Path.Combine(f.Root, "profile", "answer-attempts.json");
        source = new SessionSource(f.Options(), f.Processes, TimeProvider.System);
        await source.RefreshAsync();
    }

    public async Task DisposeAsync()
    {
        await source.DisposeAsync();
        f.Dispose();
    }

    AnswerService Answerer(bool remoteApproval = true) => new(source, f.Processes, probe, guard, injector, new AnswerOptions
    {
        CopilotHome = f.CopilotHome,
        PlatformSupported = true,
        ConfirmTimeout = TimeSpan.FromMilliseconds(600),
        PollInterval = TimeSpan.FromMilliseconds(25),
        AttemptLedgerPath = ledger,
        CopilotRemoteApproval = remoteApproval,
    });

    string Question => source.Get(Id)!.Question!.Id;

    AnswerRequest Request(string decision, string? request = null) => new()
    {
        Id = Id,
        Question = Question,
        Request = request ?? Guid.NewGuid().ToString(),
        Fields = new Dictionary<string, JsonElement> { ["decision"] = JsonSerializer.SerializeToElement(decision) },
    };

    void Completes(string kind, string? feedback = null) => injector.OnInject = _ => Fixture.Append(events, Copilot.PermissionDone("r1", "c1", kind, second++, feedback));

    [Fact]
    public async Task Approve_types_only_the_digit_of_the_plain_Yes_and_is_delivered_when_the_request_is_recorded_approved()
    {
        Completes("approved");

        var result = await Answerer().AnswerAsync(Request("approve"));

        Assert.Equal(new AnswerResult(AnswerOutcome.Delivered), result);
        var (pid, plan) = Assert.Single(injector.Plans);
        Assert.Equal(500, pid);
        var send = Assert.Single(plan.Sends);
        Assert.Equal(("1", false), (send.Text, send.Enter));
        Assert.Equal(Prompt.Select((text, i) => new ScreenRow(i, text)), send.Expect);
    }

    [Fact]
    public async Task Deny_types_a_lone_Escape_and_is_delivered_when_the_request_is_recorded_denied_with_no_message()
    {
        Completes("denied-interactively-by-user");

        var result = await Answerer().AnswerAsync(Request("deny"));

        Assert.Equal(new AnswerResult(AnswerOutcome.Delivered), result);
        var send = Assert.Single(Assert.Single(injector.Plans).Plan.Sends);
        Assert.Equal((KeyPlan.Escape, false, Prompt.Length), (send.Text, send.Enter, send.Expect!.Count));
    }

    [Fact]
    public async Task Deny_is_delivered_when_Copilot_records_the_request_cancelled_and_then_stops_the_turn()
    {
        // What Escape on "No (Esc to stop)" recorded live with Copilot CLI 1.0.105.
        injector.OnInject = _ => Fixture.Append(events,
            Copilot.Event("permission.completed", new { requestId = "r1", toolCallId = "c1", result = new { kind = "cancelled", reason = "Session aborted" } }, second++),
            Copilot.Event("abort", new { reason = "user_initiated" }, second++));

        var result = await Answerer().AnswerAsync(Request("deny"));

        Assert.Equal(new AnswerResult(AnswerOutcome.Delivered), result);
    }

    [Fact]
    public async Task A_cancel_the_turn_does_not_stop_after_is_not_taken_for_a_deny()
    {
        injector.OnInject = _ => Fixture.Append(events,
            Copilot.Event("permission.completed", new { requestId = "r1", toolCallId = "c1", result = new { kind = "cancelled" } }, second++));

        var result = await Answerer().AnswerAsync(Request("deny"));

        Assert.Equal(AnswerOutcome.Unconfirmed, result.Outcome);
        Assert.Contains("cancelled", result.Reason);
        Assert.DoesNotContain("at the machine", result.Reason);
    }

    [Fact]
    public async Task A_cancel_and_stopped_turn_is_never_taken_for_an_approval()
    {
        injector.OnInject = _ => Fixture.Append(events,
            Copilot.Event("permission.completed", new { requestId = "r1", toolCallId = "c1", result = new { kind = "cancelled" } }, second++),
            Copilot.Event("abort", new { reason = "user_initiated" }, second++));

        var result = await Answerer().AnswerAsync(Request("approve"));

        Assert.Equal(AnswerOutcome.Unconfirmed, result.Outcome);
    }

    [Theory]
    [InlineData("approve", "approved-for-session", null)]
    [InlineData("approve", "denied-interactively-by-user", null)]
    [InlineData("deny", "denied-interactively-by-user", "do it differently")]
    [InlineData("deny", "approved", null)]
    public async Task Anything_recorded_but_exactly_what_was_asked_is_unconfirmed(string decision, string kind, string? feedback)
    {
        Completes(kind, feedback);

        var result = await Answerer().AnswerAsync(Request(decision));

        Assert.Equal(AnswerOutcome.Unconfirmed, result.Outcome);
        Assert.Contains(kind, result.Reason);
        Assert.DoesNotContain("at the machine", result.Reason);
    }

    [Fact]
    public async Task Nothing_recorded_is_unconfirmed_and_the_prompt_is_never_typed_into_again()
    {
        var first = await Answerer().AnswerAsync(Request("approve"));
        var again = await Answerer().AnswerAsync(Request("approve"));

        Assert.Equal(AnswerOutcome.Unconfirmed, first.Outcome);
        Assert.Contains("did not record an answer", first.Reason);
        Assert.DoesNotContain("at the machine", first.Reason);
        Assert.Equal(AnswerOutcome.Refused, again.Outcome);
        Assert.Single(injector.Plans);
    }

    [Fact]
    public async Task A_resent_request_returns_its_first_outcome_without_typing_again()
    {
        Completes("approved");
        var answerer = Answerer();
        var request = Guid.NewGuid().ToString();

        var first = await answerer.AnswerAsync(Request("approve", request));
        var resent = await answerer.AnswerAsync(Request("approve", request));

        Assert.Equal(first, resent);
        Assert.Single(injector.Plans);
    }

    // ---- an approval the host escaped anything in (#803) ----

    const string EscapedId = "c0b11070-0000-4000-8000-0000000000bb";

    // A second session whose prompt's intention holds a right-to-left override: the host shows it
    // escaped, and Approve takes the confirmation.
    async Task<(string Events, Question Question)> EscapedAsync(string intention = "Push the branch \u202E")
    {
        var escapedEvents = f.CopilotSession(EscapedId, 501);
        var prompt = Copilot.Prompts["commands"].DeepClone().AsObject();
        prompt["intention"] = intention;
        Fixture.Append(escapedEvents, Copilot.User("ship it", 1), Copilot.TurnStart(2), Copilot.ToolStart("c1", "powershell", new { command = "git push" }, 3),
            Copilot.Permission("r1", "c1", prompt, 4));
        f.Processes.Run(AgentKind.Copilot, 501);
        await source.RefreshAsync();
        var q = source.Get(EscapedId)!.Question!;
        Assert.NotNull(q.Escaped);
        return (escapedEvents, q);
    }

    static AnswerRequest EscapedRequest(Question q, string decision, string? request = null) => new()
    {
        Id = EscapedId,
        Question = q.Id,
        Request = request ?? Guid.NewGuid().ToString(),
        Fields = new Dictionary<string, JsonElement> { ["decision"] = JsonSerializer.SerializeToElement(decision) },
    };

    // A confirmation as the host's relay path would hand one over, answering as told each time asked.
    sealed class Confirmed(params string?[] answers) : IEscapedConfirmation
    {
        public int Asked;
        public List<Question> Seen { get; } = [];
        public string? Check(Question question)
        {
            Seen.Add(question);
            return answers[Math.Min(Asked++, answers.Length - 1)];
        }
    }

    [Fact]
    public async Task Approve_on_an_escaped_approval_without_a_confirmation_is_refused_and_nothing_is_read_or_typed()
    {
        var (_, q) = await EscapedAsync();

        var result = await Answerer().AnswerAsync(EscapedRequest(q, "approve"));

        Assert.Equal(AnswerOutcome.Refused, result.Outcome);
        Assert.Contains("escaped-command confirmation", result.Reason);
        Assert.Equal(0, injector.ScreenReads);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_confirm_the_pipe_or_Home_Assistant_carries_is_not_one_and_nothing_is_typed()
    {
        var (_, q) = await EscapedAsync();
        var carried = EscapedRequest(q, "approve") with { Confirm = new SignedCommand { Body = "e30", By = "AA", Mac = "AA" } };

        var result = await Answerer().AnswerAsync(carried);

        Assert.Equal(AnswerOutcome.Refused, result.Outcome);
        Assert.Contains("through the relay", result.Reason);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task Deny_on_an_escaped_approval_needs_no_confirmation()
    {
        var (escapedEvents, q) = await EscapedAsync();
        injector.OnInject = _ => Fixture.Append(escapedEvents, Copilot.PermissionDone("r1", "c1", "denied-interactively-by-user", second++));

        var result = await Answerer().AnswerAsync(EscapedRequest(q, "deny"));

        Assert.Equal(new AnswerResult(AnswerOutcome.Delivered), result);
        Assert.Equal(KeyPlan.Escape, Assert.Single(Assert.Single(injector.Plans).Plan.Sends).Text);
    }

    [Fact]
    public async Task An_approval_with_too_many_escapes_to_mark_is_denied_from_anywhere_and_approved_only_at_the_machine()
    {
        // 513 zero-width spaces, well inside a question's message (#836 review): not truncated, so
        // Deny goes, and Approve is refused even with a confirmation that would otherwise hold.
        var (escapedEvents, q) = await EscapedAsync("Push " + string.Concat(Enumerable.Repeat("\u200B", EscapedApproval.MaxMarks + 1)));
        Assert.Null(q.Truncated);
        Assert.Equal(EscapedApproval.TooMany, q.Escaped!.Approve);

        var approve = await Answerer().AnswerAsync(EscapedRequest(q, "approve"), confirmed: new Confirmed((string?)null));
        Assert.Equal(AnswerOutcome.Refused, approve.Outcome);
        Assert.Contains("too many hidden characters", approve.Reason);
        Assert.Empty(injector.Plans);

        injector.OnInject = _ => Fixture.Append(escapedEvents, Copilot.PermissionDone("r1", "c1", "denied-interactively-by-user", second++));
        var deny = await Answerer().AnswerAsync(EscapedRequest(q, "deny"));
        Assert.Equal(new AnswerResult(AnswerOutcome.Delivered), deny);
        Assert.Equal(KeyPlan.Escape, Assert.Single(Assert.Single(injector.Plans).Plan.Sends).Text);
    }

    [Fact]
    public async Task A_confirmed_escaped_approval_is_checked_again_at_its_turn_to_type_and_typed_once()
    {
        var (escapedEvents, q) = await EscapedAsync();
        injector.OnInject = _ => Fixture.Append(escapedEvents, Copilot.PermissionDone("r1", "c1", "approved", second++));
        var confirmed = new Confirmed((string?)null);

        var result = await Answerer().AnswerAsync(EscapedRequest(q, "approve"), confirmed: confirmed);

        Assert.Equal(new AnswerResult(AnswerOutcome.Delivered), result);
        Assert.Equal("1", Assert.Single(Assert.Single(injector.Plans).Plan.Sends).Text);
        // When it was taken, and again as listed once its turn to type had come.
        Assert.Equal(2, confirmed.Asked);
        Assert.All(confirmed.Seen, s => Assert.Equal(q.Escaped!.Shown, s.Escaped!.Shown));
    }

    [Fact]
    public async Task A_confirmation_that_no_longer_holds_at_its_turn_to_type_types_nothing()
    {
        // The device taken off the approved list, say, while the answer waited its turn.
        var (_, q) = await EscapedAsync();

        var result = await Answerer().AnswerAsync(EscapedRequest(q, "approve"), confirmed: new Confirmed(null, "this device is not approved at this machine"));

        Assert.Equal(AnswerOutcome.Refused, result.Outcome);
        Assert.Contains("not approved at this machine", result.Reason);
        Assert.Equal(0, injector.ScreenReads);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task An_escaped_approval_a_budget_cut_is_never_confirmed()
    {
        // An intention past what a question carries: the message is cut, and said to be.
        var (_, q) = await EscapedAsync("\u202E" + new string('x', Limits.QuestionMessage));
        Assert.True(q.Truncated);
        var confirmed = new Confirmed((string?)null);

        var result = await Answerer().AnswerAsync(EscapedRequest(q, "approve"), confirmed: confirmed);

        Assert.Equal(AnswerOutcome.Refused, result.Outcome);
        Assert.Contains("too large to show in full", result.Reason);
        Assert.Equal(0, confirmed.Asked);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task An_escaped_approval_listed_cut_when_its_Approve_is_checked_again_is_refused_before_its_confirmation_is_asked()
    {
        // As listed again under the typing slot, where Answers.Check is not asked: whole when the
        // answer came, cut by the time its keys would go.
        var (_, q) = await EscapedAsync();
        var cut = q with { Truncated = true };
        var confirmed = new Confirmed((string?)null);

        var approve = AnswerService.EscapedRefusal(cut, EscapedRequest(cut, "approve"), confirmed);
        var deny = AnswerService.EscapedRefusal(cut, EscapedRequest(cut, "deny"), confirmed);

        Assert.Equal("part of this approval didn't fit in the message from the machine, so it can only be approved at the machine", approve);
        Assert.Null(deny);
        Assert.Equal(0, confirmed.Asked);
        Assert.Null(AnswerService.EscapedRefusal(q, EscapedRequest(q, "approve"), confirmed));
        Assert.Equal(1, confirmed.Asked);
    }

    [Fact]
    public async Task With_remote_approval_off_on_this_machine_an_approval_is_refused_and_nothing_is_read_or_typed()
    {
        var result = await Answerer(remoteApproval: false).AnswerAsync(Request("approve"));

        Assert.Equal(AnswerOutcome.Refused, result.Outcome);
        Assert.Contains("turned off on this machine", result.Reason);
        Assert.Equal(0, injector.ScreenReads);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_request_answered_at_the_machine_just_before_typing_is_stale_and_nothing_is_typed()
    {
        var question = Question;
        // The transcript moves on; the session the client saw has not caught up yet.
        Fixture.Append(events, Copilot.PermissionDone("r1", "c1", "approved", second++));

        var result = await Answerer().AnswerAsync(Request("approve") with { Question = question });

        Assert.Equal(AnswerOutcome.Stale, result.Outcome);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_request_that_reads_differently_from_what_was_shown_is_stale_and_nothing_is_typed()
    {
        var question = Question;
        var other = Copilot.Prompts["commands"].DeepClone().AsObject();
        other["fullCommandText"] = "git push origin main --force";
        File.WriteAllLines(events, [Copilot.User("ship it", 1), Copilot.Permission("r1", "c1", other, 4)]);

        var result = await Answerer().AnswerAsync(Request("approve") with { Question = question });

        Assert.Equal(new AnswerResult(AnswerOutcome.Stale, "Copilot's permission prompt reads differently from what was shown"), result);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task With_another_prompt_also_waiting_the_screen_cannot_say_which_is_shown_so_nothing_is_typed()
    {
        var question = Question;
        Fixture.Append(events, Copilot.Permission("r2", "c2", Copilot.Prompts["read"], 5, agentId: "helper"));

        var result = await Answerer().AnswerAsync(Request("approve") with { Question = question });

        Assert.Equal(AnswerOutcome.Refused, result.Outcome);
        Assert.Contains("more than one prompt", result.Reason);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_screen_offering_only_persistent_grants_is_refused_untyped_and_the_prompt_may_still_be_answered()
    {
        injector.Screen = [Prompt[0], "\u276F 1. Yes, and approve all file operations for the rest of the running session", "  2. No (Esc)"];
        var refused = await Answerer().AnswerAsync(Request("approve"));
        Assert.Equal(AnswerOutcome.Refused, refused.Outcome);
        Assert.Contains("no single plain Yes", refused.Reason);
        Assert.Empty(injector.Plans);

        injector.Screen = Prompt;
        Completes("approved");
        Assert.Equal(AnswerOutcome.Delivered, (await Answerer().AnswerAsync(Request("approve"))).Outcome);
    }

    [Fact]
    public async Task A_prompt_for_something_else_drawn_with_the_same_options_is_not_approved()
    {
        injector.Screen = [" git push origin release --force", .. Prompt.Skip(1)];

        var result = await Answerer().AnswerAsync(Request("approve"));

        Assert.Equal(AnswerOutcome.Refused, result.Outcome);
        Assert.Contains("does not show what this permission prompt is for", result.Reason);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_request_answered_at_the_machine_while_the_screen_was_read_is_stale_and_nothing_is_typed()
    {
        // The screen then shows whatever came next; what it showed is no longer this request.
        injector.OnScreen = () => Fixture.Append(events, Copilot.PermissionDone("r1", "c1", "approved", second++));

        var result = await Answerer().AnswerAsync(Request("approve"));

        Assert.Equal(AnswerOutcome.Stale, result.Outcome);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_row_that_changed_before_the_key_went_types_nothing_and_leaves_the_prompt_answerable()
    {
        injector.Result = new InjectionResult(InjectionStatus.NothingTyped, "the prompt on the session's screen changed before anything was typed");
        var refused = await Answerer().AnswerAsync(Request("approve"));
        Assert.Equal(AnswerOutcome.Refused, refused.Outcome);

        injector.Result = new InjectionResult(InjectionStatus.Typed, "ok");
        Completes("approved");
        Assert.Equal(AnswerOutcome.Delivered, (await Answerer().AnswerAsync(Request("approve"))).Outcome);
    }

    [Fact]
    public async Task Agent_ha_bridge_starting_before_the_key_goes_or_an_elevated_session_types_nothing()
    {
        probe.Answers = new Queue<string?>([null, "agent-ha-bridge is running for this user"]);
        var running = await Answerer().AnswerAsync(Request("approve"));
        Assert.Equal(new AnswerResult(AnswerOutcome.Refused, "agent-ha-bridge is running for this user"), running);

        guard.Reason = "the session runs as administrator";
        var elevated = await Answerer().AnswerAsync(Request("approve"));
        Assert.Equal(new AnswerResult(AnswerOutcome.Refused, "the session runs as administrator"), elevated);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_transcript_that_cannot_be_read_types_nothing()
    {
        var question = Question;
        File.Delete(events);

        var result = await Answerer().AnswerAsync(Request("approve") with { Question = question });

        Assert.Equal(AnswerOutcome.Refused, result.Outcome);
        Assert.Contains("could not be read", result.Reason);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_reply_waits_while_a_permission_prompt_that_can_be_answered_is_pending()
    {
        var replies = new ReplyService(source, f.Processes, probe, guard, injector, new ReplyOptions { CopilotHome = f.CopilotHome, PlatformSupported = true });

        var reply = await replies.ReplyAsync(new ReplyRequest { Id = Id, Request = Guid.NewGuid().ToString(), Text = "yes" });

        Assert.Equal(new ReplyResult(AnswerOutcome.Refused, "the session is waiting for an answer to its question; answer that first"), reply);
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
        public IReadOnlyList<string> Screen = [];
        public int ScreenReads;
        public Action? OnScreen;

        public Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default)
        {
            lock (Plans) { Plans.Add((processId, plan)); }
            if (Result.Status == InjectionStatus.Typed) { OnInject?.Invoke(plan); }
            return Task.FromResult(Result);
        }

        public Task<ConsoleScreen> ReadScreenAsync(int processId, CancellationToken cancellationToken = default)
        {
            ScreenReads++;
            OnScreen?.Invoke();
            return Task.FromResult(new ConsoleScreen(Screen, null));
        }
    }
}
