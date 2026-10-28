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
    }

    [Fact]
    public async Task Nothing_recorded_is_unconfirmed_and_the_prompt_is_never_typed_into_again()
    {
        var first = await Answerer().AnswerAsync(Request("approve"));
        var again = await Answerer().AnswerAsync(Request("approve"));

        Assert.Equal(AnswerOutcome.Unconfirmed, first.Outcome);
        Assert.Contains("did not record an answer", first.Reason);
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
